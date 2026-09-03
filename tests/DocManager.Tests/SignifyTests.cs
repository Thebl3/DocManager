using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DocManager.Core;
using DocManager.Photometry;
using DocManager.Signify;
using UglyToad.PdfPig.Writer;

namespace DocManager.Tests;

public sealed class SignifyTests
{
    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("A<B>:C?.pdf", "A B C .pdf")]
    [InlineData("name. ", "name")]
    public void Windows_filename_sanitization_is_safe(string input, string expected) => Assert.Equal(expected, WindowsFileName.Sanitize(input));

    [Theory]
    [InlineData("  Đèn\tLED/840\r\nPSU  ", "Đèn LED 840 PSU")]
    [InlineData("A/B:*?  C", "A B C")]
    [InlineData("911401504347 - WT198C LED40S/840 PSU L1200", "WT198C LED40S 840 PSU L1200")]
    [InlineData("CON", "_CON")]
    public void Product_asset_basename_uses_only_canonical_product_name(string input, string expected) =>
        Assert.Equal(expected, ProductAssetName.CanonicalBaseName(input));

    [Fact]
    public void Canonical_basename_always_strips_optional_numeric_sku_prefix()
    {
        Assert.Equal("WT198C LED40S 840 PSU L1200", ProductAssetName.CanonicalBaseName("911401504347 - WT198C LED40S/840 PSU L1200"));
    }

    [Fact]
    public void Product_asset_basename_is_bounded_and_never_ends_with_windows_forbidden_punctuation()
    {
        var value = ProductAssetName.CanonicalBaseName(new string('A', 200) + ". ");
        Assert.Equal(ProductAssetName.MaximumBaseNameLength, value.Length);
        Assert.False(value.EndsWith('.') || value.EndsWith(' '));
    }

    [Fact]
    public void Product_asset_basename_replaces_format_invalid_and_control_characters_with_collapsed_spaces()
    {
        const string product = "Đèn\u200d A/B:\u0001*?  C";

        Assert.Equal("Đèn A B C", ProductAssetName.CanonicalBaseName(product));
        Assert.Equal("A B C", ProductAssetName.CanonicalBaseName("A/B:*? C"));
    }

    [Fact]
    public void Product_asset_resolution_prefers_canonical_then_unique_conservative_legacy()
    {
        var root = TempDirectory();
        try
        {
            var canonical = Path.Combine(root, "Đèn LED 840.pdf");
            var legacy = Path.Combine(root, "911401 - Đèn LED 840.pdf");
            File.WriteAllText(legacy, "x");
            Assert.Equal(legacy, ProductAssetName.Resolve([legacy], "Đèn LED/840").Path);
            File.WriteAllText(canonical, "x");
            Assert.Equal(canonical, ProductAssetName.Resolve([legacy, canonical], "Đèn LED/840").Path);
            Assert.Null(ProductAssetName.Resolve([legacy, Path.Combine(root, "999 - Đèn LED 840.pdf")], "Đèn LED/840").Path);
            Assert.True(ProductAssetName.Resolve([legacy, Path.Combine(root, "999 - Đèn LED 840.pdf")], "Đèn LED/840").HasCollision);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Product_asset_resolution_supports_unique_numeric_legacy_sku_fallback()
    {
        var root = TempDirectory();
        try
        {
            var legacy = Path.Combine(root, "911401 - Fixture 840.pdf");
            File.WriteAllText(legacy, "x");

            Assert.Equal(legacy, ProductAssetName.Resolve([legacy], "911401").Path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Product_asset_resolution_rejects_ambiguous_numeric_legacy_sku_fallback()
    {
        var root = TempDirectory();
        try
        {
            var first = Path.Combine(root, "911401 - Fixture 840.pdf");
            var second = Path.Combine(root, "911401 - Fixture 865.pdf");
            File.WriteAllText(first, "x");
            File.WriteAllText(second, "x");

            var resolution = ProductAssetName.Resolve([first, second], "911401");
            Assert.Null(resolution.Path);
            Assert.True(resolution.HasCollision);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Candidate_scoring_prefers_exact_sku_and_description()
    {
        var record = new ProductRecord("S", 2, "Family", "911401", "RC099V G3 LED69 865");
        var exact = new SignifySearchCandidate { Sku = "911401", Name = "RC099V G3 LED69 865", LeafletUrl = "https://example/pdf" };
        var fuzzy = new SignifySearchCandidate { Sku = "other", Name = "RC099V family", LeafletUrl = "https://example/pdf" };
        Assert.True(SignifyScoring.Score(record, exact) > SignifyScoring.Score(record, fuzzy));
        Assert.True(SignifyScoring.IsExact(record, exact));
    }

    [Fact]
    public void Validation_rejects_header_only_html_json_and_accepts_parser_usable_ies()
    {
        Assert.True(DownloadValidation.IsValid("%PDF-1.7"u8, "application/pdf", DownloadKind.Pdf));
        Assert.True(DownloadValidation.IsValid("%PDF-1.7"u8, "text/html", DownloadKind.Pdf));
        Assert.False(DownloadValidation.IsValid("<html>blocked"u8, "application/pdf", DownloadKind.Pdf));
        Assert.True(DownloadValidation.IsValid(ValidIes, "text/plain", DownloadKind.Ies));
        Assert.False(DownloadValidation.IsValid(Encoding.ASCII.GetBytes("IESNA:LM-63-2002\n[TEST] X\nTILT=NONE\n"), "text/plain", DownloadKind.Ies));
        Assert.False(DownloadValidation.IsValid("{\"error\":true}"u8, "application/json", DownloadKind.Ies));
    }

    [Fact]
    public async Task Search_retries_5xx_with_fake_handler()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var client = new SignifyClient(http);
        var options = new DownloadOptions { OutputRoot = Path.GetTempPath(), Retries = 2, RetryBaseDelay = TimeSpan.Zero, PerItemDelay = TimeSpan.Zero };
        var results = await client.SearchAsync("RC099V", options);
        Assert.Empty(results);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Download_uses_part_and_atomic_move_with_fake_handler()
    {
        var payload = Encoding.ASCII.GetBytes("%PDF-1.7\nfixture");
        var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        handler.Responses[0].Content.Headers.ContentType = new("application/pdf");
        using var http = new HttpClient(handler);
        var client = new SignifyClient(http);
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerDownload-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
        var target = Path.Combine(root, "file.pdf");
        try
        {
            var status = await client.DownloadAsync("https://example.test/file", target, DownloadKind.Pdf, new DownloadOptions { OutputRoot = root, RetryBaseDelay = TimeSpan.Zero, PerItemDelay = TimeSpan.Zero });
            Assert.Equal("downloaded", status);
            Assert.Equal(payload, await File.ReadAllBytesAsync(target));
            Assert.Empty(Directory.EnumerateFiles(root, "*.part"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Pricelist_reader_reads_multisheet_numeric_material_and_dedupes_without_loading_drawings()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "fixture.xlsx");
        try
        {
            CreatePricelistFixture(path);
            var reader = new PricelistReader();
            var records = reader.Read(path);
            Assert.Equal(2, records.Count);
            Assert.Contains(records, item => item.Sheet == "Main" && item.ExcelRow == 3 && item.Material == "911401685409" && item.MaterialDescription == "RC099V G3 LED69 865");
            Assert.Contains(records, item => item.Sheet == "Second" && item.Material == "929002295302");
            Assert.Single(reader.Search(records, "RC099V"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Pricelist_reader_probes_real_file_read_only_when_available()
    {
        const string path = @"D:\Claude\Catalouge_Downloader\Prof Pricelist V1.0 2026 effective_Mar2026.xlsx";
        if (!File.Exists(path)) return;
        var before = File.GetLastWriteTimeUtc(path);
        var records = new PricelistReader().Read(path);
        Assert.NotEmpty(records);
        Assert.Contains(records, item => item.Material == "911401685409");
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Resolver_accepts_only_unique_model_prefix_and_autocomplete_replaces_last_query()
    {
        var reader = new PricelistReader();
        var records = new[]
        {
            new ProductRecord("S", 2, "F", "1", "RC099V G3 LED69 865"),
            new ProductRecord("S", 3, "F", "2", "RC132V LED36S 840")
        };
        Assert.Equal("RC099V G3 LED69 865", reader.ResolveCode(records, "RC099V").MaterialDescription);
        Assert.Equal("RC", reader.ResolveCode(records, "RC").MaterialDescription);
        Assert.Equal("old\r\nRC099V G3 LED69 865", SignifyTextInput.ReplaceLastQuery("old\r\nRC0", "RC099V G3 LED69 865"));
    }

    [Theory]
    [InlineData("RC099V G3 LED69 840\r\nRC099V G3 LED69 865", 0, "RC099V G3 LED69 840")]
    [InlineData("RC099V G3 LED69 840\r\nRC099V G3 LED69 865", 10, "RC099V G3 LED69 840")]
    [InlineData("RC099V G3 LED69 840\r\nRC099V G3 LED69 865", 19, "RC099V G3 LED69 840")]
    [InlineData("RC099V G3 LED69 840\r\nRC099V G3 LED69 865", 21, "RC099V G3 LED69 865")]
    [InlineData("RC099V G3 LED69 840\r\nRC099V G3 LED69 865", 40, "RC099V G3 LED69 865")]
    [InlineData("RC099V G3 LED69 840\nRC099V G3 LED69 865", 20, "RC099V G3 LED69 865")]
    [InlineData("RC099V G3 LED69 840\nRC099V G3 LED69 865", 39, "RC099V G3 LED69 865")]
    public void Code_at_caret_returns_the_trimmed_token_on_its_row(string text, int caretIndex, string expected) =>
        Assert.Equal(expected, SignifyTextInput.CodeAtCaret(text, caretIndex));

    [Fact]
    public void Code_at_caret_handles_comma_separated_tokens_and_token_boundaries()
    {
        const string text = "  RC099V G3 LED69 840  ,\tRC099V G3 LED69 865  ";

        Assert.Equal("RC099V G3 LED69 840", SignifyTextInput.CodeAtCaret(text, 22));
        Assert.Equal("RC099V G3 LED69 840", SignifyTextInput.CodeAtCaret(text, 23));
        Assert.Equal("RC099V G3 LED69 865", SignifyTextInput.CodeAtCaret(text, 26));
        Assert.Equal("RC099V G3 LED69 865", SignifyTextInput.CodeAtCaret(text, text.Length));
    }

    [Fact]
    public void Code_at_caret_returns_null_for_blank_lines_and_invalid_indices()
    {
        const string text = "RC099V G3 LED69 840\r\n\r\nRC099V G3 LED69 865";

        Assert.Null(SignifyTextInput.CodeAtCaret(text, 22));
        Assert.Null(SignifyTextInput.CodeAtCaret(text, -1));
        Assert.Null(SignifyTextInput.CodeAtCaret(text, text.Length + 1));
        Assert.Equal(["RC099V G3 LED69 840", "RC099V G3 LED69 865"], DocManager.Desktop.MainWindow.ParseCodes(text));
        Assert.Null(DocManager.Desktop.MainWindow.CodeForProductPdf(text, 22));
        Assert.Equal("RC099V G3 LED69 840", DocManager.Desktop.MainWindow.CodeForProductPdf("  RC099V G3 LED69 840  ", 0));
    }

    [Fact]
    public void Product_codes_parse_direct_and_wrapped_arrays_and_live_paging_metadata()
    {
        using var document = JsonDocument.Parse("""{"results":[{"sku":"a","product_codes":["1"]},{"sku":"b","product_codes":{"value":["2","3"]}}],"meta":{"page":{"current":1,"total_pages":2,"total_results":12}}}""");
        var results = SignifyScoring.ParseResults(document);
        Assert.Equal(new[] { "1" }, results[0].ProductCodes);
        Assert.Equal(new[] { "2", "3" }, results[1].ProductCodes);
        Assert.Equal(2, SignifyScoring.NextPage(document, 1));
    }

    [Fact]
    public void Exact_matching_requires_identity_when_both_descriptions_are_blank()
    {
        var record = new ProductRecord("S", 1, "F", "", "");

        Assert.False(SignifyScoring.IsExact(record, new SignifySearchCandidate()));
        Assert.False(SignifyScoring.IsExact(record, new SignifySearchCandidate { Sku = "unrelated" }));
    }

    [Fact]
    public void Catalogue_product_resolution_is_consistent_for_unique_and_ambiguous_prefixes()
    {
        var root = TempDirectory();
        try
        {
            var unique = new[] { new ProductRecord("S", 1, "Family", "100", "RC099V G3 LED69 840") };
            var ambiguous = new[]
            {
                new ProductRecord("S", 1, "Family", "840", "RC099V G3 LED69 840"),
                new ProductRecord("S", 2, "Family", "865", "RC099V G3 LED69 865")
            };

            Assert.Equal(CatalogueProductResolutionStatus.Found, CatalogueProductResolver.Resolve(unique, "RC099V G3").Status);
            Assert.Equal(CatalogueProductResolutionStatus.Ambiguous, CatalogueProductResolver.Resolve(ambiguous, "RC099V G3").Status);
            Assert.Equal(CataloguePdfResolutionStatus.AmbiguousProduct, new CataloguePdfResolver(ambiguous).Resolve("RC099V G3", root).Status);
            Assert.Equal(CatalogueProductResolutionStatus.Missing, CatalogueProductResolver.Resolve(ambiguous, "8400").Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Search_paginates_and_final_5xx_throws()
    {
        var paging = new SequenceHandler(
            JsonResponse("""{"results":[{"sku":"1"}],"meta":{"page":{"current":1,"total_pages":2,"total_results":2}}}"""),
            JsonResponse("""{"results":[{"sku":"2"}],"meta":{"page":{"current":2,"total_pages":2,"total_results":2}}}"""));
        using (var http = new HttpClient(paging))
        {
            var client = new SignifyClient(http);
            var results = await client.SearchAsync("RC", TestOptions(Path.GetTempPath()) with { MaxSearchPages = 3 });
            Assert.Equal(new[] { "1", "2" }, results.Select(item => item.Sku));
            Assert.Contains("page=2", paging.Requests[1].Query);
        }

        var failing = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var failingHttp = new HttpClient(failing);
        await Assert.ThrowsAsync<HttpRequestException>(() => new SignifyClient(failingHttp).SearchAsync("RC", TestOptions(Path.GetTempPath()) with { Retries = 2 }));
        Assert.Equal(2, failing.Calls);
    }

    [Fact]
    public async Task Batch_malformed_json_logs_error_and_continues_to_next_item()
    {
        var handler = new RoutingHandler(request =>
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            if (query.Contains("BAD", StringComparison.OrdinalIgnoreCase)) return JsonResponse("not-json");
            return JsonResponse("""{"results":[]}""");
        });
        using var http = new HttpClient(handler);
        var service = new CatalogueDownloadService(new SignifyClient(http));
        var records = new[]
        {
            new ProductRecord("S", 1, "F", "", "BAD"),
            new ProductRecord("S", 2, "F", "", "GOOD")
        };
        var streamed = new List<DownloadLogEntry>();
        var logs = await service.DownloadAsync(records, TestOptions(Path.GetTempPath()), logSink: streamed.Add);
        Assert.Equal(2, logs.Count);
        Assert.Equal("download_failed", logs[0].Status);
        Assert.StartsWith("Product download failed; family 'F'; material ''; description 'BAD': ", logs[0].Error, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidDataException), logs[0].Error, StringComparison.Ordinal);
        Assert.Contains("JSON", logs[0].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not-json", logs[0].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("at ", logs[0].Error, StringComparison.Ordinal);
        Assert.Equal("no_leaflet", logs[1].Status);
        Assert.Equal(logs, streamed);
    }

    [Fact]
    public async Task Throwing_csv_sink_does_not_abort_batch()
    {
        var handler = new RoutingHandler(_ => JsonResponse("""{"results":[]}"""));
        using var http = new HttpClient(handler);
        var service = new CatalogueDownloadService(new SignifyClient(http));
        var progressMessages = new List<string>();
        var progress = new InlineProgress<DocManager.Core.OperationProgress>(item => progressMessages.Add(item.Message));
        var logs = await service.DownloadAsync(
            [new ProductRecord("S", 1, "F", "", "ONE"), new ProductRecord("S", 2, "F", "", "TWO")],
            TestOptions(Path.GetTempPath()),
            progress,
            logSink: _ => throw new IOException("disk full"));
        Assert.Equal(2, logs.Count);
        Assert.All(logs, item => Assert.Equal("no_leaflet", item.Status));
        Assert.Contains(progressMessages, message => message.Contains("disk full", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Catalogue_pdf_resolver_opens_product_name_canonical_pdf()
    {
        var root = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "911401", "Fixture 840");
            var pdf = Path.Combine(root, "Family", "Fixture 840.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
            File.WriteAllText(pdf, "pdf");

            var resolution = new CataloguePdfResolver([record]).Resolve("Fixture 840", root);

            Assert.True(resolution.IsFound);
            Assert.Equal(Path.GetFullPath(pdf), resolution.PdfPath);
            Assert.Equal("canonical", resolution.Source);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Catalogue_pdf_resolver_prefers_manifest_pdf_for_numeric_material_name_drift()
    {
        var root = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "911401", "Pricelist name changed");
            const string verifiedName = "API matched name drift";
            var pdf = Path.Combine(root, "Family", verifiedName + ".pdf");
            var manifest = new CanonicalAssetManifest(root);
            var reservation = manifest.Reserve(
                record,
                new ProductMatch { MatchedSku = "911401", MatchedName = verifiedName },
                verifiedName,
                pdf,
                null);
            Assert.NotNull(reservation.Entry);
            Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
            File.WriteAllText(pdf, "pdf");
            manifest.UpdateDownloadedPaths(reservation.Entry!, pdf);

            var resolution = new CataloguePdfResolver([record]).Resolve("911401", root);

            Assert.True(resolution.IsFound);
            Assert.Equal(Path.GetFullPath(pdf), resolution.PdfPath);
            Assert.Equal("manifest", resolution.Source);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Catalogue_pdf_resolver_opens_unique_legacy_prefixed_pdf()
    {
        var root = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "911401", "Fixture 840");
            var pdf = Path.Combine(root, "Family", "911401 - Fixture 840.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
            File.WriteAllText(pdf, "pdf");

            var resolution = new CataloguePdfResolver([record]).Resolve("911401", root);

            Assert.True(resolution.IsFound);
            Assert.Equal(Path.GetFullPath(pdf), resolution.PdfPath);
            Assert.Equal("legacy-sku", resolution.Source);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Catalogue_pdf_resolver_refuses_legacy_variant_ambiguity()
    {
        var root = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "911401", "Fixture 840");
            var family = Path.Combine(root, "Family");
            Directory.CreateDirectory(family);
            File.WriteAllText(Path.Combine(family, "911401 - Fixture 840.pdf"), "pdf");
            File.WriteAllText(Path.Combine(family, "911401 - Fixture 865.pdf"), "pdf");

            var resolution = new CataloguePdfResolver([record]).Resolve("911401", root);

            Assert.Equal(CataloguePdfResolutionStatus.AmbiguousPdf, resolution.Status);
            Assert.Null(resolution.PdfPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Catalogue_pdf_resolver_keeps_840_and_865_exact_when_caret_supplies_each_variant()
    {
        var root = TempDirectory();
        try
        {
            var records = new[]
            {
                new ProductRecord("S", 1, "Family", "840", "RC099V G3 LED69 840"),
                new ProductRecord("S", 2, "Family", "865", "RC099V G3 LED69 865")
            };
            var family = Path.Combine(root, "Family");
            Directory.CreateDirectory(family);
            var pdf840 = Path.Combine(family, "RC099V G3 LED69 840.pdf");
            var pdf865 = Path.Combine(family, "RC099V G3 LED69 865.pdf");
            File.WriteAllText(pdf840, "pdf");
            File.WriteAllText(pdf865, "pdf");
            const string text = "RC099V G3 LED69 840\r\nRC099V G3 LED69 865";

            var code840 = DocManager.Desktop.MainWindow.CodeForProductPdf(text, 10);
            var code865 = DocManager.Desktop.MainWindow.CodeForProductPdf(text, text.Length);
            var resolver = new CataloguePdfResolver(records);

            Assert.Equal("RC099V G3 LED69 840", code840);
            Assert.Equal("RC099V G3 LED69 865", code865);
            Assert.Equal(Path.GetFullPath(pdf840), resolver.Resolve(code840, root).PdfPath);
            Assert.Equal(Path.GetFullPath(pdf865), resolver.Resolve(code865, root).PdfPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Catalogue_pdf_resolver_rejects_manifest_path_outside_root()
    {
        var root = TempDirectory();
        var outside = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "911401", "Fixture 840");
            var manifestPath = Path.Combine(root, CanonicalAssetManifest.FileName);
            File.WriteAllText(manifestPath, $$"""{"version":1,"entries":[{"material":"911401","matchedSku":"911401","matchedName":"Fixture 840","productFamily":"Family","canonicalBaseName":"Fixture 840","pdfPath":"{{Path.Combine(outside, "outside.pdf").Replace("\\", "\\\\")}}","iesPath":"","ldtPath":""}]}""");

            var resolution = new CataloguePdfResolver([record]).Resolve("911401", root);

            Assert.Equal(CataloguePdfResolutionStatus.InvalidManifest, resolution.Status);
            Assert.Null(resolution.PdfPath);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Product_aware_file_matching_accepts_canonical_and_unique_legacy_but_not_partial_names()
    {
        Assert.True(SignifyTextInput.FileNameMatchesCode(@"C:\Family\123 - Fixture.pdf", "123"));
        Assert.False(SignifyTextInput.FileNameMatchesCode(@"C:\Other\ABC 123 Fixture.pdf", "123"));
        Assert.True(SignifyTextInput.FileNameMatchesCode(@"C:\Family\999 - RC099V G3 LED69 865.pdf", "ignored", "RC099V G3 LED69 865"));
        Assert.True(SignifyTextInput.FileNameMatchesCode(@"C:\Family\RC099V G3 LED69 865.ies", "ignored", "RC099V G3 LED69 865"));
        Assert.False(SignifyTextInput.FileNameMatchesCode(@"C:\Family\XRC099V G3 LED69 865 Extra.pdf", "ignored", "RC099V G3 LED69 865"));
    }

    [Fact]
    public void Ldt_export_prefers_manifest_and_skips_duplicate_codes_and_sources()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family A", "100", "Pricelist name drift");
            const string canonical = "Verified Fixture 840";
            var family = Path.Combine(root, "Family A");
            var pdf = Path.Combine(family, canonical + ".pdf");
            var ldt = Path.Combine(family, "LDT", canonical + ".ldt");
            var manifest = new CanonicalAssetManifest(root);
            var reservation = manifest.Reserve(
                record,
                new ProductMatch { MatchedSku = "100", MatchedName = canonical },
                canonical,
                pdf,
                null);
            Assert.NotNull(reservation.Entry);
            Directory.CreateDirectory(Path.GetDirectoryName(ldt)!);
            File.WriteAllText(ldt, "manifest-owned");
            manifest.UpdateDownloadedPaths(reservation.Entry!, ldtPath: ldt);

            var result = new CatalogueLdtExportService().Export(
                ["100", "100", "Pricelist name drift"],
                [record],
                root,
                destinationParent);

            Assert.Equal(1, result.Copied);
            Assert.Equal(2, result.Skipped);
            Assert.Equal(0, result.Missing);
            Assert.Equal(0, result.Errors);
            Assert.Equal("manifest-owned", File.ReadAllText(Path.Combine(destinationParent, "File đèn", canonical + ".ldt")));
            Assert.Contains(result.Items, item => item.Code == "100" && item.Status == CatalogueLdtExportStatus.Copied && item.Message.Contains("manifest", StringComparison.Ordinal));
            Assert.Contains(result.Items, item => item.Code == "Pricelist name drift" && item.Status == CatalogueLdtExportStatus.Skipped && item.Message.Contains("file nguồn", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Ldt_export_reports_missing_and_refuses_near_variant()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "100", "Fixture 840");
            var ldtDirectory = Path.Combine(root, "Family", "LDT");
            Directory.CreateDirectory(ldtDirectory);
            File.WriteAllText(Path.Combine(ldtDirectory, "Fixture 865.ldt"), "wrong variant");

            var result = new CatalogueLdtExportService().Export(
                ["100", "unknown"],
                [record],
                root,
                destinationParent);

            Assert.Equal(0, result.Copied);
            Assert.Equal(2, result.Missing);
            Assert.Equal(0, result.Errors);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(destinationParent, "File đèn")));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Ldt_export_legacy_numeric_prefix_requires_exact_material()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "100", "Fixture 840");
            var ldtDirectory = Path.Combine(root, "Family", "LDT");
            Directory.CreateDirectory(ldtDirectory);
            File.WriteAllText(Path.Combine(ldtDirectory, "999 - Fixture 840.ldt"), "wrong sku");

            var wrongSku = new CatalogueLdtExportService().Export(["100"], [record], root, destinationParent);

            Assert.Equal(1, wrongSku.Missing);
            Assert.False(File.Exists(Path.Combine(destinationParent, "File đèn", "999 - Fixture 840.ldt")));

            File.WriteAllText(Path.Combine(ldtDirectory, "100 - Fixture 840.ldt"), "correct sku");
            var correctSku = new CatalogueLdtExportService().Export(["100"], [record], root, destinationParent);

            Assert.Equal(1, correctSku.Copied);
            Assert.Equal("correct sku", File.ReadAllText(Path.Combine(destinationParent, "File đèn", "100 - Fixture 840.ldt")));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Desktop_code_parser_preserves_duplicates_only_when_requested()
    {
        const string codes = "100, 100\r\n200,100";

        Assert.Equal(["100", "200"], DocManager.Desktop.MainWindow.ParseCodes(codes));
        Assert.Equal(["100", "100", "200", "100"], DocManager.Desktop.MainWindow.ParseCodes(codes, preserveDuplicates: true));
    }

    [Fact]
    public void Desktop_export_buttons_use_one_duplicate_preserving_orchestrator()
    {
        var root = RepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("<Button Content=\"Xuất file PDF\"", xaml);
        Assert.Contains("Click=\"ExportPdfFiles_Click\"", xaml);
        Assert.Contains("private async void ExportPdfFiles_Click", codeBehind);
        Assert.Contains("private async void ExportLdtFiles_Click", codeBehind);
        Assert.Contains("ExportCatalogueFiles", codeBehind);
        Assert.Contains("var codes = RawCodes();", codeBehind);
        Assert.Contains("new CataloguePdfExportService().Export(codes, records, root, destinationParent, token)", codeBehind);
        Assert.Contains("new CatalogueLdtExportService().Export(codes, records, root, destinationParent, token)", codeBehind);
    }

    [Fact]
    public void Ldt_export_allows_unique_prefix_with_duplicate_pricelist_rows()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "100", "RC099V G3 LED69 840");
            var source = Path.Combine(root, "Family", "LDT", "RC099V G3 LED69 840.ldt");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "840");

            var result = new CatalogueLdtExportService().Export(
                ["RC099V G3"],
                [record, record with { Sheet = "Duplicate", ExcelRow = 2 }],
                root,
                destinationParent);

            Assert.Equal(1, result.Copied);
            Assert.Equal("840", File.ReadAllText(Path.Combine(destinationParent, "File đèn", "RC099V G3 LED69 840.ldt")));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Pdf_export_allows_the_same_unique_description_prefix_policy_as_ldt_export()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "100", "RC099V G3 LED69 840");
            var source = Path.Combine(root, "Family", "RC099V G3 LED69 840.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "840");

            var result = new CataloguePdfExportService().Export(["RC099V G3"], [record], root, destinationParent);

            Assert.Equal(1, result.Copied);
            Assert.Equal("840", File.ReadAllText(Path.Combine(destinationParent, "Catalouge đèn", "RC099V G3 LED69 840.pdf")));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Ldt_export_keeps_840_and_865_separate_for_exact_numeric_materials()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var records = new[]
            {
                new ProductRecord("S", 1, "Family", "840", "RC099V G3 LED69 840"),
                new ProductRecord("S", 2, "Family", "865", "RC099V G3 LED69 865")
            };
            var directory = Path.Combine(root, "Family", "LDT");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "RC099V G3 LED69 840.ldt"), "840");
            File.WriteAllText(Path.Combine(directory, "RC099V G3 LED69 865.ldt"), "865");

            var result = new CatalogueLdtExportService().Export(["840", "865"], records, root, destinationParent);

            Assert.Equal(2, result.Copied);
            Assert.Equal("840", File.ReadAllText(Path.Combine(destinationParent, "File đèn", "RC099V G3 LED69 840.ldt")));
            Assert.Equal("865", File.ReadAllText(Path.Combine(destinationParent, "File đèn", "RC099V G3 LED69 865.ldt")));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Ldt_export_rejects_ambiguous_prefix()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var result = new CatalogueLdtExportService().Export(
                ["RC099V G3"],
                [
                    new ProductRecord("S", 1, "Family", "840", "RC099V G3 LED69 840"),
                    new ProductRecord("S", 2, "Family", "865", "RC099V G3 LED69 865")
                ],
                root,
                destinationParent);

            Assert.Equal(1, result.Errors);
            Assert.Contains("nhiều sản phẩm", result.Items[0].Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Ldt_export_reports_destination_collision_and_never_overwrites()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var first = new ProductRecord("S", 1, "Family A", "100", "Shared Fixture");
            var second = new ProductRecord("S", 2, "Family B", "200", "Shared Fixture");
            var firstLdt = Path.Combine(root, "Family A", "LDT", "Shared Fixture.ldt");
            var secondLdt = Path.Combine(root, "Family B", "LDT", "Shared Fixture.ldt");
            Directory.CreateDirectory(Path.GetDirectoryName(firstLdt)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondLdt)!);
            File.WriteAllText(firstLdt, "first source");
            File.WriteAllText(secondLdt, "second source");

            var result = new CatalogueLdtExportService().Export(
                ["100", "200"],
                [first, second],
                root,
                destinationParent);

            Assert.Equal(0, result.Copied);
            Assert.Equal(2, result.Errors);
            Assert.All(result.Items, item => Assert.Contains("Trùng tên file đích", item.Message, StringComparison.Ordinal));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(destinationParent, "File đèn")));

            var destination = Path.Combine(destinationParent, "File đèn", "Shared Fixture.ldt");
            File.WriteAllText(destination, "user destination");
            var noOverwrite = new CatalogueLdtExportService().Export(["100"], [first], root, destinationParent);

            Assert.Equal(1, noOverwrite.Errors);
            Assert.Equal("user destination", File.ReadAllText(destination));
            Assert.Contains("khác nội dung", noOverwrite.Items[0].Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Ldt_export_skips_existing_identical_destination()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "100", "Fixture 840");
            var source = Path.Combine(root, "Family", "LDT", "Fixture 840.ldt");
            var destinationDirectory = Path.Combine(destinationParent, "File đèn");
            var destination = Path.Combine(destinationDirectory, "Fixture 840.ldt");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(destinationDirectory);
            File.WriteAllText(source, "same content");
            File.WriteAllText(destination, "same content");

            var result = new CatalogueLdtExportService().Export(["100"], [record], root, destinationParent);

            Assert.Equal(0, result.Copied);
            Assert.Equal(1, result.Skipped);
            Assert.Contains("cùng nội dung", result.Items[0].Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Ldt_export_reuses_identical_sources_that_share_a_destination_name()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var first = new ProductRecord("S", 1, "Family A", "100", "Shared Fixture");
            var second = new ProductRecord("S", 2, "Family B", "200", "Shared Fixture");
            var firstLdt = Path.Combine(root, "Family A", "LDT", "Shared Fixture.ldt");
            var secondLdt = Path.Combine(root, "Family B", "LDT", "Shared Fixture.ldt");
            Directory.CreateDirectory(Path.GetDirectoryName(firstLdt)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondLdt)!);
            File.WriteAllText(firstLdt, "identical source");
            File.WriteAllText(secondLdt, "identical source");

            var result = new CatalogueLdtExportService().Export(
                ["100", "200"],
                [first, second],
                root,
                destinationParent);

            Assert.Equal(1, result.Copied);
            Assert.Equal(1, result.Skipped);
            Assert.Equal(0, result.Errors);
            Assert.Contains(result.Items, item =>
                item.Status == CatalogueLdtExportStatus.Skipped &&
                item.Message.Contains("SHA-256", StringComparison.Ordinal));
            Assert.Equal(
                "identical source",
                File.ReadAllText(Path.Combine(destinationParent, "File đèn", "Shared Fixture.ldt")));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Pdf_export_prefers_manifest_and_preserves_per_code_duplicate_statuses()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "100", "Pricelist name drift");
            const string canonical = "Verified Fixture 840";
            var pdf = Path.Combine(root, "Family", canonical + ".pdf");
            var manifest = new CanonicalAssetManifest(root);
            var reservation = manifest.Reserve(
                record,
                new ProductMatch { MatchedSku = "100", MatchedName = canonical },
                canonical,
                pdf,
                null);
            Assert.NotNull(reservation.Entry);
            Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
            File.WriteAllText(pdf, "manifest-owned");
            manifest.UpdateDownloadedPaths(reservation.Entry!, pdfPath: pdf);

            var result = new CataloguePdfExportService().Export(
                ["100", "100", "Pricelist name drift"],
                [record],
                root,
                destinationParent);

            Assert.Equal(CataloguePdfExportService.DestinationDirectoryName, Path.GetFileName(result.DestinationDirectory));
            Assert.Equal("Catalouge đèn", Path.GetFileName(result.DestinationDirectory));
            Assert.Equal(1, result.Copied);
            Assert.Equal(2, result.Skipped);
            Assert.Equal(0, result.Missing);
            Assert.Equal(0, result.Errors);
            Assert.Equal("manifest-owned", File.ReadAllText(Path.Combine(destinationParent, "Catalouge đèn", canonical + ".pdf")));
            Assert.Contains(result.Items, item => item.Code == "100" && item.Status == CataloguePdfExportStatus.Copied && item.Message.Contains("manifest", StringComparison.Ordinal));
            Assert.Contains(result.Items, item => item.Code == "Pricelist name drift" && item.Status == CataloguePdfExportStatus.Skipped && item.Message.Contains("file nguồn", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Pdf_export_keeps_840_and_865_separate_and_rejects_wrong_numeric_legacy_sku()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var records = new[]
            {
                new ProductRecord("S", 1, "Family", "840", "RC099V G3 LED69 840"),
                new ProductRecord("S", 2, "Family", "865", "RC099V G3 LED69 865"),
                new ProductRecord("S", 3, "Other", "100", "Fixture 840")
            };
            var family = Path.Combine(root, "Family");
            var other = Path.Combine(root, "Other");
            Directory.CreateDirectory(family);
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(family, "RC099V G3 LED69 840.pdf"), "840");
            File.WriteAllText(Path.Combine(family, "RC099V G3 LED69 865.pdf"), "865");
            File.WriteAllText(Path.Combine(other, "999 - Fixture 840.pdf"), "wrong sku");

            var result = new CataloguePdfExportService().Export(
                ["RC099V G3 LED69 840", "RC099V G3 LED69 865", "100"],
                records,
                root,
                destinationParent);

            Assert.Equal(2, result.Copied);
            Assert.Equal(1, result.Missing);
            Assert.Equal("840", File.ReadAllText(Path.Combine(destinationParent, "Catalouge đèn", "RC099V G3 LED69 840.pdf")));
            Assert.Equal("865", File.ReadAllText(Path.Combine(destinationParent, "Catalouge đèn", "RC099V G3 LED69 865.pdf")));
            Assert.False(File.Exists(Path.Combine(destinationParent, "Catalouge đèn", "999 - Fixture 840.pdf")));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Pdf_export_continues_after_missing_and_never_overwrites_destination_collisions()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var first = new ProductRecord("S", 1, "Family A", "100", "Shared Fixture");
            var second = new ProductRecord("S", 2, "Family B", "200", "Second Fixture");
            var firstPdf = Path.Combine(root, "Family A", "Shared Fixture.pdf");
            var secondPdf = Path.Combine(root, "Family B", "Second Fixture.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(firstPdf)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondPdf)!);
            File.WriteAllText(firstPdf, "first source");
            File.WriteAllText(secondPdf, "second source");

            var initial = new CataloguePdfExportService().Export(["missing", "200"], [first, second], root, destinationParent);

            Assert.Equal(1, initial.Missing);
            Assert.Equal(1, initial.Copied);
            Assert.Equal("second source", File.ReadAllText(Path.Combine(destinationParent, "Catalouge đèn", "Second Fixture.pdf")));

            var destination = Path.Combine(destinationParent, "Catalouge đèn", "Shared Fixture.pdf");
            File.WriteAllText(destination, "first source");
            var sameContent = new CataloguePdfExportService().Export(["100"], [first, second], root, destinationParent);
            Assert.Equal(1, sameContent.Skipped);
            Assert.Contains("cùng nội dung", sameContent.Items[0].Message, StringComparison.Ordinal);

            File.WriteAllText(destination, "user destination");
            var collision = new CataloguePdfExportService().Export(["100"], [first, second], root, destinationParent);
            Assert.Equal(1, collision.Errors);
            Assert.Contains("khác nội dung", collision.Items[0].Message, StringComparison.Ordinal);
            Assert.Equal("user destination", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Pdf_export_reuses_identical_sources_that_share_a_destination_name()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        try
        {
            var first = new ProductRecord("S", 1, "Family A", "100", "Shared Fixture");
            var second = new ProductRecord("S", 2, "Family B", "200", "Shared Fixture");
            var firstPdf = Path.Combine(root, "Family A", "Shared Fixture.pdf");
            var secondPdf = Path.Combine(root, "Family B", "Shared Fixture.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(firstPdf)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondPdf)!);
            File.WriteAllText(firstPdf, "identical source");
            File.WriteAllText(secondPdf, "identical source");

            var result = new CataloguePdfExportService().Export(
                ["100", "200"],
                [first, second],
                root,
                destinationParent);

            Assert.Equal(1, result.Copied);
            Assert.Equal(1, result.Skipped);
            Assert.Equal(0, result.Errors);
            Assert.Contains(result.Items, item =>
                item.Status == CataloguePdfExportStatus.Skipped &&
                item.Message.Contains("SHA-256", StringComparison.Ordinal));
            Assert.Equal(
                "identical source",
                File.ReadAllText(Path.Combine(destinationParent, "Catalouge đèn", "Shared Fixture.pdf")));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
        }
    }

    [Fact]
    public void Filesystem_safety_contains_paths_below_filesystem_root()
    {
        var filesystemRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;

        Assert.True(FileSystemSafety.IsPathWithinDirectory(filesystemRoot, filesystemRoot));
        Assert.True(FileSystemSafety.IsPathWithinDirectory(Path.Combine(filesystemRoot, "DocManager-root-containment"), filesystemRoot));
    }

    [RequiresSymbolicLinkFact]
    public void Catalogue_pdf_resolver_refuses_reparse_point_family_and_pdf_when_supported()
    {
        var root = TempDirectory();
        var outside = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "100", "Fixture 840");
            var linkedFamily = Path.Combine(root, "Family");
            var outsideFamily = Path.Combine(outside, "Family");
            Directory.CreateDirectory(outsideFamily);
            File.WriteAllText(Path.Combine(outsideFamily, "Fixture 840.pdf"), "outside family");
            CreateDirectorySymbolicLinkOrSkip(linkedFamily, outsideFamily);
            Assert.False(FileSystemSafety.HasNoReparsePointsInExistingPath(linkedFamily));
            Assert.Equal(CataloguePdfResolutionStatus.InvalidRoot, new CataloguePdfResolver([record]).Resolve("100", root).Status);
            Directory.Delete(linkedFamily);

            Directory.CreateDirectory(linkedFamily);
            var outsidePdf = Path.Combine(outside, "Fixture 840.pdf");
            File.WriteAllText(outsidePdf, "outside pdf");
            var linkedPdf = Path.Combine(linkedFamily, "Fixture 840.pdf");
            CreateFileSymbolicLinkOrSkip(linkedPdf, outsidePdf);

            Assert.False(FileSystemSafety.HasNoReparsePointsInExistingPath(linkedPdf));
            var resolution = new CataloguePdfResolver([record]).Resolve("100", root);
            Assert.False(resolution.IsFound);
            Assert.Equal(CataloguePdfResolutionStatus.MissingPdf, resolution.Status);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [RequiresSymbolicLinkFact]
    public void Ldt_export_refuses_reparse_point_family_and_file_when_supported()
    {
        var root = TempDirectory();
        var destinationParent = TempDirectory();
        var outside = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "100", "Fixture 840");
            var linkedFamily = Path.Combine(root, "Family");
            var outsideFamily = Path.Combine(outside, "Family");
            Directory.CreateDirectory(Path.Combine(outsideFamily, "LDT"));
            File.WriteAllText(Path.Combine(outsideFamily, "LDT", "Fixture 840.ldt"), "outside family");
            CreateDirectorySymbolicLinkOrSkip(linkedFamily, outsideFamily);
            var familyResult = new CatalogueLdtExportService().Export(["100"], [record], root, destinationParent);
            Assert.Equal(1, familyResult.Errors);
            Directory.Delete(linkedFamily);

            Directory.CreateDirectory(Path.Combine(linkedFamily, "LDT"));
            var outsideLdt = Path.Combine(outside, "Fixture 840.ldt");
            File.WriteAllText(outsideLdt, "outside LDT");
            var linkedLdt = Path.Combine(linkedFamily, "LDT", "Fixture 840.ldt");
            CreateFileSymbolicLinkOrSkip(linkedLdt, outsideLdt);

            var fileResult = new CatalogueLdtExportService().Export(["100"], [record], root, destinationParent);
            Assert.Equal(1, fileResult.Missing);
            Assert.False(File.Exists(Path.Combine(destinationParent, "File đèn", "Fixture 840.ldt")));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(destinationParent, true);
            Directory.Delete(outside, true);
        }
    }

    [RequiresSymbolicLinkFact]
    public void Catalogue_asset_export_aborts_and_preserves_external_content_when_parent_becomes_junction_during_directory_creation()
    {
        var root = TempDirectory();
        var parentContainer = TempDirectory();
        var outside = TempDirectory();
        var destinationParent = Path.Combine(parentContainer, "Destination");
        const string destinationName = "Export";
        var destinationDirectory = Path.Combine(destinationParent, destinationName);
        var outsideDirectory = Path.Combine(outside, destinationName);
        try
        {
            Directory.CreateDirectory(destinationParent);
            var source = Path.Combine(root, "Fixture.pdf");
            File.WriteAllText(source, "source");
            File.WriteAllText(Path.Combine(outside, "preserve.txt"), "external content");

            var coordinator = new CatalogueAssetExportCoordinator(
                afterDestinationDirectoryCreation: () =>
                {
                    Directory.Delete(destinationParent, recursive: true);
                    Directory.CreateSymbolicLink(destinationParent, outside);
                });

            Assert.Throws<InvalidDataException>(() => coordinator.Export(
                ["100"],
                root,
                destinationParent,
                new CatalogueAssetExportOptions(destinationName, "PDF"),
                (code, _, _) => CatalogueAssetResolution.Found(source, "test")));

            Assert.Equal("external content", File.ReadAllText(Path.Combine(outside, "preserve.txt")));
            Assert.False(File.Exists(Path.Combine(outsideDirectory, "Fixture.pdf")));
            Assert.False(Directory.Exists(outsideDirectory));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(parentContainer, true);
            Directory.Delete(outside, true);
        }
    }

    [RequiresSymbolicLinkFact]
    public void Catalogue_asset_export_preserves_preexisting_external_child_when_parent_becomes_junction_during_directory_creation()
    {
        var root = TempDirectory();
        var parentContainer = TempDirectory();
        var outside = TempDirectory();
        var destinationParent = Path.Combine(parentContainer, "Destination");
        const string destinationName = "Export";
        var destinationDirectory = Path.Combine(destinationParent, destinationName);
        var outsideDirectory = Path.Combine(outside, destinationName);
        try
        {
            Directory.CreateDirectory(destinationDirectory);
            Directory.CreateDirectory(outsideDirectory);
            File.WriteAllText(Path.Combine(outsideDirectory, "keep.txt"), "preexisting external content");
            var source = Path.Combine(root, "Fixture.pdf");
            File.WriteAllText(source, "source");

            var coordinator = new CatalogueAssetExportCoordinator(
                beforeDestinationDirectoryCreation: () =>
                {
                    Directory.Delete(destinationParent, recursive: true);
                    Directory.CreateSymbolicLink(destinationParent, outside);
                });

            Assert.Throws<InvalidDataException>(() => coordinator.Export(
                ["100"],
                root,
                destinationParent,
                new CatalogueAssetExportOptions(destinationName, "PDF"),
                (code, _, _) => CatalogueAssetResolution.Found(source, "test")));

            Assert.Equal("preexisting external content", File.ReadAllText(Path.Combine(outsideDirectory, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(outsideDirectory, "Fixture.pdf")));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(parentContainer, true);
            Directory.Delete(outside, true);
        }
    }

    [RequiresSymbolicLinkFact]
    public void Pdf_and_ldt_exports_refuse_reparse_point_destination_children_when_supported()
    {
        var root = TempDirectory();
        var pdfParent = TempDirectory();
        var ldtParent = TempDirectory();
        var outside = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "100", "Fixture 840");
            var family = Path.Combine(root, "Family");
            Directory.CreateDirectory(Path.Combine(family, "LDT"));
            File.WriteAllText(Path.Combine(family, "Fixture 840.pdf"), "catalogue");
            File.WriteAllText(Path.Combine(family, "LDT", "Fixture 840.ldt"), "photometry");

            var pdfChild = Path.Combine(pdfParent, CataloguePdfExportService.DestinationDirectoryName);
            var ldtChild = Path.Combine(ldtParent, CatalogueLdtExportService.DestinationDirectoryName);
            var pdfOutside = Path.Combine(outside, "PDF");
            var ldtOutside = Path.Combine(outside, "LDT");
            Directory.CreateDirectory(pdfOutside);
            Directory.CreateDirectory(ldtOutside);
            CreateDirectorySymbolicLinkOrSkip(pdfChild, pdfOutside);
            CreateDirectorySymbolicLinkOrSkip(ldtChild, ldtOutside);

            Assert.Throws<InvalidDataException>(() => new CataloguePdfExportService().Export(["100"], [record], root, pdfParent));
            Assert.Throws<InvalidDataException>(() => new CatalogueLdtExportService().Export(["100"], [record], root, ldtParent));
            Assert.Empty(Directory.EnumerateFiles(pdfOutside));
            Assert.Empty(Directory.EnumerateFiles(ldtOutside));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(pdfParent, true);
            Directory.Delete(ldtParent, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Pdf_and_ies_targets_share_verified_product_name_only_basename()
    {
        using var http = new HttpClient(new SequenceHandler(JsonResponse("""{"results":[]}""")));
        var client = new SignifyClient(http);
        var record = new ProductRecord("S", 1, "Family", "911401", "911401 - Fallback/840");
        var match = new ProductMatch { MatchedSku = "other", MatchedName = "Verified: Fixture/840" };
        Assert.EndsWith(Path.Combine("Family", "Verified Fixture 840.pdf"), client.PdfTarget(record, match, Path.GetTempPath()));
        Assert.EndsWith(Path.Combine("Family", "IES", "Verified Fixture 840.ies"), client.IesTarget(record, match, Path.GetTempPath()));

        var fallback = new ProductMatch();
        Assert.EndsWith(Path.Combine("Family", "Fallback 840.pdf"), client.PdfTarget(record, fallback, Path.GetTempPath()));
    }

    [Fact]
    public async Task Direct_pdf_uses_exact_api_ies_url()
    {
        var handler = new RoutingHandler(request =>
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.OK);
            if (uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase)) return DownloadResponse("%PDF-1.7\nfixture"u8.ToArray(), "application/pdf");
            if (uri.Host.Contains("microservices.signify.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return JsonResponse("""{"results":[{"sku":"911401","name":"RC099V G3 LED69 865","product_codes":["911401"],"ies_url":"https://example.test/authoritative.ies"}]}""");
            if (uri.AbsolutePath.EndsWith("authoritative.ies", StringComparison.OrdinalIgnoreCase)) return DownloadResponse(ValidIes, "text/plain");
            if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.StartsWith("/assets/IES/", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            throw new InvalidOperationException($"Unexpected request: {uri}");
        });
        using var http = new HttpClient(handler);
        var match = await new SignifyClient(http).FindProductAsync(new ProductRecord("S", 1, "F", "911401", "RC099V G3 LED69 865"), TestOptions(Path.GetTempPath()));
        Assert.NotNull(match);
        Assert.Equal("https://example.test/authoritative.ies", match.IesUrl);
        Assert.Contains("api:", match.QueryUsed);
    }

    [Fact]
    public async Task Direct_pdf_does_not_inherit_ies_from_different_primary_sku_alias()
    {
        const string material = "100";
        var suppliedIes = "https://example.test/wrong-primary.ies";
        var requests = new List<Uri>();
        using var http = new HttpClient(new RoutingHandler(request =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri);
            if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.OK);
            if (uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase)) return DownloadResponse("%PDF-1.7\nfixture"u8.ToArray(), "application/pdf");
            if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return JsonResponse($$"""{"results":[{"sku":"200","name":"Shared Fixture","product_codes":["{{material}}"],"ies_url":"{{suppliedIes}}"}]}""");
            if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.StartsWith("/assets/IES/", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            throw new InvalidOperationException($"Unexpected request: {uri}");
        }));

        var match = await new SignifyClient(http).FindProductAsync(new ProductRecord("S", 1, "F", material, "Shared Fixture"), TestOptions(Path.GetTempPath()));

        Assert.NotNull(match);
        Assert.Empty(match.IesUrl);
        Assert.DoesNotContain(requests, uri => uri.AbsoluteUri.Equals(suppliedIes, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Numeric_aliases_from_different_primary_skus_do_not_merge_split_pdf_and_ies_assets()
    {
        const string material = "100";
        const string pdf = "https://example.test/primary-200.pdf";
        const string ies = "https://example.test/primary-300.ies";
        using var http = new HttpClient(new RoutingHandler(request =>
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return JsonResponse($$"""{"results":[{"sku":"200","name":"Shared Fixture","product_codes":["{{material}}"],"leaflet_url":"{{pdf}}"},{"sku":"300","name":"Shared Fixture","product_codes":["{{material}}"],"ies_url":"{{ies}}"}]}""");
            throw new InvalidOperationException($"Unexpected request: {uri}");
        }));

        var match = await new SignifyClient(http).FindProductAsync(
            new ProductRecord("S", 1, "F", material, "Shared Fixture"),
            TestOptions(Path.GetTempPath()));

        Assert.Null(match);
    }

    [Fact]
    public async Task Direct_pdf_keeps_exact_api_leaflet_as_independent_fallback_after_get_failure()
    {
        var root = TempDirectory();
        try
        {
            const string material = "100";
            const string fallback = "https://example.test/exact-leaflet.pdf";
            var directGets = 0;
            using var http = new HttpClient(new RoutingHandler(request =>
            {
                var uri = request.RequestUri!;
                if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.OK);
                if (uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase))
                {
                    directGets++;
                    return directGets == 1
                        ? DownloadResponse("%PDF-1.7\nvalidated"u8.ToArray(), "application/pdf")
                        : new HttpResponseMessage(HttpStatusCode.BadGateway);
                }
                if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse(ExactSearchJson(material, "Fixture 840", fallback, string.Empty));
                if (uri.AbsolutePath.EndsWith("exact-leaflet.pdf", StringComparison.OrdinalIgnoreCase)) return DownloadResponse("%PDF-1.7\nfallback"u8.ToArray(), "application/pdf");
                if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (uri.AbsolutePath.StartsWith("/assets/IES/", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                throw new InvalidOperationException($"Unexpected request: {uri}");
            }));

            var log = Assert.Single(await new CatalogueDownloadService(new SignifyClient(http)).DownloadAsync(
                [new ProductRecord("S", 1, "Family", material, "Fixture 840")], TestOptions(root) with { DownloadIes = false }));

            Assert.Equal("pdf_only", log.Status);
            Assert.True(File.Exists(log.OutputFile));
            Assert.Contains("fallback", File.ReadAllText(log.OutputFile));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Stale_api_ies_url_falls_back_to_configurator_candidate()
    {
        const string material = "100";
        using var http = new HttpClient(new RoutingHandler(request =>
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(ExactSearchJson(material, "Fixture 840", string.Empty, "https://example.test/stale.ies"));
            if (uri.AbsolutePath.EndsWith("stale.ies", StringComparison.OrdinalIgnoreCase)) return DownloadResponse("<html>stale"u8.ToArray(), "text/html");
            if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase)) return DownloadResponse(ValidIes, "text/plain");
            if (uri.AbsolutePath.StartsWith("/assets/IES/", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            throw new InvalidOperationException($"Unexpected request: {uri}");
        }));

        var match = await new SignifyClient(http).FindProductAsync(new ProductRecord("S", 1, "F", material, "Fixture 840"), TestOptions(Path.GetTempPath()));

        Assert.NotNull(match);
        Assert.Contains("getPhotometricAssets/ies", match.IesUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("C:\\Catalogue", true)]
    public void Download_requires_nonblank_product_family_root(string? root, bool expected) =>
        Assert.Equal(expected, DocManager.Desktop.MainWindow.HasDownloadRoot(root));

    [Fact]
    public async Task Per_asset_log_sink_continues_after_a_sink_write_failure()
    {
        using var http = new HttpClient(new RoutingHandler(_ => JsonResponse("""{"results":[]}""")));
        var delivered = 0;

        var logs = await new CatalogueDownloadService(new SignifyClient(http)).DownloadAsync(
            [new ProductRecord("S", 1, "Family", "", "One"), new ProductRecord("S", 2, "Family", "", "Two")],
            TestOptions(Path.GetTempPath()),
            logSink: _ =>
            {
                delivered++;
                if (delivered == 1) throw new IOException("CSV unavailable");
            });

        Assert.Equal(2, logs.Count);
        Assert.Equal(2, delivered);
    }

    [Fact]
    public void Aggregate_status_reports_any_failed_asset_before_unavailable_or_skipped()
    {
        var status = CatalogueDownloadService.AggregateStatus(
        [
            new(DownloadAssetKind.Pdf, DownloadAssetStatus.Unavailable),
            new(DownloadAssetKind.Ies, DownloadAssetStatus.Failed),
            new(DownloadAssetKind.Ldt, DownloadAssetStatus.Skipped)
        ]);

        Assert.Equal("download_failed", status);
    }

    [Fact]
    public void Autocomplete_replaces_token_at_caret_without_disturbing_delimiters_or_caret()
    {
        const string text = "first,  RC0  \r\nsecond";
        var replacement = SignifyTextInput.ReplaceQueryAtCaret(text, 9, "RC099V G3 LED69 865");

        Assert.Equal("first,  RC099V G3 LED69 865  \r\nsecond", replacement.Text);
        Assert.Equal("RC099V G3 LED69 865", SignifyTextInput.CurrentQuery(replacement.Text, replacement.CaretIndex));
        Assert.Equal(27, replacement.CaretIndex);
    }

    [Fact]
    public void Catalogue_pdf_resolver_rejects_ambiguous_pricelist_match()
    {
        var root = TempDirectory();
        try
        {
            var records = new[]
            {
                new ProductRecord("S", 1, "Family A", "100", "Shared Fixture"),
                new ProductRecord("S", 2, "Family B", "200", "Shared Fixture")
            };
            var resolution = new CataloguePdfResolver(records).Resolve("Shared Fixture", root);

            Assert.Equal(CataloguePdfResolutionStatus.AmbiguousProduct, resolution.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Ies_download_sets_prefixed_path_only_after_validated_success()
    {
        var root = TempDirectory();
        try
        {
            var handler = new RoutingHandler(request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(request.RequestUri?.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase) == true ? ValidIes : Encoding.ASCII.GetBytes("%PDF-1.7\nfixture"))
                };
                response.Content.Headers.ContentType = new(request.RequestUri?.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase) == true ? "text/plain" : "application/pdf");
                return response;
            });
            using var http = new HttpClient(handler);
            var client = new SignifyClient(http);
            var service = new CatalogueDownloadService(client);
            var record = new ProductRecord("S", 2, "Family", "911", "Fixture");
            var match = new ProductMatch { MatchedSku = "911", MatchedName = "Fixture" };
            Assert.EndsWith(Path.Combine("IES", "Fixture.ies"), client.IesTarget(record, match, root));

            var direct = await client.DownloadAsync("https://example.test/file.ies", client.IesTarget(record, match, root), DownloadKind.Ies, TestOptions(root));
            Assert.Equal("downloaded", direct);
            Assert.True(File.Exists(client.IesTarget(record, match, root)));

            var invalidPath = Path.Combine(root, "invalid.ies");
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync("https://example.test/not-ies", invalidPath, DownloadKind.Ies, TestOptions(root)));
            Assert.False(File.Exists(invalidPath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Csv_append_serializes_concurrent_writers_without_losing_rows()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "concurrent.csv");
        try
        {
            var tasks = Enumerable.Range(0, 24).Select(index => Task.Run(() =>
                CatalogueDownloadService.AppendCsvLog(path, [new DownloadLogEntry { Material = index.ToString(), Status = "written" }]))).ToArray();
            await Task.WhenAll(tasks);
            var lines = File.ReadAllLines(path).Where(line => line.Length > 0).ToArray();
            Assert.Equal(25, lines.Length);
            Assert.Equal(24, lines.Skip(1).Select(line => line.Split(',')[3]).Distinct().Count());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Csv_quotes_fields_and_download_only_does_not_claim_ldt()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "download_log.csv");
        try
        {
            CatalogueDownloadService.AppendCsvLog(path, [new DownloadLogEntry { ProductFamily = "A,B", MaterialDescription = "A \"quoted\" fixture", OutputFile = "x.pdf", Status = "downloaded" }]);
            var text = File.ReadAllText(path);
            Assert.Contains("\"A,B\"", text);
            Assert.Contains("\"A \"\"quoted\"\" fixture\"", text);
            Assert.StartsWith("sheet,excel_row,product_family", text);
            Assert.Equal(16, text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Split(',').Length);
            var data = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[1];
            Assert.DoesNotContain(".ldt", data, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("x.pdf", data);
            Assert.Contains("downloaded", data);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Desktop_download_summary_distinguishes_asset_outcomes_without_counting_unavailable_as_errors()
    {
        var logs = new[]
        {
            new DownloadLogEntry
            {
                Sheet = "S",
                ExcelRow = 1,
                Status = "converted",
                AssetResults =
                [
                    new(DownloadAssetKind.Pdf, DownloadAssetStatus.Downloaded),
                    new(DownloadAssetKind.Ies, DownloadAssetStatus.Reused),
                    new(DownloadAssetKind.Ldt, DownloadAssetStatus.Downloaded)
                ]
            },
            new DownloadLogEntry
            {
                Sheet = "S",
                ExcelRow = 2,
                Status = "conversion_failed",
                AssetResults =
                [
                    new(DownloadAssetKind.Pdf, DownloadAssetStatus.Unavailable),
                    new(DownloadAssetKind.Ies, DownloadAssetStatus.Skipped),
                    new(DownloadAssetKind.Ldt, DownloadAssetStatus.Failed)
                ]
            }
        };

        Assert.Equal(
            "Hoàn tất 2: đã tải/tạo 2; đã dùng lại 1; bỏ qua 1; không có nguồn 1; thiếu một phần 0; lỗi 1.",
            DocManager.Desktop.MainWindow.DownloadCompletionSummary(logs, downloadIes: true));
    }

    [Fact]
    public void Csv_schema_migration_preserves_legacy_log_as_backup()
    {
        var root = TempDirectory();
        var path = Path.Combine(root, "download_log.csv");
        try
        {
            File.WriteAllText(path, "old,header\nold,row\n");
            CatalogueDownloadService.AppendCsvLog(path, [new DownloadLogEntry { MaterialDescription = "Fixture", Status = "converted" }]);

            Assert.StartsWith("sheet,excel_row,product_family", File.ReadAllText(path));
            var backup = Assert.Single(Directory.EnumerateFiles(root).Where(file => file.EndsWith(".legacy.bak", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains("old,header", File.ReadAllText(backup));
        }
        finally { Directory.Delete(root, true); }
    }

    [RequiresSymbolicLinkFact]
    public void Desktop_legacy_scan_and_product_folder_stay_contained_and_skip_reparse_paths_when_supported()
    {
        var root = TempDirectory();
        var outside = TempDirectory();
        try
        {
            var safePdf = Path.Combine(root, "Family", "Fixture 840.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(safePdf)!);
            File.WriteAllText(safePdf, "safe");
            var outsidePdf = Path.Combine(outside, "Fixture 840.pdf");
            File.WriteAllText(outsidePdf, "outside");
            var linkedDirectory = Path.Combine(root, "Family", "linked");
            CreateDirectorySymbolicLinkOrSkip(linkedDirectory, outside);
            var files = DocManager.Desktop.MainWindow.EnumerateCatalogueFiles(root, CancellationToken.None);
            Assert.Contains(Path.GetFullPath(safePdf), files, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(files, path => path.Contains("linked", StringComparison.OrdinalIgnoreCase));
            Directory.Delete(linkedDirectory);

            var linkedPdf = Path.Combine(root, "Family", "Linked Fixture.pdf");
            CreateFileSymbolicLinkOrSkip(linkedPdf, outsidePdf);
            Assert.DoesNotContain(DocManager.Desktop.MainWindow.EnumerateCatalogueFiles(root, CancellationToken.None),
                path => path.Equals(Path.GetFullPath(linkedPdf), StringComparison.OrdinalIgnoreCase));
            Assert.Equal(FileSystemSafety.NormalizeDirectoryPath(root),
                DocManager.Desktop.MainWindow.ProductFamilyDirectory(outsidePdf, root));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [RequiresSymbolicLinkFact]
    public void Canonical_asset_manifest_rejects_reparse_roots_families_and_manifest_owned_assets_when_supported()
    {
        var root = TempDirectory();
        var outside = TempDirectory();
        try
        {
            var record = new ProductRecord("S", 1, "Family", "100", "Fixture 840");
            var linkedRoot = Path.Combine(root, "linked-root");
            CreateDirectorySymbolicLinkOrSkip(linkedRoot, outside);
            Assert.Throws<ArgumentException>(() => new CanonicalAssetManifest(linkedRoot));
            Directory.Delete(linkedRoot);

            var family = Path.Combine(root, "Family");
            var linkedFamily = Path.Combine(root, "Family");
            CreateDirectorySymbolicLinkOrSkip(linkedFamily, outside);
            Assert.Throws<ArgumentException>(() => new CanonicalAssetManifest(root).Reserve(
                record,
                new ProductMatch { MatchedSku = "100", MatchedName = "Fixture 840" },
                "Fixture 840",
                Path.Combine(family, "Fixture 840.pdf"),
                Path.Combine(family, "IES", "Fixture 840.ies")));
            Directory.Delete(linkedFamily);

            Directory.CreateDirectory(Path.Combine(family, "IES"));
            var manifest = new CanonicalAssetManifest(root);
            var reservation = manifest.Reserve(
                record,
                new ProductMatch { MatchedSku = "100", MatchedName = "Fixture 840" },
                "Fixture 840",
                Path.Combine(family, "Fixture 840.pdf"),
                Path.Combine(family, "IES", "Fixture 840.ies"));
            Assert.NotNull(reservation.Entry);
            var outsidePdf = Path.Combine(outside, "Fixture 840.pdf");
            File.WriteAllText(outsidePdf, "outside");
            var linkedPdf = Path.Combine(family, "Fixture 840.pdf");
            CreateFileSymbolicLinkOrSkip(linkedPdf, outsidePdf);

            Assert.Throws<InvalidDataException>(() => manifest.ExistingPath(reservation.Entry!, ".pdf"));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Canonical_asset_manifest_normalizes_drive_and_unc_roots_without_erasing_them()
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        var normalizedDrive = FileSystemSafety.NormalizeDirectoryPath(driveRoot);
        const string uncRoot = "\\\\server\\share\\";
        var fullUnc = Path.GetFullPath(uncRoot);
        var normalizedUnc = FileSystemSafety.NormalizeDirectoryPath(uncRoot);

        Assert.Equal(Path.GetFullPath(driveRoot), normalizedDrive);
        Assert.True(FileSystemSafety.IsPathWithinDirectory(normalizedDrive, normalizedDrive));
        Assert.Equal(Path.TrimEndingDirectorySeparator(fullUnc), normalizedUnc);
        Assert.Equal(Path.GetPathRoot(fullUnc), Path.GetPathRoot(normalizedUnc));
        Assert.True(FileSystemSafety.IsPathWithinDirectory(normalizedUnc, normalizedUnc));
    }

    [Fact]
    public void Canonical_asset_manifest_fails_closed_across_fresh_instances_but_reuses_matched_sku_alias()
    {
        var root = TempDirectory();
        try
        {
            var first = new CanonicalAssetManifest(root);
            var record = new ProductRecord("S", 1, "Family", "100", "Pricelist name");
            var match = new ProductMatch { MatchedSku = "200", MatchedName = "Verified/fixture" };
            var pdf = Path.Combine(root, "Family", "Verified fixture.pdf");
            var ies = Path.Combine(root, "Family", "IES", "Verified fixture.ies");
            var reservation = first.Reserve(record, match, "Verified fixture", pdf, ies);
            Assert.False(reservation.HasCollision);
            Assert.NotNull(reservation.Entry);

            var alias = new CanonicalAssetManifest(root).Reserve(
                new ProductRecord("S", 2, "Family", "101", "Alias description"),
                new ProductMatch { MatchedSku = "200", MatchedName = "Verified/fixture" },
                "Verified fixture", pdf, ies);
            Assert.False(alias.HasCollision);
            Assert.Equal("100", alias.Entry!.Material);

            var collision = new CanonicalAssetManifest(root).Reserve(
                new ProductRecord("S", 3, "Family", "102", "Different description"),
                new ProductMatch { MatchedSku = "201", MatchedName = "Verified/fixture" },
                "Verified fixture", pdf, ies);
            Assert.True(collision.HasCollision);
            Assert.Equal("name_collision", collision.HasCollision ? "name_collision" : string.Empty);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Canonical_asset_manifest_rejects_primary_sku_drift_for_the_same_material()
    {
        var root = TempDirectory();
        try
        {
            const string canonical = "Verified fixture";
            var family = Path.Combine(root, "Family");
            var pdf = Path.Combine(family, canonical + ".pdf");
            var ies = Path.Combine(family, "IES", canonical + ".ies");
            var record = new ProductRecord("S", 1, "Family", "100", "Pricelist name");
            var manifest = new CanonicalAssetManifest(root);
            var first = manifest.Reserve(
                record,
                new ProductMatch { MatchedSku = "200", MatchedName = canonical },
                canonical,
                pdf,
                ies);
            Assert.False(first.HasCollision);
            Directory.CreateDirectory(Path.GetDirectoryName(ies)!);
            File.WriteAllText(pdf, "primary 200 pdf");
            File.WriteAllText(ies, "primary 200 ies");
            manifest.UpdateDownloadedPaths(first.Entry!, pdf, ies);

            var conflict = new CanonicalAssetManifest(root).Reserve(
                record,
                new ProductMatch { MatchedSku = "201", MatchedName = canonical },
                canonical,
                pdf,
                ies);

            Assert.True(conflict.HasCollision);
            Assert.Null(conflict.Entry);
            Assert.True(new CanonicalAssetManifest(root).Resolve(record, "201").Entry is null);
            var original = Assert.IsType<CanonicalAssetManifestEntry>(new CanonicalAssetManifest(root).Resolve(record, "200").Entry);
            Assert.Equal("200", original.MatchedSku);
            Assert.Equal("primary 200 pdf", File.ReadAllText(pdf));
            Assert.Equal("primary 200 ies", File.ReadAllText(ies));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Canonical_asset_manifest_rejects_unproven_existing_canonical_file()
    {
        var root = TempDirectory();
        try
        {
            var pdf = Path.Combine(root, "Family", "Verified fixture.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
            File.WriteAllText(pdf, "manual file");

            var reservation = new CanonicalAssetManifest(root).Reserve(
                new ProductRecord("S", 1, "Family", "100", "Pricelist name"),
                new ProductMatch { MatchedSku = "100", MatchedName = "Verified fixture" },
                "Verified fixture",
                pdf,
                null);

            Assert.True(reservation.HasCollision);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Manifest_resolves_api_name_drift_for_conversion_and_updates_ldt_path()
    {
        var root = TempDirectory();
        try
        {
            const string canonical = "API WT198C LED40S 840 PSU L1200";
            var family = Path.Combine(root, "Family");
            var iesDirectory = Path.Combine(family, "IES");
            Directory.CreateDirectory(iesDirectory);
            var record = new ProductRecord("S", 1, "Family", "911401504347", "Pricelist description changed");
            var match = new ProductMatch { MatchedSku = "911401504347", MatchedName = canonical };
            var pdf = Path.Combine(family, canonical + ".pdf");
            var ies = Path.Combine(iesDirectory, canonical + ".ies");
            var manifest = new CanonicalAssetManifest(root);
            var reservation = manifest.Reserve(record, match, canonical, pdf, ies);
            Assert.NotNull(reservation.Entry);
            File.WriteAllText(ies, Encoding.ASCII.GetString(ValidIes).Replace("[LUMINAIRE] Fixture", $"[LUMINAIRE] {canonical}", StringComparison.Ordinal), new UTF8Encoding(false));
            WriteTechnicalPdf(pdf);
            manifest.UpdateDownloadedPaths(reservation.Entry!, pdf, ies);
            using var http = new HttpClient(new SequenceHandler(JsonResponse("""{"results":[]}""")));

            var conversion = Assert.Single(new CatalogueDownloadService(new SignifyClient(http)).ConvertProducts([record], root));
            var resolved = new CanonicalAssetManifest(root).Resolve(record);

            Assert.Equal(canonical + ".ldt", Path.GetFileName(conversion.Result.OutputPath));
            Assert.NotNull(resolved.Entry);
            Assert.EndsWith(Path.Combine("Family", "LDT", canonical + ".ldt"), resolved.Entry!.LdtPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Same_family_canonical_name_collision_fails_closed_before_http()
    {
        var root = TempDirectory();
        try
        {
            var handler = new SequenceHandler(JsonResponse("""{"results":[]}"""));
            using var http = new HttpClient(handler);
            var service = new CatalogueDownloadService(new SignifyClient(http));
            var logs = await service.DownloadAsync([
                new ProductRecord("S", 1, "Family", "1", "Name/Variant"),
                new ProductRecord("S", 2, "Family", "2", "Name:Variant")
            ], TestOptions(root));

            Assert.All(logs, entry => Assert.Equal("name_collision", entry.Status));
            Assert.Equal(0, handler.Calls);
            Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Same_material_aliases_are_not_reported_as_name_collisions()
    {
        var handler = new RoutingHandler(request => request.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : JsonResponse("""{"results":[]}"""));
        using var http = new HttpClient(handler);
        var service = new CatalogueDownloadService(new SignifyClient(http));
        var logs = await service.DownloadAsync([
            new ProductRecord("S", 1, "Family", "1", "Name/Variant"),
            new ProductRecord("S", 2, "Family", "1", "Name:Variant")
        ], TestOptions(Path.GetTempPath()));

        Assert.All(logs, entry => Assert.Equal("no_leaflet", entry.Status));
    }

    [Fact]
    public void Canonical_batch_does_not_accept_manual_converter_auto_outputs_as_existing()
    {
        var root = TempDirectory();
        try
        {
            var family = Path.Combine(root, "Family");
            var iesDirectory = Path.Combine(family, "IES");
            var ldtDirectory = Path.Combine(family, "LDT");
            Directory.CreateDirectory(iesDirectory);
            Directory.CreateDirectory(ldtDirectory);
            const string product = "Fixture 840";
            var ies = Path.Combine(iesDirectory, product + ".ies");
            var pdf = Path.Combine(family, product + ".pdf");
            File.WriteAllText(ies, Encoding.ASCII.GetString(ValidIes).Replace("[LUMINAIRE] Fixture", $"[LUMINAIRE] {product}", StringComparison.Ordinal), new UTF8Encoding(false));
            WriteTechnicalPdf(pdf);
            File.WriteAllText(Path.Combine(ldtDirectory, product + "_auto.ldt"), "manual output");
            using var http = new HttpClient(new SequenceHandler(JsonResponse("""{"results":[]}""")));

            var result = Assert.Single(new CatalogueDownloadService(new SignifyClient(http)).ConvertProducts(
                [new ProductRecord("S", 1, "Family", "911", product)], root));

            Assert.Equal(product + ".ldt", Path.GetFileName(result.Result.OutputPath));
            Assert.True(File.Exists(Path.Combine(ldtDirectory, product + "_auto.ldt")));
            Assert.True(LdtIdentity.IsIdentityMatch(product, ies, Path.Combine(ldtDirectory, product + "_auto.ldt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Canonical_batch_fails_closed_for_same_family_distinct_sku_collision()
    {
        var root = TempDirectory();
        try
        {
            var family = Path.Combine(root, "Family");
            Directory.CreateDirectory(Path.Combine(family, "IES"));
            using var http = new HttpClient(new SequenceHandler(JsonResponse("""{"results":[]}""")));
            var service = new CatalogueDownloadService(new SignifyClient(http));

            var exception = Assert.Throws<InvalidDataException>(() => service.ConvertProducts(
                [
                    new ProductRecord("S", 1, "Family", "1", "Name/Variant"),
                    new ProductRecord("S", 2, "Family", "2", "Name:Variant")
                ], root));

            Assert.Contains("name_collision", exception.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Convert_products_uses_exact_product_context_pdf_and_writes_canonical_unicode_ldt()
    {
        var root = TempDirectory();
        var family = Path.Combine(root, "Family");
        var iesDirectory = Path.Combine(family, "IES");
        Directory.CreateDirectory(iesDirectory);
        try
        {
            const string product = "Đèn WT198C LED40S/840 PSU L1200";
            var basename = ProductAssetName.CanonicalBaseName(product);
            var ies = Path.Combine(iesDirectory, basename + ".ies");
            var pdf = Path.Combine(family, basename + ".pdf");
            var iesText = Encoding.ASCII.GetString(ValidIes).Replace("[LUMINAIRE] Fixture", $"[LUMINAIRE] {product}", StringComparison.Ordinal);
            File.WriteAllText(ies, iesText, new UTF8Encoding(false));
            WriteTechnicalPdf(pdf);
            using var http = new HttpClient(new SequenceHandler(JsonResponse("""{"results":[]}""")));

            var result = Assert.Single(new CatalogueDownloadService(new SignifyClient(http)).ConvertProducts([
                new ProductRecord("S", 2, "Family", "911", product)
            ], root));
            var lines = File.ReadAllLines(result.Result.OutputPath);

            Assert.Equal(basename + ".ldt", Path.GetFileName(result.Result.OutputPath));
            Assert.Equal(basename, lines[8]);
            Assert.Equal(string.Empty, lines[9]);
            Assert.Equal(basename, lines[10]);
            Assert.Equal(Path.GetFullPath(ies), result.Context.IesPath);
            Assert.Equal(Path.GetFullPath(pdf), result.Context.PdfPath);
            Assert.Equal(Path.GetFullPath(pdf), result.Result.CataloguePdfPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Download_fresh_pdf_and_ies_automatically_converts_canonical_ldt_with_catalogue_geometry()
    {
        var root = TempDirectory();
        try
        {
            const string material = "911";
            const string product = "Fixture 840";
            using var http = new HttpClient(ProductDownloadHandler(material, product, TechnicalPdfBytes("Overall dimensions L x W x H: 480 x 320 x 109 mm"), ValidIes));
            var record = new ProductRecord("S", 2, "Family", material, product);

            var log = Assert.Single(await new CatalogueDownloadService(new SignifyClient(http)).DownloadAsync([record], TestOptions(root)));
            var lines = File.ReadAllLines(log.LdtFile);
            var manifest = new CanonicalAssetManifest(root).Resolve(record).Entry;

            Assert.Equal("converted", log.Status);
            Assert.Equal("Fixture 840.pdf", Path.GetFileName(log.OutputFile));
            Assert.Equal("Fixture 840.ies", Path.GetFileName(log.IesFile));
            Assert.Equal("Fixture 840.ldt", Path.GetFileName(log.LdtFile));
            Assert.Equal(["480", "320", "109"], lines[12..15]);
            Assert.Equal(["480", "320", "109"], lines[15..18]);
            Assert.Equal(["0", "0", "0"], lines[18..21]);
            Assert.NotNull(manifest);
            Assert.EndsWith(Path.Combine("Family", "LDT", "Fixture 840.ldt"), manifest!.LdtPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Download_existing_pdf_and_ies_converts_missing_ldt_then_never_overwrites_existing_ldt()
    {
        var root = TempDirectory();
        try
        {
            const string material = "911";
            const string product = "Fixture 840";
            var record = new ProductRecord("S", 2, "Family", material, product);
            var family = Path.Combine(root, "Family");
            var pdf = Path.Combine(family, product + ".pdf");
            var ies = Path.Combine(family, "IES", product + ".ies");
            var manifest = new CanonicalAssetManifest(root);
            var reservation = manifest.Reserve(record, new ProductMatch { MatchedSku = material, MatchedName = product }, product, pdf, ies);
            Assert.NotNull(reservation.Entry);
            Directory.CreateDirectory(Path.GetDirectoryName(ies)!);
            WriteTechnicalPdf(pdf);
            File.WriteAllBytes(ies, ValidIes);
            manifest.UpdateDownloadedPaths(reservation.Entry!, pdf, ies);
            using var http = new HttpClient(ProductDownloadHandler(material, product, TechnicalPdfBytes(), ValidIes));
            var service = new CatalogueDownloadService(new SignifyClient(http));

            var first = Assert.Single(await service.DownloadAsync([record], TestOptions(root)));
            Assert.Equal("converted", first.Status);
            File.WriteAllText(first.LdtFile, "user edited LDT", new UTF8Encoding(false));
            var second = Assert.Single(await service.DownloadAsync([record], TestOptions(root) with { Force = true }));

            Assert.Equal("conversion_exists", second.Status);
            Assert.Equal("user edited LDT", File.ReadAllText(first.LdtFile));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Download_reuses_valid_manifest_owned_ies_when_discovery_has_no_ies_url()
    {
        var root = TempDirectory();
        try
        {
            const string material = "911";
            const string product = "Fixture 840";
            var record = new ProductRecord("S", 2, "Family", material, product);
            var family = Path.Combine(root, "Family");
            var pdf = Path.Combine(family, product + ".pdf");
            var ies = Path.Combine(family, "IES", product + ".ies");
            var manifest = new CanonicalAssetManifest(root);
            var reservation = manifest.Reserve(record, new ProductMatch { MatchedSku = material, MatchedName = product }, product, pdf, ies);
            Assert.NotNull(reservation.Entry);
            Directory.CreateDirectory(Path.GetDirectoryName(ies)!);
            File.WriteAllBytes(ies, ValidIes);
            manifest.UpdateDownloadedPaths(reservation.Entry!, iesPath: ies);
            var requests = new List<Uri>();
            using var http = new HttpClient(new RoutingHandler(request =>
            {
                requests.Add(request.RequestUri!);
                var uri = request.RequestUri!;
                if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse(ExactSearchJson(material, product, "https://example.test/fixture.pdf", string.Empty));
            if (uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase) ||
                    uri.AbsolutePath.StartsWith("/assets/IES/", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    return DownloadResponse(TechnicalPdfBytes(), "application/pdf");
                throw new InvalidOperationException($"Unexpected request: {uri}");
            }));

            var log = Assert.Single(await new CatalogueDownloadService(new SignifyClient(http)).DownloadAsync([record], TestOptions(root)));

            Assert.Equal("converted", log.Status);
            Assert.Equal(Path.GetFullPath(ies), log.IesFile);
            Assert.True(File.Exists(log.LdtFile));
            Assert.DoesNotContain(requests, uri => uri.Host.Equals("example.test", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Download_without_pdf_still_downloads_ies_and_converts_relative_ies()
    {
        var root = TempDirectory();
        try
        {
            const string material = "NO-PDF";
            const string product = "Fixture 840";
            using var http = new HttpClient(IndependentAssetHandler(material, product, string.Empty, "https://example.test/fixture.ies", pdfResponse: null, iesResponse: ValidIes));

            var log = Assert.Single(await new CatalogueDownloadService(new SignifyClient(http)).DownloadAsync(
                [new ProductRecord("S", 1, "Family", material, product)], TestOptions(root)));

            Assert.Equal("ies_converted_no_pdf", log.Status);
            Assert.False(File.Exists(Path.Combine(root, "Family", product + ".pdf")));
            Assert.True(File.Exists(log.IesFile));
            Assert.True(File.Exists(log.LdtFile));
            Assert.Contains(log.AssetResults, asset => asset.Kind == DownloadAssetKind.Pdf && asset.Status == DownloadAssetStatus.Unavailable);
            Assert.Contains(log.AssetResults, asset => asset.Kind == DownloadAssetKind.Ldt && asset.Status == DownloadAssetStatus.Downloaded);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Download_without_ies_saves_pdf_and_skips_ldt_as_unavailable()
    {
        var root = TempDirectory();
        try
        {
            const string material = "NO-IES";
            const string product = "Fixture 840";
            using var http = new HttpClient(IndependentAssetHandler(material, product, "https://example.test/fixture.pdf", string.Empty, TechnicalPdfBytes(), null));

            var log = Assert.Single(await new CatalogueDownloadService(new SignifyClient(http)).DownloadAsync(
                [new ProductRecord("S", 1, "Family", material, product)], TestOptions(root)));

            Assert.Equal("pdf_only", log.Status);
            Assert.True(File.Exists(log.OutputFile));
            Assert.Empty(log.LdtFile);
            Assert.Contains(log.AssetResults, asset => asset.Kind == DownloadAssetKind.Ies && asset.Status == DownloadAssetStatus.Unavailable);
            Assert.Contains(log.AssetResults, asset => asset.Kind == DownloadAssetKind.Ldt && asset.Status == DownloadAssetStatus.Skipped);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Download_without_sources_is_non_error_product_outcome()
    {
        var root = TempDirectory();
        try
        {
            const string material = "NO-ASSETS";
            const string product = "Fixture 840";
            using var http = new HttpClient(IndependentAssetHandler(material, product, string.Empty, string.Empty, null, null));

            var log = Assert.Single(await new CatalogueDownloadService(new SignifyClient(http)).DownloadAsync(
                [new ProductRecord("S", 1, "Family", material, product)], TestOptions(root)));

            Assert.Equal("assets_unavailable", log.Status);
            Assert.EndsWith("không có nguồn 1; thiếu một phần 0; lỗi 0.", DocManager.Desktop.MainWindow.DownloadCompletionSummary([log], downloadIes: true));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Pdf_download_failure_does_not_block_ies_conversion()
    {
        var root = TempDirectory();
        try
        {
            const string material = "PDF-FAIL";
            const string product = "Fixture 840";
            using var http = new HttpClient(IndependentAssetHandler(material, product, "https://example.test/fixture.pdf", "https://example.test/fixture.ies", null, ValidIes, pdfStatus: HttpStatusCode.BadGateway));

            var log = Assert.Single(await new CatalogueDownloadService(new SignifyClient(http)).DownloadAsync(
                [new ProductRecord("S", 1, "Family", material, product)], TestOptions(root)));

            Assert.Equal("pdf_failed_ies_converted", log.Status);
            Assert.True(File.Exists(log.IesFile));
            Assert.True(File.Exists(log.LdtFile));
            Assert.Contains(log.AssetResults, asset => asset.Kind == DownloadAssetKind.Pdf && asset.Status == DownloadAssetStatus.Failed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Ies_download_failure_does_not_block_pdf()
    {
        var root = TempDirectory();
        try
        {
            const string material = "IES-FAIL";
            const string product = "Fixture 840";
            var iesRequests = 0;
            using var http = new HttpClient(new RoutingHandler(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse(ExactSearchJson(material, product, "https://example.test/fixture.pdf", "https://example.test/fixture.ies"));
                if (uri.AbsolutePath.EndsWith("fixture.pdf", StringComparison.OrdinalIgnoreCase)) return DownloadResponse(TechnicalPdfBytes(), "application/pdf");
                if (uri.AbsolutePath.EndsWith("fixture.ies", StringComparison.OrdinalIgnoreCase))
                    return ++iesRequests == 1 ? DownloadResponse(ValidIes, "text/plain") : new HttpResponseMessage(HttpStatusCode.BadGateway);
                throw new InvalidOperationException($"Unexpected request: {uri}");
            }));

            var log = Assert.Single(await new CatalogueDownloadService(new SignifyClient(http)).DownloadAsync(
                [new ProductRecord("S", 1, "Family", material, product)], TestOptions(root)));

            Assert.Equal("ies_failed_pdf_saved", log.Status);
            Assert.True(File.Exists(log.OutputFile));
            Assert.False(File.Exists(Path.Combine(root, "Family", "LDT", product + ".ldt")));
            Assert.Contains(log.AssetResults, asset => asset.Kind == DownloadAssetKind.Ies && asset.Status == DownloadAssetStatus.Failed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Download_honors_cancellation_before_later_products()
    {
        var root = TempDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var http = new HttpClient(new RoutingHandler(request =>
            {
                if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (request.RequestUri!.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                {
                    cancellation.Cancel();
                    return JsonResponse(ExactSearchJson("CANCEL", "Fixture 840", "https://example.test/fixture.pdf", "https://example.test/fixture.ies"));
                }
                throw new InvalidOperationException("Download must not start after cancellation.");
            }));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CatalogueDownloadService(new SignifyClient(http)).DownloadAsync(
                [new ProductRecord("S", 1, "Family", "CANCEL", "Fixture 840"), new ProductRecord("S", 2, "Family", "LATER", "Later Fixture")],
                TestOptions(root), cancellationToken: cancellation.Token));
            Assert.False(Directory.Exists(Path.Combine(root, "Family", "IES")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Automatic_conversion_failure_continues_to_next_product()
    {
        var root = TempDirectory();
        try
        {
            var pdf = TechnicalPdfBytes();
            var conversionRejectedIes = Encoding.ASCII.GetBytes("IESNA:LM-63-2002\n[TEST] fixture\n[MANUFAC] Signify\n[LUMINAIRE] Rejected\nTILT=NONE\n1 1000 1 2 1 1 2 0.1 0.1 0.1\n1 1 10\n10 90\n0\n100 50\n");
            Assert.True(DownloadValidation.IsValid(conversionRejectedIes, "text/plain", DownloadKind.Ies));
            var handler = new RoutingHandler(request =>
            {
                var uri = request.RequestUri!;
                if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                {
                    var query = Uri.UnescapeDataString(uri.Query);
                    var first = query.Contains("FIRST", StringComparison.OrdinalIgnoreCase);
                    return JsonResponse(ExactSearchJson(first ? "FIRST" : "SECOND", first ? "First Fixture" : "Second Fixture", first ? "https://example.test/first.pdf" : "https://example.test/second.pdf", first ? "https://example.test/first.ies" : "https://example.test/second.ies"));
                }
                if (uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return DownloadResponse(pdf, "application/pdf");
                if (uri.AbsolutePath.EndsWith("first.ies", StringComparison.OrdinalIgnoreCase)) return DownloadResponse(conversionRejectedIes, "text/plain");
                if (uri.AbsolutePath.EndsWith("second.ies", StringComparison.OrdinalIgnoreCase)) return DownloadResponse(ValidIes, "text/plain");
                throw new InvalidOperationException($"Unexpected request: {uri}");
            });
            using var http = new HttpClient(handler);
            var records = new[]
            {
                new ProductRecord("S", 1, "Family", "FIRST", "First Fixture"),
                new ProductRecord("S", 2, "Family", "SECOND", "Second Fixture")
            };

            var logs = await new CatalogueDownloadService(new SignifyClient(http)).DownloadAsync(records, TestOptions(root));

            Assert.Equal("conversion_failed", logs[0].Status);
            Assert.Contains("Gamma", logs[0].Error, StringComparison.Ordinal);
            Assert.True(File.Exists(logs[0].IesFile));
            Assert.False(File.Exists(Path.Combine(root, "Family", "LDT", "First Fixture.ldt")));
            Assert.Equal("converted", logs[1].Status);
            Assert.True(File.Exists(logs[1].LdtFile));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Invalid_ies_logs_failure_and_batch_continues_to_next_product()
    {
        var root = TempDirectory();
        try
        {
            var pdf = TechnicalPdfBytes();
            var badIesRequests = 0;
            var handler = new RoutingHandler(request =>
            {
                var uri = request.RequestUri!;
                if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                {
                    var query = Uri.UnescapeDataString(uri.Query);
                    var bad = query.Contains("BAD", StringComparison.OrdinalIgnoreCase);
                    return JsonResponse(ExactSearchJson(bad ? "BAD" : "GOOD", bad ? "Bad Fixture" : "Good Fixture", bad ? "https://example.test/bad.pdf" : "https://example.test/good.pdf", bad ? "https://example.test/bad.ies" : "https://example.test/good.ies"));
                }
                if (uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return DownloadResponse(pdf, "application/pdf");
                if (uri.AbsolutePath.EndsWith("bad.ies", StringComparison.OrdinalIgnoreCase))
                    return ++badIesRequests == 1 ? DownloadResponse(ValidIes, "text/plain") : DownloadResponse("not ies"u8.ToArray(), "text/plain");
                if (uri.AbsolutePath.EndsWith("good.ies", StringComparison.OrdinalIgnoreCase)) return DownloadResponse(ValidIes, "text/plain");
                throw new InvalidOperationException($"Unexpected request: {uri}");
            });
            using var http = new HttpClient(handler);
            var records = new[]
            {
                new ProductRecord("S", 1, "Family", "BAD", "Bad Fixture"),
                new ProductRecord("S", 2, "Family", "GOOD", "Good Fixture")
            };

            var logs = await new CatalogueDownloadService(new SignifyClient(http)).DownloadAsync(records, TestOptions(root));

            Assert.Equal("ies_failed_pdf_saved", logs[0].Status);
            Assert.Equal("converted", logs[1].Status);
            Assert.False(File.Exists(Path.Combine(root, "Family", "LDT", "Bad Fixture.ldt")));
            Assert.True(File.Exists(logs[1].LdtFile));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Discovery_does_not_probe_direct_pdf_for_alphanumeric_material_with_embedded_digits()
    {
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new RoutingHandler(request =>
        {
            requests.Add(request);
            if (request.RequestUri!.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return JsonResponse("""{"results":[{"sku":"AB-123","name":"Fixture 840"}]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var match = await new SignifyClient(http).FindProductAsync(
            new ProductRecord("S", 1, "Family", "  AB-123  ", "Fixture 840"),
            TestOptions(Path.GetTempPath()));

        Assert.NotNull(match);
        Assert.DoesNotContain(requests, request => request.Method == HttpMethod.Head);
        Assert.Contains(requests, request => request.RequestUri!.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Discovery_continues_to_search_after_direct_pdf_probe_timeout()
    {
        const string material = "100";
        const string product = "Fixture 840";
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new RoutingHandler(request =>
        {
            requests.Add(request);
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head) throw new TaskCanceledException("direct probe timed out");
            if (uri.AbsolutePath.EndsWith("search.ies", StringComparison.OrdinalIgnoreCase)) return DownloadResponse(ValidIes, "text/plain");
            if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(ExactSearchJson(material, product, "https://example.test/search.pdf", "https://example.test/search.ies"));
            if (uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            throw new InvalidOperationException($"Unexpected request: {uri}");
        }));

        var match = await new SignifyClient(http).FindProductAsync(
            new ProductRecord("S", 1, "Family", material, product),
            TestOptions(Path.GetTempPath()));

        Assert.NotNull(match);
        Assert.Equal("https://example.test/search.pdf", match.LeafletUrl);
        Assert.Equal("https://example.test/search.ies", match.IesUrl);
        Assert.Contains(requests, request => request.Method == HttpMethod.Head);
        Assert.Contains(requests, request => request.RequestUri!.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Direct_pdf_for_numeric_material_never_uses_same_description_ies_from_different_sku()
    {
        const string material = "100";
        const string product = "Shared Fixture";
        var suppliedIes = "https://example.test/wrong-sku.ies";
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new RoutingHandler(request =>
        {
            requests.Add(request);
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.OK);
            if (uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase)) return DownloadResponse("%PDF-1.7\nfixture"u8.ToArray(), "application/pdf");
            if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(ExactSearchJson("200", product, string.Empty, suppliedIes));
            if (uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            throw new InvalidOperationException($"Unexpected request: {uri}");
        }));

        var match = await new SignifyClient(http).FindProductAsync(
            new ProductRecord("S", 1, "Family", material, product),
            TestOptions(Path.GetTempPath()));

        Assert.NotNull(match);
        Assert.Equal(material, match.MatchedSku);
        Assert.NotEmpty(match.LeafletUrl);
        Assert.Empty(match.IesUrl);
        Assert.DoesNotContain(requests, request => request.RequestUri!.AbsoluteUri.Equals(suppliedIes, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Discovery_merges_complementary_urls_only_for_the_same_exact_sku_across_queries()
    {
        const string material = "100";
        const string product = "Fixture 840";
        using var http = new HttpClient(new RoutingHandler(request =>
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.EndsWith("fixture.ies", StringComparison.OrdinalIgnoreCase)) return DownloadResponse(ValidIes, "text/plain");
            if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (!uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unexpected request: {uri}");

            var query = Uri.UnescapeDataString(uri.Query);
            return query.Contains("query=100", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(ExactSearchJson(material, product, string.Empty, "https://example.test/fixture.ies"))
                : query.Contains("query=Fixture 840", StringComparison.OrdinalIgnoreCase)
                    ? JsonResponse(ExactSearchJson(material, product, "https://example.test/fixture.pdf", string.Empty))
                    : JsonResponse("""{"results":[]}""");
        }));

        var match = await new SignifyClient(http).FindProductAsync(
            new ProductRecord("S", 1, "Family", material, product),
            TestOptions(Path.GetTempPath()));

        Assert.NotNull(match);
        Assert.Equal("https://example.test/fixture.pdf", match.LeafletUrl);
        Assert.Equal("https://example.test/fixture.ies", match.IesUrl);
        Assert.Contains("100", match.QueryUsed, StringComparison.Ordinal);
        Assert.Contains(product, match.QueryUsed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ies_discovery_ungates_slug_fallback_for_numeric_material()
    {
        const string material = "911401549542";
        const string product = "SP570P LED50/840 L150W12 CD PSU";
        var iesRequests = new List<Uri>();
        using var http = new HttpClient(new RoutingHandler(request =>
        {
            var uri = request.RequestUri!;
            iesRequests.Add(uri);
            if (request.Method == HttpMethod.Head && uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase))
                return DownloadResponse("%PDF-1.7\nfixture"u8.ToArray(), "application/pdf");
            if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(ExactSearchJson(material, product, "https://example.test/fixture.pdf", string.Empty));
            if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            if (uri.AbsolutePath.StartsWith("/assets/IES/", StringComparison.OrdinalIgnoreCase))
                return DownloadResponse(ValidIes, "text/plain");
            if (uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
            throw new InvalidOperationException($"Unexpected request: {uri}");
        }));

        var match = await new SignifyClient(http).FindProductAsync(
            new ProductRecord("S", 1, "Family", material, product),
            TestOptions(Path.GetTempPath()));

        Assert.NotNull(match);
        Assert.NotEmpty(match.IesUrl);
        Assert.EndsWith(".ies", match.IesUrl);
        Assert.Contains(iesRequests, uri => uri.AbsolutePath.StartsWith("/assets/IES/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ies_discovery_extracts_url_from_aem_download_assets_json()
    {
        const string material = "911401549542";
        const string product = "SP570P LED50/840 L150W12 CD PSU";
        const string aemIesUrl = "https://www.docs.signify.com/assets/IES/SP570P-LED50-840-L150W12-CD-PSU.ies";
        var aemJson = $$"""{"items":[{"title":"Leaflets","list":[]},{"title":"Visuals","list":[]},{"title":"Photometry and Lighting design","list":[{"type":"ies","url":"{{aemIesUrl}}","downloadUrl":"{{aemIesUrl}}","fileName":"IES File - {{product}}","fileSize":"10.4 kB"}]}],"translations":{},"enableSelectAllButton":false}""";

        using var http = new HttpClient(new RoutingHandler(request =>
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head && uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase))
                return DownloadResponse("%PDF-1.7\nfixture"u8.ToArray(), "application/pdf");
            if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(ExactSearchJson(material, product, "https://example.test/fixture.pdf", string.Empty));
            if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(aemJson);
            if (uri.AbsoluteUri.Equals(aemIesUrl, StringComparison.OrdinalIgnoreCase))
                return DownloadResponse(ValidIes, "text/plain");
            if (uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.StartsWith("/assets/IES/", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
            throw new InvalidOperationException($"Unexpected request: {uri}");
        }));

        var match = await new SignifyClient(http).FindProductAsync(
            new ProductRecord("S", 1, "Family", material, product),
            TestOptions(Path.GetTempPath()));

        Assert.NotNull(match);
        Assert.Equal(aemIesUrl, match.IesUrl);
    }

    [Fact]
    public async Task Ies_discovery_falls_back_aem_market_segments()
    {
        const string material = "911401549542";
        const string product = "SP570P LED50/840 L150W12 CD PSU";
        const string aemIesUrl = "https://www.docs.signify.com/assets/IES/SP570P-LED50-840-L150W12-CD-PSU.ies";
        var aemJson = $$"""{"items":[{"title":"Photometry and Lighting design","list":[{"type":"ies","downloadUrl":"{{aemIesUrl}}"}]}]}""";
        var aemAttempts = 0;

        using var http = new HttpClient(new RoutingHandler(request =>
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head && uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase))
                return DownloadResponse("%PDF-1.7\nfixture"u8.ToArray(), "application/pdf");
            if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(ExactSearchJson(material, product, "https://example.test/fixture.pdf", string.Empty));
            if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase))
            {
                aemAttempts++;
                if (uri.AbsolutePath.Contains("/ae/en/", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (uri.AbsolutePath.Contains("/sa/en/", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse(aemJson);
                throw new InvalidOperationException($"Unexpected market segment: {uri}");
            }
            if (uri.AbsoluteUri.Equals(aemIesUrl, StringComparison.OrdinalIgnoreCase))
                return DownloadResponse(ValidIes, "text/plain");
            if (uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.StartsWith("/assets/IES/", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
            throw new InvalidOperationException($"Unexpected request: {uri}");
        }));

        var match = await new SignifyClient(http).FindProductAsync(
            new ProductRecord("S", 1, "Family", material, product),
            TestOptions(Path.GetTempPath()));

        Assert.NotNull(match);
        Assert.Equal(aemIesUrl, match.IesUrl);
        Assert.Equal(2, aemAttempts);
    }

    [Fact]
    public async Task Ies_discovery_continues_on_malformed_aem_json()
    {
        const string material = "911401549542";
        const string product = "SP570P LED50/840 L150W12 CD PSU";
        var slugIesUrl = string.Format("https://www.docs.signify.com/assets/IES/{0}.ies", product.ToUpperInvariant().Replace(" ", "-").Replace("/", "-"));

        using var http = new HttpClient(new RoutingHandler(request =>
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head && uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase))
                return DownloadResponse("%PDF-1.7\nfixture"u8.ToArray(), "application/pdf");
            if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(ExactSearchJson(material, product, "https://example.test/fixture.pdf", string.Empty));
            if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not valid json", Encoding.UTF8, "application/json") };
            if (uri.AbsoluteUri.Equals(slugIesUrl, StringComparison.OrdinalIgnoreCase))
                return DownloadResponse(ValidIes, "text/plain");
            if (uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.StartsWith("/assets/IES/", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
            throw new InvalidOperationException($"Unexpected request: {uri}");
        }));

        var match = await new SignifyClient(http).FindProductAsync(
            new ProductRecord("S", 1, "Family", material, product),
            TestOptions(Path.GetTempPath()));

        Assert.NotNull(match);
        Assert.Equal(slugIesUrl, match.IesUrl);
    }

    [Fact]
    public async Task Ies_discovery_resolves_relative_aem_download_url()
    {
        const string material = "911401549542";
        const string product = "SP570P LED50/840 L150W12 CD PSU";
        const string relativeIesUrl = "/assets/IES/SP570P-LED50-840-L150W12-CD-PSU.ies";
        const string absoluteIesUrl = "https://www.signify.com/assets/IES/SP570P-LED50-840-L150W12-CD-PSU.ies";
        var aemJson = $$"""{"items":[{"title":"Photometry and Lighting design","list":[{"type":"ies","downloadUrl":"{{relativeIesUrl}}"}]}]}""";

        using var http = new HttpClient(new RoutingHandler(request =>
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head && uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (uri.Host.Equals("www.assets.signify.com", StringComparison.OrdinalIgnoreCase))
                return DownloadResponse("%PDF-1.7\nfixture"u8.ToArray(), "application/pdf");
            if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(ExactSearchJson(material, product, "https://example.test/fixture.pdf", string.Empty));
            if (uri.AbsoluteUri.Equals(absoluteIesUrl, StringComparison.OrdinalIgnoreCase))
                return DownloadResponse(ValidIes, "text/plain");
            if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(aemJson);
            if (uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.StartsWith("/assets/IES/", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
            throw new InvalidOperationException($"Unexpected request: {uri}");
        }));

        var match = await new SignifyClient(http).FindProductAsync(
            new ProductRecord("S", 1, "Family", material, product),
            TestOptions(Path.GetTempPath()));

        Assert.NotNull(match);
        Assert.Equal(absoluteIesUrl, match.IesUrl);
    }

    [RequiresSymbolicLinkFact]
    public void Manual_conversion_rejects_reparse_family_ies_pdf_and_ldt_when_supported()
    {
        var root = TempDirectory();
        var outside = TempDirectory();
        try
        {
            const string product = "Fixture 840";
            var record = new ProductRecord("S", 1, "Family", "100", product);
            using var http = new HttpClient(new SequenceHandler(JsonResponse("""{"results":[]}""")));
            var service = new CatalogueDownloadService(new SignifyClient(http));
            var family = Path.Combine(root, "Family");

            CreateDirectorySymbolicLinkOrSkip(family, outside);
            var linkedFamilyProgress = new List<string>();
            Assert.Empty(service.ConvertProducts([record], root, new InlineProgress<string>(linkedFamilyProgress.Add)));
            Assert.Contains(linkedFamilyProgress, message => message.Contains("reparse", StringComparison.OrdinalIgnoreCase));
            Directory.Delete(family);

            var iesDirectory = Path.Combine(family, "IES");
            Directory.CreateDirectory(iesDirectory);
            var outsideIes = Path.Combine(outside, "Fixture.ies");
            File.WriteAllBytes(outsideIes, ValidIes);
            var linkedIes = Path.Combine(iesDirectory, product + ".ies");
            CreateFileSymbolicLinkOrSkip(linkedIes, outsideIes);
            var linkedIesProgress = new List<string>();
            Assert.Empty(service.ConvertProducts([record], root, new InlineProgress<string>(linkedIesProgress.Add)));
            Assert.Contains(linkedIesProgress, message => message.Contains("reparse", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(Path.Combine(family, "LDT")));
            File.Delete(linkedIes);

            File.WriteAllBytes(Path.Combine(iesDirectory, product + ".ies"), ValidIes);
            var outsidePdf = Path.Combine(outside, "Fixture.pdf");
            WriteTechnicalPdf(outsidePdf);
            var linkedPdf = Path.Combine(family, product + ".pdf");
            CreateFileSymbolicLinkOrSkip(linkedPdf, outsidePdf);
            var linkedPdfProgress = new List<string>();
            Assert.Empty(service.ConvertProducts([record], root, new InlineProgress<string>(linkedPdfProgress.Add)));
            Assert.Contains(linkedPdfProgress, message => message.Contains("reparse", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(Path.Combine(family, "LDT")));
            File.Delete(linkedPdf);

            var ldtDirectory = Path.Combine(family, "LDT");
            CreateDirectorySymbolicLinkOrSkip(ldtDirectory, outside);
            var linkedLdtProgress = new List<string>();
            Assert.Empty(service.ConvertProducts([record], root, new InlineProgress<string>(linkedLdtProgress.Add)));
            Assert.Contains(linkedLdtProgress, message => message.Contains("reparse", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(Directory.EnumerateFiles(outside, "*.ldt", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Manual_conversion_allows_relative_ies_without_catalogue_pdf()
    {
        var root = TempDirectory();
        try
        {
            const string product = "Relative Fixture";
            var ies = Path.Combine(root, "Family", "IES", product + ".ies");
            Directory.CreateDirectory(Path.GetDirectoryName(ies)!);
            File.WriteAllBytes(ies, ValidIes);
            using var http = new HttpClient(new SequenceHandler(JsonResponse("""{"results":[]}""")));

            var conversion = Assert.Single(new CatalogueDownloadService(new SignifyClient(http)).ConvertProducts(
                [new ProductRecord("S", 1, "Family", "100", product)], root));

            Assert.Equal(string.Empty, conversion.Context.PdfPath);
            Assert.True(File.Exists(conversion.Result.OutputPath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Manual_conversion_skips_absolute_ies_without_pdf_and_continues_to_later_product()
    {
        var root = TempDirectory();
        try
        {
            const string absoluteProduct = "Absolute Fixture";
            const string relativeProduct = "Relative Fixture";
            var absoluteIes = Path.Combine(root, "Family", "IES", absoluteProduct + ".ies");
            var relativeIes = Path.Combine(root, "Family", "IES", relativeProduct + ".ies");
            Directory.CreateDirectory(Path.GetDirectoryName(absoluteIes)!);
            File.WriteAllText(
                absoluteIes,
                Encoding.ASCII.GetString(ValidIes)
                    .Replace("[LUMINAIRE] Fixture", $"[LUMINAIRE] {absoluteProduct}", StringComparison.Ordinal)
                    .Replace("\n1 1000 1", "\n1 -1 1", StringComparison.Ordinal),
                new UTF8Encoding(false));
            File.WriteAllBytes(relativeIes, ValidIes);
            var progress = new List<string>();
            using var http = new HttpClient(new SequenceHandler(JsonResponse("""{"results":[]}""")));

            var conversions = new CatalogueDownloadService(new SignifyClient(http)).ConvertProducts(
                [
                    new ProductRecord("S", 1, "Family", "100", absoluteProduct),
                    new ProductRecord("S", 2, "Family", "200", relativeProduct)
                ],
                root,
                new InlineProgress<string>(progress.Add));

            var conversion = Assert.Single(conversions);
            Assert.Equal(relativeProduct + ".ldt", Path.GetFileName(conversion.Result.OutputPath));
            Assert.False(File.Exists(Path.Combine(root, "Family", "LDT", absoluteProduct + ".ldt")));
            Assert.Contains(progress, message => message.Contains("Absolute Fixture", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    private static readonly byte[] ValidIes = Encoding.ASCII.GetBytes("IESNA:LM-63-2002\n[TEST] fixture\n[MANUFAC] Signify\n[LUMINAIRE] Fixture\nTILT=NONE\n1 1000 1 2 1 1 2 0.1 0.1 0.1\n1 1 10\n0 90\n0\n100 50\n");

    private static void WriteTechnicalPdf(string path, string? geometry = null)
    {
        using var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(600, 800);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        page.AddText("Fixture technical data", 10, new UglyToad.PdfPig.Core.PdfPoint(40, 740), font);
        page.AddText("Luminous flux: 1000 lm", 10, new UglyToad.PdfPig.Core.PdfPoint(40, 720), font);
        page.AddText("Input power: 10 W", 10, new UglyToad.PdfPig.Core.PdfPoint(40, 700), font);
        page.AddText("CRI: 80", 10, new UglyToad.PdfPig.Core.PdfPoint(40, 680), font);
        page.AddText("CCT: 4000 K", 10, new UglyToad.PdfPig.Core.PdfPoint(40, 660), font);
        if (!string.IsNullOrWhiteSpace(geometry)) page.AddText(geometry, 10, new UglyToad.PdfPig.Core.PdfPoint(40, 640), font);
        File.WriteAllBytes(path, builder.Build());
    }

    private static byte[] TechnicalPdfBytes(string? geometry = null)
    {
        var path = Path.Combine(TempDirectory(), "fixture.pdf");
        try
        {
            WriteTechnicalPdf(path, geometry);
            return File.ReadAllBytes(path);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DocManager.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Không tìm thấy thư mục mã nguồn DocManager.");
    }

    private static void CreateDirectorySymbolicLinkOrSkip(string linkPath, string targetPath) =>
        Directory.CreateSymbolicLink(linkPath, targetPath);

    private static void CreateFileSymbolicLinkOrSkip(string linkPath, string targetPath) =>
        File.CreateSymbolicLink(linkPath, targetPath);

    private static HttpResponseMessage DownloadResponse(byte[] bytes, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(contentType);
        return response;
    }

    private static string ExactSearchJson(string material, string name, string pdfUrl, string iesUrl) =>
        $$"""{"results":[{"sku":"{{material}}","name":"{{name}}","product_codes":["{{material}}"],"leaflet_url":"{{pdfUrl}}","ies_url":"{{iesUrl}}"}]}""";

    private static RoutingHandler ProductDownloadHandler(string material, string name, byte[] pdf, byte[] ies) => new(request =>
    {
        var uri = request.RequestUri!;
        if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase))
            return JsonResponse(ExactSearchJson(material, name, "https://example.test/fixture.pdf", "https://example.test/fixture.ies"));
        if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (uri.AbsolutePath.StartsWith("/assets/IES/", StringComparison.OrdinalIgnoreCase))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return DownloadResponse(pdf, "application/pdf");
        if (uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase)) return DownloadResponse(ies, "text/plain");
        throw new InvalidOperationException($"Unexpected request: {uri}");
    });

    private static RoutingHandler IndependentAssetHandler(string material, string name, string pdfUrl, string iesUrl, byte[]? pdfResponse, byte[]? iesResponse, HttpStatusCode pdfStatus = HttpStatusCode.OK, HttpStatusCode iesStatus = HttpStatusCode.OK) => new(request =>
    {
        var uri = request.RequestUri!;
        if (request.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase)) return JsonResponse(ExactSearchJson(material, name, pdfUrl, iesUrl));
        if (uri.AbsolutePath.Contains("download-assets", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("/getPhotometricAssets/ies", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.StartsWith("/assets/IES/", StringComparison.OrdinalIgnoreCase))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return pdfStatus == HttpStatusCode.OK && pdfResponse is not null ? DownloadResponse(pdfResponse, "application/pdf") : new HttpResponseMessage(pdfStatus);
        if (uri.AbsolutePath.EndsWith(".ies", StringComparison.OrdinalIgnoreCase))
            return iesStatus == HttpStatusCode.OK && iesResponse is not null ? DownloadResponse(iesResponse, "text/plain") : new HttpResponseMessage(iesStatus);
        throw new InvalidOperationException($"Unexpected request: {uri}");
    });

    private static DownloadOptions TestOptions(string root) => new() { OutputRoot = root, Retries = 1, RetryBaseDelay = TimeSpan.Zero, PerItemDelay = TimeSpan.Zero };
    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private static ByteArrayContent PdfContent() { var content = new ByteArrayContent("%PDF-1.7"u8.ToArray()); content.Headers.ContentType = new("application/pdf"); return content; }
    private static string TempDirectory() { var path = Path.Combine(Path.GetTempPath(), $"DocManagerSignify-{Guid.NewGuid():N}"); Directory.CreateDirectory(path); return path; }

    private static void CreatePricelistFixture(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddXml(archive, "[Content_Types].xml", """<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
        AddXml(archive, "_rels/.rels", """<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        AddXml(archive, "xl/workbook.xml", """<?xml version="1.0"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Main" sheetId="1" r:id="rId1"/><sheet name="Second" sheetId="2" r:id="rId2"/></sheets></workbook>""");
        AddXml(archive, "xl/_rels/workbook.xml.rels", """<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/></Relationships>""");
        AddXml(archive, "xl/worksheets/sheet1.xml", SheetXml("911401685409", "RC099V G3 LED69 865", duplicate: true));
        AddXml(archive, "xl/worksheets/sheet2.xml", SheetXml("929002295302", "TLED Fixture", duplicate: false));
    }

    private static string SheetXml(string material, string description, bool duplicate)
    {
        var duplicateRow = duplicate ? $"<row r=\"4\"><c r=\"A4\" t=\"inlineStr\"><is><t>Family</t></is></c><c r=\"B4\"><v>{material}</v></c><c r=\"C4\" t=\"inlineStr\"><is><t>{description}</t></is></c></row>" : string.Empty;
        return $"<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Title</t></is></c></row><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Product Family</t></is></c><c r=\"B2\" t=\"inlineStr\"><is><t>Material</t></is></c><c r=\"C2\" t=\"inlineStr\"><is><t>Material description</t></is></c></row><row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>Family</t></is></c><c r=\"B3\"><v>{material}</v></c><c r=\"C3\" t=\"inlineStr\"><is><t>{description}</t></is></c></row>{duplicateRow}</sheetData></worksheet>";
    }

    private static void AddXml(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    [Fact]
    public async Task Parallel_downloads_preserve_input_order_when_items_complete_out_of_order()
    {
        var delayMs = new[] { 100, 0, 0 };
        var callCount = 0;
        var handler = new RoutingHandler(request =>
        {
            var idx = Interlocked.Increment(ref callCount) - 1;
            var delay = idx < delayMs.Length ? delayMs[idx] : 0;
            if (delay > 0)
            {
                Task.Delay(delay).Wait();
            }
            return JsonResponse("""{"results":[]}""");
        });
        using var http = new HttpClient(handler);
        var service = new CatalogueDownloadService(new SignifyClient(http));
        var records = new[]
        {
            new ProductRecord("S", 1, "F", "", "First"),
            new ProductRecord("S", 2, "F", "", "Second"),
            new ProductRecord("S", 3, "F", "", "Third")
        };
        var logs = await service.DownloadAsync(records, TestOptions(Path.GetTempPath()) with { MaxConcurrency = 3 });
        Assert.Equal(3, logs.Count);
        Assert.Equal("First", logs[0].MaterialDescription);
        Assert.Equal("Second", logs[1].MaterialDescription);
        Assert.Equal("Third", logs[2].MaterialDescription);
    }

    [Fact]
    public async Task Parallel_downloads_respect_max_concurrency_limit()
    {
        var maxConcurrent = 0;
        var peakConcurrent = 0;
        var concurrentLock = new object();
        var handler = new RoutingHandler(request =>
        {
            lock (concurrentLock)
            {
                Interlocked.Increment(ref maxConcurrent);
                peakConcurrent = Math.Max(peakConcurrent, maxConcurrent);
            }
            Task.Delay(50).Wait();
            lock (concurrentLock)
            {
                Interlocked.Decrement(ref maxConcurrent);
            }
            return JsonResponse("""{"results":[]}""");
        });
        using var http = new HttpClient(handler);
        var service = new CatalogueDownloadService(new SignifyClient(http));
        var records = Enumerable.Range(0, 10)
            .Select(i => new ProductRecord("S", i + 1, "F", "", $"Product {i}"))
            .ToArray();
        var logs = await service.DownloadAsync(records, TestOptions(Path.GetTempPath()) with { MaxConcurrency = 2 });
        Assert.Equal(10, logs.Count);
        Assert.True(peakConcurrent <= 2, $"Peak concurrent requests was {peakConcurrent}, expected <= 2");
    }

    [Fact]
    public async Task Parallel_downloads_continue_after_one_product_throws()
    {
        var handler = new RoutingHandler(request =>
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            if (query.Contains("FAIL", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("Simulated failure");
            return JsonResponse("""{"results":[]}""");
        });
        using var http = new HttpClient(handler);
        var service = new CatalogueDownloadService(new SignifyClient(http));
        var records = new[]
        {
            new ProductRecord("S", 1, "F", "", "FAIL"),
            new ProductRecord("S", 2, "F", "", "OK")
        };
        var logs = await service.DownloadAsync(records, TestOptions(Path.GetTempPath()) with { MaxConcurrency = 2 });
        Assert.Equal(2, logs.Count);
        Assert.Equal("download_failed", logs[0].Status);
        Assert.Equal("no_leaflet", logs[1].Status);
    }

    [Fact]
    public async Task Parallel_downloads_cancellation_propagates_and_does_not_mark_items_as_failed()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RoutingHandler(request =>
        {
            cts.Cancel();
            Task.Delay(50).Wait();
            return JsonResponse("""{"results":[]}""");
        });
        using var http = new HttpClient(handler);
        var service = new CatalogueDownloadService(new SignifyClient(http));
        var records = new[]
        {
            new ProductRecord("S", 1, "F", "", "One"),
            new ProductRecord("S", 2, "F", "", "Two")
        };
        var exception = await Record.ExceptionAsync(
            async () => await service.DownloadAsync(records, TestOptions(Path.GetTempPath()), cancellationToken: cts.Token));
        Assert.NotNull(exception);
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public List<HttpResponseMessage> Responses { get; } = [.. responses];
        public List<Uri> Requests { get; } = [];
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var response = Responses[Math.Min(Calls, Responses.Count - 1)]; Calls++; return Task.FromResult(response);
        }
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(route(request));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
