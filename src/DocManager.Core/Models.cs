namespace DocManager.Core;

public sealed record LuminaireRecord
{
    public string Position { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Quantity { get; init; } = 1;
    public double? PowerWatts { get; init; }
    public double? LuminousFluxLumens { get; init; }
    public int? CctKelvin { get; init; }
    public int? Cri { get; init; }
    public string IpRating { get; init; } = string.Empty;
    public string IkRating { get; init; } = string.Empty;
    public string Voltage { get; init; } = string.Empty;
    public string AmbientTemperature { get; init; } = string.Empty;
    public string Dimensions { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string CataloguePath { get; init; } = string.Empty;
    public string SourcePdf { get; init; } = string.Empty;
}

public sealed record RoomRecord
{
    public string Name { get; init; } = string.Empty;
    public string Number { get; init; } = string.Empty;
    public double? LengthMetres { get; init; }
    public double? WidthMetres { get; init; }
    public double? HeightMetres { get; init; }
    public double? TargetLux { get; init; }
    public double? MaintainedLux { get; init; }
    public IReadOnlyList<LuminaireRecord> Luminaires { get; init; } = [];
}

public sealed record CatalogueMetadata
{
    public string ProductCode { get; init; } = string.Empty;
    public string PdfPath { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public double? PowerWatts { get; init; }
    public double? LuminousFluxLumens { get; init; }
    public int? CctKelvin { get; init; }
    public int? Cri { get; init; }
    public string IpRating { get; init; } = string.Empty;
    public string IkRating { get; init; } = string.Empty;
    public string Voltage { get; init; } = string.Empty;
    public string AmbientTemperature { get; init; } = string.Empty;
    public string Dimensions { get; init; } = string.Empty;
}

public sealed record OperationProgress(int Completed, int Total, string Message)
{
    public double Percentage => Total <= 0 ? 0 : Completed * 100d / Total;
}
