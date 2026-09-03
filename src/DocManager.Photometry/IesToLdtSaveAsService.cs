using DocManager.Core;

namespace DocManager.Photometry;

public sealed record IesToLdtSaveAsRequest(
    string SourceIesPath,
    string DestinationLdtPath,
    string? CataloguePdfPath = null,
    string? ProductName = null,
    bool Overwrite = false);

public sealed record IesToLdtSaveAsResult(
    LdtDocument Document,
    LdtConversionResult Conversion,
    IReadOnlyList<IesToLdtWarning> Warnings);

/// <summary>
/// Converts one IES document to an exact caller-selected LDT path. Parsing,
/// catalogue extraction and conversion complete before the destination is touched.
/// </summary>
public sealed class IesToLdtSaveAsService
{
    private readonly IesParser _parser;
    private readonly PhotometryCatalogue _catalogue;
    private readonly LdtConverter _converter;
    private readonly Func<string, LdtDocument> _loadLdt;
    private readonly Action<string>? _beforePromote;

    public IesToLdtSaveAsService(
        IesParser? parser = null,
        PhotometryCatalogue? catalogue = null,
        LdtConverter? converter = null)
        : this(LdtDocument.Load, null, parser, catalogue, converter)
    {
    }

    internal IesToLdtSaveAsService(
        Func<string, LdtDocument> loadLdt,
        Action<string>? beforePromote = null,
        IesParser? parser = null,
        PhotometryCatalogue? catalogue = null,
        LdtConverter? converter = null)
    {
        _loadLdt = loadLdt ?? throw new ArgumentNullException(nameof(loadLdt));
        _beforePromote = beforePromote;
        _parser = parser ?? new IesParser();
        _catalogue = catalogue ?? new PhotometryCatalogue();
        _converter = converter ?? new LdtConverter();
    }

    public IesToLdtSaveAsResult SaveAs(IesToLdtSaveAsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceIesPath);
        var sourcePath = Path.GetFullPath(request.SourceIesPath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Tệp IES không tồn tại.", sourcePath);
        var snapshot = new IesSourceReader(_parser).ReadFile(sourcePath);
        return SaveAs(snapshot, request, cancellationToken);
    }

    /// <summary>
    /// Converts the immutable document already shown in preview. The source file
    /// is not read again, so later source changes cannot make the saved LDT differ
    /// from the reviewed snapshot.
    /// </summary>
    public IesToLdtSaveAsResult SaveAs(
        IesSourceSnapshot source,
        IesToLdtSaveAsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceIesPath);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationLdtPath);

        var sourcePath = Path.GetFullPath(request.SourceIesPath);
        var destinationPath = Path.GetFullPath(request.DestinationLdtPath);
        if (!source.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Snapshot IES không khớp đường dẫn nguồn trong yêu cầu chuyển đổi.");
        if (!Path.GetExtension(sourcePath).Equals(".ies", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tệp nguồn phải có phần mở rộng .ies.");
        if (!Path.GetExtension(destinationPath).Equals(".ldt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tệp đích phải có phần mở rộng .ldt.");

        var ies = source.Document;
        var requestedName = string.IsNullOrWhiteSpace(request.ProductName)
            ? LdtIdentity.SourceStem(sourcePath)
            : request.ProductName.Trim();
        var canonicalName = ProductAssetName.CanonicalBaseName(requestedName);
        var warnings = new List<IesToLdtWarning>();
        if (!requestedName.Equals(canonicalName, StringComparison.Ordinal))
        {
            warnings.Add(new(
                IesToLdtWarningCode.ProductNameNormalized,
                $"Tên sản phẩm đã được chuẩn hóa thành '{canonicalName}'."));
        }

        var withoutCatalogue = string.IsNullOrWhiteSpace(request.CataloguePdfPath);
        PhotometryMetadata metadata;
        string cataloguePath;
        if (withoutCatalogue)
        {
            metadata = IesMetadataFallback.Create(ies);
            cataloguePath = string.Empty;
        }
        else
        {
            cataloguePath = Path.GetFullPath(request.CataloguePdfPath!);
            if (!Path.GetExtension(cataloguePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Catalogue phải là tệp .pdf.");
            if (!File.Exists(cataloguePath)) throw new FileNotFoundException("Catalogue PDF không tồn tại.", cataloguePath);
            metadata = _catalogue.ExtractPdf(cataloguePath, ies, cancellationToken);
        }

        warnings.AddRange(IesMetadataFallback.Warnings(metadata, ies, withoutCatalogue));
        foreach (var warning in metadata.GeometryWarnings)
            warnings.Add(new(IesToLdtWarningCode.CatalogueGeometryFallback, warning));
        cancellationToken.ThrowIfCancellationRequested();
        var content = _converter.Convert(ies, metadata, canonicalName, out var symmetry, out var flux, out var power);
        var destinationStem = Path.GetFileNameWithoutExtension(destinationPath);
        if (!destinationStem.Equals(canonicalName, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new(
                IesToLdtWarningCode.DestinationNameMismatch,
                $"Tên tệp đích '{destinationStem}' khác tên sản phẩm canonical '{canonicalName}'; vẫn lưu đúng đường dẫn đã chọn."));
        }

        // The conversion above also enforces positive catalogue flux for absolute
        // IES, so no target mutation occurs before every fallible transform step.
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(destinationPath) && !request.Overwrite)
            throw new IOException($"Tệp đích đã tồn tại: {destinationPath}");

        var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        FileSystemSafety.CreateDirectoryIfMissing(destinationDirectory, out var createdDirectoryIdentity);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.part");
        LdtDocument loadedDocument;
        var promoted = false;
        var overwritten = false;
        try
        {
            var document = LdtDocument.CreateForSave(temporaryPath, content);
            document.Save(createBackup: false, overwrite: false);
            var validatedDocument = _loadLdt(temporaryPath);
            loadedDocument = LdtDocument.CreateForSave(destinationPath, validatedDocument.RawText);
            cancellationToken.ThrowIfCancellationRequested();
            _beforePromote?.Invoke(destinationPath);
            overwritten = PromoteValidatedTemporary(temporaryPath, destinationPath, request.Overwrite);
            promoted = true;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            finally
            {
                if (!promoted && createdDirectoryIdentity is { } identity)
                    FileSystemSafety.TryRemoveEmptyDirectoryWithIdentity(destinationDirectory, identity);
            }
        }

        if (overwritten)
        {
            warnings.Add(new(
                IesToLdtWarningCode.Overwritten,
                $"Đã ghi đè tệp đích và lưu bản cũ tại '{destinationPath}.bak'."));
        }

        var provenance = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["flux"] = metadata.LuminousFluxProvenance,
            ["power"] = metadata.InputWattsProvenance,
            ["cri"] = metadata.CriProvenance,
            ["cct"] = metadata.CctProvenance,
            ["dimensions"] = metadata.OverallGeometry?.Provenance ?? "ies:physical-dimensions"
        };
        var conversion = new LdtConversionResult(
            destinationPath,
            symmetry,
            flux,
            power,
            warnings.Select(warning => warning.Message).ToArray(),
            sourcePath,
            cataloguePath,
            canonicalName,
            provenance,
            source.Fingerprint);
        return new IesToLdtSaveAsResult(loadedDocument, conversion, warnings);
    }

    private static bool PromoteValidatedTemporary(string temporaryPath, string destinationPath, bool overwrite)
    {
        if (!File.Exists(destinationPath))
        {
            try
            {
                File.Move(temporaryPath, destinationPath, false);
                return false;
            }
            catch (IOException) when (overwrite && File.Exists(destinationPath))
            {
                // The destination appeared after the no-overwrite move. Preserve it
                // through the normal overwrite-and-backup path below.
            }
        }

        if (!overwrite)
            throw new IOException($"Tệp đích đã tồn tại: {destinationPath}");

        var pendingBackup = $"{destinationPath}.{Guid.NewGuid():N}.backup";
        var targetReplaced = false;
        try
        {
            File.Replace(temporaryPath, destinationPath, pendingBackup);
            targetReplaced = true;
            File.Move(pendingBackup, $"{destinationPath}.bak", true);
            pendingBackup = string.Empty;
            return true;
        }
        catch (Exception promotionException)
        {
            if (targetReplaced && File.Exists(pendingBackup))
            {
                try
                {
                    File.Replace(pendingBackup, destinationPath, null);
                    pendingBackup = string.Empty;
                }
                catch (Exception restoreException)
                {
                    throw new IOException(
                        $"Không thể khôi phục '{destinationPath}'. Bản sao lưu phục hồi còn tại '{pendingBackup}'.",
                        new AggregateException(promotionException, restoreException));
                }
            }
            throw;
        }
    }
}
