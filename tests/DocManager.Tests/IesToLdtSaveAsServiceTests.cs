using System.Globalization;
using System.Text;
using DocManager.Photometry;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DocManager.Tests;

public sealed class IesToLdtSaveAsServiceTests
{
    [Fact]
    public void Already_cancelled_request_fails_before_source_io()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new IesToLdtSaveAsService().SaveAs(
            new("missing.ies", "target.ldt"),
            cancellation.Token));
    }

    [Fact]
    public void Relative_ies_without_pdf_honors_exact_save_as_and_reports_fallbacks()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "Fixture.ies");
            var target = Path.Combine(root, "manual-name.ldt");
            File.WriteAllText(source, PhotometryFixture.Ies(), Encoding.UTF8);

            var result = new IesToLdtSaveAsService().SaveAs(new(source, target));
            var lines = PhotometryFixture.ParseLdt(File.ReadAllText(target)).Lines;

            Assert.Equal(Path.GetFullPath(target), result.Document.Path);
            Assert.Equal(Path.GetFullPath(target), result.Conversion.OutputPath);
            Assert.Equal("Fixture", lines[8]);
            Assert.Equal(string.Empty, lines[9]);
            Assert.Equal("Fixture", lines[10]);
            Assert.Contains(result.Warnings, warning => warning.Code == IesToLdtWarningCode.NoCataloguePdf);
            Assert.Contains(result.Warnings, warning => warning.Code == IesToLdtWarningCode.CatalogueFluxFallback);
            Assert.Contains(result.Warnings, warning => warning.Code == IesToLdtWarningCode.CataloguePowerFallback);
            Assert.Contains(result.Warnings, warning => warning.Code == IesToLdtWarningCode.CatalogueCctFallback);
            Assert.Contains(result.Warnings, warning => warning.Code == IesToLdtWarningCode.CatalogueCriFallback);
            Assert.Contains(result.Warnings, warning => warning.Code == IesToLdtWarningCode.DestinationNameMismatch);
            Assert.DoesNotContain("_auto", Path.GetFileName(target), StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Batch_and_save_as_no_catalogue_fallbacks_have_matching_warnings_and_provenance()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "Fixture.ies");
            var batchDirectory = Path.Combine(root, "batch");
            var target = Path.Combine(root, "Fixture.ldt");
            File.WriteAllText(source, PhotometryFixture.Ies(), new UTF8Encoding(false));

            var batch = new LdtConverter().ConvertFileWithoutCatalogue(source, batchDirectory, "Fixture");
            var saveAs = new IesToLdtSaveAsService().SaveAs(new(source, target));

            Assert.Equal(batch.Provenance, saveAs.Conversion.Provenance);
            Assert.Equal(batch.Warnings, saveAs.Warnings
                .Where(warning => warning.Code is not IesToLdtWarningCode.DestinationNameMismatch)
                .Select(warning => warning.Message)
                .ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Prepared_snapshot_matches_regular_conversion_and_remains_authoritative_after_source_changes()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "Prepared.ies");
            var preparedTarget = Path.Combine(root, "prepared.ldt");
            var regularTarget = Path.Combine(root, "regular.ldt");
            var original = PhotometryFixture.Ies(lumensPerLamp: 1000);
            File.WriteAllText(source, original, new UTF8Encoding(false));
            var snapshot = new IesSourceReader().ReadFile(source);

            var service = new IesToLdtSaveAsService();
            var prepared = service.SaveAs(snapshot, new(source, preparedTarget));
            File.WriteAllText(source, PhotometryFixture.Ies(lumensPerLamp: 2000), new UTF8Encoding(false));
            var preparedAfterChange = service.SaveAs(snapshot, new(source, regularTarget));

            Assert.Equal(prepared.Document.RawText, preparedAfterChange.Document.RawText);
            Assert.Equal("1000", File.ReadAllLines(preparedTarget)[28]);
            Assert.Equal("1000", File.ReadAllLines(regularTarget)[28]);
            Assert.Equal(snapshot.Fingerprint, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(snapshot.OriginalBytes)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Prepared_snapshot_path_mismatch_fails_without_mutating_destination()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "Prepared.ies");
            var otherSource = Path.Combine(root, "Other.ies");
            var target = Path.Combine(root, "target.ldt");
            File.WriteAllText(source, PhotometryFixture.Ies(), new UTF8Encoding(false));
            File.WriteAllText(otherSource, PhotometryFixture.Ies(), new UTF8Encoding(false));
            File.WriteAllText(target, "untouched", new UTF8Encoding(false));
            var snapshot = new IesSourceReader().ReadFile(source);

            Assert.Throws<InvalidDataException>(() => new IesToLdtSaveAsService().SaveAs(
                snapshot,
                new(otherSource, target, Overwrite: true)));

            Assert.Equal("untouched", File.ReadAllText(target));
            Assert.False(File.Exists(target + ".bak"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Technical_pdf_overrides_flux_power_cct_and_cri()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "Fixture.ies");
            var pdf = Path.Combine(root, "Fixture.pdf");
            var target = Path.Combine(root, "Fixture.ldt");
            File.WriteAllText(source, PhotometryFixture.Ies(lumensPerLamp: 1000), Encoding.UTF8);
            WriteTechnicalPdf(pdf, 2345, 27.5, 90, 4000);

            var result = new IesToLdtSaveAsService().SaveAs(new(source, target, pdf));
            var lines = File.ReadAllLines(target);

            Assert.Equal(["2345", "4000", "90", "27.5"], lines[28..32]);
            Assert.Equal(2345, result.Conversion.FluxLumens);
            Assert.Equal(27.5, result.Conversion.PowerWatts);
            Assert.DoesNotContain(result.Warnings, warning => warning.Code is
                IesToLdtWarningCode.CatalogueFluxFallback or
                IesToLdtWarningCode.CataloguePowerFallback or
                IesToLdtWarningCode.CatalogueCctFallback or
                IesToLdtWarningCode.CatalogueCriFallback);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Absolute_without_pdf_fails_before_creating_or_mutating_target()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "Absolute.ies");
            var target = Path.Combine(root, "Absolute.ldt");
            File.WriteAllText(source, PhotometryFixture.Ies(lumensPerLamp: -1), Encoding.UTF8);

            Assert.Throws<InvalidDataException>(() => new IesToLdtSaveAsService().SaveAs(new(source, target)));
            Assert.False(File.Exists(target));

            File.WriteAllText(target, "untouched", Encoding.UTF8);
            Assert.Throws<InvalidDataException>(() => new IesToLdtSaveAsService().SaveAs(new(source, target, Overwrite: true)));
            Assert.Equal("untouched", File.ReadAllText(target));
            Assert.False(File.Exists(target + ".bak"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_bad_pdf_fails_without_target(bool createBadPdf)
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "Fixture.ies");
            var pdf = Path.Combine(root, "missing.pdf");
            var target = Path.Combine(root, "Fixture.ldt");
            File.WriteAllText(source, PhotometryFixture.Ies(), Encoding.UTF8);
            if (createBadPdf) File.WriteAllText(pdf, "not a PDF", Encoding.UTF8);

            Assert.ThrowsAny<Exception>(() => new IesToLdtSaveAsService().SaveAs(new(source, target, pdf)));
            Assert.False(File.Exists(target));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Product_sku_and_slash_normalize_lines_9_and_11_with_blank_line_10()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source.ies");
            var target = Path.Combine(root, "selected.ldt");
            File.WriteAllText(source, PhotometryFixture.Ies(), Encoding.UTF8);

            var result = new IesToLdtSaveAsService().SaveAs(new(source, target, ProductName: "911401862587 - DN068B/840"));
            var lines = File.ReadAllLines(target);

            Assert.Equal("DN068B 840", lines[8]);
            Assert.Equal(string.Empty, lines[9]);
            Assert.Equal("DN068B 840", lines[10]);
            Assert.Equal("DN068B 840", result.Conversion.CanonicalProductName);
            Assert.Contains(result.Warnings, warning => warning.Code == IesToLdtWarningCode.ProductNameNormalized);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validation_failure_leaves_destination_unchanged_and_cleans_temporary_file(bool targetExisted)
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "Fixture.ies");
            var target = Path.Combine(root, "Fixture.ldt");
            File.WriteAllText(source, PhotometryFixture.Ies(), Encoding.UTF8);
            if (targetExisted) File.WriteAllText(target, "untouched", Encoding.UTF8);
            var service = new IesToLdtSaveAsService(_ => throw new InvalidDataException("Injected validation failure."));

            Assert.Throws<InvalidDataException>(() => service.SaveAs(new(source, target, Overwrite: targetExisted)));

            Assert.Equal(targetExisted, File.Exists(target));
            if (targetExisted) Assert.Equal("untouched", File.ReadAllText(target));
            Assert.False(File.Exists(target + ".bak"));
            Assert.Empty(Directory.EnumerateFiles(root, ".Fixture.ldt.*.part"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Target_created_before_promotion_is_overwritten_with_backup()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "Fixture.ies");
            var target = Path.Combine(root, "Fixture.ldt");
            File.WriteAllText(source, PhotometryFixture.Ies(), Encoding.UTF8);
            var service = new IesToLdtSaveAsService(LdtDocument.Load, path => File.WriteAllText(path, "concurrent", Encoding.UTF8));

            var result = service.SaveAs(new(source, target, Overwrite: true));

            Assert.Equal("concurrent", File.ReadAllText(target + ".bak"));
            Assert.Equal(File.ReadAllText(target), result.Document.RawText);
            Assert.Contains(result.Warnings, warning => warning.Code == IesToLdtWarningCode.Overwritten);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Validation_failure_removes_new_empty_destination_directory()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "Fixture.ies");
            var directory = Path.Combine(root, "new-output");
            var target = Path.Combine(directory, "Fixture.ldt");
            File.WriteAllText(source, PhotometryFixture.Ies(), Encoding.UTF8);
            var service = new IesToLdtSaveAsService(_ => throw new InvalidDataException("Injected validation failure."));

            Assert.Throws<InvalidDataException>(() => service.SaveAs(new(source, target)));

            Assert.False(Directory.Exists(directory));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Existing_target_fails_closed_or_overwrites_with_backup_and_loaded_result()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "Fixture.ies");
            var target = Path.Combine(root, "manual.ldt");
            File.WriteAllText(source, PhotometryFixture.Ies(), Encoding.UTF8);
            File.WriteAllText(target, "old", Encoding.UTF8);

            Assert.Throws<IOException>(() => new IesToLdtSaveAsService().SaveAs(new(source, target)));
            Assert.Equal("old", File.ReadAllText(target));
            Assert.False(File.Exists(target + ".bak"));

            var result = new IesToLdtSaveAsService().SaveAs(new(source, target, Overwrite: true));

            Assert.Equal("old", File.ReadAllText(target + ".bak"));
            Assert.Equal(File.ReadAllText(target), result.Document.RawText);
            Assert.Equal(Path.GetFullPath(target), result.Document.Path);
            Assert.Contains(result.Warnings, warning => warning.Code == IesToLdtWarningCode.Overwritten);
            Assert.Contains(result.Warnings, warning => warning.Code == IesToLdtWarningCode.DestinationNameMismatch);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Pdf_resolver_accepts_canonical_and_unique_legacy_and_rejects_ambiguity_or_outside_root()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        var outside = PhotometryFixture.TemporaryDirectory();
        try
        {
            var family = Path.Combine(root, "Family");
            var iesDirectory = Path.Combine(family, "IES");
            Directory.CreateDirectory(iesDirectory);
            var ies = Path.Combine(iesDirectory, "Fixture.ies");
            File.WriteAllText(ies, "fixture");
            var exact = Path.Combine(family, "Fixture.pdf");
            File.WriteAllText(exact, "pdf");

            var canonical = IesCataloguePdfResolver.Resolve(ies, "Fixture", allowedRoot: root);
            Assert.Equal(IesCataloguePdfResolutionStatus.Found, canonical.Status);
            Assert.Equal(Path.GetFullPath(exact), canonical.PdfPath);
            Assert.False(canonical.IsLegacy);

            File.Delete(exact);
            var legacy = Path.Combine(family, "911401 - Fixture.pdf");
            File.WriteAllText(legacy, "pdf");
            var legacyResult = IesCataloguePdfResolver.Resolve(ies, "Fixture", allowedRoot: root);
            Assert.Equal(IesCataloguePdfResolutionStatus.Found, legacyResult.Status);
            Assert.True(legacyResult.IsLegacy);

            File.WriteAllText(Path.Combine(family, "999999 - Fixture.pdf"), "pdf");
            Assert.Equal(IesCataloguePdfResolutionStatus.Ambiguous, IesCataloguePdfResolver.Resolve(ies, "Fixture", allowedRoot: root).Status);

            var outsidePdf = Path.Combine(outside, "Fixture.pdf");
            File.WriteAllText(outsidePdf, "pdf");
            Assert.Equal(IesCataloguePdfResolutionStatus.UnsafePath, IesCataloguePdfResolver.Resolve(ies, "Fixture", outsidePdf, root).Status);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [RequiresSymbolicLinkFact]
    public void Pdf_resolver_rejects_reparse_source_family_and_pdf_when_supported()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        var outside = PhotometryFixture.TemporaryDirectory();
        try
        {
            var family = Path.Combine(root, "Family");
            var iesDirectory = Path.Combine(family, "IES");
            Directory.CreateDirectory(iesDirectory);
            var ies = Path.Combine(iesDirectory, "Fixture.ies");
            File.WriteAllText(ies, "fixture");
            var outsideFamily = Path.Combine(outside, "Family");
            var outsideIesDirectory = Path.Combine(outsideFamily, "IES");
            Directory.CreateDirectory(outsideIesDirectory);
            var outsideIes = Path.Combine(outsideIesDirectory, "Fixture.ies");
            File.WriteAllText(outsideIes, "fixture");
            var linkedFamily = Path.Combine(root, "LinkedFamily");
            CreateDirectorySymbolicLinkOrSkip(linkedFamily, outsideFamily);
            var linkedIes = Path.Combine(linkedFamily, "IES", "Fixture.ies");
            Assert.Equal(IesCataloguePdfResolutionStatus.UnsafePath, IesCataloguePdfResolver.Resolve(linkedIes, "Fixture", allowedRoot: root).Status);
            Directory.Delete(linkedFamily);

            var outsidePdf = Path.Combine(outside, "Fixture.pdf");
            File.WriteAllText(outsidePdf, "pdf");
            var linkedPdf = Path.Combine(family, "Fixture.pdf");
            CreateFileSymbolicLinkOrSkip(linkedPdf, outsidePdf);

            Assert.Equal(IesCataloguePdfResolutionStatus.UnsafePath, IesCataloguePdfResolver.Resolve(ies, "Fixture", linkedPdf, root).Status);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Drop_route_accepts_ldt_ies_case_insensitively_and_preserves_optional_pdf()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var ldt = CreateDropFile(root, "Fixture.LdT");
            var ies = CreateDropFile(root, "Fixture.iEs");
            var pdf = CreateDropFile(root, "Fixture.PdF");

            var ldtRoute = PhotometryDropRoute.Resolve([ldt]);
            Assert.Equal(PhotometryDropKind.Ldt, ldtRoute.Kind);
            Assert.Equal(Path.GetFullPath(ldt), ldtRoute.DocumentPath);

            var iesRoute = PhotometryDropRoute.Resolve([ies]);
            Assert.Equal(PhotometryDropKind.Ies, iesRoute.Kind);
            Assert.Null(iesRoute.CataloguePdfPath);

            var pairedRoute = PhotometryDropRoute.Resolve([pdf, ies]);
            Assert.Equal(PhotometryDropKind.Ies, pairedRoute.Kind);
            Assert.Equal(Path.GetFullPath(ies), pairedRoute.DocumentPath);
            Assert.Equal(Path.GetFullPath(pdf), pairedRoute.CataloguePdfPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Drop_route_rejects_invalid_extension_and_ambiguous_photometry_documents()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var text = CreateDropFile(root, "Fixture.txt");
            var firstLdt = CreateDropFile(root, "First.ldt");
            var secondLdt = CreateDropFile(root, "Second.LDT");
            var firstIes = CreateDropFile(root, "First.ies");
            var secondIes = CreateDropFile(root, "Second.IES");

            Assert.Equal(PhotometryDropKind.Invalid, PhotometryDropRoute.Resolve([text]).Kind);
            Assert.Equal(PhotometryDropKind.Invalid, PhotometryDropRoute.Resolve([firstLdt, secondLdt]).Kind);
            Assert.Equal(PhotometryDropKind.Invalid, PhotometryDropRoute.Resolve([firstIes, secondIes]).Kind);
            Assert.Equal(PhotometryDropKind.Invalid, PhotometryDropRoute.Resolve([firstLdt, firstIes]).Kind);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Ldt_editor_wires_one_preview_drop_handler_for_the_whole_window_route()
    {
        var root = RepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("<TabItem x:Name=\"LdtEditorTab\" Header=\"LDT Editor\" AllowDrop=\"True\" PreviewDragOver=\"LdtEditor_DragOver\" PreviewDrop=\"LdtEditor_Drop\">", xaml);
        Assert.Contains("<Grid x:Name=\"LdtEditorRoot\" Background=\"Transparent\">", xaml);
        Assert.Contains("<TextBlock x:Name=\"LdtEditorPlaceholder\"", xaml);
        Assert.Contains("Kéo một file .ldt, hoặc một file .ies kèm tối đa một file PDF", xaml);
        Assert.DoesNotContain("LdtRawBox_Drop", xaml);
        Assert.Contains("private void LdtEditor_DragOver", codeBehind);
        Assert.Contains("private async void LdtEditor_Drop", codeBehind);
        Assert.Contains("System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None", codeBehind);
        Assert.Contains("e.Handled = true;", codeBehind);
    }

    [Fact]
    public void Desktop_removes_fuzzy_and_implements_changes()
    {
        var root = RepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml.cs"));
        var signifyModels = File.ReadAllText(Path.Combine(root, "src", "DocManager.Signify", "SignifyModels.cs"));

        // Cleanup A: Fuzzy removed
        Assert.DoesNotContain("FuzzyCheck", xaml);
        Assert.DoesNotContain("AllowFuzzy", codeBehind);
        Assert.DoesNotContain("[Obsolete]", signifyModels);
        Assert.DoesNotContain("AllowFuzzy", signifyModels);

        // Change 2: Settings persistence
        Assert.Contains("CodesBox.Text = _userSettings.PendingCodes;", codeBehind);
        Assert.Contains("MergeCheck.IsChecked = _userSettings.MergeReports;", codeBehind);
        Assert.Contains("AutoUpdateCadSymbolsCheck.IsChecked", codeBehind);
        Assert.Contains("DownloadIesCheck.IsChecked", codeBehind);
        Assert.Contains("PendingCodes = CodesBox.Text,", codeBehind);
        Assert.Contains("MergeReports = MergeCheck.IsChecked == true,", codeBehind);
        Assert.Contains("AutoUpdateCadSymbols = AutoUpdateCadSymbolsCheck.IsChecked == true,", codeBehind);
        Assert.Contains("DownloadIes = DownloadIesCheck.IsChecked == true", codeBehind);

        // Change 3: Options() wires Force and DownloadIes
        Assert.Contains("Force = ForceCheck.IsChecked == true", codeBehind);
        Assert.Contains("DownloadIes = DownloadIesCheck.IsChecked == true,", codeBehind);

        // Change 4: Drag-and-drop hints
        Assert.Contains("PdfListPlaceholder", xaml);
        Assert.Contains("Kéo file PDF vào đây, hoặc bấm Chọn PDF", xaml);
        Assert.Contains("x:Name=\"DownloadIesCheck\"", xaml);
        Assert.Contains("x:Name=\"ForceCheck\"", xaml);

        // Change 5: Results drawer
        Assert.Contains("x:Name=\"DownloadResultsDrawer\"", xaml);
        Assert.Contains("x:Name=\"DownloadResultsGrid\"", xaml);
        Assert.Contains("ShowDownloadResultsDrawer_Click", codeBehind);
        Assert.Contains("CloseDownloadResultsDrawer", codeBehind);
        Assert.Contains("IsReadOnly=\"True\"", xaml);
        Assert.Contains("PopulateDownloadResults", codeBehind);

        // Change 1: Async pricelist
        Assert.Contains("LoadPricelistAsync", codeBehind);
        Assert.Contains("Task.Run", codeBehind);
        Assert.Contains("await LoadPricelistAsync()", codeBehind);
    }


    private static void CreateDirectorySymbolicLinkOrSkip(string linkPath, string targetPath) =>
        Directory.CreateSymbolicLink(linkPath, targetPath);

    private static void CreateFileSymbolicLinkOrSkip(string linkPath, string targetPath) =>
        File.CreateSymbolicLink(linkPath, targetPath);

    private static string CreateDropFile(string root, string name)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, "fixture");
        return path;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DocManager.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Không tìm thấy thư mục mã nguồn DocManager.");
    }

    private static void WriteTechnicalPdf(string path, double flux, double power, int cri, int cct)
    {
        using var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(600, 800);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText($"Luminous flux: {flux.ToString(CultureInfo.InvariantCulture)} lm", 10, new PdfPoint(40, 720), font);
        page.AddText($"Input power: {power.ToString(CultureInfo.InvariantCulture)} W", 10, new PdfPoint(40, 700), font);
        page.AddText($"CRI: {cri}", 10, new PdfPoint(40, 680), font);
        page.AddText($"CCT: {cct} K", 10, new PdfPoint(40, 660), font);
        File.WriteAllBytes(path, builder.Build());
    }
}
