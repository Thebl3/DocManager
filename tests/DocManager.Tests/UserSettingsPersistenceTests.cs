using DocManager.Core;
using DocManager.Desktop;

namespace DocManager.Tests;

public sealed class UserSettingsPersistenceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), "DocManager.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Valid_paths_roundtrip_and_output_remains_a_directory()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var catalogue = CreateDirectory("Catalogue");
        var output = CreateDirectory("Output");
        var cadExcel = CreateDirectory("CadExcel");
        var pdfInput = CreateDirectory("PdfInput");
        var ldt = CreateDirectory("Ldt");
        var ies = CreateDirectory("Ies");
        var pdfCatalogue = CreateDirectory("PdfCatalogue");
        var pricelist = CreateFile("Input", "prices.xlsx");
        var form = CreateFile("Input", "form.xlsx");
        var store = new UserSettingsStore(baseDirectory, localAppData);

        store.Save(new UserSettingsData
        {
            CatalogueRoot = catalogue,
            PricelistPath = pricelist,
            LastExcelOutputDirectory = output,
            LastCadExcelDirectory = cadExcel,
            ExistingFormExcelPath = form,
            LastPdfInputDirectory = pdfInput,
            LastLdtDirectory = ldt,
            LastIesDirectory = ies,
            LastPdfCatalogueDirectory = pdfCatalogue
        });

        var restored = store.LoadResolved();
        Assert.Equal(Path.GetFullPath(catalogue), restored.CatalogueRoot);
        Assert.Equal(Path.GetFullPath(pricelist), restored.PricelistPath);
        Assert.Equal(Path.GetFullPath(output), restored.LastExcelOutputDirectory);
        Assert.Equal(Path.GetFullPath(cadExcel), restored.LastCadExcelDirectory);
        Assert.Equal(Path.GetFullPath(form), restored.ExistingFormExcelPath);
        Assert.Equal(Path.GetFullPath(pdfInput), restored.LastPdfInputDirectory);
        Assert.Equal(Path.GetFullPath(ldt), restored.LastLdtDirectory);
        Assert.Equal(Path.GetFullPath(ies), restored.LastIesDirectory);
        Assert.Equal(Path.GetFullPath(pdfCatalogue), restored.LastPdfCatalogueDirectory);
        Assert.DoesNotContain("Lighting Schedule.xlsx", File.ReadAllText(store.Path));
    }

    [Fact]
    public void Corrupt_json_silently_uses_portable_defaults()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var store = new UserSettingsStore(baseDirectory, localAppData);
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, "{ invalid json");

        var restored = store.LoadResolved();

        Assert.Equal(Path.Combine(Path.GetFullPath(baseDirectory), "Product Family"), restored.CatalogueRoot);
        Assert.Equal(string.Empty, restored.PricelistPath);
    }

    [Fact]
    public void Missing_saved_root_after_move_falls_back_to_new_adjacent_product_family()
    {
        var oldRoot = CreateDirectory("OldMachine", "Product Family");
        var oldBase = CreateDirectory("OldMachine", "Released");
        var localAppData = CreateDirectory("LocalAppData");
        new UserSettingsStore(oldBase, localAppData).Save(new UserSettingsData { CatalogueRoot = oldRoot });
        Directory.Delete(oldRoot, true);
        var newBase = CreateDirectory("NewMachine", "Released");
        var adjacent = Path.Combine(newBase, "Product Family");

        var restored = new UserSettingsStore(newBase, localAppData).LoadResolved();

        Assert.Equal(Path.GetFullPath(adjacent), restored.CatalogueRoot);
        Assert.False(Directory.Exists(adjacent));
    }

    [Fact]
    public void Missing_file_paths_are_cleared_and_valid_paths_are_restored()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var validPricelist = CreateFile("Input", "Prof Pricelist custom.xlsx");
        var validForm = CreateFile("Input", "form.xlsx");
        var store = new UserSettingsStore(baseDirectory, localAppData);

        store.Save(new UserSettingsData { PricelistPath = validPricelist, ExistingFormExcelPath = validForm });
        var valid = store.LoadResolved();
        Assert.Equal(Path.GetFullPath(validPricelist), valid.PricelistPath);
        Assert.Equal(Path.GetFullPath(validForm), valid.ExistingFormExcelPath);

        File.Delete(validPricelist);
        File.Delete(validForm);
        var missing = store.LoadResolved();
        Assert.Equal(string.Empty, missing.PricelistPath);
        Assert.Equal(string.Empty, missing.ExistingFormExcelPath);
    }

    [Fact]
    public void Missing_saved_cad_excel_directory_is_cleared_without_persisting_an_active_workbook()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var cadDirectory = CreateDirectory("CadExcel");
        var store = new UserSettingsStore(baseDirectory, localAppData);
        store.Save(new UserSettingsData { LastCadExcelDirectory = cadDirectory });

        Directory.Delete(cadDirectory, true);
        var restored = store.LoadResolved();

        Assert.Equal(string.Empty, restored.LastCadExcelDirectory);
        var json = File.ReadAllText(store.Path);
        Assert.Contains("LastCadExcelDirectory", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveCad", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Atomic_save_overwrites_existing_json_without_part_files()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var first = CreateDirectory("FirstCatalogue");
        var second = CreateDirectory("SecondCatalogue");
        var store = new UserSettingsStore(baseDirectory, localAppData);

        store.Save(new UserSettingsData { CatalogueRoot = first });
        store.Save(new UserSettingsData { CatalogueRoot = second });

        Assert.Equal(Path.GetFullPath(second), store.LoadResolved().CatalogueRoot);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(store.Path)!, "*.part"));
    }

    [Fact]
    public void Pricelist_discovery_prefers_canonical_name_and_ignores_unrelated_workbooks()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        CreateFileInDirectory(baseDirectory, "Unrelated.xlsx");
        CreateFileInDirectory(baseDirectory, "Prof Pricelist Z.xlsx");
        var canonical = CreateFileInDirectory(baseDirectory, UserSettingsStore.CanonicalPricelistFileName);

        var restored = new UserSettingsStore(baseDirectory, localAppData).LoadResolved();

        Assert.Equal(Path.GetFullPath(canonical), restored.PricelistPath);
    }

    [Fact]
    public void Pricelist_discovery_finds_canonical_file_in_grouped_product_excel_directory()
    {
        var baseDirectory = CreateDirectory("App");
        var localAppData = CreateDirectory("LocalAppData");
        var groupedDirectory = CreateDirectory("App", "Product excel file");
        var canonical = CreateFileInDirectory(groupedDirectory, UserSettingsStore.CanonicalPricelistFileName);

        var restored = new UserSettingsStore(baseDirectory, localAppData).LoadResolved();

        Assert.Equal(Path.GetFullPath(canonical), restored.PricelistPath);
    }

    [Fact]
    public void Pricelist_discovery_prefers_exact_grouped_file_before_direct_root_wildcard()
    {
        var baseDirectory = CreateDirectory("App");
        var localAppData = CreateDirectory("LocalAppData");
        var groupedDirectory = CreateDirectory("App", "Product excel file");
        var groupedCanonical = CreateFileInDirectory(groupedDirectory, UserSettingsStore.CanonicalPricelistFileName);
        CreateFileInDirectory(baseDirectory, "Prof Pricelist older.xlsx");

        var restored = new UserSettingsStore(baseDirectory, localAppData).LoadResolved();

        Assert.Equal(Path.GetFullPath(groupedCanonical), restored.PricelistPath);
    }

    [Fact]
    public void Pricelist_discovery_finds_wildcard_file_in_grouped_product_excel_directory()
    {
        var baseDirectory = CreateDirectory("App");
        var localAppData = CreateDirectory("LocalAppData");
        var groupedDirectory = CreateDirectory("App", "Product excel file");
        var groupedPricelist = CreateFileInDirectory(groupedDirectory, "Prof Pricelist custom.xlsx");

        var restored = new UserSettingsStore(baseDirectory, localAppData).LoadResolved();

        Assert.Equal(Path.GetFullPath(groupedPricelist), restored.PricelistPath);
    }

    [Fact]
    public void Pricelist_discovery_keeps_direct_root_canonical_support()
    {
        var baseDirectory = CreateDirectory("App");
        var localAppData = CreateDirectory("LocalAppData");
        var directCanonical = CreateFileInDirectory(baseDirectory, UserSettingsStore.CanonicalPricelistFileName);

        var restored = new UserSettingsStore(baseDirectory, localAppData).LoadResolved();

        Assert.Equal(Path.GetFullPath(directCanonical), restored.PricelistPath);
    }

    [Fact]
    public void Valid_saved_pricelist_still_wins_over_portable_discovery()
    {
        var baseDirectory = CreateDirectory("App");
        var localAppData = CreateDirectory("LocalAppData");
        var groupedDirectory = CreateDirectory("App", "Product excel file");
        CreateFileInDirectory(groupedDirectory, UserSettingsStore.CanonicalPricelistFileName);
        var savedPricelist = CreateFile("Selected", "chosen.xlsx");
        var store = new UserSettingsStore(baseDirectory, localAppData);
        store.Save(new UserSettingsData { PricelistPath = savedPricelist });

        var restored = store.LoadResolved();

        Assert.Equal(Path.GetFullPath(savedPricelist), restored.PricelistPath);
    }

    [Fact]
    public void New_fields_roundtrip_with_multilevel_text_and_vietnamese_diacritics()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var pendingCodes = "SKU001\r\nĐèn LED Xinh Đẹp\r\nPL-002-VN\r\n";
        var store = new UserSettingsStore(baseDirectory, localAppData);

        store.Save(new UserSettingsData
        {
            PendingCodes = pendingCodes,
            MergeReports = true,
            AutoUpdateCadSymbols = false,
            DownloadIes = false
        });

        var restored = store.LoadResolved();
        Assert.Equal(pendingCodes, restored.PendingCodes);
        Assert.True(restored.MergeReports);
        Assert.False(restored.AutoUpdateCadSymbols);
        Assert.False(restored.DownloadIes);
    }

    [Fact]
    public void Old_json_without_new_fields_loads_with_correct_bool_defaults()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var store = new UserSettingsStore(baseDirectory, localAppData);
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        // Simulate an old settings file with only the original fields
        var oldJson = @"{
  ""CatalogueRoot"": """",
  ""PricelistPath"": """",
  ""LastExcelOutputDirectory"": """",
  ""LastCadExcelDirectory"": """",
  ""ExistingFormExcelPath"": """",
  ""LastPdfInputDirectory"": """",
  ""LastLdtDirectory"": """",
  ""LastIesDirectory"": """",
  ""LastPdfCatalogueDirectory"": """"
}";
        File.WriteAllText(store.Path, oldJson);

        var restored = store.LoadResolved();

        Assert.Equal(string.Empty, restored.PendingCodes);
        Assert.False(restored.MergeReports);
        Assert.True(restored.AutoUpdateCadSymbols);
        Assert.True(restored.DownloadIes);
    }

    [Fact]
    public void Pending_codes_exceeding_100k_chars_is_truncated_on_save()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var store = new UserSettingsStore(baseDirectory, localAppData);
        var oversized = new string('A', 150_000);

        store.Save(new UserSettingsData { PendingCodes = oversized });

        var restored = store.LoadResolved();
        Assert.Equal(100_000, restored.PendingCodes.Length);
        Assert.Equal(new string('A', 100_000), restored.PendingCodes);
    }

    [Fact]
    public void Corrupt_json_loads_new_fields_with_correct_defaults()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var store = new UserSettingsStore(baseDirectory, localAppData);
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, "{ invalid json with no closing brace");

        var restored = store.LoadResolved();

        Assert.Equal(string.Empty, restored.PendingCodes);
        Assert.False(restored.MergeReports);
        Assert.True(restored.AutoUpdateCadSymbols);
        Assert.True(restored.DownloadIes);
    }

    [Fact]
    public void Released_payload_uses_parent_portable_data_layout()
    {
        var appRoot = CreateDirectory("PortableApp");
        var released = CreateDirectory("PortableApp", "Released");
        var productFamily = CreateDirectory("PortableApp", PortableAppPaths.ProductFamilyDirectoryName);
        var productExcel = CreateDirectory("PortableApp", PortableAppPaths.ProductExcelDirectoryName);
        var pricelist = CreateFileInDirectory(productExcel, UserSettingsStore.CanonicalPricelistFileName);
        var localAppData = CreateDirectory("LocalAppData");

        var restored = new UserSettingsStore(released, localAppData).LoadResolved();

        Assert.Equal(Path.GetFullPath(productFamily), restored.CatalogueRoot);
        Assert.Equal(Path.GetFullPath(pricelist), restored.PricelistPath);
        Assert.Equal(Path.GetFullPath(appRoot), PortableAppPaths.ResolveRoot(released));
        Assert.Equal(Path.GetFullPath(appRoot), PortableAppPaths.ResolveRoot(released + Path.DirectorySeparatorChar));
        Assert.Equal(Path.GetFullPath(productFamily), DesktopCataloguePaths.DefaultProductFamilyRoot(released));
        Assert.Equal(Path.GetFullPath(productFamily), DesktopCataloguePaths.DefaultProductFamilyRoot(released + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Released_leaf_without_layout_marker_remains_its_own_root()
    {
        var released = CreateDirectory("Unrelated", "Released");

        var resolved = PortableAppPaths.ResolveRoot(released);

        Assert.Equal(Path.GetFullPath(released), resolved);
    }

    [Theory]
    [InlineData(PortableAppPaths.LauncherFileName)]
    [InlineData(PortableAppPaths.PublishManifestFileName)]
    public void Released_payload_accepts_root_file_layout_markers(string markerName)
    {
        var appRoot = CreateDirectory("MarkerApp", Guid.NewGuid().ToString("N"));
        var released = Path.Combine(appRoot, PortableAppPaths.ReleasedDirectoryName);
        Directory.CreateDirectory(released);
        CreateFileInDirectory(appRoot, markerName);

        Assert.Equal(Path.GetFullPath(appRoot), PortableAppPaths.ResolveRoot(released));
    }

    private string CreateDirectory(params string[] parts)
    {
        var path = parts.Aggregate(_temporaryDirectory, Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateFile(string directory, string fileName) => CreateFileInDirectory(CreateDirectory(directory), fileName);

    private static string CreateFileInDirectory(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "test");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, true);
    }
}
