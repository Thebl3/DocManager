using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using ClosedXML.Excel;
using ClosedXML.Excel.Drawings;
using DocManager.Core;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;

namespace DocManager.Dialux;

public sealed class CadSymbolExcelService
{
    public const string ManagedPicturePrefix = "docmanager-cad-symbol-v1-";
    private const int HorizontalPadding = 6;
    private const int VerticalPadding = 6;
    private const int MinimumCellWidthPixels = 96;
    private const double MinimumColumnWidth = 13;
    private const double MinimumRowHeightPoints = 85;
    private readonly ExcelExporter _exporter;

    public CadSymbolExcelService(ExcelExporter? exporter = null) => _exporter = exporter ?? new ExcelExporter();

    public CadSymbolScanResult ScanModels(string workbookPath)
    {
        var fullPath = RequireWorkbook(workbookPath);
        var definition = _exporter.DetectLightingForm(fullPath);
        return ScanModels(fullPath, definition);
    }

    public CadSymbolScanResult ScanTargetWorkbook(string workbookPath)
    {
        try
        {
            var fullPath = RequireWorkbook(workbookPath);
            var missingHeaders = MissingCadHeaders(fullPath);
            if (missingHeaders.Count > 0)
                throw new InvalidDataException($"File Excel thiếu cột {string.Join(" và ", missingHeaders)}; chưa chọn file và chưa thay đổi dữ liệu.");
            var definition = _exporter.DetectLightingForm(fullPath);
            return ScanModels(fullPath, definition);
        }
        catch (IOException exception) when (exception is not FileNotFoundException)
        {
            throw new IOException("Không thể đọc file đã chọn. Hãy đóng file Excel rồi chọn lại; dữ liệu hiện tại được giữ nguyên.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("Không thể đọc file đã chọn. Hãy đóng file Excel và kiểm tra quyền truy cập; dữ liệu hiện tại được giữ nguyên.", exception);
        }
    }

    private static CadSymbolScanResult ScanModels(string fullPath, LightingFormDefinition definition)
    {
        using var workbook = new XLWorkbook(fullPath);
        var sheet = workbook.Worksheet(definition.WorksheetName);
        var models = new Dictionary<string, MutableScanModel>(StringComparer.Ordinal);
        foreach (var row in ModelDataRows(sheet, definition))
        {
            var key = CadSymbolRepository.ModelKey(row.DisplayModel);
            if (key.Length == 0) continue;
            if (models.TryGetValue(key, out var existing)) existing.UsageCount++;
            else models.Add(key, new MutableScanModel(key, row.DisplayModel, 1));
        }
        return new CadSymbolScanResult(
            fullPath,
            definition,
            models.Values.Select(model => new CadSymbolScannedModel(model.Key, model.DisplayModel, model.UsageCount))
                .OrderBy(model => model.DisplayModel, StringComparer.CurrentCultureIgnoreCase)
                .ToArray());
    }

    private static IReadOnlyList<string> MissingCadHeaders(string fullPath)
    {
        using var workbook = new XLWorkbook(fullPath);
        var hasModel = false;
        var hasSymbol = false;
        foreach (var sheet in workbook.Worksheets)
        {
            var maxColumn = Math.Min(Math.Max(sheet.LastColumnUsed()?.ColumnNumber() ?? 1, 1), 80);
            for (var top = 1; top <= 30 && (!hasModel || !hasSymbol); top++)
            {
                for (var column = 1; column <= maxColumn && (!hasModel || !hasSymbol); column++)
                {
                    var header = NormalizeHeader($"{MergedValue(sheet.Cell(top, column))} {MergedValue(sheet.Cell(top + 1, column))}");
                    hasModel |= header.Contains("proposed calculation model", StringComparison.Ordinal);
                    hasSymbol |= header.Contains("symbol in dwg", StringComparison.Ordinal);
                }
            }
        }

        var missing = new List<string>(2);
        if (!hasModel) missing.Add("Proposed calculation model");
        if (!hasSymbol) missing.Add("Symbol in DWG");
        return missing;
    }

    private static string NormalizeHeader(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var previousWasSpace = true;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }
        return builder.ToString().Trim();
    }

    public CadSymbolExcelUpdateResult ApplyMappings(
        string workbookPath,
        IEnumerable<CadSymbolExcelMapping> mappings,
        IEnumerable<string>? removedModelKeys = null)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var fullPath = RequireWorkbook(workbookPath);
        var mappingByKey = new Dictionary<string, ValidatedMapping>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            ArgumentNullException.ThrowIfNull(mapping);
            var key = NormalizeKey(mapping.Key, mapping.DisplayModel);
            if (key.Length == 0) throw new ArgumentException("CAD symbol mapping không có model hợp lệ.", nameof(mappings));
            var png = mapping.PngBytes.ToArray();
            var dimensions = PngValidation.Validate(png);
            var actualHash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(mapping.ImageHash) && !actualHash.Equals(mapping.ImageHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"PNG của '{mapping.DisplayModel}' không khớp hash mapping.");
            mappingByKey[key] = new ValidatedMapping(key, mapping.DisplayModel.Trim(), png, actualHash, dimensions.Width, dimensions.Height);
        }
        var removalKeys = (removedModelKeys ?? [])
            .Select(value => NormalizeKey(value, value))
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var definition = _exporter.DetectLightingForm(fullPath);
        if (!definition.Columns.TryGetValue("symbol", out var symbolColumn))
        {
            var scan = ScanModels(fullPath);
            return new CadSymbolExcelUpdateResult(fullPath, 0, [],
                scan.Models.Where(model => !mappingByKey.ContainsKey(model.Key)).ToArray(),
                scan.Models.Sum(model => model.UsageCount), 0, ["Form không có cột Symbol in DWG; đã bỏ qua cập nhật CAD symbol."]);
        }

        using var workbook = new XLWorkbook(fullPath);
        var sheet = workbook.Worksheet(definition.WorksheetName);
        var rows = new List<TargetRow>();
        var scanned = new Dictionary<string, MutableScanModel>(StringComparer.Ordinal);
        foreach (var modelRow in ModelDataRows(sheet, definition))
        {
            var key = CadSymbolRepository.ModelKey(modelRow.DisplayModel);
            if (key.Length == 0) continue;
            if (scanned.TryGetValue(key, out var existing)) existing.UsageCount++;
            else scanned.Add(key, new MutableScanModel(key, modelRow.DisplayModel, 1));
            var symbolCell = sheet.Cell(modelRow.Row, symbolColumn);
            var symbolRange = symbolCell.IsMerged() ? symbolCell.MergedRange()! : symbolCell.AsRange();
            var anchor = symbolRange.FirstCell();
            rows.Add(new TargetRow(modelRow.Row, key, modelRow.DisplayModel, symbolRange, anchor));
        }

        var diagnostics = new List<string>();
        var conflicts = 0;
        var skipped = 0;
        var updatedRows = 0;
        var mappedKeys = new HashSet<string>(StringComparer.Ordinal);
        var groups = rows.GroupBy(row => row.Anchor.Address.ToString(), StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var targetRows = group.ToArray();
            var keys = targetRows.Select(row => row.Key).Distinct(StringComparer.Ordinal).ToArray();
            if (keys.Length > 1)
            {
                conflicts++;
                skipped += targetRows.Length;
                diagnostics.Add($"Ô Symbol in DWG gộp {targetRows[0].Range.RangeAddress} chứa nhiều model; không thay đổi ảnh.");
                continue;
            }

            var key = keys[0];
            var anchor = targetRows[0].Anchor;
            var managed = sheet.Pictures
                .Where(picture => picture.Name.StartsWith(ManagedPicturePrefix, StringComparison.OrdinalIgnoreCase) &&
                                  picture.TopLeftCell.Address.RowNumber == anchor.Address.RowNumber &&
                                  picture.TopLeftCell.Address.ColumnNumber == anchor.Address.ColumnNumber)
                .ToArray();
            if (mappingByKey.TryGetValue(key, out var mapping))
            {
                var expectedName = ManagedPictureName(anchor, mapping.Hash);
                var alreadyCurrent = managed.Length == 1 && managed[0].Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase);
                if (!alreadyCurrent)
                {
                    EnsureMinimumCell(sheet, targetRows[0].Range, symbolColumn);
                    if (managed.Length == 1)
                        ReplaceManagedPicture(sheet, managed[0], targetRows[0].Range, anchor, mapping);
                    else
                    {
                        foreach (var picture in managed) picture.Delete();
                        AddPicture(sheet, targetRows[0].Range, anchor, mapping);
                    }
                    updatedRows += targetRows.Length;
                }
                mappedKeys.Add(key);
            }
            else if (removalKeys.Contains(key))
            {
                foreach (var picture in managed) picture.Delete();
                if (managed.Length > 0) updatedRows += targetRows.Length;
            }
        }

        var missing = scanned.Values
            .Where(model => !mappingByKey.ContainsKey(model.Key))
            .Select(model => new CadSymbolScannedModel(model.Key, model.DisplayModel, model.UsageCount))
            .OrderBy(model => model.DisplayModel, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var mappedModels = scanned.Values
            .Where(model => mappedKeys.Contains(model.Key))
            .Select(model => new CadSymbolScannedModel(model.Key, model.DisplayModel, model.UsageCount))
            .OrderBy(model => model.DisplayModel, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        if (updatedRows > 0)
        {
            var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileNameWithoutExtension(fullPath)}.{Guid.NewGuid():N}.cad-symbol.part.xlsx");
            try
            {
                workbook.SaveAs(temporary);
                RepairManagedDrawingRelationships(temporary);
                File.Move(temporary, fullPath, true);
            }
            catch (IOException exception)
            {
                throw new IOException($"Không thể cập nhật '{fullPath}'. File có thể đang mở/khóa; bản gốc được giữ nguyên.", exception);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        return new CadSymbolExcelUpdateResult(fullPath, updatedRows, mappedModels, missing, skipped, conflicts, diagnostics);
    }

    private static void EnsureMinimumCell(IXLWorksheet sheet, IXLRange range, int symbolColumn)
    {
        if (range.ColumnCount() == 1 && sheet.Column(symbolColumn).Width < MinimumColumnWidth)
            sheet.Column(symbolColumn).Width = MinimumColumnWidth;
        if (range.RowCount() == 1 && sheet.Row(range.FirstRow().RowNumber()).Height < MinimumRowHeightPoints)
            sheet.Row(range.FirstRow().RowNumber()).Height = MinimumRowHeightPoints;
    }

    private static void AddPicture(IXLWorksheet sheet, IXLRange range, IXLCell anchor, ValidatedMapping mapping)
    {
        var layout = PictureLayout(sheet, range, mapping.Width, mapping.Height);
        var name = ManagedPictureName(anchor, mapping.Hash);
        using var stream = new MemoryStream(mapping.Png, writable: false);
        sheet.AddPicture(stream, XLPictureFormat.Png, name)
            .WithPlacement(XLPicturePlacement.Move)
            .WithSize(layout.Width, layout.Height)
            .MoveTo(anchor, layout.X, layout.Y);
    }

    private static void ReplaceManagedPicture(IXLWorksheet sheet, IXLPicture picture, IXLRange range, IXLCell anchor, ValidatedMapping mapping)
    {
        var layout = PictureLayout(sheet, range, mapping.Width, mapping.Height);
        using var stream = new MemoryStream(mapping.Png, writable: false);
        var source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(source);
        var target = picture.ImageStream;
        target.SetLength(0);
        encoder.Save(target);
        target.Position = 0;
        picture.Name = ManagedPictureName(anchor, mapping.Hash);
        picture.WithPlacement(XLPicturePlacement.Move)
            .WithSize(layout.Width, layout.Height)
            .MoveTo(anchor, layout.X, layout.Y);
    }

    private static (int Width, int Height, int X, int Y) PictureLayout(IXLWorksheet sheet, IXLRange range, int imageWidth, int imageHeight)
    {
        var cellWidth = Enumerable.Range(range.FirstColumn().ColumnNumber(), range.ColumnCount())
            .Sum(column => ColumnWidthToPixels(sheet.Column(column).Width));
        var cellHeight = Enumerable.Range(range.FirstRow().RowNumber(), range.RowCount())
            .Sum(row => PointsToPixels(sheet.Row(row).Height));
        var availableWidth = Math.Max(1, cellWidth - HorizontalPadding * 2);
        var availableHeight = Math.Max(1, cellHeight - VerticalPadding * 2);
        var size = Fit(imageWidth, imageHeight, availableWidth, availableHeight);
        return (size.Width, size.Height,
            Math.Max(HorizontalPadding, (cellWidth - size.Width) / 2),
            Math.Max(VerticalPadding, (cellHeight - size.Height) / 2));
    }

    private static (int Width, int Height) Fit(int width, int height, int maxWidth, int maxHeight)
    {
        var scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
        return (
            Math.Clamp((int)Math.Round(width * scale), 1, maxWidth),
            Math.Clamp((int)Math.Round(height * scale), 1, maxHeight));
    }

    private static void RepairManagedDrawingRelationships(string workbookPath)
    {
        using var document = SpreadsheetDocument.Open(workbookPath, true);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("Workbook thiếu WorkbookPart.");
        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToArray() ?? [];
        foreach (var sheet in sheets)
        {
            if (sheet.Id?.Value is not { Length: > 0 } sheetRelationshipId) continue;
            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheetRelationshipId);
            var drawing = worksheetPart.Worksheet.Elements<Drawing>().FirstOrDefault();
            if (drawing?.Id?.Value is not { Length: > 0 } drawingRelationshipId) continue;
            var drawingsPart = (DrawingsPart)worksheetPart.GetPartById(drawingRelationshipId);
            var anchors = drawingsPart.WorksheetDrawing?.ChildElements
                .Select(element => element.Descendants<Xdr.NonVisualDrawingProperties>().FirstOrDefault())
                .Where(properties => properties?.Name?.Value?.StartsWith(ManagedPicturePrefix, StringComparison.OrdinalIgnoreCase) == true)
                .Select(properties => properties!.Parent?.Parent?.Parent)
                .OfType<OpenXmlCompositeElement>()
                .ToArray() ?? [];
            foreach (var anchor in anchors)
            {
                var relationshipId = anchor.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value;
                if (relationshipId is not null && drawingsPart.Parts.Any(part => part.RelationshipId == relationshipId)) continue;
                anchor.Remove();
            }
            drawingsPart.WorksheetDrawing?.Save();
        }
    }

    private static IEnumerable<ModelDataRow> ModelDataRows(IXLWorksheet sheet, LightingFormDefinition definition)
    {
        var modelColumn = definition.Columns["model"];
        var lastRow = Math.Max(definition.DataStartRow - 1, sheet.LastRowUsed()?.RowNumber() ?? definition.DataStartRow - 1);
        for (var row = definition.DataStartRow; row <= lastRow; row++)
        {
            if (!TryGetModelDataRow(sheet, definition, row, modelColumn, out var displayModel)) continue;
            yield return new ModelDataRow(row, displayModel);
        }
    }

    private static bool TryGetModelDataRow(IXLWorksheet sheet, LightingFormDefinition definition, int row, int modelColumn, out string displayModel)
    {
        var modelCell = sheet.Cell(row, modelColumn);
        displayModel = string.Empty;
        if (!modelCell.IsMerged())
        {
            displayModel = modelCell.GetString().Trim();
            return displayModel.Length > 0;
        }

        var range = modelCell.MergedRange()!;
        if (IsCrossFieldHorizontalBanner(range, definition, modelColumn) || !IsLegitimateModelMerge(range, definition, modelColumn)) return false;
        if (range.FirstRow().RowNumber() != row) return false;
        displayModel = range.FirstCell().GetString().Trim();
        return displayModel.Length > 0;
    }

    private static bool IsCrossFieldHorizontalBanner(IXLRange range, LightingFormDefinition definition, int modelColumn) =>
        range.RowCount() == 1 && SpansConfiguredSemanticColumn(range, definition, modelColumn);

    private static bool IsLegitimateModelMerge(IXLRange range, LightingFormDefinition definition, int modelColumn) =>
        range.ColumnCount() == 1 || !SpansConfiguredSemanticColumn(range, definition, modelColumn);

    private static bool SpansConfiguredSemanticColumn(IXLRange range, LightingFormDefinition definition, int modelColumn) =>
        definition.Columns
            .Where(pair => pair.Key is "no" or "area" or "qty" or "power" or "totpower" or "flux" or "eff" or "symbol" or "param" or "refimg" or "ipik" or "std_lux" or "std_uo" or "des_lux" or "des_uo")
            .Any(pair => pair.Value != modelColumn && ContainsColumn(range, pair.Value));

    private static bool ContainsColumn(IXLRange range, int column) =>
        range.FirstColumn().ColumnNumber() <= column && column <= range.LastColumn().ColumnNumber();

    private static string MergedValue(IXLCell cell) => cell.IsMerged() ? cell.MergedRange()!.FirstCell().GetString() : cell.GetString();
    private static int ColumnWidthToPixels(double width) => Math.Max(1, (int)Math.Floor(width * 7 + 5));
    private static int PointsToPixels(double points) => Math.Max(1, (int)Math.Round(points * 96d / 72d));
    private static string ManagedPictureName(IXLCell anchor, string imageHash)
    {
        var identity = $"{anchor.Worksheet.Name}!{anchor.Address}:{imageHash}";
        var suffix = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..6];
        return ManagedPicturePrefix + suffix;
    }

    private static string NormalizeKey(string? key, string? display)
    {
        var candidate = key?.Trim() ?? string.Empty;
        if (candidate.StartsWith("v1|", StringComparison.Ordinal)) return candidate;
        return CadSymbolRepository.ModelKey(string.IsNullOrWhiteSpace(display) ? candidate : display);
    }

    private static string RequireWorkbook(string workbookPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        var fullPath = Path.GetFullPath(workbookPath);
        if (!Path.GetExtension(fullPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Chỉ hỗ trợ file Excel .xlsx.");
        if (Path.GetFileName(fullPath).StartsWith("~$", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(fullPath).StartsWith(".", StringComparison.Ordinal))
            throw new InvalidDataException("Không thể chọn file tạm/khóa của Excel. Hãy chọn file .xlsx gốc.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Không tìm thấy file Excel .xlsx.", fullPath);
        return fullPath;
    }

    private sealed record ModelDataRow(int Row, string DisplayModel);

    private sealed class MutableScanModel(string key, string displayModel, int usageCount)
    {
        public string Key { get; } = key;
        public string DisplayModel { get; } = displayModel;
        public int UsageCount { get; set; } = usageCount;
    }

    private sealed record ValidatedMapping(string Key, string DisplayModel, byte[] Png, string Hash, int Width, int Height);
    private sealed record TargetRow(int Row, string Key, string DisplayModel, IXLRange Range, IXLCell Anchor);
}

public sealed record CadSymbolExcelMapping(string Key, string DisplayModel, byte[] PngBytes, string ImageHash = "");
public sealed record CadSymbolScannedModel(string Key, string DisplayModel, int UsageCount);
public sealed record CadSymbolScanResult(string WorkbookPath, LightingFormDefinition Form, IReadOnlyList<CadSymbolScannedModel> Models);
public sealed record CadSymbolExcelUpdateResult(
    string WorkbookPath,
    int UpdatedRows,
    IReadOnlyList<CadSymbolScannedModel> MappedModels,
    IReadOnlyList<CadSymbolScannedModel> MissingMappings,
    int Skipped,
    int Conflicts,
    IReadOnlyList<string> Diagnostics);
