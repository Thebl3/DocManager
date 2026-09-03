using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ClosedXML.Excel.Drawings;

namespace DocManager.Dialux;

public sealed partial class ExcelExporter
{
    private const string RoomIdentityHeader = "__DocManagerRoomIdentity";
    private const string MetadataSheetName = "__DocManagerMetadata";
    private const int VisibleColumnCount = 16;
    private static readonly string[] Keys = ["no", "area", "std_lux", "std_uo", "des_lux", "des_uo", "symbol", "model", "param", "refimg", "qty", "power", "totpower", "flux", "eff", "ipik"];
    private static readonly string[] Headers = ["No.", "AREA", "Illuminance (lux)", "Uniformity (Uo)", "Illuminance design (lux)", "Uniformity design (Uo)", "Symbol in DWG", "Proposed calculation model", "Parameter", "Reference image", "Quantity", "Power (W)", "Total power (W)", "Luminous flux (lm)", "Efficiency (lm/W)", "IP / IK"];

    public string Save(IEnumerable<DialuxReport> reports, string outputPath, CatalogueIndex? catalogue = null, bool includeReportTitles = false)
    {
        var reportArray = reports.ToArray();
        if (reportArray.Length == 0) throw new InvalidOperationException("Không có báo cáo DiaLux để xuất.");
        ValidateRooms(reportArray);
        var fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Lighting Schedule");
        BuildHeaders(sheet);
        var metadata = workbook.AddWorksheet(MetadataSheetName);
        metadata.Cell(1, 1).Value = RoomIdentityHeader;
        metadata.Cell(1, 2).Value = "Room identity";
        metadata.Visibility = XLWorksheetVisibility.VeryHidden;
        var metadataRow = 2;
        var row = 3;
        var roomNumber = 1;
        foreach (var report in reportArray)
        {
            if (includeReportTitles && reportArray.Length > 1)
            {
                WriteSection(sheet, row++, Path.GetFileNameWithoutExtension(report.SourceFile));
            }

            foreach (var room in report.Rooms)
            {
                var luminaires = room.Luminaires.Count > 0 ? room.Luminaires.Cast<DialuxLuminaire?>().ToArray() : [null];
                var first = row;
                foreach (var luminaire in luminaires)
                {
                    var targetRow = row++;
                    WriteDataRow(sheet, targetRow, roomNumber, room, luminaire, catalogue);
                    metadata.Cell(metadataRow, 1).Value = targetRow;
                    metadata.Cell(metadataRow++, 2).Value = room.Identity;
                    AddReferenceImages(sheet, targetRow, 10, CatalogueImages(catalogue, luminaire));
                }

                if (row - first > 1)
                {
                    for (var column = 1; column <= 6; column++)
                    {
                        var range = sheet.Range(first, column, row - 1, column);
                        range.Merge();
                        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        SetBorder(range);
                    }
                }

                roomNumber++;
            }
        }

        sheet.SheetView.FreezeRows(2);
        sheet.SheetView.FreezeColumns(2);
        if (row > 3) sheet.Range(2, 1, row - 1, VisibleColumnCount).SetAutoFilter();
        SetColumns(sheet);
        var temporary = Path.Combine(Path.GetDirectoryName(fullOutput)!, $".{Path.GetFileNameWithoutExtension(fullOutput)}.{Guid.NewGuid():N}.part.xlsx");
        try
        {
            workbook.SaveAs(temporary);
            File.Move(temporary, fullOutput, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return fullOutput;
    }

    public LightingFormDefinition DetectLightingForm(string workbookPath)
    {
        using var workbook = new XLWorkbook(workbookPath);
        LightingFormDefinition? best = null;
        foreach (var sheet in workbook.Worksheets)
        {
            var maxColumn = Math.Min(Math.Max(sheet.LastColumnUsed()?.ColumnNumber() ?? 1, 1), 80);
            for (var top = 1; top <= 30; top++)
            {
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var column = 1; column <= maxColumn; column++)
                {
                    var combined = $"{MergedValue(sheet.Cell(top, column))} {MergedValue(sheet.Cell(top + 1, column))}";
                    var key = HeaderKey(combined);
                    if (key is not null && !map.ContainsKey(key)) map[key] = column;
                }

                var mandatory = new[] { "no", "area", "model", "qty", "power" }.Count(map.ContainsKey);
                if (mandatory < 5) continue;
                var score = map.Count + mandatory * 10;
                if (best is null || score > best.Score) best = new LightingFormDefinition(sheet.Name, top, top + 1, top + 2, map, score);
            }
        }

        return best ?? throw new InvalidDataException("Không tìm thấy form có No., AREA, model, Quantity và Power.");
    }

    public AppendResult Append(string workbookPath, IEnumerable<DialuxReport> reports, CatalogueIndex? catalogue = null)
    {
        var reportArray = reports.ToArray();
        ValidateRooms(reportArray);
        var definition = DetectLightingForm(workbookPath);
        using var workbook = new XLWorkbook(workbookPath);
        var sheet = workbook.Worksheet(definition.WorksheetName);
        var metadata = workbook.Worksheets.FirstOrDefault(candidate => candidate.Name.Equals(MetadataSheetName, StringComparison.Ordinal));
        if (metadata is not null && !HasValidMetadataHeader(metadata)) metadata = null;
        if (metadata is null) definition = EnsureRoomIdentityColumn(sheet, definition);
        var lastRow = Math.Max(definition.DataStartRow - 1, sheet.LastRowUsed()?.RowNumber() ?? definition.DataStartRow - 1);
        var metadataIdentities = metadata is null ? new Dictionary<int, string>() : ReadMetadataIdentities(metadata);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingAreas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var row = definition.DataStartRow; row <= lastRow; row++)
        {
            existing.Add(DuplicateKey(sheet, row, definition.Columns, metadataIdentities.GetValueOrDefault(row)));
            existingAreas.Add(Normalize(MergedValue(sheet.Cell(row, definition.Columns["area"]))));
        }
        var noColumn = definition.Columns["no"];
        var nextNo = Enumerable.Range(definition.DataStartRow, Math.Max(0, lastRow - definition.DataStartRow + 1))
            .Select(row => sheet.Cell(row, noColumn).GetDoubleOrDefault())
            .DefaultIfEmpty(0).Max() + 1;
        var added = 0;
        var skipped = 0;
        foreach (var report in reportArray)
        {
            foreach (var room in report.Rooms)
            {
                var luminaires = room.Luminaires.Count > 0 ? room.Luminaires.Cast<DialuxLuminaire?>().ToArray() : [null];
                var pending = new List<(DialuxLuminaire? Luminaire, RowValues Values)>();
                foreach (var luminaire in luminaires)
                {
                    var values = BuildValues((int)nextNo, room, luminaire, catalogue);
                    var key = DuplicateKey(values);
                    if (!existing.Add(key)) { skipped++; continue; }
                    pending.Add((luminaire, values));
                }

                var firstAddedRow = lastRow + 1;
                foreach (var item in pending)
                {
                    var target = ++lastRow;
                    if (target > definition.DataStartRow && definition.DataStartRow <= sheet.LastRowUsed()?.RowNumber())
                    {
                        sheet.Row(definition.DataStartRow).CopyTo(sheet.Row(target));
                        sheet.Row(target).Clear(XLClearOptions.Contents);
                    }
                    WriteMappedRow(sheet, target, definition.Columns, item.Values);
                    if (metadata is not null) WriteMetadataIdentity(metadata, target, item.Values.RoomIdentity);
                    sheet.Row(target).Height = Math.Max(sheet.Row(target).Height, 85);
                    if (definition.Columns.TryGetValue("refimg", out var imageColumn))
                        AddReferenceImages(sheet, target, imageColumn, CatalogueImages(catalogue, item.Luminaire));
                    added++;
                }

                var areaKey = Normalize(CleanArea(room.Name));
                if (pending.Count > 1 && !existingAreas.Contains(areaKey))
                {
                    MergeMappedRoomColumns(sheet, firstAddedRow, lastRow, definition.Columns);
                }
                if (pending.Count > 0) existingAreas.Add(areaKey);
                nextNo++;
            }
        }

        var fullWorkbookPath = Path.GetFullPath(workbookPath);
        var temporary = Path.Combine(Path.GetDirectoryName(fullWorkbookPath)!, $".{Path.GetFileNameWithoutExtension(fullWorkbookPath)}.{Guid.NewGuid():N}.part.xlsx");
        try { workbook.SaveAs(temporary); File.Move(temporary, fullWorkbookPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new AppendResult(added, skipped, definition);
    }

    private static void ValidateRooms(IEnumerable<DialuxReport> reports)
    {
        var malformed = reports.SelectMany(report => report.Rooms).FirstOrDefault(room => !DialuxParser.IsPlausibleRoomName(room.Name));
        if (malformed is not null)
        {
            throw new InvalidDataException("Tên phòng/khu vực DiaLux không hợp lệ; báo cáo PDF có thể đã được trích xuất sai bố cục.");
        }
    }

    private static LightingFormDefinition EnsureRoomIdentityColumn(IXLWorksheet sheet, LightingFormDefinition definition)
    {
        var maxColumn = Math.Max(VisibleColumnCount, sheet.LastColumnUsed()?.ColumnNumber() ?? VisibleColumnCount);
        var identityColumn = Enumerable.Range(1, maxColumn)
            .FirstOrDefault(column => sheet.Cell(definition.HeaderTopRow, column).GetString().Equals(RoomIdentityHeader, StringComparison.Ordinal));
        if (identityColumn == 0)
        {
            identityColumn = maxColumn + 1;
            sheet.Cell(definition.HeaderTopRow, identityColumn).Value = RoomIdentityHeader;
            sheet.Column(identityColumn).Hide();
        }
        var columns = definition.Columns.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        columns["roomidentity"] = identityColumn;
        return definition with { Columns = columns };
    }

    private static void BuildHeaders(IXLWorksheet sheet)
    {
        sheet.Cell(1, 1).Value = Headers[0]; sheet.Range(1, 1, 2, 1).Merge();
        sheet.Cell(1, 2).Value = Headers[1]; sheet.Range(1, 2, 2, 2).Merge();
        sheet.Cell(1, 3).Value = "STANDARD"; sheet.Range(1, 3, 1, 4).Merge();
        sheet.Cell(1, 5).Value = Headers[4]; sheet.Range(1, 5, 2, 5).Merge();
        sheet.Cell(1, 6).Value = Headers[5]; sheet.Range(1, 6, 2, 6).Merge();
        sheet.Cell(1, 7).Value = "Select light type"; sheet.Range(1, 7, 1, 10).Merge();
        sheet.Cell(1, 11).Value = "Lamp specifications"; sheet.Range(1, 11, 1, VisibleColumnCount).Merge();
        for (var column = 3; column <= 4; column++) sheet.Cell(2, column).Value = Headers[column - 1];
        for (var column = 7; column <= VisibleColumnCount; column++) sheet.Cell(2, column).Value = Headers[column - 1];
        var header = sheet.Range(1, 1, 2, VisibleColumnCount);
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("D9D9D9");
        sheet.Range(1, 5, 2, 6).Style.Fill.BackgroundColor = XLColor.FromHtml("FCE4A6");
        header.Style.Font.Bold = true; header.Style.Font.FontSize = 9;
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        header.Style.Alignment.WrapText = true;
        SetBorder(header);
        sheet.Row(1).Height = 30; sheet.Row(2).Height = 42;
    }

    private static void WriteDataRow(IXLWorksheet sheet, int row, int no, DialuxRoom room, DialuxLuminaire? luminaire, CatalogueIndex? catalogue)
    {
        var values = BuildValues(no, room, luminaire, catalogue);
        WriteMappedRow(sheet, row, Keys.Select((key, index) => (key, index)).ToDictionary(item => item.key, item => item.index + 1), values);
        sheet.Row(row).Height = 85;
        var range = sheet.Range(row, 1, row, VisibleColumnCount); range.Style.Font.FontSize = 9; range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center; range.Style.Alignment.WrapText = true; SetBorder(range);
        foreach (var column in new[] { 2, 8, 9 }) sheet.Cell(row, column).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        foreach (var column in Enumerable.Range(1, VisibleColumnCount).Except(new[] { 2, 8, 9 })) sheet.Cell(row, column).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        if (values.Power is > 0 && values.Flux is > 0) { sheet.Cell(row, 15).FormulaA1 = $"N{row}/L{row}"; sheet.Cell(row, 15).Style.NumberFormat.Format = "0.0"; }
        if (values.Quantity.HasValue && values.Power.HasValue) sheet.Cell(row, 13).FormulaA1 = $"K{row}*L{row}";
    }

    private static RowValues BuildValues(int no, DialuxRoom room, DialuxLuminaire? luminaire, CatalogueIndex? catalogue)
    {
        var entry = luminaire is null ? null : catalogue?.Find(luminaire.ArticleNumber, luminaire.Name);
        var power = entry?.PowerWatts ?? luminaire?.PowerWatts;
        var flux = entry?.LuminousFluxLumens ?? luminaire?.LuminousFluxLumens;
        return new RowValues(no, CleanArea(room.Name), room.Identity, room.TargetLux, room.TargetUniformity, room.AverageLux, room.Uniformity,
            FormatModel(luminaire), entry is null ? string.Empty : FormatParameter(entry), luminaire?.Count, power, flux, FormatIpIk(entry));
    }

    private static void WriteMappedRow(IXLWorksheet sheet, int row, IReadOnlyDictionary<string, int> map, RowValues values)
    {
        var data = new Dictionary<string, object?> { ["no"] = values.No, ["area"] = values.Area, ["std_lux"] = values.StandardLux, ["std_uo"] = values.StandardUniformity, ["des_lux"] = values.DesignLux, ["des_uo"] = values.DesignUniformity, ["symbol"] = "", ["model"] = values.Model, ["param"] = values.Parameter, ["refimg"] = "", ["qty"] = values.Quantity, ["power"] = values.Power, ["flux"] = values.Flux, ["ipik"] = values.IpIk };
        if (map.ContainsKey("roomidentity")) data["roomidentity"] = values.RoomIdentity;
        foreach (var (key, value) in data) if (map.TryGetValue(key, out var column)) SetCell(sheet.Cell(row, column), value);
        if (map.TryGetValue("totpower", out var total) && map.TryGetValue("qty", out var quantity) && map.TryGetValue("power", out var power) && values.Quantity.HasValue && values.Power.HasValue) sheet.Cell(row, total).FormulaA1 = $"{sheet.Cell(row, quantity).Address}*{sheet.Cell(row, power).Address}";
        if (map.TryGetValue("eff", out var efficiency) && map.TryGetValue("flux", out var flux) && map.TryGetValue("power", out power) && values.Flux is > 0 && values.Power is > 0) sheet.Cell(row, efficiency).FormulaA1 = $"{sheet.Cell(row, flux).Address}/{sheet.Cell(row, power).Address}";
    }

    private static void MergeMappedRoomColumns(IXLWorksheet sheet, int firstRow, int lastRow, IReadOnlyDictionary<string, int> map)
    {
        if (lastRow <= firstRow) return;
        foreach (var key in new[] { "no", "area", "std_lux", "std_uo", "des_lux", "des_uo" })
        {
            if (!map.TryGetValue(key, out var column)) continue;
            var range = sheet.Range(firstRow, column, lastRow, column);
            range.Merge();
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            SetBorder(range);
        }
    }

    private static bool HasValidMetadataHeader(IXLWorksheet metadata) =>
        metadata.Cell(1, 1).GetString().Equals(RoomIdentityHeader, StringComparison.Ordinal) &&
        metadata.Cell(1, 2).GetString().Equals("Room identity", StringComparison.Ordinal);

    private static Dictionary<int, string> ReadMetadataIdentities(IXLWorksheet metadata)
    {
        var identities = new Dictionary<int, string>();
        var lastRow = metadata.LastRowUsed()?.RowNumber() ?? 1;
        for (var row = 2; row <= lastRow; row++)
        {
            if (!metadata.Cell(row, 1).TryGetValue<int>(out var dataRow) || dataRow < 1) continue;
            var identity = metadata.Cell(row, 2).GetString().Trim();
            if (identity.Length > 0) identities[dataRow] = identity;
        }
        return identities;
    }

    private static void WriteMetadataIdentity(IXLWorksheet metadata, int dataRow, string identity)
    {
        var target = Math.Max(2, (metadata.LastRowUsed()?.RowNumber() ?? 1) + 1);
        metadata.Cell(target, 1).Value = dataRow;
        metadata.Cell(target, 2).Value = identity;
    }

    private static void SetCell(IXLCell cell, object? value)
    {
        switch (value) { case null: cell.Clear(XLClearOptions.Contents); break; case int i: cell.Value = i; break; case double d: cell.Value = d; break; default: cell.Value = value.ToString(); break; }
    }

    private static IReadOnlyList<CatalogueEntry> CatalogueImages(CatalogueIndex? catalogue, DialuxLuminaire? luminaire)
    {
        if (catalogue is null || luminaire is null) return [];
        var components = catalogue.FindComponents(luminaire.ArticleNumber, luminaire.Name)
            .Select(component => component.Entry)
            .Where(entry => entry is not null)
            .Cast<CatalogueEntry>()
            .ToArray();
        if (components.Length > 1)
        {
            var main = components[^1];
            components = [main, .. components[..^1]];
        }
        return components
            .Where(entry => entry.ImagePng is { Length: > 0 })
            .DistinctBy(entry => Convert.ToHexString(SHA256.HashData(entry.ImagePng!)), StringComparer.Ordinal)
            .Take(2)
            .ToArray();
    }

    private static void AddReferenceImages(IXLWorksheet sheet, int row, int column, IReadOnlyList<CatalogueEntry> entries)
    {
        if (entries.Count == 0 || column <= 0) return;
        var images = entries.Where(entry => entry.ImagePng is { Length: > 0 }).Take(2).ToArray();
        if (images.Length == 0) return;

        const int horizontalPadding = 6;
        const int verticalPadding = 6;
        const int gap = 6;
        const int minimumCellWidth = 96;
        const int minimumRowHeight = 114;
        var cellWidth = Math.Max(minimumCellWidth, ColumnWidthToPixels(sheet.Column(column).Width));
        var rowHeight = Math.Max(minimumRowHeight, PointsToPixels(sheet.Row(row).Height));
        var availableWidth = Math.Max(1, cellWidth - horizontalPadding * 2);
        var availableHeight = Math.Max(1, rowHeight - verticalPadding * 2 - gap * (images.Length - 1));
        var slotHeight = availableHeight / images.Length;
        var sizes = images.Select(entry => FitImage(entry.ImageAspect ?? 1, availableWidth, slotHeight)).ToArray();
        var totalHeight = sizes.Sum(size => size.Height) + gap * (images.Length - 1);
        var y = Math.Max(verticalPadding, (rowHeight - totalHeight) / 2);

        for (var index = 0; index < images.Length; index++)
        {
            var png = images[index].ImagePng!;
            var size = sizes[index];
            using var stream = new MemoryStream(png, writable: false);
            var hash = Convert.ToHexString(SHA256.HashData(png))[..12];
            var name = UniquePictureName(sheet, $"refimg-r{row}-{hash}");
            var x = Math.Max(horizontalPadding, (cellWidth - size.Width) / 2);
            sheet.AddPicture(stream, XLPictureFormat.Png, name)
                .WithPlacement(XLPicturePlacement.Move)
                .WithSize(size.Width, size.Height)
                .MoveTo(sheet.Cell(row, column), x, y);
            y += size.Height + gap;
        }
    }

    private static (int Width, int Height) FitImage(double aspect, int maxWidth, int maxHeight)
    {
        aspect = double.IsFinite(aspect) && aspect > .05 ? aspect : 1;
        var width = maxWidth;
        var height = (int)Math.Round(width / aspect);
        if (height > maxHeight)
        {
            height = maxHeight;
            width = (int)Math.Round(height * aspect);
        }
        return (Math.Clamp(width, 1, maxWidth), Math.Clamp(height, 1, maxHeight));
    }

    private static int ColumnWidthToPixels(double width) => Math.Max(1, (int)Math.Floor(width * 7 + 5));
    private static int PointsToPixels(double points) => Math.Max(1, (int)Math.Round(points * 96d / 72d));

    private static string UniquePictureName(IXLWorksheet sheet, string candidate)
    {
        var names = sheet.Pictures.Select(picture => picture.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(candidate)) return candidate;
        for (var suffix = 2; ; suffix++) if (!names.Contains($"{candidate}-{suffix}")) return $"{candidate}-{suffix}";
    }

    private static string DuplicateKey(IXLWorksheet sheet, int row, IReadOnlyDictionary<string, int> map, string? metadataIdentity = null) => string.Join('|', new[] { "roomidentity", "model", "param", "qty", "power", "flux", "ipik" }.Select(key =>
        key == "roomidentity"
            ? !string.IsNullOrWhiteSpace(metadataIdentity)
                ? Normalize(metadataIdentity)
                : map.TryGetValue(key, out var identityColumn) && !string.IsNullOrWhiteSpace(sheet.Cell(row, identityColumn).GetString())
                    ? Normalize(sheet.Cell(row, identityColumn).GetString())
                    : map.TryGetValue("area", out var areaColumn) ? Normalize(MergedValue(sheet.Cell(row, areaColumn))) : string.Empty
            : map.TryGetValue(key, out var column) ? Normalize(MergedValue(sheet.Cell(row, column))) : string.Empty));
    private static string DuplicateKey(RowValues values) => string.Join('|', new object?[] { values.RoomIdentity, values.Model, values.Parameter, values.Quantity, values.Power, values.Flux, values.IpIk }.Select(value => Normalize(value?.ToString())));
    private static string Normalize(string? value) => Regex.Replace(value?.Trim().ToUpperInvariant() ?? string.Empty, @"\s+", " ");
    private static string MergedValue(IXLCell cell) => cell.IsMerged() ? cell.MergedRange()!.FirstCell().Value.ToString() : cell.Value.ToString();

    private static string? HeaderKey(string header)
    {
        var value = Regex.Replace(header.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        if (Regex.IsMatch(value, @"\bno\b")) return "no"; if (Regex.IsMatch(value, @"\barea\b")) return "area"; if (value.Contains("proposed calculation model") || Regex.IsMatch(value, @"\bmodel\b")) return "model";
        if (value.Contains("quantity") || value.Contains("qty")) return "qty"; if (value.Contains("total power")) return "totpower"; if (value.Contains("power")) return "power";
        if (value.Contains("luminous flux") || value.Contains("lumen")) return "flux"; if (value.Contains("efficiency")) return "eff"; if (value.Contains("parameter")) return "param";
        if (value.Contains("reference image")) return "refimg"; if (value.Contains("ip") || value.Contains("ik")) return "ipik"; if (value.Contains("symbol")) return "symbol";
        if (value.Contains("illuminance design")) return "des_lux"; if (value.Contains("uniformity design")) return "des_uo"; if (value.Contains("illuminance")) return "std_lux"; if (value.Contains("uniformity")) return "std_uo"; if (value.Contains("type")) return "type";
        return null;
    }

    private static string CleanArea(string name) { var value = name.Split('·').Last().Trim(); return Regex.Replace(value, @"\s*\([^)]*\)\s*$", string.Empty).Trim() is { Length: > 0 } clean ? clean : name.Trim(); }
    private static string FormatModel(DialuxLuminaire? luminaire) { if (luminaire is null) return string.Empty; if (luminaire.Name.StartsWith(luminaire.ArticleNumber, StringComparison.OrdinalIgnoreCase)) return luminaire.Name; return $"{luminaire.ArticleNumber} {luminaire.Name}".Trim(); }
    private static string FormatIpIk(CatalogueEntry? entry) => entry is null ? string.Empty : string.Join(" / ", new[] { entry.IpCode, entry.IkCode }.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string FormatParameter(CatalogueEntry entry)
    {
        var dimensions = FormatDimensions(entry.Dimensions);
        var lines = new[]
        {
            !string.IsNullOrWhiteSpace(entry.Cct) ? $"CCT: {entry.Cct.Trim()}" : string.Empty,
            FormatCri(entry.Cri),
            ValueOnly(entry.Voltage, VoltageLabelRegex()),
            FormatBeamAngle(entry.BeamAngle),
            FormatAmbientTemperature(entry.AmbientTemperature),
            dimensions.Length > 0 ? $"Size: {dimensions}" : string.Empty
        };
        return string.Join('\n', lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string FormatCri(string value)
    {
        var match = CriValueRegex().Match(value ?? string.Empty);
        if (!match.Success) return string.Empty;
        var numeric = match.Groups["value"].Value;
        var comparator = match.Groups["comparator"].Value;
        return $"CRI: {(comparator.Length > 0 ? comparator : ">")}{numeric}";
    }

    private static string FormatBeamAngle(string value)
    {
        var match = BeamAngleValueRegex().Match(value ?? string.Empty);
        if (!match.Success) return string.Empty;
        var normalized = DegreeUnitRegex().Replace(match.Value, " độ");
        return $"Beam: {Regex.Replace(normalized, @"\s+", " ").Trim()}";
    }

    private static string ValueOnly(string value, Regex labelRegex) => Regex.Replace(labelRegex.Replace(value ?? string.Empty, string.Empty), @"\s+", " ").Trim();

    private static string FormatAmbientTemperature(string value) => AmbientTemperatureNormalizer.Normalize(value);

    private static string FormatDimensions(string value)
    {
        var normalized = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (LabeledDimensionRegex().IsMatch(normalized))
        {
            normalized = LabeledDimensionSpacingRegex().Replace(normalized, "$1 ");
            normalized = DimensionSeparatorRegex().Replace(normalized, " x ");
            return Regex.Replace(normalized, @"(?<=\d)\s*mm\b", " mm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        var components = DimensionSeparatorRegex().Split(normalized);
        var matches = components.Select(component => DimensionComponentRegex().Match(component)).ToArray();
        var unitCount = matches.Count(match => match.Success && match.Groups["unit"].Success);
        if (components.Length > 1 && matches.All(match => match.Success) &&
            (unitCount == components.Length || unitCount == 1 && matches[^1].Groups["unit"].Success))
        {
            return string.Join(" x ", matches.Select(match => $"{match.Groups["value"].Value}mm"));
        }

        normalized = Regex.Replace(normalized, @"\s*([x×])\s*", "$1");
        return Regex.Replace(normalized, @"\s+mm\b", "mm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"^\s*(?:(?:Input|Operating|Supply|Mains)\s+voltage|Điện\s+áp(?:\s+(?:đầu\s+vào|hoạt\s+động|vận\s+hành|nguồn))?)\s*:?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VoltageLabelRegex();

    [GeneratedRegex(@"^\s*(?<comparator>>|≥)?\s*(?<value>\d{2,3})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CriValueRegex();

    [GeneratedRegex(@"\d[\d.,]*\s*(?:°|deg(?:ree)?s?|độ)(?:\s*(?:to|đến|[-–—])\s*\d[\d.,]*\s*(?:°|deg(?:ree)?s?|độ))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BeamAngleValueRegex();

    [GeneratedRegex(@"°|deg(?:ree)?s?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DegreeUnitRegex();

    [GeneratedRegex(@"\b[DLWH]\s*\d[\d.,]*\s*mm\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabeledDimensionRegex();

    [GeneratedRegex(@"\b([DLWH])\s*(?=\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabeledDimensionSpacingRegex();

    [GeneratedRegex(@"\s*(?:x|×)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DimensionSeparatorRegex();

    [GeneratedRegex(@"^\s*(?<value>\d[\d.,]*)\s*(?<unit>mm)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DimensionComponentRegex();

    private static void WriteSection(IXLWorksheet sheet, int row, string title) { sheet.Range(row, 2, row, VisibleColumnCount).Merge(); sheet.Cell(row, 2).Value = string.IsNullOrWhiteSpace(title) ? "PDF" : title; sheet.Range(row, 2, row, VisibleColumnCount).Style.Fill.BackgroundColor = XLColor.FromHtml("BFBFBF"); sheet.Cell(row, 2).Style.Font.Bold = true; SetBorder(sheet.Range(row, 1, row, VisibleColumnCount)); }
    private static void SetBorder(IXLRange range) { range.Style.Border.TopBorder = XLBorderStyleValues.Thin; range.Style.Border.BottomBorder = XLBorderStyleValues.Thin; range.Style.Border.LeftBorder = XLBorderStyleValues.Thin; range.Style.Border.RightBorder = XLBorderStyleValues.Thin; }
    private static void SetColumns(IXLWorksheet sheet) { var widths = new[] { 6d, 24, 12, 12, 14, 14, 12, 30, 28, 16, 10, 11, 14, 14, 14, 12 }; for (var index = 0; index < widths.Length; index++) sheet.Column(index + 1).Width = widths[index]; }

    private sealed record RowValues(int No, string Area, string RoomIdentity, double? StandardLux, double? StandardUniformity, double? DesignLux, double? DesignUniformity, string Model, string Parameter, double? Quantity, double? Power, double? Flux, string IpIk);
}

public sealed record LightingFormDefinition(string WorksheetName, int HeaderTopRow, int HeaderBottomRow, int DataStartRow, IReadOnlyDictionary<string, int> Columns, int Score);
public sealed record AppendResult(int AddedRows, int SkippedDuplicates, LightingFormDefinition Form);
internal static class ClosedXmlExtensions { public static double GetDoubleOrDefault(this IXLCell cell) => cell.TryGetValue<double>(out var value) ? value : 0; }
