#Requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($PSScriptRoot, '..'))
$buildScript = [System.IO.Path]::Combine($projectRoot, 'build_app.ps1')
$launcherProject = [System.IO.Path]::Combine($projectRoot, 'tools', 'DocManager.Launcher', 'DocManager.Launcher.csproj')
$testChildProject = [System.IO.Path]::Combine($PSScriptRoot, 'DocManager.Launcher.TestChild', 'DocManager.Launcher.TestChild.csproj')
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$temporaryRoot = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'DocManager.BuildTests', [Guid]::NewGuid().ToString('N'))
$launcherStaging = [System.IO.Path]::Combine($temporaryRoot, 'launcher-staging')
$testChildStaging = [System.IO.Path]::Combine($temporaryRoot, 'test-child-staging')
$launcherPath = [System.IO.Path]::Combine($launcherStaging, 'DocManager.Desktop.exe')
$testChildPath = [System.IO.Path]::Combine($testChildStaging, 'DocManager.Desktop.exe')

function Invoke-DotnetPublish {
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $Output
    )

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($Project)
    $testBuildRoot = [System.IO.Path]::Combine($temporaryRoot, 'publish-intermediate', $projectName)
    $baseOutputPath = [System.IO.Path]::Combine($testBuildRoot, 'bin') + [System.IO.Path]::DirectorySeparatorChar
    $baseIntermediateOutputPath = [System.IO.Path]::Combine($testBuildRoot, 'obj') + [System.IO.Path]::DirectorySeparatorChar
    & dotnet publish $Project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false "-p:BaseOutputPath=$baseOutputPath" "-p:BaseIntermediateOutputPath=$baseIntermediateOutputPath" -o $Output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project with exit code $LASTEXITCODE"
    }
}

function Invoke-Launcher {
    param(
        [Parameter(Mandatory)] [string] $Launcher,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $Arguments,
        [hashtable] $EnvironmentVariables = @{}
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Launcher
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $startInfo.Environment['DOCMANAGER_LAUNCHER_NO_UI'] = '1'
    foreach ($entry in $EnvironmentVariables.GetEnumerator()) {
        $startInfo.Environment[[string]$entry.Key] = [string]$entry.Value
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw "Windows did not return a process for launcher test: $Launcher" }
    try {
        $process.WaitForExit()
        return $process.ExitCode
    } finally {
        $process.Dispose()
    }
}

function Assert-True {
    param([Parameter(Mandatory)] [bool] $Condition, [Parameter(Mandatory)] [string] $Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Assert-Equal {
    param($Expected, $Actual, [Parameter(Mandatory)] [string] $Message)
    if ($Expected -ne $Actual) { throw "Assertion failed: $Message. Expected '$Expected', actual '$Actual'." }
}

function Invoke-BuildScript {
    param(
        [Parameter(Mandatory)] [string[]] $Arguments,
        [Parameter(Mandatory)] [bool] $ExpectSuccess
    )

    $output = @(& $pwsh -NoProfile -File $buildScript @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    if ($ExpectSuccess -and $exitCode -ne 0) {
        throw "build_app.ps1 failed unexpectedly ($exitCode):`n$($output -join [Environment]::NewLine)"
    }
    if (-not $ExpectSuccess -and $exitCode -eq 0) {
        throw "build_app.ps1 succeeded unexpectedly:`n$($output -join [Environment]::NewLine)"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function New-FakePublishStaging {
    param([Parameter(Mandatory)] [string] $Path, [string] $Marker = 'first')

    [System.IO.Directory]::CreateDirectory([System.IO.Path]::Combine($Path, 'runtimes', 'win-x64', 'native')) | Out-Null
    [System.IO.File]::Copy([System.IO.Path]::Combine($env:SystemRoot, 'System32', 'where.exe'), [System.IO.Path]::Combine($Path, 'DocManager.Desktop.exe'), $false)
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($Path, 'DocManager.Desktop.deps.json'), '{"runtimeTarget":{"name":"test"}}')
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($Path, 'DocManager.Desktop.runtimeconfig.json'), '{"runtimeOptions":{"tfm":"net8.0-windows"}}')
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($Path, 'DocManager.Core.dll'), "managed-$Marker")
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($Path, 'runtimes', 'win-x64', 'native', 'payload.bin'), "native-$Marker")
}

function Get-RelativeFileMap {
    param([Parameter(Mandatory)] [string] $Root)

    $map = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse -Force) {
        $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName)
        $map[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $map
}

function Assert-SeedCopyMatches {
    param([Parameter(Mandatory)] [string] $Source, [Parameter(Mandatory)] [string] $Destination)

    $sourceMap = Get-RelativeFileMap -Root $Source
    $destinationMap = Get-RelativeFileMap -Root $Destination
    Assert-Equal $sourceMap.Count $destinationMap.Count "seed file count for $Destination"
    foreach ($relative in $sourceMap.Keys) {
        Assert-True $destinationMap.ContainsKey($relative) "destination contains seed file $relative"
        Assert-Equal $sourceMap[$relative] $destinationMap[$relative] "seed bytes for $relative"
    }
}

function Get-DeployArguments {
    param(
        [Parameter(Mandatory)] [string] $App,
        [Parameter(Mandatory)] [string] $Staging,
        [switch] $SkipDataCopy,
        [string] $NetworkApp,
        [switch] $DeployNetwork
    )

    $arguments = @(
        '-SkipPublish',
        '-StagingDirectory', $Staging,
        '-LauncherStagingDirectory', $launcherStaging,
        '-AppDirectory', $App,
        '-ProductFamilySourceDirectory', $productFamilySource,
        '-ProductExcelSourceDirectory', $productExcelSource
    )
    if ($DeployNetwork) {
        if ([string]::IsNullOrWhiteSpace($NetworkApp)) { throw 'DeployNetwork requires NetworkApp.' }
        $arguments += '-NetworkAppDirectory', $NetworkApp
    } else {
        $arguments += '-SkipNetworkDeployment'
    }
    if ($SkipDataCopy) { $arguments += '-SkipDataCopy' }
    return $arguments
}

try {
    [System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    Invoke-DotnetPublish -Project $launcherProject -Output $launcherStaging
    Invoke-DotnetPublish -Project $testChildProject -Output $testChildStaging
    Assert-True -Condition (Test-Path -LiteralPath $launcherPath -PathType Leaf) -Message 'managed launcher publish creates expected root executable'
    Assert-Equal 1 @(Get-ChildItem -LiteralPath $launcherStaging -File -Force).Count 'launcher staging contains one file'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $launcherStaging -Directory -Force).Count 'launcher staging contains no sidecar directories'

    $probePath = [System.IO.Path]::Combine($temporaryRoot, 'launcher-probe.json')
    Assert-Equal 73 (Invoke-Launcher -Launcher $launcherPath -Arguments @('--docmanager-launcher-probe', $probePath)) 'launcher hidden probe exit code'
    $probe = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
    Assert-Equal 'DocManager.ManagedLauncher.v2' ([string]$probe.marker) 'launcher hidden probe marker'
    Assert-Equal 'X64' ([string]$probe.architecture) 'launcher hidden probe architecture'
    Assert-True -Condition ([bool]$probe.is64BitProcess) -Message 'launcher hidden probe reports a 64-bit process'
    Assert-Equal ([System.IO.Path]::GetFullPath($launcherPath)) ([System.IO.Path]::GetFullPath([string]$probe.processPath)) 'launcher hidden probe process path'
    Assert-Equal ([System.IO.Path]::Combine($launcherStaging, 'Released', 'DocManager.Desktop.exe')) ([System.IO.Path]::GetFullPath([string]$probe.targetPath)) 'launcher hidden probe target path'

    $missingLayout = [System.IO.Path]::Combine($temporaryRoot, 'missing-target-layout')
    [System.IO.Directory]::CreateDirectory($missingLayout) | Out-Null
    $missingLauncher = [System.IO.Path]::Combine($missingLayout, 'DocManager.Desktop.exe')
    [System.IO.File]::Copy($launcherPath, $missingLauncher, $false)
    $missingExitCode = Invoke-Launcher -Launcher $missingLauncher -Arguments @()
    Assert-True -Condition ($missingExitCode -ne 0) -Message 'launcher returns nonzero when Released target is missing'

    $recursiveLayout = [System.IO.Path]::Combine($temporaryRoot, 'recursive-layout')
    $recursiveReleased = [System.IO.Path]::Combine($recursiveLayout, 'Released')
    [System.IO.Directory]::CreateDirectory($recursiveReleased) | Out-Null
    $recursiveLauncher = [System.IO.Path]::Combine($recursiveLayout, 'DocManager.Desktop.exe')
    $recursiveTarget = [System.IO.Path]::Combine($recursiveReleased, 'DocManager.Desktop.exe')
    [System.IO.File]::Copy($launcherPath, $recursiveLauncher, $false)
    $recursiveHardLinkCreated = $true
    try {
        New-Item -ItemType HardLink -Path $recursiveTarget -Target $recursiveLauncher -ErrorAction Stop | Out-Null
    } catch {
        $recursiveHardLinkCreated = $false
    }
    if ($recursiveHardLinkCreated) {
        $recursiveExitCode = Invoke-Launcher -Launcher $recursiveLauncher -Arguments @()
        Assert-True -Condition ($recursiveExitCode -ne 0) -Message 'launcher refuses a target that resolves to itself'
    }

    $forwardLayout = [System.IO.Path]::Combine($temporaryRoot, 'forward-layout')
    $forwardReleased = [System.IO.Path]::Combine($forwardLayout, 'Released')
    [System.IO.Directory]::CreateDirectory($forwardReleased) | Out-Null
    $forwardLauncher = [System.IO.Path]::Combine($forwardLayout, 'DocManager.Desktop.exe')
    [System.IO.File]::Copy($launcherPath, $forwardLauncher, $false)
    [System.IO.File]::Copy($testChildPath, [System.IO.Path]::Combine($forwardReleased, 'DocManager.Desktop.exe'), $false)
    $forwardResultPath = [System.IO.Path]::Combine($temporaryRoot, 'forward-result.json')
    $forwardArguments = @('plain', 'two words', 'quote"inside', 'trailing\', 'Đèn_日本', '')
    $forwardExitCode = Invoke-Launcher -Launcher $forwardLauncher -Arguments $forwardArguments -EnvironmentVariables @{
        DOCMANAGER_LAUNCHER_TEST_OUTPUT = $forwardResultPath
    }
    Assert-Equal 37 $forwardExitCode 'launcher returns child exit code'
    $forwardResult = Get-Content -LiteralPath $forwardResultPath -Raw | ConvertFrom-Json
    Assert-Equal $forwardReleased ([System.IO.Path]::GetFullPath([string]$forwardResult.workingDirectory)) 'launcher sets Released working directory'
    Assert-Equal $forwardArguments.Count @($forwardResult.arguments).Count 'launcher preserves argument count including empty argument'
    for ($index = 0; $index -lt $forwardArguments.Count; $index++) {
        Assert-Equal $forwardArguments[$index] ([string]$forwardResult.arguments[$index]) "launcher preserves argument $index"
    }

    $productFamilySource = [System.IO.Path]::Combine($temporaryRoot, 'seed-source', 'Product Family')
    $productExcelSource = [System.IO.Path]::Combine($temporaryRoot, 'seed-source', 'Product excel file')
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::Combine($productFamilySource, 'Family A', 'IES')) | Out-Null
    [System.IO.Directory]::CreateDirectory($productExcelSource) | Out-Null
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($productFamilySource, 'Family A', 'fixture.pdf'), 'synthetic-pdf-seed')
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($productFamilySource, 'Family A', 'IES', 'fixture.ies'), 'synthetic-ies-seed')
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($productExcelSource, 'Prof Pricelist V1.0 2026 effective_Mar2026.xlsx'), 'synthetic-xlsx-seed')

    $tokens = $null
    $parseErrors = $null
    $buildAst = [System.Management.Automation.Language.Parser]::ParseFile($buildScript, [ref]$tokens, [ref]$parseErrors)
    Assert-Equal 0 @($parseErrors).Count 'PowerShell parser error count'
    Assert-True -Condition (($buildAst.ParamBlock.Parameters.Name.VariablePath.UserPath -contains 'SkipNetworkDeployment')) -Message 'build script exposes offline network deployment opt-out'
    Assert-True -Condition (($buildAst.ParamBlock.Parameters.Name.VariablePath.UserPath -contains 'NetworkAppDirectory')) -Message 'build script parameterizes the secondary destination for isolated tests'
    $buildScriptText = [System.IO.File]::ReadAllText($buildScript)
    Assert-True -Condition ($buildScriptText -match [regex]::Escape("\\ADMIN\Public\y.DocManager\App")) -Message 'build script defaults to the requested secondary UNC App destination'
    Assert-True -Condition ($buildScriptText -match 'Sync-AppLayout -AppRoot \$networkAppRoot') -Message 'network deployment reuses managed layout synchronization'
    Assert-True -Condition ($buildScriptText -match 'Assert-NetworkDeploymentDestination -AppRoot \$networkAppRoot') -Message 'network destination is checked before real and dry-run deployment'
    Assert-True -Condition ($buildScriptText -match 'docmanager-network-write-probe') -Message 'network preflight validates create write move and delete access with a unique probe'
    Assert-True -Condition ($buildScriptText -match 'Secondary network deployment was blocked before the local App deployment started') -Message 'network preflight prevents partial local deployment when unavailable'
    Assert-True -Condition ($buildScriptText -match 'New-DeploymentPlan -AppRoot \$networkAppRoot') -Message 'network collision and manifest preflight occurs before the local deployment'
    Assert-True -Condition ($buildScriptText -match 'Secondary network deployment failed after the local App deployment committed successfully') -Message 'post-local network failures explain committed local deployment'

    $productFamilySourceBefore = Get-RelativeFileMap -Root $productFamilySource
    $productExcelSourceBefore = Get-RelativeFileMap -Root $productExcelSource

    $staging = [System.IO.Path]::Combine($temporaryRoot, 'staging')
    New-FakePublishStaging -Path $staging

    $dryRunApp = [System.IO.Path]::Combine($temporaryRoot, 'dry-run-app')
    $dryRun = Invoke-BuildScript -Arguments (@(Get-DeployArguments -App $dryRunApp -Staging $staging) + '-DryRun') -ExpectSuccess $true
    Assert-True (-not (Test-Path -LiteralPath $dryRunApp)) 'dry run creates no App directory'
    Assert-True (($dryRun.Output -join "`n") -match 'copy \d+ missing files') 'dry run reports product data copy counts'
    Assert-True (($dryRun.Output -join "`n") -match 'no restore, publish, launcher execution, copy, delete') 'dry run reports prohibited work was skipped'

    $invalidLauncherStaging = [System.IO.Path]::Combine($temporaryRoot, 'invalid-launcher-staging')
    [System.IO.Directory]::CreateDirectory($invalidLauncherStaging) | Out-Null
    [System.IO.File]::Copy([System.IO.Path]::Combine($env:SystemRoot, 'System32', 'where.exe'), [System.IO.Path]::Combine($invalidLauncherStaging, 'DocManager.Desktop.exe'), $false)
    $invalidLauncherApp = [System.IO.Path]::Combine($temporaryRoot, 'invalid-launcher-app')
    $invalidLauncherFailure = Invoke-BuildScript -Arguments @(
        '-SkipPublish', '-SkipDataCopy', '-SkipNetworkDeployment',
        '-StagingDirectory', $staging,
        '-LauncherStagingDirectory', $invalidLauncherStaging,
        '-AppDirectory', $invalidLauncherApp,
        '-ProductFamilySourceDirectory', $productFamilySource,
        '-ProductExcelSourceDirectory', $productExcelSource
    ) -ExpectSuccess $false
    Assert-True -Condition (($invalidLauncherFailure.Output -join "`n") -match 'runtime probe failed') -Message 'arbitrary system executable fails the launcher runtime probe'
    Assert-True -Condition (-not (Test-Path -LiteralPath $invalidLauncherApp)) -Message 'invalid launcher staging rejection changes no App directory'

    $sidecarLauncherStaging = [System.IO.Path]::Combine($temporaryRoot, 'sidecar-launcher-staging')
    [System.IO.Directory]::CreateDirectory($sidecarLauncherStaging) | Out-Null
    [System.IO.File]::Copy($launcherPath, [System.IO.Path]::Combine($sidecarLauncherStaging, 'DocManager.Desktop.exe'), $false)
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($sidecarLauncherStaging, 'unexpected.json'), '{}')
    $sidecarLauncherApp = [System.IO.Path]::Combine($temporaryRoot, 'sidecar-launcher-app')
    $sidecarLauncherFailure = Invoke-BuildScript -Arguments @(
        '-SkipPublish', '-SkipDataCopy', '-SkipNetworkDeployment',
        '-StagingDirectory', $staging,
        '-LauncherStagingDirectory', $sidecarLauncherStaging,
        '-AppDirectory', $sidecarLauncherApp,
        '-ProductFamilySourceDirectory', $productFamilySource,
        '-ProductExcelSourceDirectory', $productExcelSource
    ) -ExpectSuccess $false
    Assert-True -Condition (($sidecarLauncherFailure.Output -join "`n") -match 'exactly one file') -Message 'launcher staging rejects sidecar files'
    Assert-True -Condition (-not (Test-Path -LiteralPath $sidecarLauncherApp)) -Message 'launcher sidecar rejection changes no App directory'

    $app = [System.IO.Path]::Combine($temporaryRoot, 'portable-app')
    $deployArguments = Get-DeployArguments -App $app -Staging $staging
    [void](Invoke-BuildScript -Arguments $deployArguments -ExpectSuccess $true)

    $rootExecutables = @(Get-ChildItem -LiteralPath $app -File -Filter '*.exe')
    Assert-Equal 1 $rootExecutables.Count 'App root executable count'
    Assert-Equal 'DocManager.Desktop.exe' $rootExecutables[0].Name 'App root launcher name'
    $allowedRootFiles = @('.docmanager-publish-manifest.json', 'DocManager.Desktop.exe')
    $unexpectedRootFiles = @(Get-ChildItem -LiteralPath $app -File -Force | Where-Object { $_.Name -notin $allowedRootFiles })
    Assert-Equal 0 $unexpectedRootFiles.Count 'App root contains only launcher and manifest files'
    Assert-True -Condition (Test-Path -LiteralPath ([System.IO.Path]::Combine($app, 'Released', 'DocManager.Desktop.exe')) -PathType Leaf) -Message 'real desktop executable is under Released'
    Assert-True -Condition (Test-Path -LiteralPath ([System.IO.Path]::Combine($app, 'Released', 'DocManager.Core.dll')) -PathType Leaf) -Message 'publish DLL is under Released'
    Assert-True -Condition (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($app, 'DocManager.Core.dll')))) -Message 'publish DLL is absent from App root'
    Assert-SeedCopyMatches -Source $productFamilySource -Destination ([System.IO.Path]::Combine($app, 'Product Family'))
    Assert-SeedCopyMatches -Source $productExcelSource -Destination ([System.IO.Path]::Combine($app, 'Product excel file'))

    $manifestPath = [System.IO.Path]::Combine($app, '.docmanager-publish-manifest.json')
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-Equal 2 ([int]$manifest.version) 'manifest version'
    Assert-True -Condition ($manifest.PSObject.Properties.Name -notcontains 'testLauncher') -Message 'manifest has unchanged v2 release semantics'
    Assert-True -Condition (@($manifest.files).Contains('DocManager.Desktop.exe')) -Message 'manifest owns root launcher'
    Assert-True -Condition (@($manifest.files | Where-Object { $_ -like 'Released\*' }).Count -gt 0) -Message 'manifest owns Released payload'
    Assert-Equal 0 @($manifest.files | Where-Object { $_ -like 'Product Family\*' -or $_ -like 'Product excel file\*' }).Count 'manifest excludes product data'

    $modifiedDataFile = Get-ChildItem -LiteralPath ([System.IO.Path]::Combine($app, 'Product Family')) -File -Recurse | Select-Object -First 1
    [System.IO.File]::AppendAllText($modifiedDataFile.FullName, 'user-preserved-change')
    $modifiedDataHash = (Get-FileHash -LiteralPath $modifiedDataFile.FullName -Algorithm SHA256).Hash
    $destinationOnlyData = [System.IO.Path]::Combine($app, 'Product Family', 'destination-only.txt')
    [System.IO.File]::WriteAllText($destinationOnlyData, 'keep me')
    $unknownReleased = [System.IO.Path]::Combine($app, 'Released', 'unknown-user.bin')
    [System.IO.File]::WriteAllText($unknownReleased, 'unknown')
    $staleOwned = [System.IO.Path]::Combine($app, 'Released', 'stale-owned.bin')
    [System.IO.File]::WriteAllText($staleOwned, 'stale')
    $manifest.files = @($manifest.files) + 'Released\stale-owned.bin'
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4))
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($staging, 'DocManager.Core.dll'), 'managed-second')

    [void](Invoke-BuildScript -Arguments $deployArguments -ExpectSuccess $true)
    Assert-Equal $modifiedDataHash (Get-FileHash -LiteralPath $modifiedDataFile.FullName -Algorithm SHA256).Hash 'second deploy preserves modified product data byte-for-byte'
    Assert-True -Condition (Test-Path -LiteralPath $destinationOnlyData -PathType Leaf) -Message 'second deploy preserves destination-only product data'
    Assert-True -Condition (Test-Path -LiteralPath $unknownReleased -PathType Leaf) -Message 'second deploy preserves unknown Released file'
    Assert-True -Condition (-not (Test-Path -LiteralPath $staleOwned)) -Message 'second deploy deletes stale manifest-owned payload'
    Assert-Equal 'managed-second' ([System.IO.File]::ReadAllText([System.IO.Path]::Combine($app, 'Released', 'DocManager.Core.dll'))) 'second deploy replaces managed payload'

    $networkPrimaryApp = [System.IO.Path]::Combine($temporaryRoot, 'network-primary-app')
    $networkSecondaryApp = [System.IO.Path]::Combine($temporaryRoot, 'network-secondary-app')
    $networkDeployArguments = Get-DeployArguments -App $networkPrimaryApp -Staging $staging -NetworkApp $networkSecondaryApp -DeployNetwork
    [void](Invoke-BuildScript -Arguments $networkDeployArguments -ExpectSuccess $true)
    Assert-True -Condition (Test-Path -LiteralPath ([System.IO.Path]::Combine($networkPrimaryApp, '.docmanager-publish-manifest.json')) -PathType Leaf) -Message 'primary deploy succeeds alongside isolated secondary deployment'
    Assert-True -Condition (Test-Path -LiteralPath ([System.IO.Path]::Combine($networkSecondaryApp, 'Released', 'DocManager.Desktop.exe')) -PathType Leaf) -Message 'secondary deploy installs the Released target before exposing the launcher layout'
    $networkRootExecutables = @(Get-ChildItem -LiteralPath $networkSecondaryApp -File -Filter '*.exe')
    Assert-Equal 1 $networkRootExecutables.Count 'secondary App root executable count'
    Assert-Equal 'DocManager.Desktop.exe' $networkRootExecutables[0].Name 'secondary App root launcher name'
    $networkManifestPath = [System.IO.Path]::Combine($networkSecondaryApp, '.docmanager-publish-manifest.json')
    $networkManifest = Get-Content -LiteralPath $networkManifestPath -Raw | ConvertFrom-Json
    Assert-Equal 2 ([int]$networkManifest.version) 'secondary manifest version'
    Assert-True -Condition (@($networkManifest.files).Contains('DocManager.Desktop.exe')) -Message 'secondary manifest owns launcher installed after Released payload'
    Assert-True -Condition (@($networkManifest.files | Where-Object { $_ -like 'Released\*' }).Count -gt 0) -Message 'secondary manifest owns Released payload'
    Assert-Equal 0 @($networkManifest.files | Where-Object { $_ -like 'Product Family\*' -or $_ -like 'Product excel file\*' }).Count 'secondary manifest excludes seed data'
    Assert-SeedCopyMatches -Source $productFamilySource -Destination ([System.IO.Path]::Combine($networkSecondaryApp, 'Product Family'))
    Assert-SeedCopyMatches -Source $productExcelSource -Destination ([System.IO.Path]::Combine($networkSecondaryApp, 'Product excel file'))

    $secondaryModifiedDataFile = Get-ChildItem -LiteralPath ([System.IO.Path]::Combine($networkSecondaryApp, 'Product Family')) -File -Recurse | Select-Object -First 1
    [System.IO.File]::AppendAllText($secondaryModifiedDataFile.FullName, 'secondary-user-preserved-change')
    $secondaryModifiedDataHash = (Get-FileHash -LiteralPath $secondaryModifiedDataFile.FullName -Algorithm SHA256).Hash
    $secondaryDestinationOnlyData = [System.IO.Path]::Combine($networkSecondaryApp, 'Product Family', 'secondary-destination-only.txt')
    [System.IO.File]::WriteAllText($secondaryDestinationOnlyData, 'keep secondary data')
    $secondaryUnknownReleased = [System.IO.Path]::Combine($networkSecondaryApp, 'Released', 'secondary-unknown-user.bin')
    [System.IO.File]::WriteAllText($secondaryUnknownReleased, 'keep secondary payload')
    $secondaryStaleOwned = [System.IO.Path]::Combine($networkSecondaryApp, 'Released', 'secondary-stale-owned.bin')
    [System.IO.File]::WriteAllText($secondaryStaleOwned, 'remove secondary stale payload')
    $networkManifest.files = @($networkManifest.files) + 'Released\secondary-stale-owned.bin'
    [System.IO.File]::WriteAllText($networkManifestPath, ($networkManifest | ConvertTo-Json -Depth 4))
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($staging, 'DocManager.Core.dll'), 'managed-network-third')

    [void](Invoke-BuildScript -Arguments $networkDeployArguments -ExpectSuccess $true)
    Assert-Equal 'managed-network-third' ([System.IO.File]::ReadAllText([System.IO.Path]::Combine($networkSecondaryApp, 'Released', 'DocManager.Core.dll'))) 'secondary deployment replaces managed payload'
    Assert-True -Condition (-not (Test-Path -LiteralPath $secondaryStaleOwned)) -Message 'secondary deployment deletes stale manifest-owned payload'
    Assert-True -Condition (Test-Path -LiteralPath $secondaryUnknownReleased -PathType Leaf) -Message 'secondary deployment preserves unknown Released file'
    Assert-Equal $secondaryModifiedDataHash (Get-FileHash -LiteralPath $secondaryModifiedDataFile.FullName -Algorithm SHA256).Hash 'secondary deployment preserves modified product data byte-for-byte'
    Assert-True -Condition (Test-Path -LiteralPath $secondaryDestinationOnlyData -PathType Leaf) -Message 'secondary deployment preserves destination-only product data'
    Assert-True -Condition (@(Get-ChildItem -LiteralPath $networkSecondaryApp -Directory -Filter '.docmanager-deploy-*').Count -eq 0) -Message 'secondary deployment cleans its transaction directory'
    Assert-Equal 0 @((Get-ChildItem -LiteralPath $networkSecondaryApp -File -Force | Where-Object { $_.Name -like '.docmanager-network-write-probe-*' })).Count 'secondary deployment cleans its App-root write probe'
    Assert-Equal 0 @((Get-ChildItem -LiteralPath $temporaryRoot -File -Force | Where-Object { $_.Name -like '.docmanager-network-write-probe-*' })).Count 'secondary deployment cleans its parent write probe'

    $v1App = [System.IO.Path]::Combine($temporaryRoot, 'v1-app')
    [System.IO.Directory]::CreateDirectory($v1App) | Out-Null
    foreach ($name in @('DocManager.Desktop.exe', 'DocManager.Desktop.deps.json', 'DocManager.Desktop.runtimeconfig.json', 'DocManager.Core.dll')) {
        [System.IO.File]::Copy([System.IO.Path]::Combine($staging, $name), [System.IO.Path]::Combine($v1App, $name), $false)
    }
    [System.IO.File]::WriteAllText(
        [System.IO.Path]::Combine($v1App, '.docmanager-publish-manifest.json'),
        (@{ version = 1; files = @('DocManager.Desktop.exe', 'DocManager.Desktop.deps.json', 'DocManager.Desktop.runtimeconfig.json', 'DocManager.Core.dll') } | ConvertTo-Json))
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::Combine($v1App, 'Released')) | Out-Null
    $v1Unknown = [System.IO.Path]::Combine($v1App, 'Released', 'unknown.bin')
    [System.IO.File]::WriteAllText($v1Unknown, 'keep')
    [void](Invoke-BuildScript -Arguments (Get-DeployArguments -App $v1App -Staging $staging -SkipDataCopy) -ExpectSuccess $true)
    Assert-True -Condition (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($v1App, 'DocManager.Core.dll')))) -Message 'v1 migration removes only old manifest-owned root DLL'
    Assert-True -Condition (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($v1App, 'DocManager.Desktop.deps.json')))) -Message 'v1 migration removes old manifest-owned root deps'
    Assert-True -Condition (Test-Path -LiteralPath $v1Unknown -PathType Leaf) -Message 'v1 migration preserves unknown Released file'
    Assert-Equal 2 ([int]((Get-Content -LiteralPath ([System.IO.Path]::Combine($v1App, '.docmanager-publish-manifest.json')) -Raw | ConvertFrom-Json).version)) 'v1 migrates to manifest v2'

    $unsafeV1App = [System.IO.Path]::Combine($temporaryRoot, 'unsafe-v1-app')
    [System.IO.Directory]::CreateDirectory($unsafeV1App) | Out-Null
    $unsafeV1UserFile = [System.IO.Path]::Combine($unsafeV1App, 'notes.txt')
    [System.IO.File]::WriteAllText($unsafeV1UserFile, 'do not delete')
    [System.IO.File]::WriteAllText(
        [System.IO.Path]::Combine($unsafeV1App, '.docmanager-publish-manifest.json'),
        (@{ version = 1; files = @('notes.txt') } | ConvertTo-Json))
    [void](Invoke-BuildScript -Arguments (Get-DeployArguments -App $unsafeV1App -Staging $staging -SkipDataCopy) -ExpectSuccess $false)
    Assert-Equal 'do not delete' ([System.IO.File]::ReadAllText($unsafeV1UserFile)) 'v1 manifest cannot claim an arbitrary user file'

    $legacyReleasedApp = [System.IO.Path]::Combine($temporaryRoot, 'legacy-released-app')
    $legacyReleased = [System.IO.Path]::Combine($legacyReleasedApp, 'Released')
    [System.IO.Directory]::CreateDirectory($legacyReleased) | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $staging -Force) {
        Copy-Item -LiteralPath $entry.FullName -Destination $legacyReleased -Recurse -Force
    }
    $legacyUnknown = [System.IO.Path]::Combine($legacyReleased, 'legacy-unknown.txt')
    [System.IO.File]::WriteAllText($legacyUnknown, 'keep')
    [void](Invoke-BuildScript -Arguments (Get-DeployArguments -App $legacyReleasedApp -Staging $staging -SkipDataCopy) -ExpectSuccess $true)
    Assert-True -Condition (Test-Path -LiteralPath $legacyUnknown -PathType Leaf) -Message 'legacy Released adoption preserves unknown files'

    $identicalReleasedApp = [System.IO.Path]::Combine($temporaryRoot, 'identical-released-app')
    $identicalReleased = [System.IO.Path]::Combine($identicalReleasedApp, 'Released')
    [System.IO.Directory]::CreateDirectory($identicalReleased) | Out-Null
    [System.IO.File]::Copy([System.IO.Path]::Combine($staging, 'DocManager.Core.dll'), [System.IO.Path]::Combine($identicalReleased, 'DocManager.Core.dll'), $false)
    [void](Invoke-BuildScript -Arguments (Get-DeployArguments -App $identicalReleasedApp -Staging $staging -SkipDataCopy) -ExpectSuccess $true)
    Assert-True -Condition (Test-Path -LiteralPath ([System.IO.Path]::Combine($identicalReleasedApp, '.docmanager-publish-manifest.json')) -PathType Leaf) -Message 'hash-identical Released file is adopted without explicit switch'

    $collisionApp = [System.IO.Path]::Combine($temporaryRoot, 'collision-app')
    [System.IO.Directory]::CreateDirectory($collisionApp) | Out-Null
    $collisionFile = [System.IO.Path]::Combine($collisionApp, 'DocManager.Core.dll')
    [System.IO.File]::WriteAllText($collisionFile, 'unowned-root-binary')
    $collision = Invoke-BuildScript -Arguments (Get-DeployArguments -App $collisionApp -Staging $staging -SkipDataCopy) -ExpectSuccess $false
    Assert-Equal 'unowned-root-binary' ([System.IO.File]::ReadAllText($collisionFile)) 'root collision remains unchanged'
    Assert-True -Condition (($collision.Output -join "`n") -match 'unowned legacy root publish file') -Message 'root collision fails with actionable message'
    Assert-True -Condition (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($collisionApp, '.docmanager-publish-manifest.json')))) -Message 'root collision writes no manifest'

    $unmarkedReleasedApp = [System.IO.Path]::Combine($temporaryRoot, 'unmarked-released-app')
    $unmarkedReleased = [System.IO.Path]::Combine($unmarkedReleasedApp, 'Released')
    [System.IO.Directory]::CreateDirectory($unmarkedReleased) | Out-Null
    foreach ($requiredName in @('DocManager.Desktop.exe', 'DocManager.Desktop.deps.json', 'DocManager.Desktop.runtimeconfig.json')) {
        [System.IO.File]::Copy([System.IO.Path]::Combine($staging, $requiredName), [System.IO.Path]::Combine($unmarkedReleased, $requiredName), $false)
    }
    $unmarkedCollision = [System.IO.Path]::Combine($unmarkedReleased, 'DocManager.Core.dll')
    [System.IO.File]::WriteAllText($unmarkedCollision, 'unknown-released-binary')
    [void](Invoke-BuildScript -Arguments (Get-DeployArguments -App $unmarkedReleasedApp -Staging $staging -SkipDataCopy) -ExpectSuccess $false)
    Assert-Equal 'unknown-released-binary' ([System.IO.File]::ReadAllText($unmarkedCollision)) 'differing legacy Released collision remains unchanged despite standard markers'
    [void](Invoke-BuildScript -Arguments (@(Get-DeployArguments -App $unmarkedReleasedApp -Staging $staging -SkipDataCopy) + '-AdoptLegacyReleased') -ExpectSuccess $true)
    Assert-Equal 'managed-second' ([System.IO.File]::ReadAllText($unmarkedCollision)) 'explicit legacy Released adoption replaces verified collision'

    $typeCollisionApp = [System.IO.Path]::Combine($temporaryRoot, 'type-collision-app')
    [System.IO.Directory]::CreateDirectory($typeCollisionApp) | Out-Null
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($typeCollisionApp, 'Released'), 'blocking-file')
    [void](Invoke-BuildScript -Arguments (Get-DeployArguments -App $typeCollisionApp -Staging $staging -SkipDataCopy) -ExpectSuccess $false)
    Assert-Equal 'blocking-file' ([System.IO.File]::ReadAllText([System.IO.Path]::Combine($typeCollisionApp, 'Released'))) 'file-directory collision remains unchanged'

    $dataCollisionApp = [System.IO.Path]::Combine($temporaryRoot, 'data-collision-app')
    [System.IO.Directory]::CreateDirectory($dataCollisionApp) | Out-Null
    [System.IO.File]::WriteAllText([System.IO.Path]::Combine($dataCollisionApp, 'Product Family'), 'blocking-product-data-file')
    [void](Invoke-BuildScript -Arguments (Get-DeployArguments -App $dataCollisionApp -Staging $staging) -ExpectSuccess $false)
    Assert-Equal 'blocking-product-data-file' ([System.IO.File]::ReadAllText([System.IO.Path]::Combine($dataCollisionApp, 'Product Family'))) 'product data type collision remains unchanged'
    Assert-True -Condition (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($dataCollisionApp, 'Released')))) -Message 'product data collision changes no payload'

    $dataFileCollisionApp = [System.IO.Path]::Combine($temporaryRoot, 'data-file-collision-app')
    $dataFileDestinationRoot = [System.IO.Path]::Combine($dataFileCollisionApp, 'Product Family')
    [System.IO.Directory]::CreateDirectory($dataFileDestinationRoot) | Out-Null
    $seedRelativeFile = [System.IO.Path]::GetRelativePath($productFamilySource, (Get-ChildItem -LiteralPath $productFamilySource -File -Recurse | Select-Object -First 1).FullName)
    $seedFileBlocker = [System.IO.Path]::Combine($dataFileDestinationRoot, $seedRelativeFile)
    [System.IO.Directory]::CreateDirectory($seedFileBlocker) | Out-Null
    [void](Invoke-BuildScript -Arguments (Get-DeployArguments -App $dataFileCollisionApp -Staging $staging) -ExpectSuccess $false)
    Assert-True -Condition (Test-Path -LiteralPath $seedFileBlocker -PathType Container) -Message 'directory blocking a seed file remains unchanged'
    Assert-True -Condition (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($dataFileCollisionApp, 'Released')))) -Message 'seed file collision changes no payload'

    $traversalApp = [System.IO.Path]::Combine($temporaryRoot, 'traversal-app')
    [System.IO.Directory]::CreateDirectory($traversalApp) | Out-Null
    [System.IO.File]::WriteAllText(
        [System.IO.Path]::Combine($traversalApp, '.docmanager-publish-manifest.json'),
        (@{ version = 2; files = @('DocManager.Desktop.exe', '..\outside.bin') } | ConvertTo-Json))
    $traversalManifestBefore = (Get-FileHash -LiteralPath ([System.IO.Path]::Combine($traversalApp, '.docmanager-publish-manifest.json')) -Algorithm SHA256).Hash
    [void](Invoke-BuildScript -Arguments (Get-DeployArguments -App $traversalApp -Staging $staging -SkipDataCopy) -ExpectSuccess $false)
    Assert-Equal $traversalManifestBefore (Get-FileHash -LiteralPath ([System.IO.Path]::Combine($traversalApp, '.docmanager-publish-manifest.json')) -Algorithm SHA256).Hash 'traversal manifest remains unchanged'
    Assert-True -Condition (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($temporaryRoot, 'outside.bin')))) -Message 'traversal creates no outside file'

    $rollbackAppParent = [System.IO.Path]::Combine($temporaryRoot, 'rollback-parent')
    $rollbackApp = [System.IO.Path]::Combine($rollbackAppParent, 'new-app')
    $rollbackFailure = Invoke-BuildScript -Arguments (@(Get-DeployArguments -App $rollbackApp -Staging $staging -SkipDataCopy) + '-TestFailAfterAppCreation') -ExpectSuccess $false
    Assert-True -Condition (($rollbackFailure.Output -join "`n") -match 'Injected test failure') -Message 'rollback test reaches controlled post-App-creation failure'
    Assert-True -Condition (-not (Test-Path -LiteralPath $rollbackApp)) -Message 'failed first deployment removes newly created App directory'
    Assert-True -Condition (-not (Test-Path -LiteralPath $rollbackAppParent)) -Message 'failed first deployment removes newly created App parent directories'

    $lockApp = [System.IO.Path]::Combine($temporaryRoot, 'lock-app')
    $lockArguments = Get-DeployArguments -App $lockApp -Staging $staging -SkipDataCopy
    [void](Invoke-BuildScript -Arguments $lockArguments -ExpectSuccess $true)
    $lockedPath = [System.IO.Path]::Combine($lockApp, 'Released', 'DocManager.Core.dll')
    $lockedHash = (Get-FileHash -LiteralPath $lockedPath -Algorithm SHA256).Hash
    $lockStream = [System.IO.File]::Open($lockedPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $lockFailure = Invoke-BuildScript -Arguments $lockArguments -ExpectSuccess $false
        Assert-True -Condition (($lockFailure.Output -join "`n") -match 'locked or cannot be replaced') -Message 'locked payload fails preflight with actionable message'
    } finally {
        $lockStream.Dispose()
    }
    Assert-Equal $lockedHash (Get-FileHash -LiteralPath $lockedPath -Algorithm SHA256).Hash 'locked payload remains unchanged'
    Assert-True -Condition (@(Get-ChildItem -LiteralPath $lockApp -Directory -Filter '.docmanager-deploy-*').Count -eq 0) -Message 'lock preflight creates no transaction directory'

    $productFamilySourceAfter = Get-RelativeFileMap -Root $productFamilySource
    $productExcelSourceAfter = Get-RelativeFileMap -Root $productExcelSource
    Assert-Equal $productFamilySourceBefore.Count $productFamilySourceAfter.Count 'Product Family source file count is unchanged'
    Assert-Equal $productExcelSourceBefore.Count $productExcelSourceAfter.Count 'Product excel source file count is unchanged'
    foreach ($relative in $productFamilySourceBefore.Keys) {
        Assert-Equal $productFamilySourceBefore[$relative] $productFamilySourceAfter[$relative] "Product Family source bytes remain unchanged: $relative"
    }
    foreach ($relative in $productExcelSourceBefore.Keys) {
        Assert-Equal $productExcelSourceBefore[$relative] $productExcelSourceAfter[$relative] "Product excel source bytes remain unchanged: $relative"
    }

    Write-Host 'PASS: managed launcher probe/missing-target/argument forwarding, parser, dry-run, v2 layout, primary and isolated-secondary data preservation, stale cleanup, manifest/launcher layout, v1 migration, legacy Released adoption, unknown preservation, traversal, collision, and lock preflight.'
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -Confirm:$false
    }
}
