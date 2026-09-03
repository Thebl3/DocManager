namespace DocManager.Dialux;

public sealed record DialuxLuminaire
{
    public string Manufacturer { get; init; } = string.Empty;
    public string ArticleNumber { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public double? Count { get; init; }
    public double? PowerWatts { get; init; }
    public double? LuminousFluxLumens { get; init; }
    public double? EfficacyLumensPerWatt { get; init; }
}

public sealed record DialuxRoom
{
    public string Name { get; init; } = string.Empty;
    public string Identity => Name.Trim();
    public double? AverageLux { get; init; }
    public double? MinimumLux { get; init; }
    public double? MaximumLux { get; init; }
    public double? Uniformity { get; init; }
    public double? TargetLux { get; init; }
    public double? TargetUniformity { get; init; }
    public double? AreaSquareMetres { get; init; }
    public double? PowerDensityWattsPerSquareMetre { get; init; }
    public IReadOnlyList<DialuxLuminaire> Luminaires { get; init; } = [];
}

public sealed record DialuxReport
{
    public string SourceFile { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string Operator { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public IReadOnlyList<DialuxLuminaire> Luminaires { get; init; } = [];
    public IReadOnlyList<DialuxRoom> Rooms { get; init; } = [];

    public double TotalPowerWatts
    {
        get
        {
            var reportInventory = Luminaires.Where(item => item.PowerWatts.HasValue).ToArray();
            var reportKeys = reportInventory.Select(InventoryKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var roomOnly = Rooms.SelectMany(room => room.Luminaires)
                .Where(item => item.PowerWatts.HasValue && !reportKeys.Contains(InventoryKey(item)));
            return reportInventory.Concat(roomOnly).Sum(item => item.PowerWatts!.Value * (item.Count ?? 1));
        }
    }

    private static string InventoryKey(DialuxLuminaire luminaire) => $"{luminaire.ArticleNumber.Trim()}\n{luminaire.Name.Trim()}";
}
