#Requires -Version 7.0

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateNotNullOrEmpty()]
    [string] $Configuration = 'Release',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]*$')]
    [string] $RuntimeIdentifier = 'win-x64',

    [bool] $SelfContained = $true,

    [switch] $SkipRestore,
    [switch] $KeepSymbols,
    [switch] $DryRun,
    [switch] $SkipDataCopy,
    [switch] $SkipNetworkDeployment,
    [switch] $AdoptLegacyReleased,

    # Advanced validation/deployment inputs. Relative values are resolved from this script's directory.
    [string] $AppDirectory,
    [string] $NetworkAppDirectory,
    [string] $StagingDirectory,
    [string] $LauncherStagingDirectory,
    [switch] $SkipPublish,

    # Test-only seed roots must be supplied together and cannot be inside App or the production source data trees.
    [string] $ProductFamilySourceDirectory,
    [string] $ProductExcelSourceDirectory,

    # Internal test control. Normal builds do not use this switch.
    [switch] $TestFailAfterAppCreation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'DocManager Desktop can only be published and deployed on Windows.'
}
if (-not $RuntimeIdentifier.Equals('win-x64', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The managed launcher and portable layout currently support only RuntimeIdentifier win-x64, not '$RuntimeIdentifier'."
}
if (-not $SelfContained) {
    throw 'The portable Released payload must be self-contained.'
}

$script:ManifestName = '.docmanager-publish-manifest.json'
$script:LauncherName = 'DocManager.Desktop.exe'
$script:ReleasedDirectoryName = 'Released'
$script:ProtectedDirectoryNames = @('Product Family', 'Product excel file')
$script:RequiredPublishFiles = @(
    'DocManager.Desktop.exe',
    'DocManager.Desktop.deps.json',
    'DocManager.Desktop.runtimeconfig.json'
)

function Resolve-FromProjectRoot {
    param(
        [Parameter(Mandatory)] [string] $Value,
        [Parameter(Mandatory)] [string] $ProjectRoot
    )

    if ([System.IO.Path]::IsPathRooted($Value)) {
        return [System.IO.Path]::GetFullPath($Value)
    }

    return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($ProjectRoot, $Value))
}

function Test-SameOrChildPath {
    param(
        [Parameter(Mandatory)] [string] $Candidate,
        [Parameter(Mandatory)] [string] $Root
    )

    $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if ($candidateFull.Equals($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    return $candidateFull.StartsWith(
        $rootFull + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-DirectoryIsNotReparsePoint {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Description
    )

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        throw "$Description must be a directory, but a file exists there: $Path"
    }

    if (Test-Path -LiteralPath $Path -PathType Container) {
        $attributes = [System.IO.File]::GetAttributes($Path)
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description cannot be a symbolic link or junction: $Path"
        }
    }
}

function ConvertTo-SafeRelativePath {
    param(
        [Parameter(Mandatory)] [string] $RelativePath,
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $SourceDescription
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        throw "$SourceDescription contains an empty path."
    }
    if ([System.IO.Path]::IsPathRooted($RelativePath) -or $RelativePath.Contains(':')) {
        throw "$SourceDescription contains a rooted or drive-qualified path: $RelativePath"
    }

    $normalized = $RelativePath.Replace('/', '\')
    $segments = $normalized.Split('\', [System.StringSplitOptions]::None)
    if ($segments.Count -eq 0 -or
        $segments.Where({ [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..') }).Count -ne 0) {
        throw "$SourceDescription contains traversal or an invalid segment: $RelativePath"
    }

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $fullPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($rootFull, $normalized))
    if (-not (Test-SameOrChildPath -Candidate $fullPath -Root $rootFull) -or
        $fullPath.Equals($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$SourceDescription escapes its allowed root: $RelativePath"
    }

    return $normalized
}

function ConvertTo-SafePayloadPath {
    param(
        [Parameter(Mandatory)] [string] $RelativePath,
        [Parameter(Mandatory)] [string] $AppRoot,
        [Parameter(Mandatory)] [string] $SourceDescription
    )

    $normalized = ConvertTo-SafeRelativePath -RelativePath $RelativePath -Root $AppRoot -SourceDescription $SourceDescription
    $segments = $normalized.Split('\')

    if ($script:ProtectedDirectoryNames.Where({
        $_.Equals($segments[0], [System.StringComparison]::OrdinalIgnoreCase)
    }).Count -ne 0) {
        throw "$SourceDescription targets protected product data: $RelativePath"
    }

    if ($segments.Count -eq 1 -and
        ($segments[0].Equals($script:ManifestName, [System.StringComparison]::OrdinalIgnoreCase) -or
         $segments[0] -like 'Prof Pricelist*.xlsx')) {
        throw "$SourceDescription targets a protected file: $RelativePath"
    }

    return $normalized
}

function Assert-AppPathHasNoReparsePoint {
    param(
        [Parameter(Mandatory)] [string] $AppRoot,
        [Parameter(Mandatory)] [string] $RelativePath
    )

    Assert-DirectoryIsNotReparsePoint -Path $AppRoot -Description 'App directory'
    $current = $AppRoot
    $segments = $RelativePath.Replace('/', '\').Split('\')
    for ($index = 0; $index -lt $segments.Count - 1; $index++) {
        $current = [System.IO.Path]::Combine($current, $segments[$index])
        if (Test-Path -LiteralPath $current -PathType Leaf) {
            throw "A file blocks a required deployment directory: $current"
        }
        if (Test-Path -LiteralPath $current -PathType Container) {
            $attributes = [System.IO.File]::GetAttributes($current)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Deployment refuses to traverse a symbolic link or junction: $current"
            }
        }
    }
}

function Assert-TreeHasNoReparsePoints {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $Description
    )

    Assert-DirectoryIsNotReparsePoint -Path $Root -Description $Description
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return
    }

    $queue = [System.Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($Root)
    while ($queue.Count -gt 0) {
        $directory = $queue.Dequeue()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description must not contain symbolic links, junctions, or reparse-point files: $($item.FullName)"
            }
            if ($item.PSIsContainer) {
                $queue.Enqueue($item.FullName)
            }
        }
    }
}

function Get-StagingPayload {
    param(
        [Parameter(Mandatory)] [string] $StagingRoot,
        [Parameter(Mandatory)] [bool] $IncludeSymbols
    )

    Assert-DirectoryIsNotReparsePoint -Path $StagingRoot -Description 'Publish staging directory'
    if (-not (Test-Path -LiteralPath $StagingRoot -PathType Container)) {
        throw "Publish staging directory does not exist: $StagingRoot"
    }

    $queue = [System.Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($StagingRoot)
    $allFiles = [System.Collections.Generic.List[object]]::new()
    while ($queue.Count -gt 0) {
        $directory = $queue.Dequeue()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Publish staging must not contain symbolic links, junctions, or reparse-point files: $($item.FullName)"
            }

            $relativePath = [System.IO.Path]::GetRelativePath($StagingRoot, $item.FullName)
            $safeRelativePath = ConvertTo-SafeRelativePath -RelativePath $relativePath -Root $StagingRoot -SourceDescription 'Publish staging'
            if ($item.PSIsContainer) {
                $queue.Enqueue($item.FullName)
                continue
            }
            if ([System.IO.Path]::GetExtension($safeRelativePath).Equals('.xlsx', [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Publish staging contains product/user data instead of publish output: $safeRelativePath"
            }

            $allFiles.Add([pscustomobject]@{
                PublishRelativePath = $safeRelativePath
                RelativePath = "$($script:ReleasedDirectoryName)\$safeRelativePath"
                SourcePath = $item.FullName
                Kind = 'Publish'
            })
        }
    }

    $allRelativePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $allFiles) {
        if (-not $allRelativePaths.Add($file.PublishRelativePath)) {
            throw "Publish staging contains duplicate paths that differ only by case: $($file.PublishRelativePath)"
        }
    }

    foreach ($requiredFile in $script:RequiredPublishFiles) {
        if (-not $allRelativePaths.Contains($requiredFile)) {
            throw "Publish staging is incomplete; required file is missing: $requiredFile"
        }
    }
    if (@($allFiles | Where-Object {
        [System.IO.Path]::GetExtension($_.PublishRelativePath).Equals('.dll', [System.StringComparison]::OrdinalIgnoreCase)
    }).Count -eq 0) {
        throw 'Publish staging is incomplete; it does not contain any DLL files.'
    }

    $payload = @($allFiles | Where-Object {
        $IncludeSymbols -or -not [System.IO.Path]::GetExtension($_.PublishRelativePath).Equals('.pdb', [System.StringComparison]::OrdinalIgnoreCase)
    } | Sort-Object -Property PublishRelativePath)
    if ($payload.Count -eq 0) {
        throw 'Publish staging does not contain any deployable files.'
    }

    return $payload
}

function Assert-LauncherPeX64 {
    param([Parameter(Mandatory)] [string] $Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 256 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        throw "Launcher is not a valid Windows PE executable: $Path"
    }

    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 24 -gt $bytes.Length -or
        $bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0 -or $bytes[$peOffset + 3] -ne 0) {
        throw "Launcher has an invalid PE header: $Path"
    }

    $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
    $optionalSize = [System.BitConverter]::ToUInt16($bytes, $peOffset + 20)
    $optionalOffset = $peOffset + 24
    if ($machine -ne 0x8664 -or $optionalSize -lt 240 -or $optionalOffset + $optionalSize -gt $bytes.Length -or
        [System.BitConverter]::ToUInt16($bytes, $optionalOffset) -ne 0x20b) {
        throw "Launcher must be an x64 PE32+ executable: $Path"
    }
}

function Assert-ManagedLauncherStaging {
    param(
        [Parameter(Mandatory)] [string] $StagingRoot,
        [bool] $RunProbe = $true
    )

    Assert-DirectoryIsNotReparsePoint -Path $StagingRoot -Description 'Launcher staging directory'
    if (-not (Test-Path -LiteralPath $StagingRoot -PathType Container)) {
        throw "Launcher staging directory does not exist: $StagingRoot"
    }

    $items = @(Get-ChildItem -LiteralPath $StagingRoot -Force)
    foreach ($item in $items) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Launcher staging cannot contain reparse points: $($item.FullName)"
        }
    }
    $directories = @($items | Where-Object PSIsContainer)
    $files = @($items | Where-Object { -not $_.PSIsContainer })
    if ($directories.Count -ne 0 -or $files.Count -ne 1 -or
        -not $files[0].Name.Equals($script:LauncherName, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Launcher staging must contain exactly one file named $($script:LauncherName): $StagingRoot"
    }

    $launcherPath = $files[0].FullName
    Assert-LauncherPeX64 -Path $launcherPath
    if (-not $RunProbe) {
        return $launcherPath
    }

    $probeDirectory = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'DocManager.LauncherProbe', [Guid]::NewGuid().ToString('N'))
    $probePath = [System.IO.Path]::Combine($probeDirectory, 'probe.json')
    [System.IO.Directory]::CreateDirectory($probeDirectory) | Out-Null
    try {
        $probeStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $probeStartInfo.FileName = $launcherPath
        $probeStartInfo.UseShellExecute = $false
        $probeStartInfo.CreateNoWindow = $true
        $probeStartInfo.ArgumentList.Add('--docmanager-launcher-probe')
        $probeStartInfo.ArgumentList.Add($probePath)
        $probeStartInfo.Environment['DOCMANAGER_LAUNCHER_NO_UI'] = '1'
        $probeProcess = [System.Diagnostics.Process]::Start($probeStartInfo)
        if ($null -eq $probeProcess) {
            throw "Windows did not return a process for the launcher runtime probe: $launcherPath"
        }
        try {
            $probeProcess.WaitForExit()
            $probeExitCode = $probeProcess.ExitCode
        } finally {
            $probeProcess.Dispose()
        }

        if ($probeExitCode -ne 73 -or -not (Test-Path -LiteralPath $probePath -PathType Leaf)) {
            throw "Launcher runtime probe failed with exit code ${probeExitCode}: $launcherPath"
        }
        try {
            $probe = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
        } catch {
            throw "Launcher runtime probe wrote invalid JSON: $probePath"
        }

        $expectedLauncherPath = [System.IO.Path]::GetFullPath($launcherPath)
        $expectedLauncherDirectory = [System.IO.Path]::GetFullPath($StagingRoot).TrimEnd('\', '/')
        $expectedReleasedDirectory = [System.IO.Path]::Combine($expectedLauncherDirectory, $script:ReleasedDirectoryName)
        $expectedTargetPath = [System.IO.Path]::Combine($expectedReleasedDirectory, $script:LauncherName)
        $expectedBaseDirectory = $expectedLauncherDirectory + [System.IO.Path]::DirectorySeparatorChar
        if ($null -eq $probe -or
            $probe.marker -ne 'DocManager.ManagedLauncher.v2' -or
            -not ([string]$probe.processPath).Equals($expectedLauncherPath, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not ([string]$probe.baseDirectory).Equals($expectedBaseDirectory, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not ([string]$probe.launcherDirectory).Equals($expectedLauncherDirectory, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not ([string]$probe.releasedDirectory).Equals($expectedReleasedDirectory, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not ([string]$probe.targetPath).Equals($expectedTargetPath, [System.StringComparison]::OrdinalIgnoreCase) -or
            ([string]$probe.architecture) -ne 'X64' -or -not [bool]$probe.is64BitProcess) {
            throw "Launcher runtime probe did not identify the expected managed x64 DocManager launcher: $launcherPath"
        }
    } finally {
        if (Test-Path -LiteralPath $probeDirectory) {
            Remove-Item -LiteralPath $probeDirectory -Recurse -Force -Confirm:$false
        }
    }

    return $launcherPath
}

function Read-PublishManifest {
    param([Parameter(Mandatory)] [string] $AppRoot)

    $manifestPath = [System.IO.Path]::Combine($AppRoot, $script:ManifestName)
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        return [pscustomobject]@{ Version = 0; Paths = @(); Path = $manifestPath }
    }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Publish manifest is not a file: $manifestPath"
    }
    if (([System.IO.File]::GetAttributes($manifestPath) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Publish manifest cannot be a reparse point: $manifestPath"
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    } catch {
        throw "Publish manifest is unreadable or invalid JSON; no App files were changed: $manifestPath"
    }

    if ($null -eq $manifest -or
        $manifest.PSObject.Properties.Name -notcontains 'version' -or
        [int]$manifest.version -notin @(1, 2) -or
        $manifest.PSObject.Properties.Name -notcontains 'files' -or
        $manifest.files -is [string] -or
        $manifest.files -isnot [System.Collections.IEnumerable]) {
        throw "Publish manifest has an unsupported structure; no App files were changed: $manifestPath"
    }

    $version = [int]$manifest.version
    $entries = @($manifest.files)
    if ($entries.Count -gt 10000) {
        throw "Publish manifest contains too many paths; no App files were changed: $manifestPath"
    }

    $paths = [System.Collections.Generic.List[string]]::new()
    $unique = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        if ($entry -isnot [string]) {
            throw "Publish manifest contains a non-string path; no App files were changed: $manifestPath"
        }
        $safePath = ConvertTo-SafePayloadPath -RelativePath $entry -AppRoot $AppRoot -SourceDescription 'Publish manifest'
        if (-not $unique.Add($safePath)) {
            throw "Publish manifest contains a duplicate path: $safePath"
        }

        if ($version -eq 2 -and
            -not $safePath.Equals($script:LauncherName, [System.StringComparison]::OrdinalIgnoreCase) -and
            -not $safePath.StartsWith($script:ReleasedDirectoryName + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Version 2 publish manifest can own only the root launcher and Released payload: $safePath"
        }
        if ($version -eq 1) {
            $segments = $safePath.Split('\')
            if ($segments.Count -ne 1) {
                throw "Version 1 publish manifest can own only direct App root files: $safePath"
            }
            $name = $segments[0]
            $isKnownLegacyPublishFile =
                $name.Equals($script:LauncherName, [System.StringComparison]::OrdinalIgnoreCase) -or
                $name.EndsWith('.dll', [System.StringComparison]::OrdinalIgnoreCase) -or
                $name.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase) -or
                $name.EndsWith('.deps.json', [System.StringComparison]::OrdinalIgnoreCase) -or
                $name.EndsWith('.runtimeconfig.json', [System.StringComparison]::OrdinalIgnoreCase) -or
                $name.EndsWith('.pdb', [System.StringComparison]::OrdinalIgnoreCase)
            if (-not $isKnownLegacyPublishFile) {
                throw "Version 1 publish manifest contains a path that is not a recognized root publish file: $safePath"
            }
        }
        $paths.Add($safePath)
    }

    if ($version -eq 2 -and -not $unique.Contains($script:LauncherName)) {
        throw "Version 2 publish manifest does not own the required root launcher: $manifestPath"
    }

    return [pscustomobject]@{ Version = $version; Paths = @($paths); Path = $manifestPath }
}

function Assert-FileCanBeReplaced {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return
    }
    if (([System.IO.File]::GetAttributes($Path) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "A managed App file cannot be a reparse point: $Path"
    }
    if (([System.IO.File]::GetAttributes($Path) -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
        throw "A managed App file is read-only. Close the app and correct the file attributes before retrying: $Path"
    }

    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
        $stream.Dispose()
    } catch {
        throw "A managed App file is locked or cannot be replaced. Close DocManager and retry: $Path"
    }
}

function Test-FilesIdentical {
    param(
        [Parameter(Mandatory)] [string] $First,
        [Parameter(Mandatory)] [string] $Second
    )

    $firstInfo = [System.IO.FileInfo]::new($First)
    $secondInfo = [System.IO.FileInfo]::new($Second)
    if ($firstInfo.Length -ne $secondInfo.Length) { return $false }
    return (Get-FileHash -LiteralPath $First -Algorithm SHA256).Hash.Equals(
        (Get-FileHash -LiteralPath $Second -Algorithm SHA256).Hash,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function New-DeploymentPlan {
    param(
        [Parameter(Mandatory)] [string] $AppRoot,
        [Parameter(Mandatory)] [object[]] $Payload,
        [Parameter(Mandatory)] [bool] $AllowLegacyReleasedAdoption
    )

    $manifest = Read-PublishManifest -AppRoot $AppRoot
    $oldSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($oldPath in $manifest.Paths) { [void]$oldSet.Add($oldPath) }
    $newSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $adopted = [System.Collections.Generic.List[string]]::new()

    foreach ($file in $Payload) {
        $safePath = ConvertTo-SafePayloadPath -RelativePath $file.RelativePath -AppRoot $AppRoot -SourceDescription 'Deployment payload'
        if (-not $newSet.Add($safePath)) {
            throw "Deployment payload contains a duplicate path: $safePath"
        }
        Assert-AppPathHasNoReparsePoint -AppRoot $AppRoot -RelativePath $safePath
        $targetPath = [System.IO.Path]::Combine($AppRoot, $safePath)
        if (Test-Path -LiteralPath $targetPath -PathType Container) {
            throw "A directory blocks a managed publish file: $targetPath"
        }
        if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf) -or $oldSet.Contains($safePath)) {
            continue
        }
        if (([System.IO.File]::GetAttributes($targetPath) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Deployment refuses to overwrite an unowned reparse-point file: $targetPath"
        }

        $isReleasedPayload = $safePath.StartsWith($script:ReleasedDirectoryName + '\', [System.StringComparison]::OrdinalIgnoreCase)
        $identical = $isReleasedPayload -and (Test-Path -LiteralPath $file.SourcePath -PathType Leaf) -and
            (Test-FilesIdentical -First $file.SourcePath -Second $targetPath)
        if ($isReleasedPayload -and ($identical -or $AllowLegacyReleasedAdoption)) {
            $adopted.Add($safePath)
            continue
        }

        throw "Deployment would overwrite an unowned file. Add a valid manifest, remove the collision, or use -AdoptLegacyReleased only for a verified legacy Released payload: $targetPath"
    }

    # Root publish files from a manifest-less deployment are unknown and must never be silently deleted.
    foreach ($file in @($Payload | Where-Object { $_.Kind -eq 'Publish' -and -not $_.PublishRelativePath.Contains('\') })) {
        $rootRelativePath = $file.PublishRelativePath
        $rootPath = [System.IO.Path]::Combine($AppRoot, $rootRelativePath)
        if ((Test-Path -LiteralPath $rootPath -PathType Leaf) -and -not $oldSet.Contains($rootRelativePath)) {
            throw "An unowned legacy root publish file collides with the new layout; nothing was changed: $rootPath"
        }
        if (Test-Path -LiteralPath $rootPath -PathType Container) {
            throw "A directory occupies a legacy root publish path; nothing was changed: $rootPath"
        }
    }

    if (Test-Path -LiteralPath $AppRoot -PathType Container) {
        foreach ($rootFile in Get-ChildItem -LiteralPath $AppRoot -File -Force) {
            $name = $rootFile.Name
            if ($oldSet.Contains($name) -or $newSet.Contains($name) -or
                $name.Equals($script:ManifestName, [System.StringComparison]::OrdinalIgnoreCase) -or
                $name -like 'Prof Pricelist*.xlsx') {
                continue
            }

            $isLegacyPublishBinary =
                $rootFile.Extension.Equals('.dll', [System.StringComparison]::OrdinalIgnoreCase) -or
                $rootFile.Extension.Equals('.exe', [System.StringComparison]::OrdinalIgnoreCase) -or
                $name.EndsWith('.deps.json', [System.StringComparison]::OrdinalIgnoreCase) -or
                $name.EndsWith('.runtimeconfig.json', [System.StringComparison]::OrdinalIgnoreCase)
            if ($isLegacyPublishBinary) {
                throw "An unowned legacy publish binary remains in App root; add a valid version 1 manifest or move it aside before migration: $($rootFile.FullName)"
            }
        }
    }

    $stalePaths = @($manifest.Paths | Where-Object { -not $newSet.Contains($_) } | Sort-Object)
    foreach ($stalePath in $stalePaths) {
        Assert-AppPathHasNoReparsePoint -AppRoot $AppRoot -RelativePath $stalePath
        $targetPath = [System.IO.Path]::Combine($AppRoot, $stalePath)
        if (Test-Path -LiteralPath $targetPath -PathType Container) {
            throw "A manifest-owned file path is now a directory; refusing to delete it: $targetPath"
        }
    }

    foreach ($protectedName in $script:ProtectedDirectoryNames) {
        Assert-AppPathHasNoReparsePoint -AppRoot $AppRoot -RelativePath "$protectedName\placeholder"
    }

    $managedExistingPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($relativePath in @($newSet) + $stalePaths) {
        $targetPath = [System.IO.Path]::Combine($AppRoot, $relativePath)
        if (Test-Path -LiteralPath $targetPath -PathType Leaf) { [void]$managedExistingPaths.Add($targetPath) }
    }
    if (Test-Path -LiteralPath $manifest.Path -PathType Leaf) { [void]$managedExistingPaths.Add($manifest.Path) }
    foreach ($path in $managedExistingPaths) { Assert-FileCanBeReplaced -Path $path }

    return [pscustomobject]@{
        Manifest = $manifest
        OldPaths = @($manifest.Paths)
        NewPaths = @($newSet | Sort-Object)
        StalePaths = $stalePaths
        AdoptedPaths = @($adopted)
        ExistingManagedPaths = @($managedExistingPaths)
    }
}

function Get-DataCopyPlan {
    param(
        [Parameter(Mandatory)] [string] $SourceRoot,
        [Parameter(Mandatory)] [string] $DestinationRoot,
        [Parameter(Mandatory)] [string] $LogicalRootName,
        [Parameter(Mandatory)] [string] $AppRoot
    )

    if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        throw "Required product data seed directory does not exist: $SourceRoot"
    }
    Assert-TreeHasNoReparsePoints -Root $SourceRoot -Description "$LogicalRootName source"
    Assert-TreeHasNoReparsePoints -Root $DestinationRoot -Description "$LogicalRootName destination"

    $directories = [System.Collections.Generic.List[object]]::new()
    $filesToCopy = [System.Collections.Generic.List[object]]::new()
    $existingFileCount = 0
    $queue = [System.Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($SourceRoot)

    while ($queue.Count -gt 0) {
        $sourceDirectory = $queue.Dequeue()
        $directoryRelative = [System.IO.Path]::GetRelativePath($SourceRoot, $sourceDirectory)
        $destinationDirectory = if ($directoryRelative -eq '.') {
            $DestinationRoot
        } else {
            $safeDirectoryRelative = ConvertTo-SafeRelativePath -RelativePath $directoryRelative -Root $SourceRoot -SourceDescription "$LogicalRootName source"
            [System.IO.Path]::Combine($DestinationRoot, $safeDirectoryRelative)
        }
        $logicalDirectory = if ($directoryRelative -eq '.') { $LogicalRootName } else { "$LogicalRootName\$safeDirectoryRelative" }
        Assert-AppPathHasNoReparsePoint -AppRoot $AppRoot -RelativePath "$logicalDirectory\placeholder"
        if (Test-Path -LiteralPath $destinationDirectory -PathType Leaf) {
            throw "A file blocks a product data directory: $destinationDirectory"
        }
        $directories.Add([pscustomobject]@{
            DestinationPath = $destinationDirectory
            LogicalRelativePath = $logicalDirectory
            Missing = -not (Test-Path -LiteralPath $destinationDirectory -PathType Container)
        })

        foreach ($item in Get-ChildItem -LiteralPath $sourceDirectory -Force) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$LogicalRootName source contains a prohibited reparse point: $($item.FullName)"
            }
            if ($item.PSIsContainer) {
                $queue.Enqueue($item.FullName)
                continue
            }

            $relativePath = ConvertTo-SafeRelativePath -RelativePath ([System.IO.Path]::GetRelativePath($SourceRoot, $item.FullName)) -Root $SourceRoot -SourceDescription "$LogicalRootName source"
            $logicalPath = "$LogicalRootName\$relativePath"
            Assert-AppPathHasNoReparsePoint -AppRoot $AppRoot -RelativePath $logicalPath
            $destinationPath = [System.IO.Path]::Combine($DestinationRoot, $relativePath)
            if (Test-Path -LiteralPath $destinationPath -PathType Container) {
                throw "A directory blocks a product data seed file: $destinationPath"
            }
            if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
                if (([System.IO.File]::GetAttributes($destinationPath) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Product data destination contains a prohibited reparse-point file: $destinationPath"
                }
                $existingFileCount++
                continue
            }

            $filesToCopy.Add([pscustomobject]@{
                SourcePath = $item.FullName
                DestinationPath = $destinationPath
                LogicalRelativePath = $logicalPath
            })
        }
    }

    return [pscustomobject]@{
        Name = $LogicalRootName
        Directories = @($directories | Sort-Object { $_.LogicalRelativePath.Split('\').Count }, LogicalRelativePath)
        FilesToCopy = @($filesToCopy | Sort-Object -Property LogicalRelativePath)
        ExistingFileCount = $existingFileCount
        MissingDirectoryCount = @($directories | Where-Object Missing).Count
    }
}

function New-TrackedDirectory {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $StopRoot,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [System.Collections.Generic.List[string]] $CreatedDirectories
    )

    if (Test-Path -LiteralPath $Path -PathType Container) { return }
    $missing = [System.Collections.Generic.Stack[string]]::new()
    $cursor = $Path
    while (-not (Test-Path -LiteralPath $cursor -PathType Container)) {
        if (Test-Path -LiteralPath $cursor -PathType Leaf) {
            throw "A file blocks a required directory: $cursor"
        }
        if (-not (Test-SameOrChildPath -Candidate $cursor -Root $StopRoot)) {
            throw "Refusing to create a directory outside App: $cursor"
        }
        $missing.Push($cursor)
        $parent = [System.IO.Path]::GetDirectoryName($cursor)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent.Equals($cursor, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Cannot determine a safe parent directory for: $cursor"
        }
        $cursor = $parent
    }

    while ($missing.Count -gt 0) {
        $directory = $missing.Pop()
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
        $CreatedDirectories.Add($directory)
    }
}

function Sync-AppLayout {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory)] [string] $AppRoot,
        [Parameter(Mandatory)] [string] $DestinationDescription,
        [Parameter(Mandatory)] [object[]] $Payload,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $DataPlans,
        [Parameter(Mandatory)] [bool] $Simulation,
        [Parameter(Mandatory)] [bool] $AllowLegacyReleasedAdoption,
        [Parameter(Mandatory)] [bool] $FailAfterAppCreation
    )

    $plan = New-DeploymentPlan -AppRoot $AppRoot -Payload $Payload -AllowLegacyReleasedAdoption $AllowLegacyReleasedAdoption
    $dataCopyCount = @($DataPlans | ForEach-Object { $_.FilesToCopy } | ForEach-Object { $_ }).Count
    $dataExistingCount = 0
    foreach ($dataPlan in $DataPlans) { $dataExistingCount += [int]$dataPlan.ExistingFileCount }

    if ($Simulation) {
        Write-Host "Dry run ($DestinationDescription): deploy $($plan.NewPaths.Count) managed files ($($plan.StalePaths.Count) stale manifest-owned files removed, $($plan.AdoptedPaths.Count) safe Released files adopted)."
        foreach ($dataPlan in $DataPlans) {
            Write-Host "Dry run ($DestinationDescription): $($dataPlan.Name): copy $($dataPlan.FilesToCopy.Count) missing files, preserve $($dataPlan.ExistingFileCount) existing files, create $($dataPlan.MissingDirectoryCount) directories."
        }
        Write-Host "Dry run ($DestinationDescription): destination-only product data and every unowned file remain untouched."
        Write-Host "Dry run ($DestinationDescription): no restore, publish, launcher execution, copy, delete, or directory creation was performed."
        return
    }

    if (-not $PSCmdlet.ShouldProcess($AppRoot, "Deploy v2 portable layout with $($plan.NewPaths.Count) managed files and $dataCopyCount missing data files")) {
        return
    }

    $appWasCreated = -not (Test-Path -LiteralPath $AppRoot -PathType Container)
    $createdAppParentDirectories = [System.Collections.Generic.List[string]]::new()
    if ($appWasCreated) {
        $missingParents = [System.Collections.Generic.Stack[string]]::new()
        $parentCursor = [System.IO.Path]::GetDirectoryName($AppRoot)
        while (-not [string]::IsNullOrWhiteSpace($parentCursor) -and
               -not (Test-Path -LiteralPath $parentCursor -PathType Container)) {
            if (Test-Path -LiteralPath $parentCursor -PathType Leaf) {
                throw "A file blocks the App destination parent: $parentCursor"
            }
            $missingParents.Push($parentCursor)
            $nextParent = [System.IO.Path]::GetDirectoryName($parentCursor)
            if ([string]::IsNullOrWhiteSpace($nextParent) -or $nextParent.Equals($parentCursor, [System.StringComparison]::OrdinalIgnoreCase)) {
                break
            }
            $parentCursor = $nextParent
        }
        [System.IO.Directory]::CreateDirectory($AppRoot) | Out-Null
        while ($missingParents.Count -gt 0) { $createdAppParentDirectories.Add($missingParents.Pop()) }
    }

    $transactionRoot = [System.IO.Path]::Combine($AppRoot, ".docmanager-deploy-$([Guid]::NewGuid().ToString('N'))")
    $preparedPayloadRoot = [System.IO.Path]::Combine($transactionRoot, 'new-payload')
    $preparedDataRoot = [System.IO.Path]::Combine($transactionRoot, 'new-data')
    $backupRoot = [System.IO.Path]::Combine($transactionRoot, 'backup')
    $backupManifestPath = [System.IO.Path]::Combine($transactionRoot, 'old-manifest.json')
    $preparedManifestPath = [System.IO.Path]::Combine($transactionRoot, 'new-manifest.json')
    $manifestTemporaryPath = [System.IO.Path]::Combine($AppRoot, ".$($script:ManifestName).$([Guid]::NewGuid().ToString('N')).part")
    $backedUpPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $writtenPayloadPaths = [System.Collections.Generic.List[string]]::new()
    $deletedStalePaths = [System.Collections.Generic.List[string]]::new()
    $createdDataFiles = [System.Collections.Generic.List[string]]::new()
    $createdDirectories = [System.Collections.Generic.List[string]]::new()
    $manifestWasReplaced = $false
    $manifestPreviouslyExisted = Test-Path -LiteralPath $plan.Manifest.Path -PathType Leaf

    try {
        if ($FailAfterAppCreation) {
            throw 'Injected test failure after App creation.'
        }
        [System.IO.Directory]::CreateDirectory($preparedPayloadRoot) | Out-Null
        [System.IO.Directory]::CreateDirectory($preparedDataRoot) | Out-Null
        [System.IO.Directory]::CreateDirectory($backupRoot) | Out-Null

        # Complete all source reads and backup copies before changing a managed App file.
        foreach ($file in $Payload) {
            $preparedPath = [System.IO.Path]::Combine($preparedPayloadRoot, $file.RelativePath)
            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($preparedPath)) | Out-Null
            [System.IO.File]::Copy($file.SourcePath, $preparedPath, $false)
        }
        foreach ($dataPlan in $DataPlans) {
            foreach ($file in $dataPlan.FilesToCopy) {
                $preparedPath = [System.IO.Path]::Combine($preparedDataRoot, $file.LogicalRelativePath)
                [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($preparedPath)) | Out-Null
                [System.IO.File]::Copy($file.SourcePath, $preparedPath, $false)
            }
        }

        # Recheck every data destination after staging source bytes, immediately before any App payload changes.
        foreach ($dataPlan in $DataPlans) {
            foreach ($directory in $dataPlan.Directories) {
                Assert-AppPathHasNoReparsePoint -AppRoot $AppRoot -RelativePath "$($directory.LogicalRelativePath)\placeholder"
                if (Test-Path -LiteralPath $directory.DestinationPath -PathType Leaf) {
                    throw "A file blocks a product data directory: $($directory.DestinationPath)"
                }
            }
            foreach ($file in $dataPlan.FilesToCopy) {
                Assert-AppPathHasNoReparsePoint -AppRoot $AppRoot -RelativePath $file.LogicalRelativePath
                if (Test-Path -LiteralPath $file.DestinationPath) {
                    throw "A product data path appeared after preflight; refusing to overwrite it: $($file.DestinationPath)"
                }
            }
        }

        foreach ($relativePath in @($plan.NewPaths + $plan.StalePaths | Sort-Object -Unique)) {
            $targetPath = [System.IO.Path]::Combine($AppRoot, $relativePath)
            if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) { continue }
            $backupPath = [System.IO.Path]::Combine($backupRoot, $relativePath)
            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($backupPath)) | Out-Null
            [System.IO.File]::Copy($targetPath, $backupPath, $false)
            [void]$backedUpPaths.Add($relativePath)
        }
        if (Test-Path -LiteralPath $plan.Manifest.Path -PathType Leaf) {
            [System.IO.File]::Copy($plan.Manifest.Path, $backupManifestPath, $false)
        }

        $manifestContent = [ordered]@{ version = 2; files = @($plan.NewPaths) }
        [System.IO.File]::WriteAllText(
            $preparedManifestPath,
            ($manifestContent | ConvertTo-Json -Depth 3) + [Environment]::NewLine,
            [System.Text.UTF8Encoding]::new($false))

        # Install the real Released payload first and the root launcher last.
        $orderedPayload = @(
            @($Payload | Where-Object { $_.RelativePath.StartsWith($script:ReleasedDirectoryName + '\', [System.StringComparison]::OrdinalIgnoreCase) } | Sort-Object -Property RelativePath)
            @($Payload | Where-Object { -not $_.RelativePath.StartsWith($script:ReleasedDirectoryName + '\', [System.StringComparison]::OrdinalIgnoreCase) } | Sort-Object -Property RelativePath)
        )
        foreach ($file in $orderedPayload) {
            Assert-AppPathHasNoReparsePoint -AppRoot $AppRoot -RelativePath $file.RelativePath
            $preparedPath = [System.IO.Path]::Combine($preparedPayloadRoot, $file.RelativePath)
            $targetPath = [System.IO.Path]::Combine($AppRoot, $file.RelativePath)
            New-TrackedDirectory -Path ([System.IO.Path]::GetDirectoryName($targetPath)) -StopRoot $AppRoot -CreatedDirectories $createdDirectories
            [System.IO.File]::Move($preparedPath, $targetPath, $true)
            $writtenPayloadPaths.Add($file.RelativePath)
        }

        foreach ($stalePath in $plan.StalePaths) {
            Assert-AppPathHasNoReparsePoint -AppRoot $AppRoot -RelativePath $stalePath
            $targetPath = [System.IO.Path]::Combine($AppRoot, $stalePath)
            if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
                [System.IO.File]::Delete($targetPath)
                $deletedStalePaths.Add($stalePath)
            }
        }

        foreach ($dataPlan in $DataPlans) {
            foreach ($directory in $dataPlan.Directories) {
                Assert-AppPathHasNoReparsePoint -AppRoot $AppRoot -RelativePath "$($directory.LogicalRelativePath)\placeholder"
                New-TrackedDirectory -Path $directory.DestinationPath -StopRoot $AppRoot -CreatedDirectories $createdDirectories
            }
            foreach ($file in $dataPlan.FilesToCopy) {
                Assert-AppPathHasNoReparsePoint -AppRoot $AppRoot -RelativePath $file.LogicalRelativePath
                if (Test-Path -LiteralPath $file.DestinationPath) {
                    throw "A product data path appeared after preflight; refusing to overwrite it: $($file.DestinationPath)"
                }
                $preparedPath = [System.IO.Path]::Combine($preparedDataRoot, $file.LogicalRelativePath)
                [System.IO.File]::Copy($preparedPath, $file.DestinationPath, $false)
                $createdDataFiles.Add($file.DestinationPath)
            }
        }

        [System.IO.File]::Move($preparedManifestPath, $manifestTemporaryPath, $false)
        [System.IO.File]::Move($manifestTemporaryPath, $plan.Manifest.Path, $true)
        $manifestWasReplaced = $true
    } catch {
        $deploymentError = $_
        $rollbackErrors = [System.Collections.Generic.List[string]]::new()

        if ($manifestWasReplaced -or $manifestPreviouslyExisted) {
            try {
                if (Test-Path -LiteralPath $backupManifestPath -PathType Leaf) {
                    [System.IO.File]::Copy($backupManifestPath, $plan.Manifest.Path, $true)
                } elseif (-not $manifestPreviouslyExisted -and (Test-Path -LiteralPath $plan.Manifest.Path -PathType Leaf)) {
                    [System.IO.File]::Delete($plan.Manifest.Path)
                }
            } catch { $rollbackErrors.Add("publish manifest ($($_.Exception.Message))") }
        }

        foreach ($path in @($createdDataFiles | Sort-Object -Descending)) {
            try { if (Test-Path -LiteralPath $path -PathType Leaf) { [System.IO.File]::Delete($path) } }
            catch { $rollbackErrors.Add("$path ($($_.Exception.Message))") }
        }
        foreach ($relativePath in @($writtenPayloadPaths | Sort-Object -Descending)) {
            $targetPath = [System.IO.Path]::Combine($AppRoot, $relativePath)
            try {
                if ($backedUpPaths.Contains($relativePath)) {
                    $backupPath = [System.IO.Path]::Combine($backupRoot, $relativePath)
                    [System.IO.File]::Copy($backupPath, $targetPath, $true)
                } elseif (Test-Path -LiteralPath $targetPath -PathType Leaf) {
                    [System.IO.File]::Delete($targetPath)
                }
            } catch { $rollbackErrors.Add("$targetPath ($($_.Exception.Message))") }
        }
        foreach ($relativePath in $deletedStalePaths) {
            $targetPath = [System.IO.Path]::Combine($AppRoot, $relativePath)
            $backupPath = [System.IO.Path]::Combine($backupRoot, $relativePath)
            try { [System.IO.File]::Copy($backupPath, $targetPath, $true) }
            catch { $rollbackErrors.Add("$targetPath ($($_.Exception.Message))") }
        }
        foreach ($directory in @($createdDirectories | Sort-Object { $_.Length } -Descending)) {
            try {
                if ((Test-Path -LiteralPath $directory -PathType Container) -and
                    (Get-ChildItem -LiteralPath $directory -Force | Measure-Object).Count -eq 0) {
                    [System.IO.Directory]::Delete($directory)
                }
            } catch { $rollbackErrors.Add("$directory ($($_.Exception.Message))") }
        }
        if ($appWasCreated) {
            try {
                if (Test-Path -LiteralPath $transactionRoot -PathType Container) {
                    Remove-Item -LiteralPath $transactionRoot -Recurse -Force -Confirm:$false
                }
                if ((Test-Path -LiteralPath $AppRoot -PathType Container) -and
                    (Get-ChildItem -LiteralPath $AppRoot -Force | Measure-Object).Count -eq 0) {
                    [System.IO.Directory]::Delete($AppRoot)
                }
                foreach ($parentDirectory in @($createdAppParentDirectories | Sort-Object { $_.Length } -Descending)) {
                    if ((Test-Path -LiteralPath $parentDirectory -PathType Container) -and
                        (Get-ChildItem -LiteralPath $parentDirectory -Force | Measure-Object).Count -eq 0) {
                        [System.IO.Directory]::Delete($parentDirectory)
                    }
                }
            } catch { $rollbackErrors.Add("new App directory ($($_.Exception.Message))") }
        }

        if ($rollbackErrors.Count -ne 0) {
            throw "Deployment failed: $($deploymentError.Exception.Message). Rollback also failed for: $($rollbackErrors -join '; ')"
        }
        throw "Deployment failed and App changes were rolled back: $($deploymentError.Exception.Message)"
    } finally {
        if (Test-Path -LiteralPath $manifestTemporaryPath -PathType Leaf) {
            try { Remove-Item -LiteralPath $manifestTemporaryPath -Force -Confirm:$false }
            catch { Write-Warning "Could not remove temporary publish manifest: $manifestTemporaryPath" }
        }
        if (Test-Path -LiteralPath $transactionRoot) {
            try { Remove-Item -LiteralPath $transactionRoot -Recurse -Force -Confirm:$false }
            catch { Write-Warning "Could not remove deployment transaction directory: $transactionRoot" }
        }
    }

    Write-Host "Portable App v2 updated successfully ($DestinationDescription): $AppRoot"
    Write-Host "Manifest owns only the root launcher and $($plan.NewPaths.Count - 1) Released publish files."
    if ($plan.Manifest.Version -eq 1) {
        Write-Host "Migration: removed only stale version 1 manifest-owned root payload; unowned root files were preserved or rejected on collision."
    }
    if ($plan.AdoptedPaths.Count -ne 0) {
        Write-Host "Migration: adopted $($plan.AdoptedPaths.Count) hash-identical or explicitly approved pre-v2 Released paths; unknown Released files were preserved."
    }
    Write-Host "Product data: copied $dataCopyCount missing files and preserved $dataExistingCount existing files byte-for-byte."
}

function Assert-NetworkDeploymentDestination {
    param([Parameter(Mandatory)] [string] $AppRoot)

    $parent = [System.IO.Path]::GetDirectoryName($AppRoot)
    $probePaths = [System.Collections.Generic.List[string]]::new()
    try {
        if ([string]::IsNullOrWhiteSpace($parent) -or -not (Test-Path -LiteralPath $parent -PathType Container)) {
            throw "The destination parent directory does not exist: $parent"
        }
        Assert-DirectoryIsNotReparsePoint -Path $parent -Description 'Network App destination parent'
        Assert-DirectoryIsNotReparsePoint -Path $AppRoot -Description 'Network App directory'

        $probeRoot = if (Test-Path -LiteralPath $AppRoot -PathType Container) { $AppRoot } else { $parent }
        $probePath = [System.IO.Path]::Combine($probeRoot, ".docmanager-network-write-probe-$([Guid]::NewGuid().ToString('N')).tmp")
        $probeMovedPath = "$probePath.moved"
        $probePaths.Add($probePath)
        $probePaths.Add($probeMovedPath)
        $probeStream = [System.IO.File]::Open($probePath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $probeBytes = [System.Text.UTF8Encoding]::new($false).GetBytes('DocManager network deployment preflight probe.')
            $probeStream.Write($probeBytes, 0, $probeBytes.Length)
        } finally {
            $probeStream.Dispose()
        }
        [System.IO.File]::Move($probePath, $probeMovedPath, $false)
        [System.IO.File]::Delete($probeMovedPath)
    } catch {
        throw "Network deployment destination is unavailable or unauthorized: $AppRoot. Verify that \\ADMIN\Public\y.DocManager is reachable and that your account has create/write/rename/delete permission, then retry or use -SkipNetworkDeployment when offline. No production payload was changed. Details: $($_.Exception.Message)"
    } finally {
        $cleanupErrors = [System.Collections.Generic.List[string]]::new()
        foreach ($probeCandidate in @($probePaths | Sort-Object -Unique)) {
            if (-not (Test-Path -LiteralPath $probeCandidate -PathType Leaf)) { continue }
            try { [System.IO.File]::Delete($probeCandidate) }
            catch { $cleanupErrors.Add("$probeCandidate ($($_.Exception.Message))") }
        }
        if ($cleanupErrors.Count -ne 0) {
            throw "Network deployment preflight could not remove its temporary access probe. Remove it manually before retrying: $($cleanupErrors -join '; ')"
        }
    }
}

function Get-DeploymentDataPlans {
    param(
        [Parameter(Mandatory)] [string] $AppRoot,
        [Parameter(Mandatory)] [string] $ProductFamilySourceRoot,
        [Parameter(Mandatory)] [string] $ProductExcelSourceRoot,
        [Parameter(Mandatory)] [bool] $SkipSeedCopy
    )

    if ($SkipSeedCopy) { return @() }

    return @(
        Get-DataCopyPlan -SourceRoot $ProductFamilySourceRoot -DestinationRoot ([System.IO.Path]::Combine($AppRoot, 'Product Family')) -LogicalRootName 'Product Family' -AppRoot $AppRoot
        Get-DataCopyPlan -SourceRoot $ProductExcelSourceRoot -DestinationRoot ([System.IO.Path]::Combine($AppRoot, 'Product excel file')) -LogicalRootName 'Product excel file' -AppRoot $AppRoot
    )
}

$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$desktopProject = [System.IO.Path]::Combine($projectRoot, 'src', 'DocManager.Desktop', 'DocManager.Desktop.csproj')
$launcherProject = [System.IO.Path]::Combine($projectRoot, 'tools', 'DocManager.Launcher', 'DocManager.Launcher.csproj')
$defaultAppDirectory = [System.IO.Path]::Combine($projectRoot, 'App')
$defaultNetworkAppDirectory = '\\ADMIN\Public\y.DocManager\App'
$artifactsRoot = [System.IO.Path]::Combine($projectRoot, 'artifacts')
$defaultStagingDirectory = [System.IO.Path]::Combine($artifactsRoot, 'publish', 'DocManager.Desktop')
$defaultLauncherStagingDirectory = [System.IO.Path]::Combine($artifactsRoot, 'publish', 'DocManager.Launcher')
$defaultProductFamilySource = [System.IO.Path]::Combine($projectRoot, 'Product Family')
$defaultProductExcelSource = [System.IO.Path]::Combine($projectRoot, 'Product excel file')

$hasProductFamilySourceOverride = -not [string]::IsNullOrWhiteSpace($ProductFamilySourceDirectory)
$hasProductExcelSourceOverride = -not [string]::IsNullOrWhiteSpace($ProductExcelSourceDirectory)
if ($hasProductFamilySourceOverride -ne $hasProductExcelSourceOverride) {
    throw 'ProductFamilySourceDirectory and ProductExcelSourceDirectory must be supplied together.'
}
$useTestSeedSources = $hasProductFamilySourceOverride -and $hasProductExcelSourceOverride
$productFamilySourceRoot = if ($useTestSeedSources) {
    Resolve-FromProjectRoot -Value $ProductFamilySourceDirectory -ProjectRoot $projectRoot
} else { $defaultProductFamilySource }
$productExcelSourceRoot = if ($useTestSeedSources) {
    Resolve-FromProjectRoot -Value $ProductExcelSourceDirectory -ProjectRoot $projectRoot
} else { $defaultProductExcelSource }

$appRoot = if ([string]::IsNullOrWhiteSpace($AppDirectory)) { $defaultAppDirectory } else {
    Resolve-FromProjectRoot -Value $AppDirectory -ProjectRoot $projectRoot
}
$networkAppRoot = if ([string]::IsNullOrWhiteSpace($NetworkAppDirectory)) { $defaultNetworkAppDirectory } else {
    Resolve-FromProjectRoot -Value $NetworkAppDirectory -ProjectRoot $projectRoot
}
$stagingRoot = if ([string]::IsNullOrWhiteSpace($StagingDirectory)) { $defaultStagingDirectory } else {
    Resolve-FromProjectRoot -Value $StagingDirectory -ProjectRoot $projectRoot
}
$launcherStagingRoot = if ([string]::IsNullOrWhiteSpace($LauncherStagingDirectory)) { $defaultLauncherStagingDirectory } else {
    Resolve-FromProjectRoot -Value $LauncherStagingDirectory -ProjectRoot $projectRoot
}

$appRoot = [System.IO.Path]::GetFullPath($appRoot)
$networkAppRoot = [System.IO.Path]::GetFullPath($networkAppRoot)
$stagingRoot = [System.IO.Path]::GetFullPath($stagingRoot)
$launcherStagingRoot = [System.IO.Path]::GetFullPath($launcherStagingRoot)

if ($appRoot.Equals($networkAppRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "AppDirectory must remain distinct from the secondary network destination: $networkAppRoot"
}
foreach ($destination in @(
    [pscustomobject]@{ Path = $appRoot; DefaultPath = $defaultAppDirectory; Description = 'App directory' }
    [pscustomobject]@{ Path = $networkAppRoot; DefaultPath = $defaultNetworkAppDirectory; Description = 'secondary network App directory' }
)) {
    if ($destination.Path.Equals([System.IO.Path]::GetPathRoot($destination.Path), [System.StringComparison]::OrdinalIgnoreCase) -or
        $destination.Path.Equals($projectRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe $($destination.Description): $($destination.Path)"
    }
    if ((Test-SameOrChildPath -Candidate $destination.Path -Root $projectRoot) -and
        -not $destination.Path.Equals($destination.DefaultPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "A custom $($destination.Description) inside the source tree is not allowed: $($destination.Path)"
    }
}
foreach ($pair in @(
    [pscustomobject]@{ First = $appRoot; Second = $networkAppRoot; Description = 'local App and secondary network App' },
    [pscustomobject]@{ First = $appRoot; Second = $stagingRoot; Description = 'App and Desktop publish staging' },
    [pscustomobject]@{ First = $appRoot; Second = $launcherStagingRoot; Description = 'App and launcher staging' },
    [pscustomobject]@{ First = $networkAppRoot; Second = $stagingRoot; Description = 'secondary network App and Desktop publish staging' },
    [pscustomobject]@{ First = $networkAppRoot; Second = $launcherStagingRoot; Description = 'secondary network App and launcher staging' },
    [pscustomobject]@{ First = $stagingRoot; Second = $launcherStagingRoot; Description = 'Desktop publish staging and launcher staging' }
)) {
    if ((Test-SameOrChildPath -Candidate $pair.First -Root $pair.Second) -or
        (Test-SameOrChildPath -Candidate $pair.Second -Root $pair.First)) {
        throw "$($pair.Description) directories must be separate and must not contain one another."
    }
}
if (-not $SkipPublish) {
    foreach ($publishRoot in @($stagingRoot, $launcherStagingRoot)) {
        if (-not (Test-SameOrChildPath -Candidate $publishRoot -Root $artifactsRoot) -or
            $publishRoot.Equals($artifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Publish staging must be a child of the project artifacts directory: $artifactsRoot"
        }
    }
}
if ($useTestSeedSources) {
    $productFamilySourceRoot = [System.IO.Path]::GetFullPath($productFamilySourceRoot)
    $productExcelSourceRoot = [System.IO.Path]::GetFullPath($productExcelSourceRoot)
    if ((Test-SameOrChildPath -Candidate $productFamilySourceRoot -Root $appRoot) -or
        (Test-SameOrChildPath -Candidate $productExcelSourceRoot -Root $appRoot) -or
        (Test-SameOrChildPath -Candidate $appRoot -Root $productFamilySourceRoot) -or
        (Test-SameOrChildPath -Candidate $appRoot -Root $productExcelSourceRoot) -or
        (Test-SameOrChildPath -Candidate $productFamilySourceRoot -Root $defaultProductFamilySource) -or
        (Test-SameOrChildPath -Candidate $productExcelSourceRoot -Root $defaultProductExcelSource) -or
        (Test-SameOrChildPath -Candidate $defaultProductFamilySource -Root $productFamilySourceRoot) -or
        (Test-SameOrChildPath -Candidate $defaultProductExcelSource -Root $productExcelSourceRoot)) {
        throw 'Test seed source overrides must be separate from App and from the production Product Family/Product excel file trees.'
    }
    Write-Host "Test seed source override: $productFamilySourceRoot and $productExcelSourceRoot"
}
if ($TestFailAfterAppCreation -and -not $useTestSeedSources) {
    throw 'Internal test controls require both isolated synthetic seed source overrides.'
}
if ($TestFailAfterAppCreation -and $appRoot.Equals($defaultAppDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Internal test controls cannot target the real App directory.'
}
if ($TestFailAfterAppCreation -and (-not $SkipPublish -or -not $SkipDataCopy)) {
    throw 'TestFailAfterAppCreation requires -SkipPublish and -SkipDataCopy.'
}

Assert-DirectoryIsNotReparsePoint -Path $appRoot -Description 'App directory'
Assert-DirectoryIsNotReparsePoint -Path $stagingRoot -Description 'Publish staging directory'
Assert-DirectoryIsNotReparsePoint -Path $launcherStagingRoot -Description 'Launcher staging directory'

$selfContainedValue = $SelfContained.ToString().ToLowerInvariant()
$desktopRestoreArguments = @(
    'restore', $desktopProject,
    '--runtime', $RuntimeIdentifier,
    "-p:SelfContained=$selfContainedValue",
    '-p:UseAppHost=true',
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false'
)
$launcherRestoreArguments = @(
    'restore', $launcherProject,
    '--runtime', 'win-x64',
    '-p:SelfContained=true',
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=false'
)
$desktopPublishArguments = @(
    'publish', $desktopProject,
    '--configuration', $Configuration,
    '--runtime', $RuntimeIdentifier,
    '--self-contained', $selfContainedValue,
    '--no-restore',
    '--output', $stagingRoot,
    '-p:UseAppHost=true',
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false'
)
$launcherPublishArguments = @(
    'publish', $launcherProject,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '--output', $launcherStagingRoot,
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)

$simulation = $DryRun -or $WhatIfPreference
if ($simulation) {
    if (-not $SkipPublish) {
        if (-not $SkipRestore) {
            Write-Host "Dry run: dotnet $($desktopRestoreArguments -join ' ')"
            Write-Host "Dry run: dotnet $($launcherRestoreArguments -join ' ')"
        }
        Write-Host "Dry run: clean Desktop publish staging directory only: $stagingRoot"
        Write-Host "Dry run: dotnet $($desktopPublishArguments -join ' ')"
        Write-Host "Dry run: clean launcher staging directory only: $launcherStagingRoot"
        Write-Host "Dry run: dotnet $($launcherPublishArguments -join ' ')"
    } else {
        Write-Host "Dry run: skip publish and use existing Desktop staging: $stagingRoot"
        Write-Host "Dry run: skip publish and use existing launcher staging: $launcherStagingRoot"
    }

    $localDataPlans = Get-DeploymentDataPlans -AppRoot $appRoot -ProductFamilySourceRoot $productFamilySourceRoot -ProductExcelSourceRoot $productExcelSourceRoot -SkipSeedCopy $SkipDataCopy.IsPresent
    $networkDataPlans = @()
    if (-not $SkipNetworkDeployment) {
        Assert-NetworkDeploymentDestination -AppRoot $networkAppRoot
        $networkDataPlans = Get-DeploymentDataPlans -AppRoot $networkAppRoot -ProductFamilySourceRoot $productFamilySourceRoot -ProductExcelSourceRoot $productExcelSourceRoot -SkipSeedCopy $SkipDataCopy.IsPresent
    } else {
        Write-Host 'Dry run: network deployment skipped by -SkipNetworkDeployment.'
    }
    if ($SkipDataCopy) {
        Write-Host 'Dry run: product data seed copy skipped by -SkipDataCopy.'
    }

    if ((Test-Path -LiteralPath $stagingRoot -PathType Container) -and
        (Test-Path -LiteralPath $launcherStagingRoot -PathType Container)) {
        $publishPayload = @(Get-StagingPayload -StagingRoot $stagingRoot -IncludeSymbols $KeepSymbols.IsPresent)
        $launcherCandidate = Assert-ManagedLauncherStaging -StagingRoot $launcherStagingRoot -RunProbe $false
        $payload = @($publishPayload) + @([pscustomobject]@{
            PublishRelativePath = $null
            RelativePath = $script:LauncherName
            SourcePath = $launcherCandidate
            Kind = 'Launcher'
        })
        Sync-AppLayout -AppRoot $appRoot -DestinationDescription 'local primary destination' -Payload $payload -DataPlans $localDataPlans -Simulation $true -AllowLegacyReleasedAdoption $AdoptLegacyReleased.IsPresent -FailAfterAppCreation $false
        if (-not $SkipNetworkDeployment) {
            Sync-AppLayout -AppRoot $networkAppRoot -DestinationDescription 'secondary network destination' -Payload $payload -DataPlans $networkDataPlans -Simulation $true -AllowLegacyReleasedAdoption $AdoptLegacyReleased.IsPresent -FailAfterAppCreation $false
        }
    } elseif ($SkipPublish) {
        if (-not (Test-Path -LiteralPath $stagingRoot -PathType Container)) {
            throw "Publish staging directory does not exist: $stagingRoot"
        }
        throw "Launcher staging directory does not exist: $launcherStagingRoot"
    } else {
        foreach ($dataPlan in $localDataPlans) {
            Write-Host "Dry run (local primary destination): $($dataPlan.Name): copy $($dataPlan.FilesToCopy.Count) missing files, preserve $($dataPlan.ExistingFileCount) existing files, create $($dataPlan.MissingDirectoryCount) directories."
        }
        foreach ($dataPlan in $networkDataPlans) {
            Write-Host "Dry run (secondary network destination): $($dataPlan.Name): copy $($dataPlan.FilesToCopy.Count) missing files, preserve $($dataPlan.ExistingFileCount) existing files, create $($dataPlan.MissingDirectoryCount) directories."
        }
        Write-Host 'Dry run: deployment file changes will be calculated only after both publish staging directories exist.'
        Write-Host 'Dry run: no files or directories were changed.'
    }
    return
}

if (-not $SkipPublish) {
    foreach ($publishRoot in @($stagingRoot, $launcherStagingRoot)) {
        if (Test-Path -LiteralPath $publishRoot) {
            Remove-Item -LiteralPath $publishRoot -Recurse -Force -Confirm:$false
        }
        [System.IO.Directory]::CreateDirectory($publishRoot) | Out-Null
    }

    if (-not $SkipRestore) {
        & dotnet @desktopRestoreArguments
        if ($LASTEXITCODE -ne 0) { throw "Desktop dotnet restore failed with exit code $LASTEXITCODE. App was not changed." }
        & dotnet @launcherRestoreArguments
        if ($LASTEXITCODE -ne 0) { throw "Launcher dotnet restore failed with exit code $LASTEXITCODE. App was not changed." }
    }
    & dotnet @desktopPublishArguments
    if ($LASTEXITCODE -ne 0) { throw "Desktop dotnet publish failed with exit code $LASTEXITCODE. App was not changed." }
    & dotnet @launcherPublishArguments
    if ($LASTEXITCODE -ne 0) { throw "Launcher dotnet publish failed with exit code $LASTEXITCODE. App was not changed." }
}

$publishPayload = @(Get-StagingPayload -StagingRoot $stagingRoot -IncludeSymbols $KeepSymbols.IsPresent)
$launcherPath = Assert-ManagedLauncherStaging -StagingRoot $launcherStagingRoot
Write-Host "Launcher: validated self-contained single-file managed x64 executable $launcherPath"

$localDataPlans = Get-DeploymentDataPlans -AppRoot $appRoot -ProductFamilySourceRoot $productFamilySourceRoot -ProductExcelSourceRoot $productExcelSourceRoot -SkipSeedCopy $SkipDataCopy.IsPresent
if ($SkipDataCopy) {
    Write-Host 'Product data seed copy skipped by -SkipDataCopy.'
}

$payload = @($publishPayload) + @([pscustomobject]@{
    PublishRelativePath = $null
    RelativePath = $script:LauncherName
    SourcePath = $launcherPath
    Kind = 'Launcher'
})

$networkDataPlans = @()
if (-not $SkipNetworkDeployment) {
    try {
        Assert-NetworkDeploymentDestination -AppRoot $networkAppRoot
        $networkDataPlans = Get-DeploymentDataPlans -AppRoot $networkAppRoot -ProductFamilySourceRoot $productFamilySourceRoot -ProductExcelSourceRoot $productExcelSourceRoot -SkipSeedCopy $SkipDataCopy.IsPresent
        [void](New-DeploymentPlan -AppRoot $networkAppRoot -Payload $payload -AllowLegacyReleasedAdoption $AdoptLegacyReleased.IsPresent)
    } catch {
        throw "Secondary network deployment was blocked before the local App deployment started: $($_.Exception.Message) Retry after resolving the network destination issue, or rerun with -SkipNetworkDeployment while offline."
    }
}

Sync-AppLayout -AppRoot $appRoot -DestinationDescription 'local primary destination' -Payload $payload -DataPlans $localDataPlans -Simulation $false -AllowLegacyReleasedAdoption $AdoptLegacyReleased.IsPresent -FailAfterAppCreation $TestFailAfterAppCreation.IsPresent

if ($SkipNetworkDeployment) {
    Write-Host 'Secondary network deployment skipped by -SkipNetworkDeployment.'
    return
}

try {
    Sync-AppLayout -AppRoot $networkAppRoot -DestinationDescription 'secondary network destination' -Payload $payload -DataPlans $networkDataPlans -Simulation $false -AllowLegacyReleasedAdoption $AdoptLegacyReleased.IsPresent -FailAfterAppCreation $false
} catch {
    throw "Secondary network deployment failed after the local App deployment committed successfully: $($_.Exception.Message) Retry the network deployment after resolving the destination issue, or rerun with -SkipNetworkDeployment while offline."
}
