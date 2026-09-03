using DocManager.Core;
using DocManager.Dialux;
using DocManager.Photometry;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace DocManager.Tests;

public sealed class CoreAndCatalogueTests
{
    [Fact]
    public void Exception_log_formatter_preserves_context_and_full_exception_chain()
    {
        var exception = CaptureException();

        var detail = ExceptionLogFormatter.Format("Catalogue conversion", exception);

        Assert.StartsWith("Catalogue conversion: ", detail, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidDataException), detail, StringComparison.Ordinal);
        Assert.Contains("outer failure", detail, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), detail, StringComparison.Ordinal);
        Assert.Contains("inner failure", detail, StringComparison.Ordinal);
        Assert.Contains("at ", detail, StringComparison.Ordinal);

        static InvalidDataException CaptureException()
        {
            try { throw new InvalidDataException("outer failure", new InvalidOperationException("inner failure")); }
            catch (InvalidDataException exception) { return exception; }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Exception_log_formatter_rejects_blank_context(string? context)
    {
        Assert.ThrowsAny<ArgumentException>(() => ExceptionLogFormatter.Format(context!, new InvalidOperationException("failure")));
    }

    [Fact]
    public void Exception_log_formatter_rejects_null_exception()
    {
        Assert.Throws<ArgumentNullException>(() => ExceptionLogFormatter.Format("context", null!));
    }

    [Theory]
    [InlineData("1.234,56 lm", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("3,210 lm", 3210)]
    [InlineData("1.200 W", 1200)]
    [InlineData("9,8", 9.8)]
    public void Localized_number_parser_handles_common_formats(string text, double expected)
    {
        Assert.True(LocalizedNumber.TryParse(text, out var actual));
        Assert.Equal(expected, actual, 4);
    }

    public static TheoryData<string> MarketingLists => new()
    {
        "CCT: 3000 K, 4000 K, 6500 K",
        "CCT: 3000 K | 4000 K | 6500 K",
        "CCT: 3000 K & 4000 K & 6500 K",
        "CCT: 3000 K to 4000 K to 6500 K",
        "CCT: 3000 K, 4000 K and 6500 K",
        "CCT: 3000 K 4000 K 6500 K"
    };

    [Theory, MemberData(nameof(MarketingLists))]
    public void Strict_cct_rejects_marketing_lists(string marketing)
    {
        Assert.Null(CctParser.ExtractStrict(marketing));
        var entry = CatalogueIndex.Parse("TEST", [], "fixture.pdf", marketing);
        Assert.Equal(string.Empty, entry.Cct);
    }

    [Theory]
    [InlineData("Correlated colour temperature (Nom.) = 4,000 K")]
    [InlineData("Nhiệt độ màu: 4.000 K")]
    public void Strict_cct_accepts_grouped_technical_kelvin(string technical)
    {
        Assert.Equal(4000, CctParser.ExtractStrict(technical));
        Assert.Equal("4000 K", CatalogueIndex.Parse("TEST", [], "fixture.pdf", technical).Cct);
    }

    [Theory]
    [InlineData("CCT: 3,000 K, 4,000 K, 6,500 K")]
    [InlineData("CCT: 3.000 K | 4.000 K | 6.500 K")]
    public void Strict_cct_rejects_grouped_marketing_lists(string marketing)
    {
        Assert.Null(CctParser.ExtractStrict(marketing));
    }

    [Theory, MemberData(nameof(MarketingLists))]
    public void Strict_cct_skips_marketing_and_reads_technical_row(string marketing)
    {
        var text = $"{marketing}\nCorrelated colour temperature (Nom.) = 4000 K";
        Assert.Equal(4000, CctParser.ExtractStrict(text));
        Assert.Equal("4000 K", CatalogueIndex.Parse("TEST", [], "fixture.pdf", text).Cct);
    }

    [Fact]
    public void Visual_word_rows_restore_catalogue_technical_fields_without_accepting_marketing_cct()
    {
        static PositionedWord Word(string text, double left, double bottom, double right, double top) => new(text, left, bottom, right, top);
        var words = new[]
        {
            Word("Dải", 10, 90, 25, 100), Word("nhiệt", 30, 90, 50, 100), Word("độ", 55, 90, 65, 100), Word("màu", 70, 90, 90, 100),
            Word("3000K,", 95, 90, 125, 100), Word("4000K", 130, 90, 160, 100), Word("và", 165, 90, 177, 100), Word("6500K", 182, 90, 212, 100),
            Word("Quang", 10, 70, 35, 80), Word("thông", 40, 70, 65, 80), Word("1.235", 100, 70, 125, 80), Word("lm", 130, 70, 140, 80),
            Word("Nhiệt", 10, 50, 32, 60), Word("độ", 37, 50, 47, 60), Word("màu", 52, 50, 72, 60), Word("(CCT)", 77, 50, 102, 60), Word("3000", 110, 50, 132, 60), Word("K", 137, 50, 143, 60),
            Word("Công", 10, 30, 30, 40), Word("suất", 35, 30, 52, 40), Word("12", 100, 30, 110, 40), Word("W", 115, 30, 123, 40)
        };

        var visualText = PdfTextExtractor.ReconstructVisualText(words);
        Assert.Null(CctParser.ExtractStrict("Nhiệt độ màu (CCT) 3000 K, 4000 K và 6500 K"));
        var entry = CatalogueIndex.Parse("DN068B", ["G2", "LED12", "830", "PSU", "D125"], "fixture.pdf", visualText);

        Assert.Equal("3000 K", entry.Cct);
        Assert.Equal(1235, entry.LuminousFluxLumens);
        Assert.Equal(12, entry.PowerWatts);
    }

    [Fact]
    public void Split_vietnamese_correlated_nominal_cct_row_is_accepted_without_compacting_unrelated_text()
    {
        static PositionedWord Word(string text, double left, double y) => new(text, left, y, left + Math.Max(8, text.Length * 5), y + 9);
        var words = new[]
        {
            Word("Nhi", 10, 50), Word("ệt", 32, 50), Word("độ", 50, 50), Word("màu", 68, 50), Word("tương", 92, 50), Word("quan", 122, 50),
            Word("(Danh", 150, 50), Word("định)", 180, 50), Word("4000", 220, 50), Word("K", 250, 50),
            Word("M", 280, 50), Word("ức", 292, 50), Word("tiêu", 310, 50), Word("thụ", 335, 50), Word("điện", 355, 50), Word("9,8", 385, 50), Word("W", 410, 50)
        };

        var visualText = PdfTextExtractor.ReconstructVisualText(words);

        Assert.Contains("Nhi ệt độ màu tương quan (Danh định) 4000 K", visualText);
        Assert.Equal(4000, CctParser.ExtractStrict(visualText));
        Assert.Equal("4000 K", CatalogueIndex.Parse("DN391B", ["LED12", "840"], "fixture.pdf", visualText).Cct);
    }

    [Theory]
    [InlineData("Nhi ệt độ màu tương quan (Danh định) tùy chọn 3000 K, 4000 K")]
    [InlineData("Nhi ệt độ màu tương quan (Danh định) 3000 K 4000 K")]
    public void Split_vietnamese_cct_still_rejects_marketing_or_multiple_kelvin_rows(string text)
    {
        Assert.Null(CctParser.ExtractStrict(text));
    }

    [Theory]
    [InlineData("Ta25̊C", "25°C")]
    [InlineData("Operating temperature Ta25̊C", "25°C")]
    [InlineData("Tq 25̊C", "25°C")]
    public void Ambient_temperature_normalizer_repairs_pdf_control_and_combining_degree_glyphs(string source, string expected)
    {
        Assert.Equal(expected, AmbientTemperatureNormalizer.Normalize(source));
        Assert.DoesNotContain("Ta", AmbientTemperatureNormalizer.Normalize(source), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tq", AmbientTemperatureNormalizer.Normalize(source), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Family_matrix_maps_supported_variant_cct_dimensions_and_malformed_ambient()
    {
        var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Color & CCT"] = "4000K, 6500K",
            ["CRI"] = "80",
            ["Operating temperature"] = "Ta25̊C",
            ["Dimension. (mm)"] = "1200mm / 600mm x 54mm x 36mm"
        };
        var common = new CatalogueEntry { ArticleNumber = "FAMILY", IsFamilyDocument = true };

        var nw = CatalogueIndex.MapFamilyTechnicalFacts(common,
            new CatalogueProductDescriptor("100000000001", "FAMILY", ["LED20", "NW", "L1200"], "FAMILY LED20 NW L1200", 2), "China mainland & GM", facts);
        var cw = CatalogueIndex.MapFamilyTechnicalFacts(common,
            new CatalogueProductDescriptor("100000000002", "FAMILY", ["LED20", "CW", "L1200"], "FAMILY LED20 CW L1200", 2), "China mainland & GM", facts);
        var shortNw = CatalogueIndex.MapFamilyTechnicalFacts(common,
            new CatalogueProductDescriptor("100000000003", "FAMILY", ["LED10", "NW", "L600"], "FAMILY LED10 NW L600", 2), "China mainland & GM", facts);
        var warm = CatalogueIndex.MapFamilyTechnicalFacts(common,
            new CatalogueProductDescriptor("100000000004", "FAMILY", ["LED20", "WW", "L1200"], "FAMILY LED20 WW L1200", 2), "China mainland & GM",
            new Dictionary<string, string> { ["Color & CCT"] = "3000K, 4000K, 6500K" });

        Assert.Equal("4000 K", nw.Cct);
        Assert.Equal("derived-from-variant-code:NW;source-supported-candidate:4000K", nw.CctProvenance);
        Assert.Equal("6500 K", cw.Cct);
        Assert.Equal("derived-from-variant-code:CW;source-supported-candidate:6500K", cw.CctProvenance);
        Assert.Equal("3000 K", warm.Cct);
        Assert.Equal("derived-from-variant-code:WW;source-supported-candidate:3000K", warm.CctProvenance);
        Assert.Equal("80", nw.Cri);
        Assert.Equal("25°C", nw.AmbientTemperature);
        Assert.Equal("1200x54x36mm", nw.Dimensions);
        Assert.Equal("600x54x36mm", shortNw.Dimensions);
    }

    [Fact]
    public void Family_matrix_variant_cct_requires_a_source_candidate_containing_the_mapping()
    {
        var common = new CatalogueEntry { ArticleNumber = "FAMILY", IsFamilyDocument = true };
        var descriptor = new CatalogueProductDescriptor("100000000001", "FAMILY", ["LED20", "NW", "L1200", "GM"], "FAMILY LED20 NW L1200 GM", 2);

        var withoutCctFact = CatalogueIndex.MapFamilyTechnicalFacts(common, descriptor, "GM",
            new Dictionary<string, string> { ["CRI"] = "80" });
        var unsupportedCandidate = CatalogueIndex.MapFamilyTechnicalFacts(common, descriptor, "GM",
            new Dictionary<string, string> { ["Color & CCT"] = "3000K, 6500K" });

        Assert.Equal(string.Empty, withoutCctFact.Cct);
        Assert.Equal(string.Empty, withoutCctFact.CctProvenance);
        Assert.Equal(string.Empty, unsupportedCandidate.Cct);
        Assert.Equal(string.Empty, unsupportedCandidate.CctProvenance);
    }

    [Fact]
    public void Family_technical_sections_follow_descriptor_page_then_nearest_preceding_then_following()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerFamilySections-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "BROCHURE.pdf");
        try
        {
            using (var builder = new PdfDocumentBuilder())
            {
                var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
                AddDescriptorPage(builder, font, "840");
                AddTechnicalPage(builder, font, "80");
                AddDescriptorPage(builder, font, "865");
                AddTechnicalPage(builder, font, "90");
                AddTechnicalPage(builder, font, "95", "830");
                File.WriteAllBytes(path, builder.Build());
            }

            var index = new CatalogueIndex();
            index.Build(root);

            Assert.Equal("80", index.Find("FX100", "FX100 LED20 840 L1200 GM")?.Cri);
            Assert.Equal("80", index.Find("FX100", "FX100 LED20 865 L1200 GM")?.Cri);
            Assert.Equal("95", index.Find("FX100", "FX100 LED20 830 L1200 GM")?.Cri);
        }
        finally { Directory.Delete(root, true); }

        static void AddDescriptorPage(PdfDocumentBuilder builder, PdfDocumentBuilder.AddedFont font, string cct)
        {
            var page = builder.AddPage(600, 800);
            page.AddText("12NC", 10, new PdfPoint(40, 740), font);
            page.AddText("Product", 10, new PdfPoint(150, 740), font);
            page.AddText("Description", 10, new PdfPoint(210, 740), font);
            page.AddText($"911401863{cct}", 10, new PdfPoint(40, 720), font);
            page.AddText("FX100", 10, new PdfPoint(150, 720), font);
            page.AddText($"LED20 {cct} L1200 GM", 10, new PdfPoint(210, 720), font);
        }

        static void AddTechnicalPage(PdfDocumentBuilder builder, PdfDocumentBuilder.AddedFont font, string cri, string? descriptorCct = null)
        {
            var page = builder.AddPage(600, 800);
            page.AddText("FX100", 10, new PdfPoint(40, 740), font);
            page.AddText("GM", 10, new PdfPoint(150, 720), font);
            page.AddText("CRI", 10, new PdfPoint(40, 700), font);
            page.AddText(cri, 10, new PdfPoint(150, 700), font);
            if (descriptorCct is null) return;
            page.AddText("12NC", 10, new PdfPoint(40, 660), font);
            page.AddText("Product", 10, new PdfPoint(150, 660), font);
            page.AddText("Description", 10, new PdfPoint(210, 660), font);
            page.AddText($"911401863{descriptorCct}", 10, new PdfPoint(40, 640), font);
            page.AddText("FX100", 10, new PdfPoint(150, 640), font);
            page.AddText($"LED20 {descriptorCct} L1200 GM", 10, new PdfPoint(210, 640), font);
        }
    }

    [Fact]
    public void Family_common_facts_require_every_selected_column_to_supply_the_value()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerFamilyCommonFacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "BROCHURE.pdf");
        try
        {
            using (var builder = new PdfDocumentBuilder())
            {
                var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
                var descriptors = builder.AddPage(600, 800);
                descriptors.AddText("12NC", 10, new PdfPoint(40, 740), font);
                descriptors.AddText("Product", 10, new PdfPoint(150, 740), font);
                descriptors.AddText("Description", 10, new PdfPoint(210, 740), font);
                descriptors.AddText("911401863840", 10, new PdfPoint(40, 720), font);
                descriptors.AddText("FX200", 10, new PdfPoint(150, 720), font);
                descriptors.AddText("LED20 840 L1200 TW", 10, new PdfPoint(210, 720), font);
                descriptors.AddText("911401863865", 10, new PdfPoint(40, 700), font);
                descriptors.AddText("FX200", 10, new PdfPoint(150, 700), font);
                descriptors.AddText("LED20 865 L1200 GM", 10, new PdfPoint(210, 700), font);

                var technical = builder.AddPage(600, 800);
                technical.AddText("FX200", 10, new PdfPoint(40, 740), font);
                technical.AddText("Taiwan", 10, new PdfPoint(150, 720), font);
                technical.AddText("GM", 10, new PdfPoint(260, 720), font);
                technical.AddText("Input Voltage", 10, new PdfPoint(40, 700), font);
                technical.AddText("220-240 V", 10, new PdfPoint(260, 700), font);
                technical.AddText("CRI", 10, new PdfPoint(40, 680), font);
                technical.AddText("80", 10, new PdfPoint(150, 680), font);
                technical.AddText("80", 10, new PdfPoint(260, 680), font);
                File.WriteAllBytes(path, builder.Build());
            }

            var index = new CatalogueIndex();
            index.Build(root);

            var taiwan = Assert.IsType<CatalogueEntry>(index.Find("FX200", "FX200 LED20 840 L1200 TW"));
            var gm = Assert.IsType<CatalogueEntry>(index.Find("FX200", "FX200 LED20 865 L1200 GM"));
            Assert.Equal(string.Empty, taiwan.Voltage);
            Assert.Equal(string.Empty, gm.Voltage);
            Assert.Equal("80", taiwan.Cri);
            Assert.Equal("80", gm.Cri);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Actual_dn391b_catalogue_reads_split_vietnamese_cct_when_fixture_exists()
    {
        var path = @"D:\Claude\DocManager\Product Family\GreenSpace G6 UE\911401534644 - DN391B LED12_840 P10PSU D100 AL GMG2HE.pdf";
        if (!File.Exists(path)) return;
        var pages = new PdfTextExtractor().Extract(path);
        var text = string.Join('\n', pages.Select(page => PdfTextExtractor.ReconstructVisualText(page.Words)));
        var identity = CatalogueIndex.IdentityFromFileName(path);

        Assert.Equal("4000 K", CatalogueIndex.Parse(identity.Article, identity.Tokens, path, text).Cct);
    }

    [Fact]
    public void Catalogue_parse_enriches_protection_voltage_temperature_dimensions_and_beam_angle()
    {
        var text = """
        Mã bảo vệ chống xâm nhập IP 20 / IP 54
        Mã bảo vệ khỏi tác động cơ học IK 03
        Input voltage 220 to 240 V AC
        Ambient temperature range -20 °C to +40 °C
        Beam angle of light source: 120 deg
        Dimensions 600 x 600 x 45 mm
        """;
        var entry = CatalogueIndex.Parse("RC099V", [], "fixture.pdf", text);
        Assert.Equal("IP20/IP54", entry.IpCode);
        Assert.Equal("IK03", entry.IkCode);
        Assert.Contains("220 to 240 V AC", entry.Voltage);
        Assert.Equal("-20 to +40°C", entry.AmbientTemperature);
        Assert.Equal("120 deg", entry.BeamAngle);
        Assert.Equal("L 600 mm x W 600 mm x H 45 mm", entry.Dimensions);
    }

    [Theory]
    [InlineData("Nhiệt độ môi trường hiệu quả: Ta 25 °C", "25°C")]
    [InlineData("Nhiệt độ môi trường: -20 °C đến +45 °C", "-20 đến +45°C")]
    [InlineData("Nhiệt độ xung quanh Tq 25°C", "25°C")]
    [InlineData("Phạm vi nhiệt độ môi trường xung quanh -20 °C đến +45 °C", "-20 đến +45°C")]
    [InlineData("Nhiệt độ hiệu quả Tq 25 °C", "25°C")]
    [InlineData("Operating temperature Ta -20 °C to +40 °C", "-20 to +40°C")]
    [InlineData("Ambient temperature: 25 °C", "25°C")]
    [InlineData("Ambient temperature range -20 °C to +40 °C", "-20 to +40°C")]
    public void Catalogue_parse_stores_ambient_temperature_value_without_labels(string text, string expected)
    {
        Assert.Equal(expected, CatalogueIndex.Parse("DN391B", ["LED12", "840"], "fixture.pdf", text).AmbientTemperature);
    }

    [Fact]
    public void Catalogue_parse_removes_dn391b_effective_temperature_label_and_tq_qualifier()
    {
        var entry = CatalogueIndex.Parse("DN391B", ["LED12", "840"], "fixture.pdf", "Nhiệt độ hiệu quả Tq 25 °C");

        Assert.Equal("25°C", entry.AmbientTemperature);
    }

    [Theory]
    [InlineData("Beam angle: 120°", "120°")]
    [InlineData("Optical beam angle = 36 degrees", "36 degrees")]
    [InlineData("Góc chiếu: 60 độ", "60 độ")]
    [InlineData("Góc chùm tia - 15 deg", "15 deg")]
    public void Catalogue_parse_reads_labeled_beam_angle_fields(string text, string expected)
    {
        Assert.Equal(expected, CatalogueIndex.Parse("TEST", [], "fixture.pdf", text).BeamAngle);
    }

    [Fact]
    public void Positioned_vietnamese_beam_angle_row_is_parsed_without_optics_prose_false_positive()
    {
        static PositionedWord Word(string text, double left, double y) => new(text, left, y, left + Math.Max(8, text.Length * 5), y + 9);
        var visualText = PdfTextExtractor.ReconstructVisualText([
            Word("Góc", 10, 50), Word("chiếu", 35, 50), Word("120°", 80, 50),
            Word("Ống", 10, 30), Word("kính", 35, 30), Word("quang", 60, 30), Word("học", 90, 30), Word("được", 115, 30), Word("thiết", 145, 30), Word("kế", 170, 30), Word("ở", 190, 30), Word("góc", 205, 30), Word("45°", 230, 30)
        ]);

        Assert.Equal("120°", CatalogueIndex.Parse("TEST", [], "fixture.pdf", visualText).BeamAngle);
        Assert.Equal(string.Empty, CatalogueIndex.Parse("TEST", [], "fixture.pdf", "The optics are designed at 45° for visual comfort.").BeamAngle);
    }

    [Fact]
    public void Pdf_product_extractor_returns_cropped_bounded_png_and_caches_by_file_stamp()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerPdfImage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var pdfPath = Path.Combine(root, "TEST G2.pdf");
        try
        {
            var embedded = TestPng(400, 240);
            using (var builder = new PdfDocumentBuilder())
            {
                var page = builder.AddPage(600, 800);
                page.AddPng(embedded, new PdfRectangle(220, 360, 560, 610));
                File.WriteAllBytes(pdfPath, builder.Build());
            }

            var extractor = new PdfPigProductImageExtractor();
            var first = Assert.IsType<PdfProductImage>(extractor.Extract(pdfPath));
            var second = Assert.IsType<PdfProductImage>(extractor.Extract(pdfPath));

            Assert.True(first.Png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.True(first.Png.Length > 100);
            Assert.InRange(first.Aspect, 1.60, 1.75);
            Assert.Same(first, second);
            AssertPngDimensions(first.Png, (width, height) =>
            {
                Assert.Equal(400, width);
                Assert.Equal(240, height);
                Assert.True(width < 600 && height < 800);
            });
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Pdf_product_extractor_decodes_page_one_jpeg_product_raster()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerPdfJpeg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var pdfPath = Path.Combine(root, "TEST JPEG.pdf");
        try
        {
            using (var builder = new PdfDocumentBuilder())
            {
                var page = builder.AddPage(600, 800);
                page.AddJpeg(TestJpeg(420, 280), new PdfRectangle(300, 430, 560, 610));
                File.WriteAllBytes(pdfPath, builder.Build());
            }

            var image = Assert.IsType<PdfProductImage>(new PdfPigProductImageExtractor().Extract(pdfPath));
            Assert.True(image.Png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            var diagnostics = Assert.IsType<PdfProductImageDiagnostics>(image.Diagnostics);
            Assert.Equal(420, diagnostics.SourceWidth);
            Assert.Equal(280, diagnostics.SourceHeight);
            Assert.True(diagnostics.CropAreaRatio < .50);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Pdf_product_extractor_bounds_positive_and_negative_cache_entries_by_lru()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerPdfLru-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var positive = Path.Combine(root, "A.pdf");
            var negative = Path.Combine(root, "B.pdf");
            var replacement = Path.Combine(root, "C.pdf");
            WriteImagePdf(positive, TestPng(400, 240));
            WriteEmptyPdf(negative);
            WriteImagePdf(replacement, TestPng(320, 200));
            var extractor = new PdfPigProductImageExtractor(maximumCacheEntries: 2, maximumCacheBytes: 64 * 1024 * 1024);

            var first = Assert.IsType<PdfProductImage>(extractor.Extract(positive));
            Assert.Null(extractor.Extract(negative));
            Assert.Same(first, extractor.Extract(positive)); // A becomes most recently used.
            Assert.NotNull(extractor.Extract(replacement)); // Evicts negative B.

            Assert.Equal(2, extractor.CacheUsage.Entries);
            Assert.Equal(first.Png.LongLength + extractor.Extract(replacement)!.Png.LongLength, extractor.CacheUsage.ImageBytes);
            File.Delete(positive);
            Assert.Null(extractor.Extract(positive));
            Assert.Equal(1, extractor.CacheUsage.Entries); // Removed A is evicted; missing paths are not cached.
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Pdf_product_extractor_enforces_png_byte_budget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerPdfBudget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var firstPath = Path.Combine(root, "A.pdf");
            var secondPath = Path.Combine(root, "B.pdf");
            WriteImagePdf(firstPath, TestPng(400, 240));
            WriteImagePdf(secondPath, TestPng(320, 200));
            var probe = new PdfPigProductImageExtractor();
            var firstSize = Assert.IsType<PdfProductImage>(probe.Extract(firstPath)).Png.LongLength;
            var secondSize = Assert.IsType<PdfProductImage>(probe.Extract(secondPath)).Png.LongLength;
            var budget = Math.Max(firstSize, secondSize);
            var extractor = new PdfPigProductImageExtractor(maximumCacheEntries: 8, maximumCacheBytes: budget);

            Assert.NotNull(extractor.Extract(firstPath));
            Assert.NotNull(extractor.Extract(secondPath));

            Assert.Equal(1, extractor.CacheUsage.Entries);
            Assert.InRange(extractor.CacheUsage.ImageBytes, 1, budget);
        }
        finally { Directory.Delete(root, true); }
    }

    [RequiresSymbolicLinkFact]
    public void Catalogue_build_skips_reparse_directories_and_files_when_supported()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerCatalogueReparse-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"DocManagerCatalogueOutside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            var direct = Path.Combine(root, "DIRECT Fixture.pdf");
            WriteEmptyPdf(direct);
            var outsidePdf = Path.Combine(outside, "OUTSIDE Fixture.pdf");
            WriteEmptyPdf(outsidePdf);
            var linkedDirectory = Path.Combine(root, "linked-directory");
            CreateDirectorySymbolicLinkOrSkip(linkedDirectory, outside);
            var index = new CatalogueIndex();
            index.Build(root);
            Assert.Contains(index.Entries, entry => entry.SourcePdf.Equals(Path.GetFullPath(direct), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(index.Entries, entry => entry.SourcePdf.Contains("OUTSIDE", StringComparison.OrdinalIgnoreCase));
            Directory.Delete(linkedDirectory);

            var linkedPdf = Path.Combine(root, "LINKED Fixture.pdf");
            CreateFileSymbolicLinkOrSkip(linkedPdf, outsidePdf);
            var second = new CatalogueIndex();
            second.Build(root);
            Assert.DoesNotContain(second.Entries, entry => entry.SourcePdf.Equals(Path.GetFullPath(linkedPdf), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [RequiresSymbolicLinkFact]
    public void Catalogue_build_rejects_reparse_root_when_supported()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerCatalogueRoot-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"DocManagerCatalogueRootOutside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            var linkedRoot = Path.Combine(root, "linked-root");
            CreateDirectorySymbolicLinkOrSkip(linkedRoot, outside);
            Assert.Throws<InvalidDataException>(() => new CatalogueIndex().Build(linkedRoot));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Catalogue_build_populates_image_and_keeps_metadata_when_image_extraction_fails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerCatalogueImage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "TEST LED12_840.pdf");
        var png = TestPng(400, 240);
        try
        {
            using (var builder = new PdfDocumentBuilder())
            {
                var page = builder.AddPage(600, 800);
                page.AddPng(png, new PdfRectangle(220, 360, 560, 610));
                File.WriteAllBytes(path, builder.Build());
            }

            var success = new CatalogueIndex(imageExtractor: new StubImageExtractor(new PdfProductImage(png, 5d / 3)));
            success.Build(root);
            var enriched = Assert.Single(success.Entries);
            Assert.Equal(png, enriched.ImagePng);
            Assert.Equal(5d / 3, enriched.ImageAspect);

            var failure = new CatalogueIndex(imageExtractor: new ThrowingImageExtractor());
            failure.Build(root);
            var metadataOnly = Assert.Single(failure.Entries);
            Assert.Equal(enriched.ArticleNumber, metadataOnly.ArticleNumber);
            Assert.Equal(enriched.Tokens, metadataOnly.Tokens);
            Assert.Null(metadataOnly.ImagePng);
            Assert.Null(metadataOnly.ImageAspect);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Generic_filename_single_descriptor_propagates_descriptor_evidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerDescriptorFallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "brochure.pdf");
            using (var builder = new PdfDocumentBuilder())
            {
                var page = builder.AddPage(600, 800);
                var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
                page.AddText("12NC", 10, new PdfPoint(40, 740), font);
                page.AddText("Product", 10, new PdfPoint(150, 740), font);
                page.AddText("Description", 10, new PdfPoint(200, 740), font);
                page.AddText("911401863187", 10, new PdfPoint(40, 720), font);
                page.AddText("DN068B", 10, new PdfPoint(150, 720), font);
                page.AddText("LED16 840 PSU", 10, new PdfPoint(200, 720), font);
                File.WriteAllBytes(path, builder.Build());
            }

            var index = new CatalogueIndex();
            index.Build(root);

            var entry = Assert.Single(index.Entries);
            Assert.Equal("DN068B", entry.ArticleNumber);
            Assert.Equal("911401863187", entry.Sku);
            Assert.Equal("DN068B LED16 840 PSU", entry.VariantDescription);
            Assert.True(entry.HasVariantEvidence);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Catalogue_build_keeps_byte_identical_pdfs_with_distinct_filename_identities()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerCatalogueIdentities-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "RC099V G3 LED69 840 PSU.pdf");
            var duplicateBytes = Path.Combine(root, "RC099V G3 LED69 865 PSU.pdf");
            WriteEmptyPdf(source);
            File.Copy(source, duplicateBytes);

            var index = new CatalogueIndex();
            index.Build(root);

            Assert.Equal(2, index.Entries.Count);
            Assert.Contains(index.Entries, entry => entry.Tokens.Contains("840", StringComparer.OrdinalIgnoreCase) && entry.SourcePdf.Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(index.Entries, entry => entry.Tokens.Contains("865", StringComparer.OrdinalIgnoreCase) && entry.SourcePdf.Equals(Path.GetFullPath(duplicateBytes), StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Actual_dn068b_catalogue_extracts_validated_fields_and_page_one_image_when_fixture_folder_exists()
    {
        var root = @"D:\Claude\DocManager\Product Family\Smartbright Pro DN068B G2";
        if (!Directory.Exists(root)) return;
        var index = new CatalogueIndex();
        index.Build(root);

        var expected = new Dictionary<string, (string Cct, double Flux, double Power)>
        {
            ["DN068B G2 LED12 830 PSU D125.pdf"] = ("3000 K", 1235, 12),
            ["DN068B G2 LED13 840 PSU D125.pdf"] = ("4000 K", 1300, 12),
            ["DN068B G2 LED13 865 PSU D125.pdf"] = ("6500 K", 1300, 12)
        };
        foreach (var (fileName, value) in expected)
        {
            var sourcePath = Path.Combine(root, fileName);
            if (!File.Exists(sourcePath)) continue;

            var entry = Assert.Single(index.Entries, item =>
                Path.GetFullPath(item.SourcePdf).Equals(Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase));
            Assert.Equal(value.Cct, entry.Cct);
            Assert.Equal(value.Flux, entry.LuminousFluxLumens);
            Assert.Equal(value.Power, entry.PowerWatts);
            if (entry.ImagePng is not null)
            {
                Assert.True(entry.ImagePng.Length > 100);
                Assert.True(entry.ImageAspect is > 0);
            }
        }
    }

    [Fact]
    public void Catalogue_dynamic_cohort_rejects_sibling_variants_and_tolerates_unknown_query_suffix()
    {
        var index = new CatalogueIndex();
        index.Add(new CatalogueEntry { ArticleNumber = "OPT", Tokens = ["LED20", "A", "L1200"], SourcePdf = "a.pdf" });
        index.Add(new CatalogueEntry { ArticleNumber = "OPT", Tokens = ["LED20", "B", "L1200"], SourcePdf = "b.pdf" });
        index.Add(new CatalogueEntry { ArticleNumber = "OPT", Tokens = ["LED40", "A", "L1200"], SourcePdf = "c.pdf" });

        Assert.Equal("a.pdf", index.Find("OPT", "OPT LED20 A L1200 X")?.SourcePdf);
        Assert.Equal("b.pdf", index.Find("OPT", "OPT LED20 B L1200")?.SourcePdf);
        Assert.Null(index.Find("OPT", "OPT LED30 A L1200"));
    }

    [Fact]
    public void Catalogue_exact_individual_datasheet_still_matches()
    {
        var index = new CatalogueIndex();
        index.Add(new CatalogueEntry { ArticleNumber = "DN068B", Tokens = ["G2", "LED13", "840", "PSU", "D125"], Cct = "4000 K", SourcePdf = "individual.pdf" });

        var entry = Assert.IsType<CatalogueEntry>(index.Find("DN068B", "DN068B G2 LED13 840 PSU D125 X"));
        Assert.Equal("4000 K", entry.Cct);
        Assert.Equal("individual.pdf", entry.SourcePdf);
    }

    [Fact]
    public void Family_brochure_descriptor_mismatch_returns_only_safe_common_facts()
    {
        var png = new byte[] { 1, 2, 3 };
        var index = new CatalogueIndex();
        index.Add(new CatalogueEntry
        {
            ArticleNumber = "OPT", Tokens = ["LED20", "B", "L1200"], Sku = "123456789012", IsFamilyDocument = true,
            HasVariantEvidence = true, PowerWatts = 20, LuminousFluxLumens = 2000, Cct = "4000 K", Cri = "80", IpCode = "IP20",
            ImagePng = png, SourcePdf = "family.pdf"
        });
        index.Add(new CatalogueEntry
        {
            ArticleNumber = "OPT", Tokens = ["LED40", "A", "L1200"], Sku = "123456789013", IsFamilyDocument = true,
            HasVariantEvidence = true, PowerWatts = 40, LuminousFluxLumens = 4000, Cct = "6500 K", Cri = "80", IpCode = "IP20",
            ImagePng = png, SourcePdf = "family.pdf"
        });

        var fallback = Assert.IsType<CatalogueEntry>(index.Find("OPT", "OPT LED20 A L1200 X"));
        Assert.False(fallback.HasVariantEvidence);
        Assert.Null(fallback.PowerWatts);
        Assert.Null(fallback.LuminousFluxLumens);
        Assert.Equal(string.Empty, fallback.Cct);
        Assert.Equal("80", fallback.Cri);
        Assert.Equal("IP20", fallback.IpCode);
        Assert.Equal(string.Empty, fallback.SourcePdf);
        Assert.Equal(string.Empty, fallback.DocumentHash);
        Assert.Null(fallback.ImagePng);
    }

    [Fact]
    public void Actual_bn008c_brochure_maps_only_supported_variant_and_common_facts_when_available()
    {
        var root = @"D:\Claude\DocManager\Product Family\0.EXTRA";
        if (!Directory.Exists(root)) return;
        var index = new CatalogueIndex();
        index.Build(root);

        Assert.Single(index.Entries.Select(entry => entry.DocumentHash).Where(value => value.Length > 0).Distinct());
        var cw = Assert.IsType<CatalogueEntry>(index.Find("BN008C", "BN008C LED20 CW L1200 G1 GM X"));
        var nw = Assert.IsType<CatalogueEntry>(index.Find("BN008C", "BN008C LED20 NW L1200 G1 GM"));
        Assert.Equal("911401724832", cw.Sku);
        Assert.Equal("911401724842", nw.Sku);
        Assert.Null(cw.LuminousFluxLumens);
        Assert.Null(cw.PowerWatts);
        Assert.Null(nw.LuminousFluxLumens);
        Assert.Null(nw.PowerWatts);
        Assert.Equal("80", cw.Cri);
        Assert.Equal("80", nw.Cri);
        Assert.Equal("IP20", cw.IpCode);
        Assert.Equal(string.Empty, cw.Voltage);
        Assert.Equal(string.Empty, cw.BeamAngle);
        Assert.Equal(string.Empty, cw.IkCode);
        Assert.Equal("25°C", cw.AmbientTemperature);
        Assert.Equal("25°C", nw.AmbientTemperature);
        Assert.Equal("1200x54x36mm", cw.Dimensions);
        Assert.Equal("1200x54x36mm", nw.Dimensions);
        Assert.Equal("6500 K", cw.Cct);
        Assert.Equal("derived-from-variant-code:CW;source-supported-candidate:6500K", cw.CctProvenance);
        Assert.Equal("4000 K", nw.Cct);
        Assert.Equal("derived-from-variant-code:NW;source-supported-candidate:4000K", nw.CctProvenance);
        Assert.Null(index.Find("BN008C", "BN008C LED20 CW L600 G1 GM X")?.PowerWatts);
    }

    [Fact]
    public void Catalogue_variant_lookup_distinguishes_840_and_865()
    {
        var index = new CatalogueIndex();
        index.Add(new CatalogueEntry { ArticleNumber = "RC099V", Tokens = ["G3", "LED69", "840"], Cct = "4000 K", SourcePdf = "a.pdf" });
        index.Add(new CatalogueEntry { ArticleNumber = "RC099V", Tokens = ["G3", "LED69", "865"], Cct = "6500 K", SourcePdf = "b.pdf" });
        Assert.Equal("4000 K", index.Find("RC099V", "RC099V G3 LED69 840 PSU")!.Cct);
        Assert.Equal("6500 K", index.Find("RC099V", "RC099V G3 LED69 865 PSU")!.Cct);
    }

    [Fact]
    public void Catalogue_variant_lookup_rejects_explicit_mismatched_cct_but_keeps_generic_queries()
    {
        var index = new CatalogueIndex();
        index.Add(new CatalogueEntry { ArticleNumber = "RC099V", Tokens = ["G3", "LED69", "840", "PSU"], Cct = "4000 K", SourcePdf = "a.pdf" });
        index.Add(new CatalogueEntry { ArticleNumber = "RC099V", Tokens = ["G3", "LED69", "865", "PSU"], Cct = "6500 K", SourcePdf = "b.pdf" });

        Assert.Null(index.Find("RC099V", "RC099V G3 LED69 830 PSU"));
        Assert.NotNull(index.Find("RC099V", "RC099V G3 LED69 PSU"));
    }

    [Fact]
    public void Family_generic_query_returns_common_facts_without_variant_source_or_image()
    {
        var index = new CatalogueIndex();
        index.Add(new CatalogueEntry
        {
            ArticleNumber = "RC099V", Tokens = ["G3", "LED69", "840", "PSU"], IsFamilyDocument = true, HasVariantEvidence = true,
            Cct = "4000 K", Cri = "80", IpCode = "IP20", ImagePng = [1], ImageAspect = 1, SourcePdf = "840.pdf", DocumentHash = "a"
        });
        index.Add(new CatalogueEntry
        {
            ArticleNumber = "RC099V", Tokens = ["G3", "LED69", "865", "PSU"], IsFamilyDocument = true, HasVariantEvidence = true,
            Cct = "6500 K", Cri = "80", IpCode = "IP20", ImagePng = [2], ImageAspect = 1, SourcePdf = "865.pdf", DocumentHash = "b"
        });

        var fallback = Assert.IsType<CatalogueEntry>(index.Find("RC099V", "RC099V G3 LED69 PSU"));
        Assert.False(fallback.HasVariantEvidence);
        Assert.Equal(string.Empty, fallback.Cct);
        Assert.Equal("80", fallback.Cri);
        Assert.Equal("IP20", fallback.IpCode);
        Assert.Equal(string.Empty, fallback.SourcePdf);
        Assert.Equal(string.Empty, fallback.DocumentHash);
        Assert.Null(fallback.ImagePng);
        Assert.Equal("4000 K", index.Find("RC099V", "RC099V G3 LED69 840 PSU")?.Cct);
        Assert.Equal("6500 K", index.Find("RC099V", "RC099V G3 LED69 865 PSU")?.Cct);
        Assert.Null(index.Find("RC099V", "RC099V G3 LED69 830 PSU"));
    }

    [Fact]
    public void Catalogue_lookup_bridges_leading_numeric_inventory_code_to_article()
    {
        var index = new CatalogueIndex();
        index.Add(new CatalogueEntry { ArticleNumber = "DN068B", Tokens = ["G2", "LED16", "840", "PSU"], Cct = "4000 K", SourcePdf = "dn068b.pdf" });

        var entry = Assert.IsType<CatalogueEntry>(index.Find("911401863187", "DN068B G2 LED16 840 PSU"));

        Assert.Equal("DN068B", entry.ArticleNumber);
        Assert.Equal("4000 K", entry.Cct);
    }

    [Fact]
    public void Paired_rs378_accessory_and_main_use_their_own_article_and_designation()
    {
        var index = new CatalogueIndex();
        index.Add(new CatalogueEntry { ArticleNumber = "RS378Z", Tokens = ["M55", "D75", "S", "S", "AJ", "D", "M", "WH", "PRO"], SourcePdf = "911401540946 - RS378Z M55 D75 S-S AJ D M-WH PRO.pdf" });
        index.Add(new CatalogueEntry { ArticleNumber = "RS378B", Tokens = ["P9", "930", "PSU", "E", "WB", "M55", "PRO"], Cct = "3000 K", SourcePdf = "911401502846 - RS378B P9 930 PSU-E WB M55 PRO.pdf" });
        const string designation = "M55 D75 S-S AJ D M-WH PRO + RS378B P9 930 PSU-E WB M55 PRO";

        var main = index.Find("RS378Z", designation);
        var accessory = index.FindAccessory("RS378Z", designation);

        Assert.Equal("RS378B", main?.ArticleNumber);
        Assert.Equal("RS378Z", accessory?.ArticleNumber);
        var report = new DialuxReport
        {
            Luminaires = [new DialuxLuminaire { ArticleNumber = "RS378Z", Name = designation }]
        };
        Assert.Empty(new DialuxConversionService().FindMissingCatalogueCodes([report], index));
    }

    [Fact]
    public void Compound_catalogue_checks_middle_components_and_preserves_last_main()
    {
        var index = new CatalogueIndex();
        index.Add(new CatalogueEntry { ArticleNumber = "A", Tokens = ["P1"], SourcePdf = "a.pdf" });
        index.Add(new CatalogueEntry { ArticleNumber = "C", Tokens = ["P3"], SourcePdf = "c.pdf" });
        const string designation = "P1 + B P2 + C P3";
        var report = new DialuxReport { Luminaires = [new DialuxLuminaire { ArticleNumber = "A", Name = designation }] };

        Assert.Equal("C", index.Find("A", designation)?.ArticleNumber);
        Assert.Equal(["B"], new DialuxConversionService().FindMissingCatalogueCodes([report], index));

        index.Add(new CatalogueEntry { ArticleNumber = "B", Tokens = ["P2"], SourcePdf = "b.pdf" });
        Assert.Equal(3, index.FindComponents("A", designation).Count);
        Assert.Empty(new DialuxConversionService().FindMissingCatalogueCodes([report], index));
    }

    [Fact]
    public void Photometry_catalogue_uses_only_strict_technical_cct()
    {
        var ies = BasicIes("RC099V-G3-LED69-865.ies");
        Assert.Null(new PhotometryCatalogue().Extract("CCT: 3000 K, 4000 K, 6500 K", ies).CctKelvin);
        Assert.Equal(6500, new PhotometryCatalogue().Extract("CCT: 6500 K", ies).CctKelvin);
    }

    private static void CreateDirectorySymbolicLinkOrSkip(string linkPath, string targetPath) =>
        Directory.CreateSymbolicLink(linkPath, targetPath);

    private static void CreateFileSymbolicLinkOrSkip(string linkPath, string targetPath) =>
        File.CreateSymbolicLink(linkPath, targetPath);

    private static void WriteImagePdf(string path, byte[] png)
    {
        using var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(600, 800);
        page.AddPng(png, new PdfRectangle(220, 360, 560, 610));
        File.WriteAllBytes(path, builder.Build());
    }

    private static void WriteEmptyPdf(string path)
    {
        using var builder = new PdfDocumentBuilder();
        builder.AddPage(600, 800);
        File.WriteAllBytes(path, builder.Build());
    }

    private static byte[] TestPng(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset] = (byte)(x % 251);
                pixels[offset + 1] = (byte)(y % 251);
                pixels[offset + 2] = 180;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] TestJpeg(int width, int height)
    {
        var stride = width * 3;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 3;
                var product = x >= 135 && x < 285 && y >= 85 && y < 195;
                var value = product ? (byte)70 : (byte)248;
                pixels[offset] = value;
                pixels[offset + 1] = product ? (byte)95 : value;
                pixels[offset + 2] = product ? (byte)120 : value;
            }
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, pixels, stride);
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void AssertPngDimensions(byte[] png, Action<int, int> assertion)
    {
        using var stream = new MemoryStream(png, writable: false);
        var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        assertion(decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
    }

    private sealed class StubImageExtractor(PdfProductImage image) : IPdfProductImageExtractor
    {
        public PdfProductImage? Extract(string pdfPath, CancellationToken cancellationToken = default) => image;
    }

    private sealed class ThrowingImageExtractor : IPdfProductImageExtractor
    {
        public PdfProductImage? Extract(string pdfPath, CancellationToken cancellationToken = default) => throw new InvalidDataException("fixture failure");
    }

    internal static IesDocument BasicIes(string sourcePath = "fixture-840.ies") => new()
    {
        SourcePath = sourcePath,
        Headers = new Dictionary<string, string> { ["LUMINAIRE"] = "SAME HEADER", ["MANUFAC"] = "Signify", ["LAMP"] = "LED" },
        LampCount = 1,
        LumensPerLamp = 1000,
        CandelaMultiplier = 1,
        VerticalAngleCount = 3,
        HorizontalAngleCount = 1,
        PhotometricType = 1,
        UnitType = 2,
        Width = .1,
        Length = 1,
        Height = .05,
        InputWatts = 10,
        VerticalAngles = [0, 90, 180],
        HorizontalAngles = [0],
        Candela = [[100d, 50d, 0d]]
    };
}
