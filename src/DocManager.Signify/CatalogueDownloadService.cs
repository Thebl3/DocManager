using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocManager.Core;
using DocManager.Photometry;

namespace DocManager.Signify;

public sealed class CatalogueDownloadService
{
    private static readonly string[] CsvHeaders = ["sheet", "excel_row", "product_family", "material", "material_description", "query_used", "matched_sku", "matched_name", "product_url", "leaflet_url", "output_file", "ies_url", "ies_file", "ldt_file", "status", "error"];
    private readonly SignifyClient _client;
    private readonly LdtConverter _converter;

    public CatalogueDownloadService(SignifyClient client, LdtConverter? converter = null) { _client = client; _converter = converter ?? new LdtConverter(); }

    public async Task<IReadOnlyList<DownloadLogEntry>> DownloadAsync(IEnumerable<ProductRecord> records, DownloadOptions options, IProgress<DocManager.Core.OperationProgress>? progress = null, CancellationToken cancellationToken = default, Action<DownloadLogEntry>? logSink = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(options);
        var items = records.ToArray();
        var outputRoot = FileSystemSafety.NormalizeDirectoryPath(options.OutputRoot);
        if (!FileSystemSafety.HasNoReparsePointsInExistingPath(outputRoot))
            throw new InvalidDataException("Product Family root chứa symbolic link, junction hoặc reparse point.");
        var manifest = new CanonicalAssetManifest(outputRoot);
        var preflightCollisions = items
            .GroupBy(record => $"{DocManager.Core.WindowsFileName.Sanitize(record.ProductFamily)}{DocManager.Core.ProductAssetName.CanonicalBaseName(record.MaterialDescription)}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(record => ManifestIdentity(record.Material, record.MaterialDescription)).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var logs = new DownloadLogEntry[items.Length];
        var completedCount = 0;
        var progressLock = new object();
        var csvLock = new object();
        Exception? sinkFailure = null;

        void AddLog(int index, DownloadLogEntry entry)
        {
            logs[index] = entry;
            lock (progressLock)
            {
                completedCount++;
                progress?.Report(new(completedCount, items.Length, entry.MaterialDescription));
            }
            lock (csvLock)
            {
                if (logSink is null) return;
                try { logSink(entry); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    sinkFailure ??= exception;
                }
            }
        }

        var maxConcurrency = Math.Max(1, options.MaxConcurrency);
        using var semaphore = new System.Threading.SemaphoreSlim(maxConcurrency);
        var tasks = new Task[items.Length];

        for (var index = 0; index < items.Length; index++)
        {
            var localIndex = index;
            var record = items[index];
            tasks[index] = ProcessProductAsync(localIndex, record);
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        progress?.Report(new(items.Length, items.Length, sinkFailure is null ? "Hoàn tất" : $"Hoàn tất; cảnh báo không ghi được CSV: {sinkFailure.Message}"));
        return logs;

        async Task ProcessProductAsync(int index, ProductRecord record)
        {
            if (options.PerItemDelay > TimeSpan.Zero)
            {
                await Task.Delay(options.PerItemDelay, cancellationToken);
            }
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProductMatch? match = null;
                try
                {
                    var preflightKey = $"{DocManager.Core.WindowsFileName.Sanitize(record.ProductFamily)}{DocManager.Core.ProductAssetName.CanonicalBaseName(record.MaterialDescription)}";
                    if (preflightCollisions.Contains(preflightKey))
                    {
                        AddLog(index, Base(record, null) with { Status = "name_collision", Error = "Nhiều SKU trong cùng family có cùng canonical basename." });
                        return;
                    }

                    match = await _client.FindProductAsync(record, options, cancellationToken);
                    if (match is null)
                    {
                        AddLog(index, Base(record, null) with
                        {
                            Status = "no_leaflet",
                            Error = "Không tìm thấy sản phẩm.",
                            AssetResults =
                            [
                                new(DownloadAssetKind.Pdf, DownloadAssetStatus.Unavailable, Detail: "Không tìm thấy sản phẩm."),
                                new(DownloadAssetKind.Ies, options.DownloadIes ? DownloadAssetStatus.Unavailable : DownloadAssetStatus.Skipped, Detail: options.DownloadIes ? "Không tìm thấy sản phẩm." : "Không yêu cầu IES."),
                                new(DownloadAssetKind.Ldt, DownloadAssetStatus.Skipped, Detail: "Không có IES hợp lệ để chuyển đổi.")
                            ]
                        });
                        return;
                    }

                    var canonicalName = DocManager.Core.ProductAssetName.FromVerifiedMatch(match.MatchedName, record.MaterialDescription);
                    var canonicalPdfPath = _client.PdfTarget(record, match, outputRoot);
                    var canonicalIesPath = _client.IesTarget(record, match, outputRoot);
                    var reservation = manifest.Reserve(record, match, canonicalName, canonicalPdfPath, canonicalIesPath);
                    if (reservation.HasCollision || reservation.Entry is null)
                    {
                        AddLog(index, Base(record, match) with
                        {
                            OutputFile = canonicalPdfPath,
                            IesFile = canonicalIesPath,
                            Status = "name_collision",
                            Error = reservation.Error
                        });
                        return;
                    }

                    var assets = new List<DownloadAssetResult>(3);
                    var pdfPath = canonicalPdfPath;
                    var iesPath = canonicalIesPath;
                    var validIesPath = string.Empty;
                    var validPdfPath = string.Empty;

                    if (LeafletCandidates(match).Count == 0)
                    {
                        var existingPdf = manifest.ExistingPath(reservation.Entry, ".pdf");
                        if (existingPdf is not null && DownloadValidation.IsValidFile(existingPdf, null, DownloadKind.Pdf))
                        {
                            pdfPath = existingPdf;
                            validPdfPath = existingPdf;
                            assets.Add(new(DownloadAssetKind.Pdf, DownloadAssetStatus.Reused, existingPdf, "PDF manifest hiện có hợp lệ."));
                        }
                        else
                        {
                            assets.Add(new(DownloadAssetKind.Pdf, DownloadAssetStatus.Unavailable, canonicalPdfPath, "Không có URL PDF hợp lệ."));
                        }
                    }
                    else
                    {
                        Exception? lastPdfFailure = null;
                        var lastPdfUrl = string.Empty;
                        foreach (var leafletUrl in LeafletCandidates(match))
                        {
                            try
                            {
                                EnsureSafeDownloadDestination(pdfPath, outputRoot);
                                var downloadStatus = await _client.DownloadAsync(leafletUrl, pdfPath, DownloadKind.Pdf, options, cancellationToken);
                                cancellationToken.ThrowIfCancellationRequested();
                                RequireSafeExistingAsset(pdfPath, outputRoot, "PDF");
                                manifest.UpdateDownloadedPaths(reservation.Entry, pdfPath: pdfPath);
                                validPdfPath = pdfPath;
                                assets.Add(new(DownloadAssetKind.Pdf, downloadStatus == "exists" ? DownloadAssetStatus.Reused : DownloadAssetStatus.Downloaded, pdfPath));
                                lastPdfFailure = null;
                                break;
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exception) when (IsPerItemFailure(exception))
                            {
                                lastPdfFailure = exception;
                                lastPdfUrl = leafletUrl;
                            }
                        }
                        if (lastPdfFailure is not null)
                            assets.Add(new(DownloadAssetKind.Pdf, DownloadAssetStatus.Failed, pdfPath, ExceptionLogFormatter.Format(
                                $"PDF download failed for '{record.MaterialDescription}'; URL '{lastPdfUrl}'; destination '{pdfPath}'", lastPdfFailure)));
                    }

                    if (!options.DownloadIes)
                    {
                        assets.Add(new(DownloadAssetKind.Ies, DownloadAssetStatus.Skipped, Detail: "Không yêu cầu IES."));
                    }
                    else if (string.IsNullOrWhiteSpace(match.IesUrl))
                    {
                        var existingIes = manifest.ExistingPath(reservation.Entry, ".ies");
                        if (existingIes is not null && DownloadValidation.IsValidFile(existingIes, null, DownloadKind.Ies))
                        {
                            iesPath = existingIes;
                            validIesPath = existingIes;
                            assets.Add(new(DownloadAssetKind.Ies, DownloadAssetStatus.Reused, existingIes, "IES manifest hiện có hợp lệ."));
                        }
                        else
                        {
                            assets.Add(new(DownloadAssetKind.Ies, DownloadAssetStatus.Unavailable, canonicalIesPath, "Không có URL IES hợp lệ."));
                        }
                    }
                    else
                    {
                        try
                        {
                            EnsureSafeDownloadDestination(iesPath, outputRoot);
                            var downloadStatus = await _client.DownloadAsync(match.IesUrl, iesPath, DownloadKind.Ies, options, cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            RequireSafeExistingAsset(iesPath, outputRoot, "IES");
                            manifest.UpdateDownloadedPaths(reservation.Entry, iesPath: iesPath);
                            validIesPath = iesPath;
                            assets.Add(new(DownloadAssetKind.Ies, downloadStatus == "exists" ? DownloadAssetStatus.Reused : DownloadAssetStatus.Downloaded, iesPath));
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception exception) when (IsPerItemFailure(exception))
                        {
                            assets.Add(new(DownloadAssetKind.Ies, DownloadAssetStatus.Failed, iesPath, ExceptionLogFormatter.Format(
                                $"IES download failed for '{record.MaterialDescription}'; URL '{match.IesUrl}'; destination '{iesPath}'", exception)));
                        }
                    }

                    var ldtPath = string.Empty;
                    if (validIesPath.Length == 0)
                    {
                        assets.Add(new(DownloadAssetKind.Ldt, DownloadAssetStatus.Skipped, Detail: "Không có IES hợp lệ để chuyển đổi."));
                    }
                    else
                    {
                        try
                        {
                            var conversion = ConvertSingleProduct(
                                manifest,
                                reservation.Entry,
                                new ProductConversionContext(record.ProductFamily, record.Material, record.MaterialDescription, canonicalName, validIesPath, validPdfPath),
                                cancellationToken);
                            ldtPath = conversion.LdtPath;
                            assets.Add(new(DownloadAssetKind.Ldt, conversion.Exists ? DownloadAssetStatus.Reused : DownloadAssetStatus.Downloaded, ldtPath,
                                conversion.Result?.Warnings is { Count: > 0 } warnings ? string.Join("; ", warnings) : string.Empty));
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception exception) when (IsPerItemFailure(exception))
                        {
                            assets.Add(new(DownloadAssetKind.Ldt, DownloadAssetStatus.Failed, Detail: ExceptionLogFormatter.Format(
                                $"LDT conversion failed for '{record.MaterialDescription}'; IES '{validIesPath}'; PDF '{validPdfPath}'; destination '{Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(validIesPath)!)!, "LDT")}'", exception)));
                        }
                    }

                    var status = AggregateStatus(assets);
                    AddLog(index, Base(record, match) with
                    {
                        OutputFile = validPdfPath.Length > 0 ? validPdfPath : pdfPath,
                        IesFile = validIesPath.Length > 0 ? validIesPath : (assets.Any(asset => asset.Kind == DownloadAssetKind.Ies && asset.Status == DownloadAssetStatus.Failed) ? iesPath : string.Empty),
                        LdtFile = ldtPath,
                        Status = status,
                        Error = AssetDetails(assets),
                        AssetResults = assets
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (IsPerItemFailure(exception))
                {
                    AddLog(index, Base(record, match) with { Status = "download_failed", Error = ExceptionLogFormatter.Format(
                        $"Product download failed; family '{record.ProductFamily}'; material '{record.Material}'; description '{record.MaterialDescription}'", exception) });
                }
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    public IReadOnlyList<ProductConversionLog> ConvertProducts(IEnumerable<ProductRecord> records, string outputRoot, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (!Directory.Exists(outputRoot)) return [];
        var root = FileSystemSafety.NormalizeDirectoryPath(outputRoot);
        if (!FileSystemSafety.HasNoReparsePointsInExistingPath(root))
            throw new InvalidDataException("Product Family root chứa symbolic link, junction hoặc reparse point.");
        var manifest = new CanonicalAssetManifest(root);
        var requested = records.Select(record => new
            {
                Record = record,
                Family = Path.Combine(root, DocManager.Core.WindowsFileName.Sanitize(record.ProductFamily)),
                Manifest = manifest.Resolve(record)
            })
            .ToArray();
        var preflightCollisions = requested
            .GroupBy(item => $"{item.Family}{DocManager.Core.ProductAssetName.CanonicalBaseName(item.Record.MaterialDescription)}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => ManifestIdentity(item.Record.Material, item.Record.MaterialDescription)).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<ProductConversionLog>();
        foreach (var item in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preflightKey = $"{item.Family}{DocManager.Core.ProductAssetName.CanonicalBaseName(item.Record.MaterialDescription)}";
            if (preflightCollisions.Contains(preflightKey)) throw new InvalidDataException($"name_collision: nhiều product trong cùng family có basename '{DocManager.Core.ProductAssetName.CanonicalBaseName(item.Record.MaterialDescription)}'.");
            if (item.Manifest.HasCollision) throw new InvalidDataException($"name_collision: manifest có nhiều identity cho material '{item.Record.Material}'.");
            if (!Directory.Exists(item.Family)) continue;
            if (!FileSystemSafety.IsSafePathWithinDirectory(item.Family, root))
                throw new InvalidDataException("Product Family chứa symbolic link, junction hoặc reparse point.");
            var iesDirectory = Path.Combine(item.Family, "IES");
            var ldtDirectory = Path.Combine(item.Family, "LDT");
            var canonicalName = item.Manifest.Entry?.CanonicalBaseName ?? DocManager.Core.ProductAssetName.CanonicalBaseName(item.Record.MaterialDescription);
            try
            {
                var resolved = item.Manifest.Entry is null
                    ? ResolveProductAssets(item.Record, canonicalName, item.Family, iesDirectory)
                    : ResolveManifestProductAssets(manifest, item.Manifest.Entry, item.Record, canonicalName, item.Family, iesDirectory);
                if (resolved is null) continue;
                var (ies, pdf) = resolved.Value;
                var context = new ProductConversionContext(
                    item.Record.ProductFamily,
                    item.Record.Material,
                    item.Record.MaterialDescription,
                    canonicalName,
                    ies,
                    pdf);
                var conversion = ConvertSingleProduct(manifest, item.Manifest.Entry, context, cancellationToken);
                if (conversion.Exists || conversion.Result is null) continue;
                results.Add(new ProductConversionLog(context, conversion.Result));
                progress?.Report(Path.GetFileName(conversion.Result.OutputPath));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (IsPerItemFailure(exception))
            {
                progress?.Report(ExceptionLogFormatter.Format(
                    $"Manual LDT conversion failed; output root '{root}'; family '{item.Record.ProductFamily}'; material '{item.Record.Material}'; description '{item.Record.MaterialDescription}'", exception));
            }
        }
        return results;
    }


    private SingleProductConversion ConvertSingleProduct(
        CanonicalAssetManifest manifest,
        CanonicalAssetManifestEntry? entry,
        ProductConversionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var iesPath = Path.GetFullPath(context.IesPath);
        var family = Path.GetDirectoryName(Path.GetDirectoryName(iesPath)!)!;
        if (!FileSystemSafety.IsSafePathWithinDirectory(iesPath, family))
            throw new InvalidDataException("IES canonical chứa symbolic link, junction hoặc reparse point.");
        if (!File.Exists(iesPath)) throw new FileNotFoundException("IES canonical không tồn tại.", iesPath);
        var ldtDirectory = Path.Combine(family, "LDT");
        if (Directory.Exists(ldtDirectory) && !FileSystemSafety.IsSafePathWithinDirectory(ldtDirectory, family))
            throw new InvalidDataException("Thư mục LDT chứa symbolic link, junction hoặc reparse point.");
        var existing = LdtIdentity.FindExisting(context.CanonicalProductName, iesPath, ldtDirectory, ReadLegacyIdentity, allowGeneratedLdtSuffix: false);
        if (existing is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonicalLdtPath = Path.Combine(ldtDirectory, $"{DocManager.Core.ProductAssetName.CanonicalBaseName(context.CanonicalProductName)}.ldt");
            if (!FileSystemSafety.IsSafePathWithinDirectory(existing, ldtDirectory))
                throw new InvalidDataException("LDT canonical chứa symbolic link, junction hoặc reparse point.");
            if (entry is not null && Path.GetFullPath(existing).Equals(Path.GetFullPath(canonicalLdtPath), StringComparison.OrdinalIgnoreCase))
                manifest.UpdateDownloadedPaths(entry, ldtPath: existing);
            return new SingleProductConversion(existing, true, null);
        }

        var pdfPath = string.IsNullOrWhiteSpace(context.PdfPath) ? string.Empty : Path.GetFullPath(context.PdfPath);
        if (pdfPath.Length > 0 && !FileSystemSafety.IsSafePathWithinDirectory(pdfPath, family))
            throw new InvalidDataException("PDF catalogue chứa symbolic link, junction hoặc reparse point.");
        var result = File.Exists(pdfPath)
            ? _converter.ConvertFile(iesPath, pdfPath, ldtDirectory, context.CanonicalProductName, overwrite: false, cancellationToken)
            : _converter.ConvertFileWithoutCatalogue(iesPath, ldtDirectory, context.CanonicalProductName, overwrite: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!FileSystemSafety.IsSafePathWithinDirectory(result.OutputPath, ldtDirectory) || !File.Exists(result.OutputPath))
            throw new IOException("Conversion không tạo LDT canonical an toàn.");
        if (entry is not null) manifest.UpdateDownloadedPaths(entry, ldtPath: result.OutputPath);
        return new SingleProductConversion(result.OutputPath, false, result);
    }

    public static string AggregateStatus(IReadOnlyList<DownloadAssetResult> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (assets.Any(asset => asset.Status == DownloadAssetStatus.Failed))
        {
            var failedKinds = assets.Where(asset => asset.Status == DownloadAssetStatus.Failed).Select(asset => asset.Kind).ToHashSet();
            if (failedKinds.SetEquals([DownloadAssetKind.Ldt])) return "conversion_failed";
            if (failedKinds.SetEquals([DownloadAssetKind.Pdf]) && HasUsableAsset(assets, DownloadAssetKind.Ldt)) return "pdf_failed_ies_converted";
            if (failedKinds.SetEquals([DownloadAssetKind.Ies]) && HasUsableAsset(assets, DownloadAssetKind.Pdf)) return "ies_failed_pdf_saved";
            return "download_failed";
        }

        var pdf = assets.Single(asset => asset.Kind == DownloadAssetKind.Pdf);
        var ies = assets.Single(asset => asset.Kind == DownloadAssetKind.Ies);
        var ldt = assets.Single(asset => asset.Kind == DownloadAssetKind.Ldt);
        if (ies.Status == DownloadAssetStatus.Unavailable && HasUsableAsset(assets, DownloadAssetKind.Pdf)) return "pdf_only";
        if (pdf.Status == DownloadAssetStatus.Unavailable && HasUsableAsset(assets, DownloadAssetKind.Ies) && HasUsableAsset(assets, DownloadAssetKind.Ldt)) return "ies_converted_no_pdf";
        if (pdf.Status == DownloadAssetStatus.Unavailable && ies.Status == DownloadAssetStatus.Unavailable) return "assets_unavailable";
        if (ies.Status == DownloadAssetStatus.Skipped && HasUsableAsset(assets, DownloadAssetKind.Pdf)) return "pdf_only";
        return ldt.Status == DownloadAssetStatus.Reused ? "conversion_exists" : ldt.Status == DownloadAssetStatus.Downloaded ? "converted" : "assets_unavailable";
    }

    private static bool HasUsableAsset(IEnumerable<DownloadAssetResult> assets, DownloadAssetKind kind) =>
        assets.Any(asset => asset.Kind == kind && asset.Status is DownloadAssetStatus.Downloaded or DownloadAssetStatus.Reused);

    private static IReadOnlyList<string> LeafletCandidates(ProductMatch match) =>
        new[] { match.LeafletUrl }
            .Concat(match.LeafletUrlCandidates)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string AssetDetails(IEnumerable<DownloadAssetResult> assets) => string.Join("; ", assets
        .Where(asset => !string.IsNullOrWhiteSpace(asset.Detail) || asset.Status is DownloadAssetStatus.Unavailable or DownloadAssetStatus.Failed or DownloadAssetStatus.Skipped)
        .Select(asset => $"{asset.Kind.ToString().ToLowerInvariant()}: {asset.Status.ToString().ToLowerInvariant()}{(string.IsNullOrWhiteSpace(asset.Detail) ? string.Empty : $" ({asset.Detail})")}"));

    private sealed record SingleProductConversion(string LdtPath, bool Exists, LdtConversionResult? Result);

    public static void AppendCsvLog(string path, IEnumerable<DownloadLogEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);
        var pending = entries.ToArray();
        if (pending.Length == 0) return;
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var mutexName = CsvMutexName(full);
        using var mutex = new Mutex(false, mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException($"Hết thời gian chờ khóa CSV: {full}");

            if (File.Exists(full) && new FileInfo(full).Length > 0)
            {
                string first;
                using (var reader = new StreamReader(full, Encoding.UTF8, true)) first = reader.ReadLine() ?? string.Empty;
                if (!string.Equals(first.TrimStart('﻿'), string.Join(',', CsvHeaders), StringComparison.Ordinal))
                {
                    var backup = $"{full}.{DateTime.Now:yyyyMMddHHmmssfff}.legacy.bak";
                    File.Move(full, backup, false);
                }
            }
            using var writer = new StreamWriter(full, true, new UTF8Encoding(true));
            if (new FileInfo(full).Length == 0) writer.WriteLine(string.Join(',', CsvHeaders));
            foreach (var item in pending) writer.WriteLine(string.Join(',', new object?[] { item.Sheet, item.ExcelRow, item.ProductFamily, item.Material, item.MaterialDescription, item.QueryUsed, item.MatchedSku, item.MatchedName, item.ProductUrl, item.LeafletUrl, item.OutputFile, item.IesUrl, item.IesFile, item.LdtFile, item.Status, item.Error }.Select(Csv)));
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static void EnsureSafeDownloadDestination(string destination, string root)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))
            ?? throw new InvalidDataException("Đích tải không có thư mục cha.");
        if (!FileSystemSafety.IsPathWithinDirectory(destination, root) ||
            !FileSystemSafety.HasNoReparsePointsInExistingPath(root) ||
            !FileSystemSafety.HasNoReparsePointsInExistingPath(directory) ||
            !FileSystemSafety.HasNoReparsePointsInExistingPath(destination))
            throw new InvalidDataException("Đích tải đi qua symbolic link, junction hoặc reparse point.");
    }

    private static void RequireSafeExistingAsset(string path, string root, string kind)
    {
        if (!FileSystemSafety.IsSafePathWithinDirectory(path, root) || !File.Exists(path))
            throw new InvalidDataException($"{kind} đã tải đi qua symbolic link, junction hoặc reparse point.");
    }

    private static string ManifestIdentity(string material, string description)
    {
        var materialKey = DocManager.Core.TextNormalization.ProductKey(material);
        return materialKey.Length > 0 ? materialKey : DocManager.Core.TextNormalization.ProductKey(description);
    }

    private static string? ReadLegacyIdentity(string path)
    {
        if (Path.GetExtension(path).Equals(".ies", StringComparison.OrdinalIgnoreCase)) return new IesParser().ParseFile(path).LuminaireName;
        if (!Path.GetExtension(path).Equals(".ldt", StringComparison.OrdinalIgnoreCase)) return null;
        var document = LdtDocument.Load(path);
        return document.Fields.FirstOrDefault(field => field.Index == 9)?.Value;
    }


    private static (string Ies, string Pdf)? ResolveManifestProductAssets(
        CanonicalAssetManifest manifest,
        CanonicalAssetManifestEntry entry,
        ProductRecord record,
        string canonicalName,
        string family,
        string iesDirectory)
    {
        var ies = manifest.ExistingPath(entry, ".ies") ?? ResolveAsset(iesDirectory, ".ies", record, canonicalName, "IES");
        if (ies is null) return null;
        var pdf = manifest.ExistingPath(entry, ".pdf") ?? ResolveAsset(family, ".pdf", record, canonicalName, "PDF") ?? string.Empty;
        return (ies, pdf);
    }

    private static (string Ies, string Pdf)? ResolveProductAssets(ProductRecord record, string canonicalName, string family, string iesDirectory)
    {
        var ies = ResolveAsset(iesDirectory, ".ies", record, canonicalName, "IES");
        if (ies is null) return null;
        var pdf = ResolveAsset(family, ".pdf", record, canonicalName, "PDF") ?? string.Empty;
        return (ies, pdf);
    }

    private static string? ResolveAsset(string directory, string extension, ProductRecord record, string canonicalName, string kind)
    {
        if (!Directory.Exists(directory)) return null;
        if (!FileSystemSafety.HasNoReparsePointsInExistingPath(directory))
            throw new InvalidDataException($"Thư mục {kind} chứa symbolic link, junction hoặc reparse point.");
        var candidates = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .Where(path => Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var unsafeCandidates = candidates
            .Where(path => !FileSystemSafety.IsSafePathWithinDirectory(path, directory))
            .ToArray();
        var unsafeCanonical = DocManager.Core.ProductAssetName.Resolve(unsafeCandidates, canonicalName);
        if (unsafeCanonical.HasCollision || unsafeCanonical.Path is not null)
            throw new InvalidDataException($"{kind} canonical chứa symbolic link, junction hoặc reparse point.");

        var numericMaterial = NumericMaterial(record.Material);
        if (numericMaterial.Length > 0 && unsafeCandidates.Any(path => HasLegacySkuPrefix(path, numericMaterial)))
            throw new InvalidDataException($"{kind} legacy SKU chứa symbolic link, junction hoặc reparse point.");

        var files = candidates
            .Where(path => FileSystemSafety.IsSafePathWithinDirectory(path, directory))
            .ToArray();
        var canonical = DocManager.Core.ProductAssetName.Resolve(files, canonicalName);
        if (canonical.HasCollision) throw new InvalidDataException($"name_collision: nhiều {kind} khớp '{canonicalName}' trong {directory}.");
        if (canonical.Path is not null) return canonical.Path;

        if (numericMaterial.Length == 0) return null;
        var legacy = files.Where(path => HasLegacySkuPrefix(path, numericMaterial))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return legacy.Length switch
        {
            0 => null,
            1 => legacy[0],
            _ => throw new InvalidDataException($"name_collision: nhiều {kind} legacy SKU khớp '{record.Material}' trong {directory}.")
        };
    }

    private static string NumericMaterial(string? material)
    {
        var value = material?.Trim() ?? string.Empty;
        return value.Length > 0 && value.All(char.IsDigit) ? value : string.Empty;
    }

    private static bool HasLegacySkuPrefix(string path, string numericMaterial)
    {
        var stem = Path.GetFileNameWithoutExtension(path).TrimStart();
        var separator = stem.IndexOf(" - ", StringComparison.Ordinal);
        if (separator <= 0) return false;
        var prefix = stem[..separator];
        return prefix.All(char.IsDigit) && prefix.Equals(numericMaterial, StringComparison.Ordinal);
    }

    private static DownloadLogEntry Base(ProductRecord record, ProductMatch? match) => new() { Sheet = record.Sheet, ExcelRow = record.ExcelRow, ProductFamily = record.ProductFamily, Material = record.Material, MaterialDescription = record.MaterialDescription, QueryUsed = match?.QueryUsed ?? string.Empty, MatchedSku = match?.MatchedSku ?? string.Empty, MatchedName = match?.MatchedName ?? string.Empty, ProductUrl = match?.ProductUrl ?? string.Empty, LeafletUrl = match?.LeafletUrl ?? string.Empty, IesUrl = match?.IesUrl ?? string.Empty };
    private static string CsvMutexName(string fullPath)
    {
        var normalized = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return $"DocManager.Csv.{hash}";
    }
    private static bool IsPerItemFailure(Exception exception) => exception is HttpRequestException or IOException or InvalidDataException or JsonException or TaskCanceledException or TimeoutException or UnauthorizedAccessException;
    private static string Csv(object? value) { var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty; return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text; }
}
