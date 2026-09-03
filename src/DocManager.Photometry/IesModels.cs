namespace DocManager.Photometry;

public sealed record IesDocument
{
    public string SourcePath { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public int LampCount { get; init; }
    public double LumensPerLamp { get; init; }
    public double CandelaMultiplier { get; init; }
    public int VerticalAngleCount { get; init; }
    public int HorizontalAngleCount { get; init; }
    public int PhotometricType { get; init; }
    public int UnitType { get; init; }
    public double Width { get; init; }
    public double Length { get; init; }
    public double Height { get; init; }
    public double BallastFactor { get; init; }
    public double FutureUse { get; init; }
    public double InputWatts { get; init; }
    public IReadOnlyList<double> VerticalAngles { get; init; } = [];
    public IReadOnlyList<double> HorizontalAngles { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<double>> Candela { get; init; } = [];
    public double TotalLumens => LampCount * LumensPerLamp;
    public string LuminaireName => GetHeader("LUMINAIRE") ?? Path.GetFileNameWithoutExtension(SourcePath).Replace('-', ' ');
    public string Manufacturer => GetHeader("MANUFAC") ?? GetHeader("MANUFACTURER") ?? "Unknown";
    public string Lamp => GetHeader("LAMP") ?? "LED";

    public string? GetHeader(string key) => Headers.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

public enum PhotometryGeometryShape
{
    Circular,
    Rectangular
}

public sealed record PhotometryDimensionEvidence(int PageNumber, string Label, string Raw);

public sealed record PhotometryGeometry(
    PhotometryGeometryShape Shape,
    double LengthOrDiameterMm,
    double WidthMm,
    double HeightMm,
    string Provenance,
    IReadOnlyList<PhotometryDimensionEvidence> Evidence);

public sealed record PhotometryLuminousArea(
    PhotometryGeometryShape Shape,
    double LengthOrDiameterMm,
    double WidthMm,
    double HeightC0Mm,
    double HeightC90Mm,
    double HeightC180Mm,
    double HeightC270Mm,
    string Provenance,
    IReadOnlyList<PhotometryDimensionEvidence> Evidence);

public sealed record PhotometryMetadata
{
    // Kept for API compatibility. Canonical product identity is supplied by the
    // conversion context; catalogue data can override technical values only.
    public string LuminaireName { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string CatalogueNumber { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public double? LuminousFluxLumens { get; init; }
    public double? InputWatts { get; init; }
    public int? Cri { get; init; }
    public int? CctKelvin { get; init; }
    public string LuminousFluxProvenance { get; init; } = string.Empty;
    public string InputWattsProvenance { get; init; } = string.Empty;
    public string CriProvenance { get; init; } = string.Empty;
    public string CctProvenance { get; init; } = string.Empty;
    public PhotometryGeometry? OverallGeometry { get; init; }
    public PhotometryLuminousArea? LuminousArea { get; init; }
    public IReadOnlyList<string> GeometryWarnings { get; init; } = [];
    public string CataloguePdfPath { get; init; } = string.Empty;
}

public sealed record LdtConversionResult(
    string OutputPath,
    int SymmetryIndicator,
    double FluxLumens,
    double PowerWatts,
    IReadOnlyList<string>? Warnings = null,
    string SourceIesPath = "",
    string CataloguePdfPath = "",
    string CanonicalProductName = "",
    IReadOnlyDictionary<string, string>? Provenance = null,
    string SourceFingerprint = "");

public enum IesToLdtWarningCode
{
    NoCataloguePdf,
    CatalogueFluxFallback,
    CataloguePowerFallback,
    CatalogueCctFallback,
    CatalogueCriFallback,
    CatalogueGeometryFallback,
    ProductNameNormalized,
    DestinationNameMismatch,
    Overwritten
}

public sealed record IesToLdtWarning(IesToLdtWarningCode Code, string Message);

internal static class IesMetadataFallback
{
    private const string NoCataloguePdfMessage = "Không có catalogue PDF; dùng metadata IES khi được hỗ trợ.";

    public static PhotometryMetadata Create(IesDocument ies)
    {
        ArgumentNullException.ThrowIfNull(ies);
        return new PhotometryMetadata
        {
            LuminaireName = ies.LuminaireName,
            Manufacturer = ies.Manufacturer,
            CatalogueNumber = ies.GetHeader("LUMCAT") ?? string.Empty,
            Description = ies.Lamp,
            LuminousFluxLumens = ies.TotalLumens > 0 ? ies.TotalLumens : null,
            InputWatts = ies.InputWatts >= 0 ? ies.InputWatts : null,
            LuminousFluxProvenance = ies.TotalLumens > 0 ? "ies:lamp-count-times-lumens" : string.Empty,
            InputWattsProvenance = ies.InputWatts >= 0 ? "ies:input-watts" : string.Empty
        };
    }

    public static IReadOnlyList<IesToLdtWarning> Warnings(PhotometryMetadata metadata, IesDocument ies, bool noCataloguePdf)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(ies);
        var warnings = new List<IesToLdtWarning>();
        if (noCataloguePdf)
            warnings.Add(new(IesToLdtWarningCode.NoCataloguePdf, NoCataloguePdfMessage));
        if (!metadata.LuminousFluxProvenance.StartsWith("catalogue:", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new(
                IesToLdtWarningCode.CatalogueFluxFallback,
                ies.LumensPerLamp == -1
                    ? "Catalogue thiếu quang thông kỹ thuật dương; absolute IES không thể dùng dữ liệu fallback."
                    : "Catalogue thiếu quang thông kỹ thuật; dùng quang thông từ IES."));
        }
        if (!metadata.InputWattsProvenance.StartsWith("catalogue:", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new(IesToLdtWarningCode.CataloguePowerFallback, "Catalogue thiếu công suất kỹ thuật; dùng công suất từ IES."));
        if (!metadata.CctProvenance.StartsWith("catalogue:", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new(IesToLdtWarningCode.CatalogueCctFallback, "Catalogue thiếu CCT kỹ thuật đơn trị; dùng 0."));
        if (!metadata.CriProvenance.StartsWith("catalogue:", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new(IesToLdtWarningCode.CatalogueCriFallback, "Catalogue thiếu CRI kỹ thuật; dùng 0."));
        return warnings;
    }
}
