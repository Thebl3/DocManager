using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClosedXML.Excel;
using DocManager.Core;
using DocManager.Desktop;
using DocManager.Dialux;

namespace DocManager.Tests;

public sealed class CadSymbolTests
{
    [Fact]
    public void Repository_normalizes_dedupes_preserves_discriminators_and_supports_multiple_instances()
    {
        var root = TempRoot();
        try
        {
            var png = Png(40, 20, Colors.Red);
            var first = new CadSymbolRepository(root);
            var second = new CadSymbolRepository(root);
            var model840 = first.Upsert(" DN068B  LED13/840 ", png, "test");
            var normalizedSame = second.Upsert("dn068b led13 840", png, "test-2");
            first.Upsert("DN068B LED13 865", png, "test");

            Assert.Equal(model840.Key, normalizedSame.Key);
            Assert.NotEqual(CadSymbolRepository.ModelKey("DN068B LED13 840"), CadSymbolRepository.ModelKey("DN068B LED13 865"));
            Assert.Equal(2, second.GetMappings().Count);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "images"), "*.png"));
            Assert.Empty(second.Diagnostics);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Repository_corrupt_manifest_falls_back_archives_on_write_and_rejects_traversal()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "manifest.json"), "{broken");
            var repository = new CadSymbolRepository(root);
            Assert.Empty(repository.GetMappings());
            Assert.NotEmpty(repository.Diagnostics);
            repository.Upsert("MODEL 840", Png(12, 8, Colors.Blue), "recovery");
            Assert.Single(Directory.EnumerateFiles(root, "*.corrupt.bak.json"));

            var manifest = File.ReadAllText(Path.Combine(root, "manifest.json"));
            manifest = manifest.Replace("images/", "../", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(root, "manifest.json"), manifest);
            Assert.Empty(repository.GetMappings());
            Assert.Contains(repository.Diagnostics, diagnostic => diagnostic.Contains("manifest", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [RequiresSymbolicLinkFact]
    public void Repository_rejects_reparse_root_images_and_manifest_when_supported()
    {
        var root = TempRoot();
        var outside = TempRoot();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            var linkedRoot = Path.Combine(root, "linked-root");
            CreateDirectorySymbolicLinkOrSkip(linkedRoot, outside);
            Assert.Throws<ArgumentException>(() => new CadSymbolRepository(linkedRoot));
            Directory.Delete(linkedRoot);

            var imagesLink = Path.Combine(root, "images");
            CreateDirectorySymbolicLinkOrSkip(imagesLink, outside);
            var repository = new CadSymbolRepository(root);
            Assert.Throws<InvalidDataException>(() => repository.Upsert("MODEL A", Png(8, 8, Colors.Red), "test"));
            Directory.Delete(imagesLink);

            var outsideManifest = Path.Combine(outside, "manifest.json");
            File.WriteAllText(outsideManifest, "{}");
            var manifestLink = Path.Combine(root, "manifest.json");
            CreateFileSymbolicLinkOrSkip(manifestLink, outsideManifest);
            Assert.Throws<InvalidDataException>(() => new CadSymbolRepository(root).GetMappings());
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Repository_rejects_truncated_or_structurally_invalid_png_before_persistence_and_round_trips_valid_png()
    {
        var root = TempRoot();
        try
        {
            var valid = Png(12, 8, Colors.Red);
            var ihdrOnly = valid[..33];
            var invalidCrc = valid.ToArray();
            invalidCrc[29] ^= 0x01;
            var invalidIhdr = valid.ToArray();
            invalidIhdr[25] = 1;
            RewriteChunkCrc(invalidIhdr, 8);
            var duplicateIhdr = DuplicateIhdrBeforeIdat(valid);
            var emptyIdat = ReplaceIdatData(valid, []);
            var arbitraryIdat = ReplaceIdatData(valid, [1, 2, 3, 4]);

            Assert.Throws<InvalidDataException>(() => PngValidation.Validate(ihdrOnly));
            Assert.Throws<InvalidDataException>(() => PngValidation.Validate(invalidCrc));
            Assert.Throws<InvalidDataException>(() => PngValidation.Validate(invalidIhdr));
            Assert.Throws<InvalidDataException>(() => PngValidation.Validate(duplicateIhdr));
            Assert.Throws<InvalidDataException>(() => PngValidation.Validate(emptyIdat));
            Assert.Throws<InvalidDataException>(() => PngValidation.Validate(arbitraryIdat));

            var repository = new CadSymbolRepository(root);
            Assert.Throws<InvalidDataException>(() => repository.Upsert("INVALID", ihdrOnly, "test"));
            Assert.False(Directory.Exists(Path.Combine(root, "images")));

            var mapping = repository.Upsert("VALID", valid, "test");
            Assert.Equal(new PngDimensions(12, 8), PngValidation.Validate(repository.ReadImage(mapping)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Repository_removes_only_new_unreferenced_image_when_manifest_commit_fails()
    {
        var root = TempRoot();
        try
        {
            var png = Png(12, 8, Colors.Blue);
            var hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            var repository = new CadSymbolRepository(root, () => throw new IOException("forced manifest failure"));

            Assert.Throws<IOException>(() => repository.Upsert("MODEL A", png, "test"));

            var imagePath = Path.Combine(root, "images", $"{hash}.png");
            Assert.False(File.Exists(imagePath));
            Assert.False(File.Exists(Path.Combine(root, "manifest.json")));

            var committed = new CadSymbolRepository(root).Upsert("MODEL B", png, "test");
            Assert.True(File.Exists(imagePath));

            var failingShared = new CadSymbolRepository(root, () => throw new IOException("forced manifest failure"));
            Assert.Throws<IOException>(() => failingShared.Upsert("MODEL C", png, "test"));
            Assert.True(File.Exists(imagePath));
            Assert.Equal(png, new CadSymbolRepository(root).ReadImage(committed));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Repository_registers_models_without_images_and_remove_mapping_keeps_model()
    {
        var root = TempRoot();
        try
        {
            var repository = new CadSymbolRepository(root);
            repository.RegisterModels([
                new CadSymbolModelRegistration("MODEL A"),
                new CadSymbolModelRegistration(" model   a "),
                new CadSymbolModelRegistration("MODEL B")]);
            Assert.Equal(2, repository.GetModels().Count);
            repository.Upsert("MODEL A", Png(20, 20, Colors.Green), "test");
            Assert.True(repository.RemoveMapping("MODEL A"));
            Assert.Null(repository.GetMapping("MODEL A"));
            Assert.Contains(repository.GetModels(), model => model.Key == CadSymbolRepository.ModelKey("MODEL A") && !model.HasImage);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Excel_scan_and_apply_are_exact_multirow_idempotent_bounded_and_preserve_manual_picture()
    {
        var root = TempRoot();
        var path = Path.Combine(root, "schedule.xlsx");
        try
        {
            Directory.CreateDirectory(root);
            CreateStandardSchedule(path,
                "DN068B LED13 840", "dn068b  led13/840", "DN068B LED13 865");
            AddPicture(path, "manual-user-picture", 3, 11, Png(4, 4, Colors.Black));
            var service = new CadSymbolExcelService();
            var scan = service.ScanModels(path);
            Assert.Equal(2, scan.Models.Count);
            Assert.Equal(2, scan.Models.Single(model => model.Key == CadSymbolRepository.ModelKey("DN068B LED13 840")).UsageCount);

            var first = service.ApplyMappings(path, [Mapping("DN068B LED13 840", Png(60, 30, Colors.Red))]);
            var second = service.ApplyMappings(path, [Mapping("dn068b led13 840", Png(60, 30, Colors.Red))]);
            Assert.Equal(2, first.UpdatedRows);
            Assert.Equal(0, second.UpdatedRows);
            using var workbook = new XLWorkbook(path);
            var sheet = workbook.Worksheet("Lighting Schedule");
            Assert.Equal(2, sheet.Pictures.Count(picture => picture.Name.StartsWith(CadSymbolExcelService.ManagedPicturePrefix, StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(sheet.Pictures, picture => picture.Name == "manual-user-picture");
            Assert.DoesNotContain(sheet.Pictures, picture => picture.TopLeftCell.Address.RowNumber == 5 && picture.Name.StartsWith(CadSymbolExcelService.ManagedPicturePrefix, StringComparison.OrdinalIgnoreCase));
            Assert.All(sheet.Pictures.Where(picture => picture.Name.StartsWith(CadSymbolExcelService.ManagedPicturePrefix, StringComparison.OrdinalIgnoreCase)), picture =>
            {
                var rowPixels = sheet.Row(picture.TopLeftCell.Address.RowNumber).Height * 96d / 72d;
                var columnPixels = sheet.Column(7).Width * 7d + 5d;
                Assert.True(picture.Width <= columnPixels);
                Assert.True(picture.Height <= rowPixels);
            });
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Excel_merged_report_sections_are_excluded_while_continuations_custom_rows_and_vertical_models_are_retained()
    {
        var root = TempRoot();
        var path = Path.Combine(root, "merged-reports.xlsx");
        try
        {
            Directory.CreateDirectory(root);
            CreateMergedReportsSchedule(path);
            var service = new CadSymbolExcelService();

            var scan = service.ScanModels(path);

            Assert.Equal(5, scan.Models.Count);
            Assert.Equal(5, scan.Models.Sum(model => model.UsageCount));
            Assert.Contains(scan.Models, model => model.DisplayModel == "SECOND LIGHT");
            Assert.Equal(1, scan.Models.Single(model => model.DisplayModel == "VERTICAL LIGHT").UsageCount);
            Assert.DoesNotContain(scan.Models, model => model.DisplayModel is "A1-1" or "B1-1" or "CUSTOM BANNER");

            var result = service.ApplyMappings(path, scan.Models.Select(model => Mapping(model.DisplayModel, Png(20, 10, Colors.Red))));

            Assert.Equal(5, result.UpdatedRows);
            using var workbook = new XLWorkbook(path);
            var sheet = workbook.Worksheet("Lighting Schedule");
            var managed = sheet.Pictures.Where(picture => picture.Name.StartsWith(CadSymbolExcelService.ManagedPicturePrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.Equal(5, managed.Length);
            Assert.DoesNotContain(managed, picture => picture.TopLeftCell.Address.RowNumber is 3 or 7 or 10);
            Assert.Contains(managed, picture => picture.TopLeftCell.Address.RowNumber == 4);
            Assert.Contains(managed, picture => picture.TopLeftCell.Address.RowNumber == 5);
            Assert.Contains(managed, picture => picture.TopLeftCell.Address.RowNumber == 8);
            Assert.Contains(managed, picture => picture.TopLeftCell.Address.RowNumber == 9);
            Assert.Contains(managed, picture => picture.TopLeftCell.Address.RowNumber == 11);

            var appliedScan = service.ScanModels(path);
            Assert.Equal(5, appliedScan.Models.Count);
            Assert.Equal(5, appliedScan.Models.Sum(model => model.UsageCount));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Excel_apply_snapshots_caller_png_before_mapping_enumeration_can_mutate_it()
    {
        var root = TempRoot();
        var path = Path.Combine(root, "snapshot.xlsx");
        try
        {
            Directory.CreateDirectory(root);
            CreateStandardSchedule(path, "MODEL A");
            var original = Png(24, 12, Colors.Red);
            var callerBuffer = original.ToArray();
            var service = new CadSymbolExcelService();

            IEnumerable<CadSymbolExcelMapping> MutatingMappings()
            {
                yield return Mapping("MODEL A", callerBuffer);
                callerBuffer[0] = 0;
            }

            var first = service.ApplyMappings(path, MutatingMappings());
            var second = service.ApplyMappings(path, [Mapping("MODEL A", original)]);

            Assert.Equal(1, first.UpdatedRows);
            Assert.Equal(0, second.UpdatedRows);
            Assert.Equal(0, callerBuffer[0]);
            using var workbook = new XLWorkbook(path);
            Assert.Single(
                workbook.Worksheet("Lighting Schedule").Pictures,
                picture => picture.Name.StartsWith(
                    CadSymbolExcelService.ManagedPicturePrefix,
                    StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Existing_workbook_selection_scans_without_mutation_then_manual_apply_is_idempotent()
    {
        var root = TempRoot();
        var path = Path.Combine(root, "old-schedule.xlsx");
        try
        {
            Directory.CreateDirectory(root);
            CreateStandardSchedule(path, "MODEL A", " model  a ", "MODEL B");
            var before = SHA256.HashData(File.ReadAllBytes(path));
            var service = new CadSymbolExcelService();

            var scan = service.ScanTargetWorkbook(path);

            Assert.Equal(2, scan.Models.Count);
            Assert.Equal(2, scan.Models.Single(model => model.Key == CadSymbolRepository.ModelKey("MODEL A")).UsageCount);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));

            var first = service.ApplyMappings(path, [Mapping("MODEL A", Png(24, 12, Colors.Red))]);
            var afterFirst = SHA256.HashData(File.ReadAllBytes(path));
            var second = service.ApplyMappings(path, [Mapping("MODEL A", Png(24, 12, Colors.Red))]);

            Assert.Equal(2, first.UpdatedRows);
            Assert.Equal(0, second.UpdatedRows);
            Assert.Equal(afterFirst, SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Existing_workbook_selection_reports_each_missing_required_cad_header_without_mutation()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var missingSymbol = Path.Combine(root, "missing-symbol.xlsx");
            CreateCustomForm(missingSymbol, includeSymbol: false, mergeSymbol: false);
            var missingModel = Path.Combine(root, "missing-model.xlsx");
            CreateHeaderDiagnosticWorkbook(missingModel, includeModel: false, includeSymbol: true);
            var missingBoth = Path.Combine(root, "missing-both.xlsx");
            CreateHeaderDiagnosticWorkbook(missingBoth, includeModel: false, includeSymbol: false);
            var service = new CadSymbolExcelService();

            AssertMissingHeader(service, missingSymbol, "Symbol in DWG");
            AssertMissingHeader(service, missingModel, "Proposed calculation model");
            AssertMissingHeader(service, missingBoth, "Proposed calculation model", "Symbol in DWG");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Existing_workbook_selection_rejects_excel_lock_files()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "~$schedule.xlsx");
            File.WriteAllBytes(path, [1, 2, 3]);

            var exception = Assert.Throws<InvalidDataException>(() => new CadSymbolExcelService().ScanTargetWorkbook(path));

            Assert.Contains("tạm/khóa", exception.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Excel_replacement_and_explicit_removal_touch_only_managed_symbol_picture()
    {
        var root = TempRoot();
        var path = Path.Combine(root, "schedule.xlsx");
        try
        {
            Directory.CreateDirectory(root);
            CreateStandardSchedule(path, "MODEL A");
            AddPicture(path, "manual-symbol", 3, 7, Png(4, 4, Colors.Black));
            var service = new CadSymbolExcelService();
            service.ApplyMappings(path, [Mapping("MODEL A", Png(30, 20, Colors.Red))]);
            service.ApplyMappings(path, [Mapping("MODEL A", Png(20, 30, Colors.Blue))]);
            using (var replaced = new XLWorkbook(path))
            {
                var pictures = replaced.Worksheet("Lighting Schedule").Pictures;
                Assert.Single(pictures, picture => picture.Name.StartsWith(CadSymbolExcelService.ManagedPicturePrefix, StringComparison.OrdinalIgnoreCase));
                Assert.Contains(pictures, picture => picture.Name == "manual-symbol");
            }
            service.ApplyMappings(path, [], [CadSymbolRepository.ModelKey("MODEL A")]);
            using var removed = new XLWorkbook(path);
            Assert.DoesNotContain(removed.Worksheet("Lighting Schedule").Pictures, picture => picture.Name.StartsWith(CadSymbolExcelService.ManagedPicturePrefix, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(removed.Worksheet("Lighting Schedule").Pictures, picture => picture.Name == "manual-symbol");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Excel_custom_symbol_column_missing_symbol_and_merged_conflict_are_safe()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var custom = Path.Combine(root, "custom.xlsx");
            CreateCustomForm(custom, includeSymbol: true, mergeSymbol: false);
            var service = new CadSymbolExcelService();
            service.ApplyMappings(custom, [Mapping("MODEL A", Png(20, 10, Colors.Red))]);
            using (var workbook = new XLWorkbook(custom))
                Assert.Contains(workbook.Worksheet("Custom").Pictures, picture => picture.TopLeftCell.Address.ColumnNumber == 4);

            var missing = Path.Combine(root, "missing.xlsx");
            CreateCustomForm(missing, includeSymbol: false, mergeSymbol: false);
            var skipped = service.ApplyMappings(missing, [Mapping("MODEL A", Png(20, 10, Colors.Red))]);
            Assert.Equal(2, skipped.Skipped);
            Assert.Contains(skipped.Diagnostics, diagnostic => diagnostic.Contains("không có cột Symbol", StringComparison.Ordinal));

            var merged = Path.Combine(root, "merged.xlsx");
            CreateCustomForm(merged, includeSymbol: true, mergeSymbol: true);
            var conflict = service.ApplyMappings(merged, [
                Mapping("MODEL A", Png(20, 10, Colors.Red)),
                Mapping("MODEL B", Png(20, 10, Colors.Blue))]);
            Assert.Equal(1, conflict.Conflicts);
            using var conflicted = new XLWorkbook(merged);
            Assert.Empty(conflicted.Worksheet("Custom").Pictures);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Image_import_canonicalizes_png_jpeg_bmp_and_uses_single_file_drop_first()
    {
        var png = Png(13, 7, Colors.Transparent);
        var jpeg = Encoded(13, 7, new JpegBitmapEncoder());
        var bmp = Encoded(13, 7, new BmpBitmapEncoder());
        foreach (var bytes in new[] { png, jpeg, bmp })
        {
            using var stream = new MemoryStream(bytes);
            var canonical = CadSymbolImageImport.DecodeAndEncode(stream, "synthetic");
            Assert.Equal(13, canonical.PixelWidth);
            Assert.Equal(7, canonical.PixelHeight);
            Assert.Equal(new PngDimensions(13, 7), PngValidation.Validate(canonical.PngBytes));
        }

        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var file = Path.Combine(root, "symbol.png");
            File.WriteAllBytes(file, png);
            var fake = new FakeClipboard([file], BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8));
            var imported = CadSymbolImageImport.FromClipboard(fake);
            Assert.Equal(13, imported.PixelWidth);
            Assert.StartsWith("file:", imported.Source);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CreateDirectorySymbolicLinkOrSkip(string linkPath, string targetPath) =>
        Directory.CreateSymbolicLink(linkPath, targetPath);

    private static void CreateFileSymbolicLinkOrSkip(string linkPath, string targetPath) =>
        File.CreateSymbolicLink(linkPath, targetPath);

    private static CadSymbolExcelMapping Mapping(string model, byte[] png) => new(CadSymbolRepository.ModelKey(model), model, png);

    private static void CreateStandardSchedule(string path, params string[] models)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Lighting Schedule");
        sheet.Cell("A1").Value = "No.";
        sheet.Cell("B1").Value = "AREA";
        sheet.Cell("G1").Value = "Symbol in DWG";
        sheet.Cell("H1").Value = "Proposed calculation model";
        sheet.Cell("K1").Value = "Quantity";
        sheet.Cell("L1").Value = "Power (W)";
        var modelColumn = HeaderColumn(sheet, "Proposed calculation model");
        var quantityColumn = HeaderColumn(sheet, "Quantity");
        var powerColumn = HeaderColumn(sheet, "Power (W)");
        for (var index = 0; index < models.Length; index++)
        {
            var row = index + 3;
            sheet.Cell(row, 1).Value = index + 1;
            sheet.Cell(row, 2).Value = $"Room {index + 1}";
            sheet.Cell(row, modelColumn).Value = models[index];
            sheet.Cell(row, quantityColumn).Value = 1;
            sheet.Cell(row, powerColumn).Value = 10;
        }
        workbook.SaveAs(path);
    }

    private static void CreateMergedReportsSchedule(string path)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Lighting Schedule");
        sheet.Cell("A1").Value = "No.";
        sheet.Cell("B1").Value = "AREA";
        sheet.Cell("G1").Value = "Symbol in DWG";
        sheet.Cell("H1").Value = "Proposed calculation model";
        sheet.Cell("K1").Value = "Quantity";
        sheet.Cell("L1").Value = "Power (W)";

        sheet.Range("B3:P3").Merge();
        sheet.Cell("B3").Value = "A1-1";
        WriteScheduleRow(sheet, 4, 1, "Room A", "FIRST LIGHT");
        WriteScheduleRow(sheet, 5, null, null, "SECOND LIGHT");
        sheet.Range("A4:A5").Merge();
        sheet.Range("B4:B5").Merge();

        sheet.Range("B7:P7").Merge();
        sheet.Cell("B7").Value = "B1-1";
        WriteScheduleRow(sheet, 8, 2, "Room B", "CONTINUATION LIGHT");
        WriteScheduleRow(sheet, 9, null, null, "MODEL ONLY CUSTOM", includeMeasurements: false);
        sheet.Range("A8:A9").Merge();
        sheet.Range("B8:B9").Merge();

        sheet.Range("B10:L10").Merge();
        sheet.Cell("B10").Value = "CUSTOM BANNER";
        WriteScheduleRow(sheet, 11, 3, "Room C", "VERTICAL LIGHT");
        var modelColumn = HeaderColumn(sheet, "Proposed calculation model");
        sheet.Range(11, modelColumn, 12, modelColumn).Merge();
        sheet.Cell(11, modelColumn).Value = "VERTICAL LIGHT";
        workbook.SaveAs(path);
    }

    private static void WriteScheduleRow(IXLWorksheet sheet, int row, int? number, string? area, string model, bool includeMeasurements = true)
    {
        if (number.HasValue) sheet.Cell(row, 1).Value = number.Value;
        if (area is not null) sheet.Cell(row, 2).Value = area;
        sheet.Cell(row, HeaderColumn(sheet, "Proposed calculation model")).Value = model;
        if (!includeMeasurements) return;
        sheet.Cell(row, HeaderColumn(sheet, "Quantity")).Value = 1;
        sheet.Cell(row, HeaderColumn(sheet, "Power (W)")).Value = 10;
    }

    private static int HeaderColumn(IXLWorksheet sheet, string header) =>
        sheet.Row(1).CellsUsed().Single(cell => cell.GetString().Equals(header, StringComparison.Ordinal)).Address.ColumnNumber;

    private static void CreateCustomForm(string path, bool includeSymbol, bool mergeSymbol)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Custom");
        sheet.Cell("A1").Value = "No.";
        sheet.Cell("B1").Value = "AREA";
        if (includeSymbol) sheet.Cell("D1").Value = "Symbol in DWG";
        sheet.Cell("F1").Value = "Proposed calculation model";
        sheet.Cell("H1").Value = "Quantity";
        sheet.Cell("I1").Value = "Power (W)";
        foreach (var row in new[] { 3, 4 })
        {
            sheet.Cell(row, 1).Value = row - 2;
            sheet.Cell(row, 2).Value = $"Room {row}";
            sheet.Cell(row, 6).Value = row == 3 ? "MODEL A" : "MODEL B";
            sheet.Cell(row, 8).Value = 1;
            sheet.Cell(row, 9).Value = 10;
        }
        if (includeSymbol && mergeSymbol) sheet.Range("D3:D4").Merge();
        workbook.SaveAs(path);
    }

    private static void CreateHeaderDiagnosticWorkbook(string path, bool includeModel, bool includeSymbol)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Headers");
        sheet.Cell("A1").Value = "No.";
        sheet.Cell("B1").Value = "AREA";
        if (includeSymbol) sheet.Cell("C1").Value = "Symbol in DWG";
        if (includeModel) sheet.Cell("D1").Value = "Proposed calculation model";
        sheet.Cell("E1").Value = "Quantity";
        sheet.Cell("F1").Value = "Power (W)";
        workbook.SaveAs(path);
    }

    private static void AssertMissingHeader(CadSymbolExcelService service, string path, params string[] expectedHeaders)
    {
        var before = SHA256.HashData(File.ReadAllBytes(path));

        var exception = Assert.Throws<InvalidDataException>(() => service.ScanTargetWorkbook(path));

        foreach (var header in expectedHeaders) Assert.Contains(header, exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static void AddPicture(string path, string name, int row, int column, byte[] png)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheets.First();
        using var stream = new MemoryStream(png, false);
        sheet.AddPicture(stream, ClosedXML.Excel.Drawings.XLPictureFormat.Png, name).MoveTo(sheet.Cell(row, column));
        workbook.Save();
    }

    private static byte[] DuplicateIhdrBeforeIdat(byte[] png)
    {
        var firstChunkLength = 12 + (int)ReadBigEndianUInt32(png.AsSpan(8, 4));
        var duplicate = png.AsSpan(8, firstChunkLength).ToArray();
        var result = new byte[png.Length + duplicate.Length];
        Array.Copy(png, 0, result, 0, 8 + firstChunkLength);
        Array.Copy(duplicate, 0, result, 8 + firstChunkLength, duplicate.Length);
        Array.Copy(png, 8 + firstChunkLength, result, 8 + firstChunkLength + duplicate.Length, png.Length - 8 - firstChunkLength);
        return result;
    }

    private static byte[] ReplaceIdatData(byte[] png, byte[] replacement)
    {
        var offset = 8;
        while (offset < png.Length)
        {
            var length = checked((int)ReadBigEndianUInt32(png.AsSpan(offset, 4)));
            if (png.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8))
            {
                var originalChunkLength = 12 + length;
                var result = new byte[png.Length - originalChunkLength + 12 + replacement.Length];
                Array.Copy(png, 0, result, 0, offset);
                WriteBigEndianUInt32(result.AsSpan(offset, 4), checked((uint)replacement.Length));
                "IDAT"u8.CopyTo(result.AsSpan(offset + 4, 4));
                replacement.CopyTo(result.AsSpan(offset + 8));
                RewriteChunkCrc(result, offset);
                Array.Copy(
                    png,
                    offset + originalChunkLength,
                    result,
                    offset + 12 + replacement.Length,
                    png.Length - offset - originalChunkLength);
                return result;
            }
            offset += 12 + length;
        }
        throw new InvalidDataException("PNG fixture does not contain IDAT.");
    }

    private static void RewriteChunkCrc(byte[] png, int chunkOffset)
    {
        var length = checked((int)ReadBigEndianUInt32(png.AsSpan(chunkOffset, 4)));
        var crc = CalculatePngCrc(png.AsSpan(chunkOffset + 4, 4 + length));
        WriteBigEndianUInt32(png.AsSpan(chunkOffset + 8 + length, 4), crc);
    }

    private static uint CalculatePngCrc(ReadOnlySpan<byte> value)
    {
        var crc = uint.MaxValue;
        foreach (var item in value)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) == 0 ? crc >> 1 : 0xedb88320u ^ (crc >> 1);
        }
        return ~crc;
    }

    private static void WriteBigEndianUInt32(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static uint ReadBigEndianUInt32(ReadOnlySpan<byte> value) =>
        ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];

    private static byte[] Png(int width, int height, Color color)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = color.B;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.R;
            pixels[index + 3] = color.A;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static byte[] Encoded(int width, int height, BitmapEncoder encoder)
    {
        var stride = width * 4;
        var pixels = Enumerable.Repeat((byte)200, stride * height).ToArray();
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"DocManager-CadSymbol-{Guid.NewGuid():N}");

    private sealed class FakeClipboard(IReadOnlyList<string> files, BitmapSource? image) : ICadSymbolClipboard
    {
        public IReadOnlyList<string> GetFileDropList() => files;
        public BitmapSource? GetImage() => image;
    }
}
