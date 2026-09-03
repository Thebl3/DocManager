using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DocManager.Dialux;

namespace DocManager.Tests;

public sealed class DialuxAndExcelTests
{
    [Fact]
    public void Representative_dialux_text_parses_luminaires_rooms_and_locale_numbers()
    {
        var text = """
School Project
Operator: Nguyen Van A
Date: 31.07.2026
1 Signify RC099V Panel LED69 840 PSU 69,0 W 6.900 lm 100,0 lm/W
Basement · Zone 1 · Classroom (Light scene 1)
Eav 520 lx ≥ 500 lx
Emin 260 lx
Emax 700 lx
Uo 0,50 ≥ 0,40
Ground area 55,5 m²
Lighting power density 5,2 W/m²
2 Signify RC099V Panel LED69 840 PSU 69,0 W 6.900 lm 100,0 lm/W
""";
        var report = new DialuxParser().ParseText(text, "school.pdf");
        Assert.Equal("School Project", report.ProjectName);
        Assert.Single(report.Rooms);
        Assert.Equal(520, report.Rooms[0].AverageLux);
        Assert.Equal(500, report.Rooms[0].TargetLux);
        Assert.Equal(.5, report.Rooms[0].Uniformity);
        Assert.Equal(6900, report.Rooms[0].Luminaires[0].LuminousFluxLumens);
    }

    [Fact]
    public void One_dot_zone_keeps_one_room_metrics_and_luminaire_names_separate()
    {
        var text = """
School Project
1 Signify RC099V Panel LED69 840 PSU 69 W 6900 lm 100 lm/W
Floor · Classroom
Ēperpendicular 520 lx ≥ 500 lx
Emin 260 lx
Uo 0.50 ≥ 0.40
Ground area 55 m²
1 Signify RC099V Panel LED69 840 PSU 69 W 6900 lm 100 lm/W
Summary
""";

        var report = new DialuxParser().ParseText(text);

        Assert.Single(report.Luminaires);
        Assert.All(report.Luminaires, luminaire => Assert.Equal("Panel LED69 840 PSU", luminaire.Name));
        var room = Assert.Single(report.Rooms);
        Assert.Equal("Floor · Classroom", room.Name);
        Assert.Equal(520, room.AverageLux);
        Assert.Equal(500, room.TargetLux);
        Assert.Equal(.5, room.Uniformity);
        Assert.Equal("Panel LED69 840 PSU", Assert.Single(room.Luminaires).Name);
    }

    [Theory]
    [InlineData("Ēperpendicular 94.7 lx", 94.7)]
    [InlineData("Ēanything 95 lx", 95)]
    public void E_bar_variants_parse_average_lux(string metric, double expected)
    {
        var report = new DialuxParser().ParseText($"Floor · Classroom\n{metric}\nGround area 10 m²");
        Assert.Equal(expected, Assert.Single(report.Rooms).AverageLux);
    }

    [Fact]
    public void Positioned_words_reconstruct_a_luminaire_table_without_changing_text()
    {
        static PositionedWord Word(string text, double left, double bottom, double right, double top) => new(text, left, bottom, right, top);
        var words = new[]
        {
            Word("Manufacturer", 10, 90, 65, 100), Word("Article", 100, 90, 130, 100), Word("Luminaire", 165, 90, 205, 100),
            Word("Quantity", 250, 90, 285, 100), Word("Power", 330, 90, 355, 100), Word("Flux", 410, 90, 430, 100),
            Word("Signify", 10, 70, 45, 80), Word("RC099V", 100, 70, 135, 80), Word("Panel", 165, 70, 190, 80),
            Word("2", 250, 70, 255, 80), Word("69", 330, 70, 340, 80), Word("6900", 410, 70, 430, 80)
        };

        var tables = PdfTextExtractor.ReconstructTables(words);
        var table = Assert.Single(tables);
        Assert.Equal(2, table.Count);
        Assert.Equal("Article", table[0][1]);
        Assert.Equal("RC099V", table[1][1]);
        var report = new DialuxParser().ParseText("Plain project text", tables: tables);
        Assert.Equal("Plain project text", report.ProjectName);
        Assert.Equal("RC099V", Assert.Single(report.Luminaires).ArticleNumber);
    }

    [Fact]
    public void Positioned_dialux_layout_parses_real_style_metrics_and_inventory_without_footer_rows()
    {
        static PositionedWord Word(string text, double left, double bottom, double right, double top) => new(text, left, bottom, right, top);
        var words = new[]
        {
            Word("Office", 10, 290, 45, 300), Word("·", 48, 290, 51, 300), Word("Room", 54, 290, 80, 300),
            Word("Symbol", 10, 270, 40, 280), Word("Calculated", 100, 270, 150, 280), Word("Target", 180, 270, 215, 280),
            Word("Eav", 10, 250, 28, 260), Word("500", 100, 250, 120, 260), Word("500", 180, 250, 200, 260),
            Word("Emin", 10, 235, 35, 245), Word("300", 100, 235, 120, 245), Word("200", 180, 235, 200, 245),
            Word("Emax", 10, 220, 35, 230), Word("700", 100, 220, 120, 230), Word("-", 180, 220, 185, 230),
            Word("Uo", 10, 205, 25, 215), Word("0.60", 100, 205, 125, 215), Word("0.40", 180, 205, 205, 215),
            Word("Ground", 10, 190, 45, 200), Word("area", 48, 190, 70, 200), Word("[m²]", 75, 190, 100, 200), Word("55.5", 110, 190, 135, 200),
            Word("Power", 10, 175, 38, 185), Word("density", 42, 175, 82, 185), Word("[W/m²]", 86, 175, 125, 185), Word("5.2", 135, 175, 150, 185),
            Word("Manufacturer", 10, 150, 65, 160), Word("Article", 100, 150, 130, 160), Word("No.", 132, 150, 145, 160), Word("Article", 165, 150, 195, 160), Word("name", 198, 150, 225, 160), Word("Qty", 300, 150, 315, 160), Word("P", 350, 150, 355, 160), Word("[W]", 357, 150, 375, 160), Word("Phi", 420, 150, 435, 160), Word("[lm]", 437, 150, 460, 160),
            Word("Signify", 10, 130, 45, 140), Word("911401863187", 100, 130, 155, 140), Word("DN068B", 165, 130, 205, 140), Word("G2", 208, 130, 220, 140), Word("LED16", 223, 130, 255, 140), Word("2", 300, 130, 305, 140), Word("16", 350, 130, 360, 140), Word("1600", 420, 130, 445, 140),
            Word("DIALux", 10, 10, 45, 20), Word("Page", 450, 10, 470, 20), Word("1", 475, 10, 480, 20)
        };

        var report = new DialuxParser().Parse([new PdfPageContent(1, "incorrect extraction", words, PdfTextExtractor.ReconstructTables(words))]);

        var room = Assert.Single(report.Rooms);
        Assert.Equal("Office · Room", room.Name);
        Assert.Equal(500, room.AverageLux);
        Assert.Equal(300, room.MinimumLux);
        Assert.Equal(700, room.MaximumLux);
        Assert.Equal(.6, room.Uniformity);
        Assert.Equal(500, room.TargetLux);
        Assert.Equal(.4, room.TargetUniformity);
        Assert.Equal(55.5, room.AreaSquareMetres);
        Assert.Equal(5.2, room.PowerDensityWattsPerSquareMetre);
        var luminaire = Assert.Single(report.Luminaires);
        Assert.Equal("911401863187", luminaire.ArticleNumber);
        Assert.Equal("DN068B G2 LED16", luminaire.Name);
        Assert.Equal(1600, luminaire.LuminousFluxLumens);
        Assert.Single(room.Luminaires);
    }

    [Fact]
    public void Positioned_result_grid_uses_columns_merges_summary_and_excludes_level_only_heading()
    {
        static PositionedWord Word(string text, double left, double y, double width = 24) => new(text, left, y, left + width, y + 10);
        var levelWords = new[]
        {
            Word("An", 60, 700), Word("Viên", 88, 700), Word("·", 120, 700, 5), Word("Tầng", 132, 700), Word("1", 164, 700, 6),
            Word("Ground", 60, 500), Word("area", 105, 500), Word("999", 165, 500), Word("m²", 195, 500)
        };
        var summaryWords = new[]
        {
            Word("An", 60, 700), Word("Viên", 88, 700), Word("·", 120, 700, 5), Word("Tầng", 132, 700), Word("1", 164, 700, 6),
            Word("·", 178, 700, 5), Word("ACCOUNTANT", 190, 700, 72), Word("ROOM", 266, 700, 34), Word("(Light", 305, 700), Word("scene", 335, 700), Word("1)", 370, 700),
            Word("Ground", 60, 120), Word("area", 105, 120), Word("20.78", 165, 120), Word("m²", 200, 120)
        };
        var resultWords = new[]
        {
            Word("An", 60, 700), Word("Viên", 88, 700), Word("·", 120, 700, 5), Word("Tầng", 132, 700), Word("1", 164, 700, 6),
            Word("·", 178, 700, 5), Word("ACCOUNTANT", 190, 700, 72), Word("ROOM", 266, 700, 34), Word("(Light", 305, 700), Word("scene", 335, 700), Word("1)", 370, 700),
            Word("Symbol", 174, 610, 34), Word("Calculated", 277, 610, 48), Word("Target", 379, 610, 34), Word("Index", 482, 610, 28),
            Word("Working", 60, 585, 42), Word("plane", 105, 585, 30), Word("Ē", 174, 585, 8), Word("perpendicular", 184, 585, 66),
            Word("556", 277, 585, 20), Word("lx", 300, 585, 10), Word("≥", 379, 585, 8), Word("500", 390, 585, 20), Word("lx", 413, 585, 10), Word("WP9", 482, 585, 24),
            Word("U", 174, 560, 8), Word("(g", 184, 560, 12), Word("1)", 198, 560, 10), Word("0.65", 277, 560, 24), Word("≥", 379, 560, 8), Word("0.00", 390, 560, 24), Word("WP9", 482, 560, 24),
            Word("o", 176, 548, 7),
            Word("Space", 174, 510, 28), Word("Lighting", 204, 510, 34), Word("power", 240, 510, 30), Word("density", 174, 498, 38), Word("10.39", 277, 510, 28), Word("W/m²", 309, 510, 30),
            Word("12", 60, 350, 12), Word("Philips", 95, 350, 36), Word("DN068B", 155, 350, 42), Word("G2", 202, 350, 14), Word("LED20", 220, 350, 34),
            Word("PSU", 258, 350, 20), Word("D200", 282, 350, 28), Word("–", 375, 350, 8), Word("18", 404, 350, 14), Word("W", 421, 350, 10),
            Word("2000", 446, 350, 26), Word("lm", 475, 350, 12), Word("111.1", 489, 350, 28), Word("lm/W", 520, 350, 26)
        };
        var pages = new[]
        {
            new PdfPageContent(1, string.Empty, levelWords, []),
            new PdfPageContent(5, string.Empty, summaryWords, []),
            new PdfPageContent(6, string.Empty, resultWords, [])
        };

        var report = new DialuxParser().Parse(pages, "portable.pdf");

        var room = Assert.Single(report.Rooms);
        Assert.Equal("ACCOUNTANT ROOM", Regex.Replace(room.Name, @"\s*\([^)]*\)\s*$", string.Empty).Split('·').Last().Trim());
        Assert.Equal(20.78, room.AreaSquareMetres); Assert.Equal(10.39, room.PowerDensityWattsPerSquareMetre);
        Assert.Equal(556, room.AverageLux); Assert.Equal(500, room.TargetLux);
        Assert.Equal(.65, room.Uniformity); Assert.Equal(0, room.TargetUniformity);
        var luminaire = Assert.Single(room.Luminaires);
        Assert.Equal(12, luminaire.Count); Assert.Equal("DN068B", luminaire.ArticleNumber);
        Assert.Equal("G2 LED20 PSU D200", luminaire.Name); Assert.Equal(18, luminaire.PowerWatts);
        Assert.Equal(2000, luminaire.LuminousFluxLumens); Assert.Equal(111.1, luminaire.EfficacyLumensPerWatt);

        var output = Path.Combine(Path.GetTempPath(), $"DocManager-PortableGrid-{Guid.NewGuid():N}.xlsx");
        try
        {
            new ExcelExporter().Save([report], output);
            using var workbook = new XLWorkbook(output);
            var sheet = workbook.Worksheet("Lighting Schedule");
            Assert.Equal("ACCOUNTANT ROOM", sheet.Cell("B3").GetString());
            Assert.Equal(500, sheet.Cell("C3").GetDouble()); Assert.Equal(0, sheet.Cell("D3").GetDouble());
            Assert.Equal(556, sheet.Cell("E3").GetDouble()); Assert.Equal(.65, sheet.Cell("F3").GetDouble());
            Assert.Contains("DN068B G2 LED20 PSU D200", sheet.Cell("H3").GetString());
            Assert.Equal(12, sheet.Cell("K3").GetDouble()); Assert.Equal(18, sheet.Cell("L3").GetDouble());
            Assert.Equal("K3*L3", sheet.Cell("M3").FormulaA1); Assert.Equal(2000, sheet.Cell("N3").GetDouble());
            Assert.Equal("N3/L3", sheet.Cell("O3").FormulaA1);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    [Fact]
    public void New_workbook_uses_exact_sixteen_column_schema_and_semantic_cad_scan()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerNewSchema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "schedule.xlsx");
        try
        {
            var report = new DialuxReport
            {
                Rooms = [new DialuxRoom
                {
                    Name = "Floor · Room",
                    Luminaires = [new DialuxLuminaire { ArticleNumber = "MODEL", Name = "LED 840", Count = 2, PowerWatts = 10, LuminousFluxLumens = 1000 }]
                }]
            };

            new ExcelExporter().Save([report], path);

            using (var workbook = new XLWorkbook(path))
            {
                var sheet = workbook.Worksheet("Lighting Schedule");
                var expected = new[]
                {
                    "No.", "AREA", "Illuminance (lux)", "Uniformity (Uo)", "Illuminance design (lux)", "Uniformity design (Uo)",
                    "Symbol in DWG", "Proposed calculation model", "Parameter", "Reference image", "Quantity", "Power (W)",
                    "Total power (W)", "Luminous flux (lm)", "Efficiency (lm/W)", "IP / IK"
                };
                var actual = Enumerable.Range(1, 16).Select(column => SemanticHeader(sheet, column));
                Assert.Equal(expected, actual);
                Assert.DoesNotContain(sheet.CellsUsed(), cell => cell.GetString().Contains("Type (Lamp symbol)", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(16, sheet.LastColumnUsed()!.ColumnNumber());
                var metadata = workbook.Worksheet("__DocManagerMetadata");
                Assert.Equal(XLWorksheetVisibility.VeryHidden, metadata.Visibility);
                Assert.Equal("Floor · Room", metadata.Cell(2, 2).GetString());
                Assert.Equal("G1:J1", sheet.MergedRanges.Single(range => range.Contains(sheet.Cell("G1"))).RangeAddress.ToString());
                Assert.Equal("K1:P1", sheet.MergedRanges.Single(range => range.Contains(sheet.Cell("K1"))).RangeAddress.ToString());
                Assert.Equal("K3*L3", sheet.Cell("M3").FormulaA1);
                Assert.Equal("N3/L3", sheet.Cell("O3").FormulaA1);
            }

            var scan = new CadSymbolExcelService().ScanModels(path);
            Assert.Equal(8, scan.Form.Columns["model"]);
            Assert.Equal(7, scan.Form.Columns["symbol"]);
            Assert.Equal("MODEL LED 840", Assert.Single(scan.Models).DisplayModel);

            var append = new ExcelExporter().Append(path, [new DialuxReport
            {
                Rooms = [new DialuxRoom
                {
                    Name = "Floor · Appended",
                    Luminaires = [new DialuxLuminaire { ArticleNumber = "NEXT", Name = "MODEL", Count = 1, PowerWatts = 8, LuminousFluxLumens = 800 }]
                }]
            }]);
            Assert.Equal(1, append.AddedRows);
            Assert.Equal(8, append.Form.Columns["model"]);
            Assert.DoesNotContain(append.Form.Columns.Keys, key => key.Equals("type", StringComparison.OrdinalIgnoreCase));
            using var appended = new XLWorkbook(path);
            Assert.Equal("NEXT MODEL", appended.Worksheet("Lighting Schedule").Cell("H4").GetString());
            Assert.Equal(16, appended.Worksheet("Lighting Schedule").LastColumnUsed()!.ColumnNumber());
            Assert.Equal("Floor · Appended", appended.Worksheet("__DocManagerMetadata").Cell(3, 2).GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Positioned_wrapped_article_name_merges_only_the_article_column_and_exports_complete_model()
    {
        static PositionedWord Word(string text, double left, double y, double width = 20) => new(text, left, y, left + width, y + 10);
        var words = new[]
        {
            Word("Block", 10, 600, 28), Word("·", 42, 600, 5), Word("WC", 52, 600, 18), Word("NỮ", 74, 600, 18), Word("3", 96, 600, 8), Word("(Light", 110, 600, 30), Word("scene", 144, 600, 28), Word("1)", 176, 600, 12),
            Word("Symbol", 10, 580, 32), Word("Calculated", 120, 580, 50), Word("Target", 220, 580, 30), Word("Index", 320, 580, 24),
            Word("Working", 10, 560, 38), Word("plane", 52, 560, 28), Word("Ē", 90, 560, 8), Word("250", 120, 560, 20), Word("200", 220, 560, 20), Word("WP1", 320, 560, 20),
            Word("Luminaire", 10, 500, 45), Word("list", 60, 500, 20),
            Word("pcs.", 10, 480, 20), Word("Manufacturer", 50, 480, 55), Word("Article", 120, 480, 30), Word("No.", 152, 480, 18),
            Word("Article", 190, 480, 30), Word("name", 222, 480, 28), Word("RUG", 330, 480, 20), Word("P", 360, 480, 10), Word("Φ", 400, 480, 10), Word("Luminous", 460, 480, 48), Word("efficacy", 510, 480, 42),
            Word("5", 10, 460, 8), Word("Philips", 50, 460, 38), Word("DN390B", 120, 460, 40), Word("LED7", 190, 460, 26), Word("840", 220, 460, 18), Word("P6PSU", 242, 460, 32), Word("D100", 278, 460, 28), Word("AL", 310, 460, 15), Word("WP", 328, 460, 16),
            Word("–", 350, 460, 8), Word("5", 360, 460, 8), Word("W", 372, 460, 10), Word("714", 400, 460, 20), Word("lm", 424, 460, 12), Word("142.9", 460, 460, 28), Word("lm/W", 492, 460, 24),
            Word("GMG2HE", 190, 445, 42),
            Word("1", 10, 430, 8), Word("Philips", 50, 430, 38), Word("DN391B", 120, 430, 40), Word("LED12", 190, 430, 30), Word("840", 224, 430, 18), Word("P10PSU", 246, 430, 38), Word("D100", 288, 430, 28), Word("AL", 320, 430, 15), Word("GMG2HE", 190, 415, 42),
            Word("–", 350, 430, 8), Word("10", 360, 430, 12), Word("W", 376, 430, 10), Word("1200", 400, 430, 24), Word("lm", 428, 430, 12), Word("120", 460, 430, 18), Word("lm/W", 482, 430, 24),
            Word("Page", 500, 20, 24), Word("1", 528, 20, 8)
        };
        var tables = PdfTextExtractor.ReconstructTables(words);
        var table = Assert.Single(tables);
        Assert.Equal("LED7 840 P6PSU D100 AL WP GMG2HE", table[1][3]);
        Assert.Equal("LED12 840 P10PSU D100 AL GMG2HE", table[2][3]);
        var page = new PdfPageContent(1, string.Empty, words, tables);
        var report = new DialuxParser().Parse([page], "wrapped.pdf");
        var textFallback = new DialuxParser().ParseText("5 Philips DN390B LED7 840 P6PSU D100 AL WP 5 W 714 lm 142.9 lm/W\nGMG2HE");
        Assert.Equal("LED7 840 P6PSU D100 AL WP GMG2HE", Assert.Single(textFallback.Luminaires).Name);
        var tableFallback = new DialuxParser().ParseText("Plain project text", tables:
        [
            [
                ["Manufacturer", "Article", "Article name", "Qty", "Power", "Flux"],
                ["Philips", "DN390B", "LED7 840 P6PSU D100 AL WP GMG2HE", "5", "5", "714"],
                ["Philips", "DN391B", "LED12 840 P10PSU D100 AL GMG2HE", "1", "10", "1200"]
            ]
        ]);
        Assert.Collection(tableFallback.Luminaires,
            luminaire => Assert.Equal("LED7 840 P6PSU D100 AL WP GMG2HE", luminaire.Name),
            luminaire => Assert.Equal("LED12 840 P10PSU D100 AL GMG2HE", luminaire.Name));
        var luminaires = report.Luminaires;
        Assert.Collection(luminaires,
            luminaire => { Assert.Equal("DN390B", luminaire.ArticleNumber); Assert.Equal("LED7 840 P6PSU D100 AL WP GMG2HE", luminaire.Name); Assert.Equal(5, luminaire.Count); },
            luminaire => { Assert.Equal("DN391B", luminaire.ArticleNumber); Assert.Equal("LED12 840 P10PSU D100 AL GMG2HE", luminaire.Name); Assert.Equal(1, luminaire.Count); });

        var output = Path.Combine(Path.GetTempPath(), $"DocManager-WrappedArticle-{Guid.NewGuid():N}.xlsx");
        try
        {
            new ExcelExporter().Save([new DialuxReport { Rooms = [new DialuxRoom { Name = "WC NỮ 3", Luminaires = luminaires }] }], output);
            using var workbook = new XLWorkbook(output);
            var sheet = workbook.Worksheet("Lighting Schedule");
            Assert.Equal("DN390B LED7 840 P6PSU D100 AL WP GMG2HE", sheet.Cell("H3").GetString());
            Assert.Equal("DN391B LED12 840 P10PSU D100 AL GMG2HE", sheet.Cell("H4").GetString());
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    [Fact]
    public void Positioned_room_with_two_blank_article_cells_exports_two_product_rows_images_and_formulas()
    {
        static PositionedWord Word(string text, double left, double y) => new(text, left, y, left + Math.Max(8, text.Length * 5), y + 9);
        static PositionedWord[] Words(string[][] rows) => rows.SelectMany((row, rowIndex) => row.Select((value, column) => Word(value, 10 + column * 55, 600 - rowIndex * 20))).ToArray();
        var roomWords = Words([
            ["Block", "·", "P.NGU", "01", "(Light", "scene", "1)"],
            ["Symbol", "", "Calculated", "", "Target", "", "Index"],
            ["Working", "plane", "Ē", "", "255", "lx", "150", "lx", "WP1"],
            ["Uo", "", "", "", "0.6", "", "0.4", "", "WP1"],
            ["Luminaire", "list"],
            ["pcs.", "Manufacturer", "Article", "No.", "Article", "name", "RUG", "P", "Φ", "Luminous", "efficacy"],
            ["30", "Philips", "BY493P", "LED160CW", "PSU", "WB", "GM", "–", "115", "W", "15980", "lm", "139", "lm/W"],
            ["2", "Philips", "DN068B", "G2", "LED13", "PSU", "D125", "19", "12", "W", "1300", "lm", "108.3", "lm/W"],
            ["DIALux", "Page", "7"]
        ]);
        var repeatedInventory = Words([
            ["Luminaire", "list"],
            ["pcs.", "Manufacturer", "Article", "No.", "Article", "name", "RUG", "P", "Φ", "Luminous", "efficacy"],
            ["30", "Philips", "BY493P", "LED160CW", "PSU", "WB", "GM", "–", "115", "W", "15980", "lm", "139", "lm/W"],
            ["2", "Philips", "DN068B", "G2", "LED13", "PSU", "D125", "–", "12", "W", "1300", "lm", "108.3", "lm/W"],
            ["230380", "8"]
        ]);
        var report = new DialuxParser().Parse([
            new PdfPageContent(7, string.Empty, roomWords, []),
            new PdfPageContent(8, string.Empty, repeatedInventory, [])
        ], "portable-two-products.pdf");

        var room = Assert.Single(report.Rooms);
        Assert.Equal("P.NGU 01", RoomLeaf(room.Name));
        Assert.Equal(255, room.AverageLux); Assert.Equal(150, room.TargetLux);
        Assert.Equal(.6, room.Uniformity); Assert.Equal(.4, room.TargetUniformity);
        Assert.Collection(room.Luminaires,
            luminaire => { Assert.Equal("BY493P", luminaire.ArticleNumber); Assert.Equal("LED160CW PSU WB GM", luminaire.Name); Assert.Equal(30, luminaire.Count); Assert.Equal(115, luminaire.PowerWatts); Assert.Equal(15980, luminaire.LuminousFluxLumens); Assert.Equal(139, luminaire.EfficacyLumensPerWatt); },
            luminaire => { Assert.Equal("DN068B", luminaire.ArticleNumber); Assert.Equal("G2 LED13 PSU D125", luminaire.Name); Assert.Equal(2, luminaire.Count); Assert.Equal(12, luminaire.PowerWatts); Assert.Equal(1300, luminaire.LuminousFluxLumens); Assert.Equal(108.3, luminaire.EfficacyLumensPerWatt); });
        Assert.Equal(3474, report.TotalPowerWatts);

        var catalogue = new CatalogueIndex();
        catalogue.Add(new CatalogueEntry { ArticleNumber = "BY493P", Tokens = ["LED160CW", "PSU", "WB", "GM"], Cct = "5700 K", PowerWatts = 115, LuminousFluxLumens = 15980, ImagePng = SolidPng(20, 20), ImageAspect = 1, SourcePdf = "by493p.pdf" });
        catalogue.Add(new CatalogueEntry { ArticleNumber = "DN068B", Tokens = ["G2", "LED13", "PSU", "D125"], Cct = "4000 K", PowerWatts = 12, LuminousFluxLumens = 1300, ImagePng = SolidPngAlt(), ImageAspect = 1, SourcePdf = "dn068b.pdf" });
        var output = Path.Combine(Path.GetTempPath(), $"DocManager-TwoProducts-{Guid.NewGuid():N}.xlsx");
        try
        {
            new ExcelExporter().Save([report], output, catalogue);
            using var workbook = new XLWorkbook(output);
            var sheet = workbook.Worksheet("Lighting Schedule");
            foreach (var column in Enumerable.Range(1, 6)) Assert.True(sheet.Range(3, column, 4, column).IsMerged());
            foreach (var column in Enumerable.Range(7, 10)) Assert.False(sheet.Range(3, column, 4, column).IsMerged());
            Assert.Equal("P.NGU 01", sheet.Cell("B3").GetString()); Assert.Equal(string.Empty, sheet.Cell("B4").GetString());
            Assert.Equal("BY493P LED160CW PSU WB GM", sheet.Cell("H3").GetString()); Assert.Equal("CCT: 5700 K", sheet.Cell("I3").GetString());
            Assert.Equal("DN068B G2 LED13 PSU D125", sheet.Cell("H4").GetString()); Assert.Equal("CCT: 4000 K", sheet.Cell("I4").GetString());
            Assert.Equal(30, sheet.Cell("K3").GetDouble()); Assert.Equal(115, sheet.Cell("L3").GetDouble()); Assert.Equal("K3*L3", sheet.Cell("M3").FormulaA1); Assert.Equal(15980, sheet.Cell("N3").GetDouble()); Assert.Equal("N3/L3", sheet.Cell("O3").FormulaA1);
            Assert.Equal(2, sheet.Cell("K4").GetDouble()); Assert.Equal(12, sheet.Cell("L4").GetDouble()); Assert.Equal("K4*L4", sheet.Cell("M4").FormulaA1); Assert.Equal(1300, sheet.Cell("N4").GetDouble()); Assert.Equal("N4/L4", sheet.Cell("O4").FormulaA1);
            Assert.Contains(sheet.Pictures, picture => picture.TopLeftCell.Address.RowNumber == 3 && picture.TopLeftCell.Address.ColumnNumber == 10);
            Assert.Contains(sheet.Pictures, picture => picture.TopLeftCell.Address.RowNumber == 4 && picture.TopLeftCell.Address.ColumnNumber == 10);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    [Fact]
    public void Positioned_metrics_and_luminaire_list_on_adjacent_pages_associate_by_normalized_room_heading()
    {
        static PositionedWord Word(string text, double left, double y) => new(text, left, y, left + Math.Max(8, text.Length * 5), y + 9);
        static PositionedWord[] MetricWords() =>
        [
            Word("Block", 10, 500), Word("·", 50, 500), Word("WC", 65, 500), Word("NU", 95, 500), Word("1", 125, 500), Word("(Light", 145, 500), Word("scene", 185, 500), Word("1)", 225, 500),
            Word("Symbol", 10, 480), Word("Calculated", 160, 480), Word("Target", 260, 480), Word("Index", 360, 480),
            Word("Working", 10, 460), Word("plane", 55, 460), Word("Ē", 110, 460), Word("209", 160, 460), Word("200", 260, 460), Word("WP1", 360, 460),
            Word("Uo", 110, 440), Word("0.50", 160, 440), Word("0.40", 260, 440), Word("WP1", 360, 440)
        ];
        static PositionedWord[] ListWords() =>
        [
            Word("Block", 10, 500), Word("·", 50, 500), Word("WC", 65, 500), Word("NỮ", 95, 500), Word("1", 130, 500), Word("(Light", 150, 500), Word("scene", 190, 500), Word("1)", 230, 500),
            Word("Luminaire", 10, 480), Word("list", 65, 480),
            Word("pcs.", 10, 460), Word("Manufacturer", 60, 460), Word("Article", 145, 460), Word("No.", 195, 460), Word("Article", 225, 460), Word("name", 270, 460),
            Word("6", 10, 440), Word("Philips", 60, 440), Word("DN390B", 110, 440), Word("LED7", 160, 440), Word("840", 195, 440), Word("P6PSU", 225, 440), Word("D100", 270, 440), Word("AL", 305, 440), Word("WP", 330, 440), Word("14", 355, 440), Word("5.0", 380, 440), Word("W", 405, 440), Word("714", 425, 440), Word("lm", 455, 440), Word("142.9", 480, 440), Word("lm/W", 520, 440)
        ];
        var pages = new[]
        {
            new PdfPageContent(10, string.Empty, MetricWords(), []),
            new PdfPageContent(11, string.Empty, ListWords(), [])
        };

        var room = Assert.Single(new DialuxParser().Parse(pages, "adjacent.pdf").Rooms);

        Assert.Equal("WC NU 1", RoomLeaf(room.Name), ignoreCase: true);
        Assert.Equal(209, room.AverageLux); Assert.Equal(200, room.TargetLux);
        var luminaire = Assert.Single(room.Luminaires);
        Assert.Equal("DN390B", luminaire.ArticleNumber); Assert.Contains("AL WP", luminaire.Name);
        Assert.Equal(6, luminaire.Count); Assert.Equal(5, luminaire.PowerWatts);
    }

    [Fact]
    public void Positioned_list_before_metrics_associates_only_matching_room_within_bounded_section()
    {
        static PositionedWord Word(string text, double left, double y) => new(text, left, y, left + Math.Max(8, text.Length * 5), y + 9);
        static PositionedWord[] Words(string[][] rows) => rows.SelectMany((row, rowIndex) => row.Select((value, column) => Word(value, 10 + column * 60, 500 - rowIndex * 20))).ToArray();
        var pages = new[]
        {
            new PdfPageContent(20, string.Empty, Words([
                ["Block", "·", "Room", "A", "(Light", "scene", "1)"], ["Luminaire", "list"],
                ["1", "Philips", "DN391B", "LED12", "840", "P10PSU", "D100", "AL", "GMG2HE", "18", "9.8", "W", "1199", "lm", "122.4", "lm/W"]
            ]), []),
            new PdfPageContent(21, string.Empty, Words([
                ["Block", "·", "Room", "A", "(Light", "scene", "1)"], ["Symbol", "Calculated", "Target", "Index"],
                ["Working", "plane", "Ē", "300", "250", "WP1"], ["Uo", "0.50", "0.40", "WP1"]
            ]), []),
            new PdfPageContent(22, string.Empty, Words([
                ["Block", "·", "Room", "B", "(Light", "scene", "1)"], ["Symbol", "Calculated", "Target", "Index"],
                ["Working", "plane", "Ē", "250", "200", "WP2"], ["Uo", "0.45", "0.40", "WP2"]
            ]), [])
        };

        var report = new DialuxParser().Parse(pages, "bounded.pdf");

        Assert.Equal(2, report.Rooms.Count);
        Assert.Single(report.Rooms.Single(room => RoomLeaf(room.Name).Equals("Room A", StringComparison.OrdinalIgnoreCase)).Luminaires);
        Assert.Empty(report.Rooms.Single(room => RoomLeaf(room.Name).Equals("Room B", StringComparison.OrdinalIgnoreCase)).Luminaires);
    }

    [Fact]
    public void Single_room_global_inventory_fallback_attaches_all_unique_products()
    {
        var text = """
        Project
        1 Philips BY493P LED160CW PSU WB GM – 115 W 15980 lm 139 lm/W
        2 Philips DN068B G2 LED13 PSU D125 – 12 W 1300 lm 108.3 lm/W
        Floor · P.NGU 01
        Eav 255 lx ≥ 150 lx
        Uo 0.6 ≥ 0.4
        """;

        var room = Assert.Single(new DialuxParser().ParseText(text).Rooms);
        Assert.Equal(2, room.Luminaires.Count);
    }

    [Fact]
    public void Same_leaf_rooms_in_different_hierarchies_remain_distinct_with_local_metrics_and_products()
    {
        static PositionedWord Word(string text, double left, double y) => new(text, left, y, left + Math.Max(8, text.Length * 5), y + 9);
        static PositionedWord[] Page(string block, string room, double lux, string article, double power, double flux) =>
        [
            Word(block, 10, 500), Word("·", 70, 500), Word(room, 85, 500), Word("(Light", 150, 500), Word("scene", 190, 500), Word("1)", 230, 500),
            Word("Symbol", 10, 480), Word("Calculated", 160, 480), Word("Target", 260, 480), Word("Index", 360, 480),
            Word("Working", 10, 460), Word("plane", 55, 460), Word("Ē", 110, 460), Word(lux.ToString(), 160, 460), Word("200", 260, 460), Word("WP1", 360, 460),
            Word("Luminaire", 10, 420), Word("list", 65, 420),
            Word("1", 10, 400), Word("Philips", 50, 400), Word(article, 100, 400), Word("MODEL", 150, 400), Word(power.ToString(), 250, 400), Word("W", 280, 400), Word(flux.ToString(), 310, 400), Word("lm", 350, 400), Word("100", 380, 400), Word("lm/W", 420, 400)
        ];
        var report = new DialuxParser().Parse([
            new PdfPageContent(1, string.Empty, Page("Block A", "WC 01", 210, "LUMA", 10, 1000), []),
            new PdfPageContent(2, string.Empty, Page("Block B", "WC 01", 310, "LUMB", 20, 2000), [])
        ]);

        Assert.Equal(2, report.Rooms.Count);
        var blockA = Assert.Single(report.Rooms, room => room.Name.StartsWith("Block A", StringComparison.OrdinalIgnoreCase));
        var blockB = Assert.Single(report.Rooms, room => room.Name.StartsWith("Block B", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(210, blockA.AverageLux); Assert.Equal("LUMA", Assert.Single(blockA.Luminaires).ArticleNumber);
        Assert.Equal(310, blockB.AverageLux); Assert.Equal("LUMB", Assert.Single(blockB.Luminaires).ArticleNumber);
    }

    [Fact]
    public void Punctuation_distinct_room_ids_do_not_share_adjacent_luminaire_lists()
    {
        static PositionedWord Word(string text, double left, double y) => new(text, left, y, left + Math.Max(8, text.Length * 5), y + 9);
        static PositionedWord[] Page(string room, bool list, string article)
        {
            var words = new List<PositionedWord>
            {
                Word("Block", 10, 500), Word("·", 60, 500), Word(room, 75, 500), Word("(Light", 120, 500), Word("scene", 160, 500), Word("1)", 200, 500),
                Word("Symbol", 10, 480), Word("Calculated", 160, 480), Word("Target", 260, 480), Word("Index", 360, 480),
                Word("Working", 10, 460), Word("plane", 55, 460), Word("Ē", 110, 460), Word("250", 160, 460), Word("200", 260, 460), Word("WP1", 360, 460)
            };
            if (list) words.AddRange([
                Word("Luminaire", 10, 420), Word("list", 65, 420), Word("1", 10, 400), Word("Philips", 50, 400), Word(article, 100, 400), Word("MODEL", 150, 400), Word("10", 250, 400), Word("W", 280, 400), Word("1000", 310, 400), Word("lm", 350, 400), Word("100", 380, 400), Word("lm/W", 420, 400)
            ]);
            return words.ToArray();
        }
        var report = new DialuxParser().Parse([
            new PdfPageContent(1, string.Empty, Page("R-1", true, "LUMA"), []),
            new PdfPageContent(2, string.Empty, Page("R1", false, "LUMB"), [])
        ]);

        Assert.Single(report.Rooms.Single(room => RoomLeaf(room.Name).Equals("R-1", StringComparison.OrdinalIgnoreCase)).Luminaires);
        Assert.Empty(report.Rooms.Single(room => RoomLeaf(room.Name).Equals("R1", StringComparison.OrdinalIgnoreCase)).Luminaires);
    }

    [Fact]
    public void Multiple_rooms_do_not_receive_global_inventory_without_local_lists()
    {
        var text = """
        Project
        1 Philips BY493P LED160CW PSU WB GM – 115 W 15980 lm 139 lm/W
        Floor · Room A
        Eav 255 lx ≥ 150 lx
        Floor · Room B
        Eav 220 lx ≥ 150 lx
        """;

        var report = new DialuxParser().ParseText(text);
        Assert.Equal(2, report.Rooms.Count);
        Assert.All(report.Rooms, room => Assert.Empty(room.Luminaires));
    }

    [Fact]
    public void Append_distinguishes_same_display_area_by_full_room_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerAppendIdentity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "identity.xlsx");
        var luminaire = new DialuxLuminaire { ArticleNumber = "LUMA", Name = "Model", Count = 1, PowerWatts = 10, LuminousFluxLumens = 1000 };
        try
        {
            using (var template = new XLWorkbook())
            {
                var templateSheet = template.AddWorksheet("Form");
                templateSheet.Cell("A1").Value = "No."; templateSheet.Cell("B1").Value = "AREA"; templateSheet.Cell("I1").Value = "Proposed calculation model";
                templateSheet.Cell("L1").Value = "Quantity"; templateSheet.Cell("M1").Value = "Power (W)";
                template.SaveAs(path);
            }
            var exporter = new ExcelExporter();
            Assert.Equal(1, exporter.Append(path, [new DialuxReport { Rooms = [new DialuxRoom { Name = "Floor 1 · Classroom", Luminaires = [luminaire] }] }]).AddedRows);
            Assert.Equal(1, exporter.Append(path, [new DialuxReport { Rooms = [new DialuxRoom { Name = "Floor 2 · Classroom", Luminaires = [luminaire] }] }]).AddedRows);

            using var workbook = new XLWorkbook(path);
            var sheet = workbook.Worksheet("Form");
            Assert.Equal("Classroom", sheet.Cell("B3").GetString()); Assert.Equal("Classroom", sheet.Cell("B4").GetString());
            var identityColumn = sheet.Row(1).CellsUsed().Single(cell => cell.GetString() == "__DocManagerRoomIdentity").Address.ColumnNumber;
            Assert.Equal("Floor 1 · Classroom", sheet.Cell(3, identityColumn).GetString());
            Assert.Equal("Floor 2 · Classroom", sheet.Cell(4, identityColumn).GetString());
            Assert.True(sheet.Column(identityColumn).IsHidden);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Save_numbers_rooms_sequentially_across_merged_reports()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DocManagerMergedNo-{Guid.NewGuid():N}.xlsx");
        try
        {
            var reports = new[]
            {
                new DialuxReport { SourceFile = "first.pdf", Rooms = [new DialuxRoom { Name = "A" }, new DialuxRoom { Name = "B" }] },
                new DialuxReport { SourceFile = "second.pdf", Rooms = [new DialuxRoom { Name = "C" }] }
            };
            new ExcelExporter().Save(reports, path, includeReportTitles: true);
            using var workbook = new XLWorkbook(path);
            var sheet = workbook.Worksheet("Lighting Schedule");
            Assert.Equal(new[] { 1d, 2d, 3d }, Enumerable.Range(3, sheet.LastRowUsed()!.RowNumber() - 2)
                .Where(row => sheet.Cell(row, 1).TryGetValue<double>(out _))
                .Select(row => sheet.Cell(row, 1).GetDouble()).ToArray());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Append_to_legacy_type_column_form_keeps_type_untouched_and_maps_other_values()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerLegacyType-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "legacy.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var templateSheet = workbook.AddWorksheet("Legacy");
                var headers = new[]
                {
                    "No.", "AREA", "Illuminance (lux)", "Uniformity (Uo)", "Illuminance design (lux)", "Uniformity design (Uo)",
                    "Symbol in DWG", "Type (Lamp symbol)", "Proposed calculation model", "Parameter", "Reference image", "Quantity", "Power (W)",
                    "Total power (W)", "Luminous flux (lm)", "Efficiency (lm/W)", "IP / IK"
                };
                for (var column = 1; column <= headers.Length; column++) templateSheet.Cell(1, column).Value = headers[column - 1];
                templateSheet.Cell("A3").Value = 1; templateSheet.Cell("B3").Value = "Existing"; templateSheet.Cell("H3").Value = "Manual legacy type";
                templateSheet.Cell("I3").Value = "EXISTING"; templateSheet.Cell("L3").Value = 1; templateSheet.Cell("M3").Value = 10;
                workbook.SaveAs(path);
            }

            var report = new DialuxReport
            {
                Rooms = [new DialuxRoom
                {
                    Name = "Floor · Appended",
                    Luminaires = [new DialuxLuminaire { ArticleNumber = "NEW", Name = "MODEL", Count = 2, PowerWatts = 12, LuminousFluxLumens = 1200 }]
                }]
            };
            var result = new ExcelExporter().Append(path, [report]);

            Assert.Equal(1, result.AddedRows);
            using var appended = new XLWorkbook(path);
            var sheet = appended.Worksheet("Legacy");
            Assert.Equal("Manual legacy type", sheet.Cell("H3").GetString());
            Assert.True(sheet.Cell("H4").IsEmpty());
            Assert.Equal("NEW MODEL", sheet.Cell("I4").GetString());
            Assert.Equal(2, sheet.Cell("L4").GetDouble()); Assert.Equal(12, sheet.Cell("M4").GetDouble());
            Assert.Equal("L4*M4", sheet.Cell("N4").FormulaA1); Assert.Equal(1200, sheet.Cell("O4").GetDouble()); Assert.Equal("O4/M4", sheet.Cell("P4").FormulaA1);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Append_fresh_multi_product_room_merges_room_columns_and_partial_duplicate_appends_conservatively()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerAppendMulti-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "form.xlsx");
        var first = new DialuxLuminaire { ArticleNumber = "BY493P", Name = "LED160CW PSU WB GM", Count = 30, PowerWatts = 115, LuminousFluxLumens = 15980 };
        var second = new DialuxLuminaire { ArticleNumber = "DN068B", Name = "G2 LED13 PSU D125", Count = 2, PowerWatts = 12, LuminousFluxLumens = 1300 };
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Form");
                sheet.Cell("A1").Value = "No."; sheet.Cell("B1").Value = "AREA"; sheet.Cell("C1").Value = "Illuminance (lux)"; sheet.Cell("D1").Value = "Uniformity (Uo)";
                sheet.Cell("E1").Value = "Illuminance design (lux)"; sheet.Cell("F1").Value = "Uniformity design (Uo)"; sheet.Cell("I1").Value = "Proposed calculation model";
                sheet.Cell("L1").Value = "Quantity"; sheet.Cell("M1").Value = "Power (W)"; sheet.Cell("N1").Value = "Total power (W)"; sheet.Cell("O1").Value = "Luminous flux (lm)"; sheet.Cell("P1").Value = "Efficiency (lm/W)";
                workbook.SaveAs(path);
            }
            var room = new DialuxRoom { Name = "P.NGU 01", TargetLux = 150, TargetUniformity = .4, AverageLux = 255, Uniformity = .6, Luminaires = [first, second] };
            var exporter = new ExcelExporter();
            var result = exporter.Append(path, [new DialuxReport { Rooms = [room] }]);
            Assert.Equal(2, result.AddedRows);
            using (var workbook = new XLWorkbook(path))
            {
                var sheet = workbook.Worksheet("Form");
                foreach (var column in Enumerable.Range(1, 6)) Assert.True(sheet.Range(3, column, 4, column).IsMerged());
                Assert.False(sheet.Range("I3:I4").IsMerged());
            }

            var third = new DialuxLuminaire { ArticleNumber = "RC099V", Name = "G3 LED69 840", Count = 1, PowerWatts = 69, LuminousFluxLumens = 6900 };
            result = exporter.Append(path, [new DialuxReport { Rooms = [room with { Luminaires = [first, third] }] }]);
            Assert.Equal(1, result.AddedRows); Assert.Equal(1, result.SkippedDuplicates);
            using var appended = new XLWorkbook(path);
            var appendedSheet = appended.Worksheet("Form");
            Assert.Equal("P.NGU 01", appendedSheet.Cell("B5").GetString());
            Assert.Equal("RC099V G3 LED69 840", appendedSheet.Cell("I5").GetString());
            Assert.False(appendedSheet.Cell("B5").IsMerged());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Calculation_object_is_detail_evidence_for_a_room_without_luminaire()
    {
        static PositionedWord Word(string text, double left, double y) => new(text, left, y, left + Math.Max(8, text.Length * 5), y + 9);
        PositionedWord[] Words(string[][] rows) => rows.SelectMany((row, rowIndex) => row.Select((value, column) => Word(value, 10 + column * 70, 300 - rowIndex * 20))).ToArray();
        var pages = new[]
        {
            new PdfPageContent(1, string.Empty, Words([["Block", "·", "Tầng", "1"], ["Ground", "area", "100", "m²"]]), []),
            new PdfPageContent(2, string.Empty, Words([
                ["Calculation", "objects"],
                ["Working", "plane", "(QUIET", "ROOM)", "250", "lx", "125", "lx", "310", "lx", "0.50", "WP1"],
                ["Perpendicular", "illuminance", "(adaptive)", "(≥", "200", "lx)", "(≥", "0.40)"]
            ]), [])
        };

        var room = Assert.Single(new DialuxParser().Parse(pages).Rooms);

        Assert.Equal("QUIET ROOM", room.Name); Assert.Empty(room.Luminaires);
        Assert.Equal(250, room.AverageLux); Assert.Equal(.50, room.Uniformity);
        Assert.Equal(200, room.TargetLux); Assert.Equal(.40, room.TargetUniformity);
    }

    [Fact]
    public void Merged_article_number_and_name_table_cell_parses_and_rejects_footer_like_rows()
    {
        IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> tables =
        [
            [
                ["Article No. Article name", "Qty", "P [W]", "Phi [lm]"],
                ["911401863187 DN068B G2 LED16", "2", "16", "1600"],
                ["230380 1", "", "", ""]
            ]
        ];

        var report = new DialuxParser().ParseText("Office · Room\nEav 500 lx", tables: tables);

        var luminaire = Assert.Single(report.Luminaires);
        Assert.Equal("911401863187", luminaire.ArticleNumber);
        Assert.Equal("DN068B G2 LED16", luminaire.Name);
    }

    [Fact]
    public void Production_parse_uses_visual_rows_for_columbarium_summary_and_deduped_luminaire()
    {
        static PositionedWord Word(string text, double left, double y) => new(text, left, y, left + Math.Max(8, text.Length * 5), y + 9);
        var rows = new[]
        {
            new[] { "Raw", "content", "order", "is", "wrong" },
            new[] { "COLUMBARIUM" },
            new[] { "Symbol", "Calculated", "Target", "Index" },
            new[] { "Ēperpendicular", "94.7", "80.0" },
            new[] { "Uo", "0.30", "0.20" },
            new[] { "Ground", "area", "[m²]", "12.5" },
            new[] { "1", "Signify", "DN068B", "G2", "LED13", "840", "PSU", "D125", "12", "W", "1300", "lm", "108.3", "lm/W" }
        };
        var words = rows.SelectMany((row, index) => row.Select((text, column) => Word(text, 10 + column * 70, 200 - index * 20))).ToArray();
        var page = new PdfPageContent(1, "This raw stream should not be parsed", words, []);

        var report = new DialuxParser().Parse([page], "columbarium.pdf");

        var room = Assert.Single(report.Rooms);
        Assert.Equal("COLUMBARIUM", room.Name);
        Assert.Equal(94.7, room.AverageLux);
        Assert.Equal(80, room.TargetLux);
        Assert.Equal(.3, room.Uniformity);
        Assert.Equal(.2, room.TargetUniformity);
        Assert.Equal(12.5, room.AreaSquareMetres);
        var luminaire = Assert.Single(room.Luminaires);
        Assert.Equal("DN068B", luminaire.ArticleNumber);
        Assert.Equal(12, luminaire.PowerWatts);
        Assert.Equal(1300, luminaire.LuminousFluxLumens);
        Assert.Single(report.Luminaires);
    }

    [Fact]
    public void Table_parser_rejects_non_product_rows_and_dedupes_visual_and_table_inventory()
    {
        IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> tables =
        [
            [
                ["Manufacturer", "Article", "Luminaire", "Quantity", "Power", "Flux"],
                ["Signify", "DN068B", "G2 LED13 840 PSU D125", "1", "12", "1300"],
                ["", "Article", "Luminaire", "Quantity", "Power", "Flux"],
                ["Signify", "", "Missing code", "1", "10", "1000"]
            ]
        ];
        var text = "1 Signify DN068B G2 LED13 840 PSU D125 12 W 1300 lm 108.3 lm/W";
        var report = new DialuxParser().ParseText(text, tables: tables);
        var luminaire = Assert.Single(report.Luminaires);
        Assert.Equal("DN068B", luminaire.ArticleNumber);
    }

    [Fact]
    public void Save_and_append_put_one_reference_picture_in_detected_refimg_cell_without_duplicate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerPictures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "pictures.xlsx");
        try
        {
            var png = SolidPng(40, 20);
            var catalogue = new CatalogueIndex();
            catalogue.Add(new CatalogueEntry { ArticleNumber = "DN068B", Tokens = ["G2", "LED13", "840"], ImagePng = png, ImageAspect = 2, SourcePdf = "fixture.pdf" });
            var report = new DialuxReport
            {
                Rooms = [new DialuxRoom { Name = "Floor · COLUMBARIUM", Luminaires = [new DialuxLuminaire { ArticleNumber = "DN068B", Name = "G2 LED13 840", Count = 1, PowerWatts = 12, LuminousFluxLumens = 1300 }] }]
            };

            var exporter = new ExcelExporter();
            exporter.Save([report], path, catalogue);
            Assert.Equal(1, DrawingCount(path));
            Assert.Contains("<xdr:from><xdr:col>9</xdr:col>", DrawingXml(path));
            var result = exporter.Append(path, [report], catalogue);
            Assert.Equal(1, result.SkippedDuplicates);
            Assert.Equal(1, DrawingCount(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Compound_main_and_accessory_stack_two_bounded_reference_pictures_main_first()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerCompoundPictures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "compound.xlsx");
        try
        {
            var catalogue = new CatalogueIndex();
            catalogue.Add(new CatalogueEntry { ArticleNumber = "RS378Z", Tokens = ["M55"], ImagePng = SolidPng(20, 20), ImageAspect = 1, SourcePdf = "accessory.pdf" });
            catalogue.Add(new CatalogueEntry { ArticleNumber = "RS378B", Tokens = ["P14", "940"], ImagePng = SolidPngAlt(), ImageAspect = 2, SourcePdf = "main.pdf" });
            var report = new DialuxReport
            {
                Rooms = [new DialuxRoom { Name = "COLUMBARIUM", Luminaires = [new DialuxLuminaire { ArticleNumber = "RS378Z", Name = "M55 + RS378B P14 940", Count = 1, PowerWatts = 14 }] }]
            };

            new ExcelExporter().Save([report], path, catalogue);

            Assert.Equal(2, DrawingCount(path));
            var xml = DrawingXml(path);
            Assert.Equal(2, Regex.Matches(xml, "<xdr:col>9</xdr:col>").Count);
            Assert.True(xml.IndexOf("refimg-r3-", StringComparison.Ordinal) >= 0);
            AssertPicturesBoundedToCell(path, 3, 10);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Append_without_reference_image_column_skips_picture()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerAppendNoPicture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "form.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Form");
                sheet.Cell("A1").Value = "No."; sheet.Cell("B1").Value = "AREA";
                sheet.Cell("F1").Value = "Proposed calculation model"; sheet.Cell("G1").Value = "Quantity"; sheet.Cell("H1").Value = "Power (W)";
                workbook.SaveAs(path);
            }
            var catalogue = new CatalogueIndex();
            catalogue.Add(new CatalogueEntry { ArticleNumber = "DN068B", ImagePng = SolidPng(20, 20), ImageAspect = 1, SourcePdf = "fixture.pdf" });
            var report = new DialuxReport { Rooms = [new DialuxRoom { Name = "COLUMBARIUM", Luminaires = [new DialuxLuminaire { ArticleNumber = "DN068B", Name = "G2", Count = 1, PowerWatts = 12 }] }] };

            var result = new ExcelExporter().Append(path, [report], catalogue);

            Assert.Equal(1, result.AddedRows);
            Assert.Equal(0, DrawingCount(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Append_adds_reference_picture_to_detected_nonstandard_column()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerAppendPicture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "form.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Form");
                sheet.Cell("A1").Value = "No."; sheet.Cell("B1").Value = "AREA"; sheet.Cell("D1").Value = "Reference image";
                sheet.Cell("F1").Value = "Proposed calculation model"; sheet.Cell("G1").Value = "Quantity"; sheet.Cell("H1").Value = "Power (W)";
                sheet.Cell("A3").Value = 1; sheet.Cell("B3").Value = "Existing"; sheet.Cell("F3").Value = "EXISTING"; sheet.Cell("G3").Value = 1; sheet.Cell("H3").Value = 10;
                workbook.SaveAs(path);
            }
            var catalogue = new CatalogueIndex();
            catalogue.Add(new CatalogueEntry { ArticleNumber = "DN068B", ImagePng = SolidPng(20, 20), ImageAspect = 1, SourcePdf = "fixture.pdf" });
            var report = new DialuxReport { Rooms = [new DialuxRoom { Name = "COLUMBARIUM", Luminaires = [new DialuxLuminaire { ArticleNumber = "DN068B", Name = "G2", Count = 1, PowerWatts = 12 }] }] };
            new ExcelExporter().Append(path, [report], catalogue);
            Assert.Contains("<xdr:from><xdr:col>3</xdr:col>", DrawingXml(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Positioned_columbarium_layout_parses_all_expected_fields_and_one_inventory_item()
    {
        static PositionedWord Word(string text, double left, double y) => new(text, left, y, left + Math.Max(8, text.Length * 5), y + 9);
        var page1Rows = new[]
        {
            new[] { "Project" }, new[] { "BLOCK-AB", "·", "Tầng", "1" }, new[] { "Luminaire", "list" },
            new[] { "pcs.", "Manufacturer", "Article", "No.", "Article", "name", "P", "Φ", "Luminous", "efficacy" },
            new[] { "128", "Philips", "DN068B", "G2", "LED16", "PSU", "D150", "15.0", "W", "1600", "lm", "106.7", "lm/W" },
            new[] { "230380", "1" }
        };
        var page3Rows = new[]
        {
            new[] { "Project" }, new[] { "Calculation", "objects" }, new[] { "Working", "planes" },
            new[] { "Working", "plane", "(COLUMBARIUM)", "346", "lx", "217", "lx", "433", "lx", "0.63", "0.50", "WP1" },
            new[] { "Perpendicular", "illuminance", "(adaptive)", "(≥", "300", "lx)", "(≥", "0.60)" }
        };
        var page4Rows = new[]
        {
            new[] { "BLOCK-AB", "·", "Tầng", "1", "·", "COLUMBARIUM", "(Light", "scene", "1)" }, new[] { "Summary" },
            new[] { "Ground", "area", "343.95", "m²" }
        };
        var page5Rows = new[]
        {
            new[] { "BLOCK-AB", "·", "Tầng", "1", "·", "COLUMBARIUM", "(Light", "scene", "1)" }, new[] { "Summary" },
            new[] { "Results" }, new[] { "Symbol", "Calculated", "Target", "Index" },
            new[] { "Working", "plane", "Ē", "perpendicular", "346", "lx", "≥", "300", "lx", "WP1" },
            new[] { "U", "(g", ")", "0.63", "≥", "0.60", "WP1" }, new[] { "o", "1" },
            new[] { "Space", "Lighting", "power", "density", "5.58", "W/m²", "–" },
            new[] { "Luminaire", "list" },
            new[] { "pcs.", "Manufacturer", "Article", "No.", "Article", "name", "RUG", "P", "Φ", "Luminous", "efficacy" },
            new[] { "128", "Philips", "DN068B", "G2", "LED16", "PSU", "D150", "–", "15.0", "W", "1600", "lm", "106.7", "lm/W" },
            new[] { "230380", "5" }
        };
        PositionedWord[] Words(string[][] rows) => rows.SelectMany((row, rowIndex) => row.Select((value, column) => Word(value, 10 + column * 75, 500 - rowIndex * 20))).ToArray();
        var page1Words = Words(page1Rows);
        var page5Words = Words(page5Rows);
        var pages = new[]
        {
            new PdfPageContent(1, "concatenated stream must not be used", page1Words, PdfTextExtractor.ReconstructTables(page1Words)),
            new PdfPageContent(3, string.Empty, Words(page3Rows), []),
            new PdfPageContent(4, string.Empty, Words(page4Rows), []),
            new PdfPageContent(5, string.Empty, page5Words, PdfTextExtractor.ReconstructTables(page5Words))
        };

        var report = new DialuxParser().Parse(pages, "columbarium.pdf");

        var room = Assert.Single(report.Rooms);
        Assert.Equal("COLUMBARIUM", room.Name.Split('·').Last().Split('(')[0].Trim(), ignoreCase: true);
        Assert.Equal(346, room.AverageLux); Assert.Equal(217, room.MinimumLux); Assert.Equal(433, room.MaximumLux);
        Assert.Equal(.63, room.Uniformity); Assert.Equal(300, room.TargetLux); Assert.Equal(.60, room.TargetUniformity);
        Assert.Equal(343.95, room.AreaSquareMetres); Assert.Equal(5.58, room.PowerDensityWattsPerSquareMetre);
        var luminaire = Assert.Single(report.Luminaires);
        Assert.Equal("Philips", luminaire.Manufacturer); Assert.Equal("DN068B", luminaire.ArticleNumber);
        Assert.Equal("G2 LED16 PSU D150", luminaire.Name); Assert.Equal(128, luminaire.Count);
        Assert.Equal(15, luminaire.PowerWatts); Assert.Equal(1600, luminaire.LuminousFluxLumens); Assert.Equal(106.7, luminaire.EfficacyLumensPerWatt);
        Assert.Equal(luminaire, Assert.Single(room.Luminaires));
    }

    [Fact]
    public void Optional_real_columbarium_fixture_parses_exact_expected_values_when_available()
    {
        var path = @"D:\1_HTI_Work\DỰ ÁN\2026\07_03_NhatAnVien\PDF\NhatAnVien_BlockAB_Tang1.pdf";
        if (!File.Exists(path)) return;
        var report = new DialuxParser().Parse(new PdfTextExtractor().Extract(path), Path.GetFileName(path));
        var room = Assert.Single(report.Rooms);
        Assert.Equal("COLUMBARIUM", Regex.Replace(room.Name, @"\s*\([^)]*\)\s*$", string.Empty).Split('·').Last().Trim(), ignoreCase: true);
        Assert.Equal(346, room.AverageLux); Assert.Equal(217, room.MinimumLux); Assert.Equal(433, room.MaximumLux);
        Assert.Equal(.63, room.Uniformity); Assert.Equal(300, room.TargetLux); Assert.Equal(.60, room.TargetUniformity);
        Assert.Equal(343.95, room.AreaSquareMetres); Assert.Equal(5.58, room.PowerDensityWattsPerSquareMetre);
        var luminaire = Assert.Single(report.Luminaires);
        Assert.Equal("DN068B", luminaire.ArticleNumber); Assert.Equal("G2 LED16 PSU D150", luminaire.Name);
        Assert.Equal(128, luminaire.Count); Assert.Equal(15, luminaire.PowerWatts); Assert.Equal(1600, luminaire.LuminousFluxLumens);
        Assert.Equal(106.7, luminaire.EfficacyLumensPerWatt); Assert.Equal(luminaire, Assert.Single(room.Luminaires));
    }

    [Fact]
    public void Optional_real_block_c_parses_fourteen_local_room_inventories_and_exports_accountant_mapping()
    {
        var reportPath = @"D:\1_HTI_Work\DỰ ÁN\2026\07_03_NhatAnVien\PDF\NhatAnVien_BlockC_Tang1.pdf";
        var catalogueRoot = @"D:\Claude\DocManager\Product Family";
        if (!File.Exists(reportPath)) return;
        var expected = new (string Room, double Area, double Lux, double TargetLux, double Uniformity, double TargetUniformity, string Model, double Quantity)[]
        {
            ("ACCOUNTANT ROOM", 20.78, 556, 500, .65, 0, "LED20", 12),
            ("FIRE CONTROL", 16.89, 350, 200, .64, 0, "LED16", 8),
            ("MAIN LOBBY", 186.53, 229, 200, .52, .40, "LED20", 42),
            ("MANAGER ROOM", 20.73, 550, 500, .69, 0, "LED20", 12),
            ("MARKETING ROOM", 22.57, 522, 500, .68, 0, "LED20", 12),
            ("OFFICE CORR.", 8.29, 174, 150, .76, 0, "LED16", 4),
            ("PUMP ROOM", 10.65, 310, 200, .69, 0, "LED16", 6),
            ("RECEPTION & WAITING", 31.30, 314, 300, .65, 0, "LED20", 9),
            ("STAIR", 52.81, 159, 150, .71, .40, "LED20", 21),
            ("TEMPLE AREA", 365.11, 323, 300, .33, 0, "LED20", 85),
            ("WAITING ROOM", 16.80, 352, 300, .63, 0, "LED16", 8),
            ("WC CORR.", 12.20, 167, 150, .75, 0, "LED16", 7),
            ("WC F", 20.37, 255, 200, .44, 0, "LED13", 12),
            ("WC M", 19.17, 222, 200, .45, 0, "LED13", 9)
        };

        var report = new DialuxParser().Parse(new PdfTextExtractor().Extract(reportPath), Path.GetFileName(reportPath));

        Assert.Equal(14, report.Rooms.Count);
        Assert.DoesNotContain(report.Rooms, room => RoomLeaf(room.Name).Equals("Tầng 1", StringComparison.OrdinalIgnoreCase));
        foreach (var item in expected)
        {
            var room = Assert.Single(report.Rooms, candidate => RoomLeaf(candidate.Name).Equals(item.Room, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(item.Area, room.AreaSquareMetres); Assert.Equal(item.Lux, room.AverageLux); Assert.Equal(item.TargetLux, room.TargetLux);
            Assert.Equal(item.Uniformity, room.Uniformity); Assert.Equal(item.TargetUniformity, room.TargetUniformity);
            var luminaire = Assert.Single(room.Luminaires);
            Assert.Contains(item.Model, luminaire.Name, StringComparison.OrdinalIgnoreCase); Assert.Equal(item.Quantity, luminaire.Count);
        }
        Assert.Equal(21, report.Rooms.SelectMany(room => room.Luminaires).Where(item => item.Name.Contains("LED13", StringComparison.OrdinalIgnoreCase)).Sum(item => item.Count ?? 0));
        Assert.Equal(33, report.Rooms.SelectMany(room => room.Luminaires).Where(item => item.Name.Contains("LED16", StringComparison.OrdinalIgnoreCase)).Sum(item => item.Count ?? 0));
        Assert.Equal(193, report.Rooms.SelectMany(room => room.Luminaires).Where(item => item.Name.Contains("LED20", StringComparison.OrdinalIgnoreCase)).Sum(item => item.Count ?? 0));
        Assert.Equal(4221, report.TotalPowerWatts);
        Assert.True(report.Rooms.Count(room => room.AverageLux == room.AreaSquareMetres) < 2);

        if (!Directory.Exists(catalogueRoot)) return;
        var output = Path.Combine(Path.GetTempPath(), $"DocManager-BlockC-{Guid.NewGuid():N}.xlsx");
        try
        {
            var catalogue = new CatalogueIndex();
            catalogue.Build(catalogueRoot);
            foreach (var model in new[] { "LED13", "LED16", "LED20" })
            {
                var luminaire = report.Rooms.SelectMany(room => room.Luminaires).First(item => item.Name.Contains(model, StringComparison.OrdinalIgnoreCase));
                var entry = Assert.IsType<CatalogueEntry>(catalogue.Find(luminaire.ArticleNumber, luminaire.Name));
                Assert.NotNull(entry.ImagePng);
            }
            var accountantLuminaire = Assert.Single(report.Rooms.Single(room => RoomLeaf(room.Name).Equals("ACCOUNTANT ROOM", StringComparison.OrdinalIgnoreCase)).Luminaires);
            var accountantEntry = Assert.IsType<CatalogueEntry>(catalogue.Find(accountantLuminaire.ArticleNumber, accountantLuminaire.Name));
            var expectedParameters = string.Join('\n', new[]
            {
                accountantEntry.Cct.Length > 0 ? $"CCT: {accountantEntry.Cct}" : string.Empty,
                ParameterCri(accountantEntry.Cri),
                ParameterValue(accountantEntry.Voltage),
                ParameterBeam(accountantEntry.BeamAngle),
                ParameterAmbient(accountantEntry.AmbientTemperature),
                accountantEntry.Dimensions.Length > 0 ? $"Size: {ParameterDimensions(accountantEntry.Dimensions)}" : string.Empty
            }.Where(value => value.Length > 0));
            var expectedIpIk = string.Join(" / ", new[] { accountantEntry.IpCode, accountantEntry.IkCode }.Where(value => value.Length > 0));

            new ExcelExporter().Save([report], output, catalogue);
            using var workbook = new XLWorkbook(output);
            var sheet = workbook.Worksheet("Lighting Schedule");
            var row = Assert.Single(Enumerable.Range(3, 14).Where(index => sheet.Cell(index, 2).GetString().Equals("ACCOUNTANT ROOM", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(500, sheet.Cell(row, 3).GetDouble()); Assert.Equal(0, sheet.Cell(row, 4).GetDouble());
            Assert.Equal(556, sheet.Cell(row, 5).GetDouble()); Assert.Equal(.65, sheet.Cell(row, 6).GetDouble());
            var columns = ColumnMap(sheet);
            Assert.Contains("DN068B G2 LED20 PSU D200", sheet.Cell(row, columns["Proposed calculation model"]).GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(expectedParameters, sheet.Cell(row, columns["Parameter"]).GetString());
            Assert.Contains(sheet.Pictures, picture => picture.TopLeftCell.Address.RowNumber == row && picture.TopLeftCell.Address.ColumnNumber == columns["Reference image"]);
            Assert.Equal(12, sheet.Cell(row, columns["Quantity"]).GetDouble()); Assert.Equal(18, sheet.Cell(row, columns["Power (W)"]).GetDouble());
            Assert.Equal($"K{row}*L{row}", sheet.Cell(row, columns["Total power (W)"]).FormulaA1); Assert.Equal(2000, sheet.Cell(row, columns["Luminous flux (lm)"]).GetDouble());
            Assert.Equal($"N{row}/L{row}", sheet.Cell(row, columns["Efficiency (lm/W)"]).FormulaA1); Assert.Equal(expectedIpIk, sheet.Cell(row, columns["IP / IK"]).GetString());
            Assert.Equal(14, Enumerable.Range(3, 14).Count(index => sheet.Cell(index, 2).GetString().Length > 0));
            Assert.Equal(14, sheet.Pictures.Select(picture => picture.TopLeftCell.Address.RowNumber).Distinct().Count());
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    [Fact]
    public void Optional_actual_a402_maps_all_report_inventory_and_exports_six_previously_blank_rooms()
    {
        var reportPath = @"D:\1_HTI_Work\Extra\A-402.1.pdf";
        var cataloguePath = @"D:\Claude\DocManager\Product Family\GreenSpace G6 UE\911401534644 - DN391B LED12_840 P10PSU D100 AL GMG2HE.pdf";
        if (!File.Exists(reportPath) || !File.Exists(cataloguePath)) return;
        var expected = new Dictionary<string, (string Article, string Model, double Quantity, double Power)>(StringComparer.OrdinalIgnoreCase)
        {
            ["T.BỘ S-ST-02"] = ("SP570P", "LED40", 2, 32),
            ["T.BỘ S-ST-03"] = ("SP570P", "LED40", 2, 32),
            ["T.BỘ S-ST-04"] = ("SP570P", "LED40", 3, 32),
            ["WC NỮ 1"] = ("DN390B", "LED7 840 P6PSU D100 AL WP GMG2HE", 6, 5),
            ["WC NỮ 2"] = ("DN390B", "LED7 840 P6PSU D100 AL WP GMG2HE", 6, 5),
            ["WC NỮ 3"] = ("DN390B", "LED7 840 P6PSU D100 AL WP GMG2HE", 5, 5)
        };
        var output = Path.Combine(Path.GetTempPath(), $"DocManager-A402-{Guid.NewGuid():N}.xlsx");
        try
        {
            var extractor = new PdfTextExtractor();
            var report = new DialuxParser().Parse(extractor.Extract(reportPath), Path.GetFileName(reportPath));
            Assert.All(report.Rooms, room => Assert.NotEmpty(room.Luminaires));
            foreach (var (roomName, item) in expected)
            {
                var room = Assert.Single(report.Rooms, candidate => RoomLeaf(candidate.Name).Equals(roomName, StringComparison.OrdinalIgnoreCase));
                var luminaire = Assert.Single(room.Luminaires);
                Assert.Equal(item.Article, luminaire.ArticleNumber, ignoreCase: true);
                if (roomName.StartsWith("WC NỮ", StringComparison.OrdinalIgnoreCase)) Assert.Equal(item.Model, luminaire.Name, ignoreCase: true);
                else Assert.Contains(item.Model, luminaire.Name, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(item.Quantity, luminaire.Count); Assert.Equal(item.Power, luminaire.PowerWatts);
            }

            static string InventoryKey(DialuxLuminaire item) => string.Join('|',
                item.ArticleNumber.ToUpperInvariant(),
                Regex.Replace(item.Name, @"\s+GMG2HE\s*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).ToUpperInvariant(),
                item.PowerWatts,
                item.LuminousFluxLumens);
            var roomTotals = report.Rooms.SelectMany(room => room.Luminaires)
                .GroupBy(InventoryKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Count ?? 0), StringComparer.OrdinalIgnoreCase);
            foreach (var inventory in report.Luminaires)
            {
                Assert.True(roomTotals.TryGetValue(InventoryKey(inventory), out var total));
                Assert.Equal(inventory.Count, total);
            }
            Assert.Equal(report.Luminaires.Sum(item => item.Count ?? 0), report.Rooms.SelectMany(room => room.Luminaires).Sum(item => item.Count ?? 0));

            var catalogue = new CatalogueIndex();
            var cataloguePages = extractor.Extract(cataloguePath);
            var identity = CatalogueIndex.IdentityFromFileName(cataloguePath);
            catalogue.Add(CatalogueIndex.Parse(identity.Article, identity.Tokens, cataloguePath,
                string.Join('\n', cataloguePages.Select(page => PdfTextExtractor.ReconstructVisualText(page.Words)))));
            new ExcelExporter().Save([report], output, catalogue);

            using var workbook = new XLWorkbook(output);
            var sheet = workbook.Worksheet("Lighting Schedule");
            var columns = ColumnMap(sheet);
            var lastRow = sheet.LastRowUsed()!.RowNumber();
            foreach (var roomName in expected.Keys)
            {
                var row = Assert.Single(Enumerable.Range(3, lastRow - 2).Where(index => sheet.Cell(index, 2).GetString().Equals(roomName, StringComparison.OrdinalIgnoreCase)));
                foreach (var header in new[] { "Proposed calculation model", "Quantity", "Power (W)", "Total power (W)", "Luminous flux (lm)", "Efficiency (lm/W)" }) Assert.False(sheet.Cell(row, columns[header]).IsEmpty());
            }
            var wcNu3Row = Assert.Single(Enumerable.Range(3, lastRow - 2).Where(index => sheet.Cell(index, 2).GetString().Equals("WC NỮ 3", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal("DN390B LED7 840 P6PSU D100 AL WP GMG2HE", sheet.Cell(wcNu3Row, columns["Proposed calculation model"]).GetString());
            Assert.Equal(5, sheet.Cell(wcNu3Row, columns["Quantity"]).GetDouble()); Assert.Equal(5, sheet.Cell(wcNu3Row, columns["Power (W)"]).GetDouble());
            var dn391Row = Enumerable.Range(3, lastRow - 2).First(index => sheet.Cell(index, columns["Proposed calculation model"]).GetString().Contains("DN391B LED12 840 P10PSU D100 AL GMG2HE", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("CCT: 4000 K", sheet.Cell(dn391Row, columns["Parameter"]).GetString());
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    [Fact]
    public void Optional_real_nhatanvien_export_has_dn068b_picture_and_validated_technical_fields()
    {
        var reportPath = @"D:\1_HTI_Work\DỰ ÁN\2026\07_03_NhatAnVien\PDF\NhatAnVien_BlockAB_Tang1.pdf";
        var catalogueRoot = @"D:\Claude\DocManager\Product Family";
        if (!File.Exists(reportPath) || !Directory.Exists(catalogueRoot)) return;
        var output = Path.Combine(Path.GetTempPath(), $"DocManager-NhatAnVien-{Guid.NewGuid():N}.xlsx");
        try
        {
            var report = new DialuxParser().Parse(new PdfTextExtractor().Extract(reportPath), Path.GetFileName(reportPath));
            var catalogue = new CatalogueIndex();
            catalogue.Build(catalogueRoot);

            new ExcelExporter().Save([report], output, catalogue);

            using var workbook = new XLWorkbook(output);
            var sheet = workbook.Worksheet("Lighting Schedule");
            Assert.Equal("COLUMBARIUM", sheet.Cell("B3").GetString(), ignoreCase: true);
            Assert.Contains("DN068B", sheet.Cell("H3").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(15, sheet.Cell("L3").GetDouble());
            Assert.Equal(1600, sheet.Cell("N3").GetDouble());
            Assert.True(sheet.Cell("I3").GetString().Length > 0);
            Assert.Contains(sheet.Pictures, picture => picture.TopLeftCell.Address.RowNumber == 3 && picture.TopLeftCell.Address.ColumnNumber == 10);
            Assert.Contains("<xdr:from><xdr:col>9</xdr:col>", DrawingXml(output));
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    [Fact]
    public void Export_rejects_a_room_name_containing_page_prose_before_writing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DocManagerMalformed-{Guid.NewGuid():N}.xlsx");
        var report = new DialuxReport
        {
            Rooms = [new DialuxRoom { Name = "COLUMBARIUM\nSummary\nGround area 343.95 m²\nDIALux Page 5", AverageLux = 346 }]
        };

        var exception = Assert.Throws<InvalidDataException>(() => new ExcelExporter().Save([report], path));

        Assert.Contains("không hợp lệ", exception.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Save_and_append_format_semantic_parameter_values_without_empty_labels()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerParameterLabels-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "schedule.xlsx");
        var catalogue = new CatalogueIndex();
        catalogue.Add(new CatalogueEntry
        {
            ArticleNumber = "EN",
            Cct = "4000 K",
            Cri = "80",
            Voltage = "Input voltage: 24 DC V",
            BeamAngle = "120 deg",
            AmbientTemperature = "Ambient temperature range: -20 °C to +45 °C",
            Dimensions = "12 x 6 x 5000 mm",
            SourcePdf = "english.pdf"
        });
        catalogue.Add(new CatalogueEntry
        {
            ArticleNumber = "VN",
            Cri = "≥90",
            Voltage = "Điện áp: 220 đến 240 V",
            AmbientTemperature = "Phạm vi nhiệt độ môi trường xung quanh: -20 °C đến +45 °C",
            Dimensions = "D 100 mm x H 50 mm",
            SourcePdf = "vietnamese.pdf"
        });
        catalogue.Add(new CatalogueEntry
        {
            ArticleNumber = "DN391B",
            Tokens = ["LED12", "840"],
            Cct = "4000 K",
            Cri = "80",
            Voltage = "Input voltage: 220 to 240 V",
            BeamAngle = "120 deg",
            AmbientTemperature = "Nhiệt độ hiệu quả Tq 25 °C",
            Dimensions = "D 100 mm x H 50 mm",
            SourcePdf = "dn391b.pdf"
        });
        catalogue.Add(new CatalogueEntry { ArticleNumber = "COMPACT", Dimensions = "1200x54mm", SourcePdf = "compact.pdf" });
        var exporter = new ExcelExporter();
        try
        {
            exporter.Save([new DialuxReport { Rooms = [new DialuxRoom { Name = "Room EN", Luminaires = [new DialuxLuminaire { ArticleNumber = "EN", Name = "Fixture", Count = 1, PowerWatts = 10 }] }] }], path, catalogue);
            Assert.Equal(1, exporter.Append(path, [new DialuxReport { Rooms = [new DialuxRoom { Name = "Room VN", Luminaires = [new DialuxLuminaire { ArticleNumber = "VN", Name = "Fixture", Count = 1, PowerWatts = 10 }] }] }], catalogue).AddedRows);
            Assert.Equal(1, exporter.Append(path, [new DialuxReport { Rooms = [new DialuxRoom { Name = "Room DN391B", Luminaires = [new DialuxLuminaire { ArticleNumber = "DN391B", Name = "LED12 840", Count = 1, PowerWatts = 10 }] }] }], catalogue).AddedRows);
            Assert.Equal(1, exporter.Append(path, [new DialuxReport { Rooms = [new DialuxRoom { Name = "Room Compact", Luminaires = [new DialuxLuminaire { ArticleNumber = "COMPACT", Name = "Fixture", Count = 1, PowerWatts = 10 }] }] }], catalogue).AddedRows);

            using var workbook = new XLWorkbook(path);
            var sheet = workbook.Worksheet("Lighting Schedule");
            Assert.Equal("CCT: 4000 K\nCRI: >80\n24 DC V\nBeam: 120 độ\n-20 to +45°C\nSize: 12mm x 6mm x 5000mm", sheet.Cell("I3").GetString());
            Assert.Equal("CRI: ≥90\n220 đến 240 V\n-20 đến +45°C\nSize: D 100 mm x H 50 mm", sheet.Cell("I4").GetString());
            Assert.Equal("CCT: 4000 K\nCRI: >80\n220 to 240 V\nBeam: 120 độ\n25°C\nSize: D 100 mm x H 50 mm", sheet.Cell("I5").GetString());
            Assert.Equal("Size: 1200mm x 54mm", sheet.Cell("I6").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("1200x54mm", "1200mm x 54mm")]
    [InlineData("12x6x5000mm", "12mm x 6mm x 5000mm")]
    [InlineData("1200mm x 54mm", "1200mm x 54mm")]
    [InlineData("1200mm × 54mm x 36mm", "1200mm x 54mm x 36mm")]
    [InlineData("12,5×6.25mm", "12,5mm x 6.25mm")]
    public void Save_normalizes_compact_dimensions_with_units_per_component(string dimensions, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerCompactDimensions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "schedule.xlsx");
        var catalogue = new CatalogueIndex();
        catalogue.Add(new CatalogueEntry { ArticleNumber = "SIZE", Dimensions = dimensions, SourcePdf = "fixture.pdf" });
        try
        {
            new ExcelExporter().Save([new DialuxReport { Rooms = [new DialuxRoom { Name = "Room", Luminaires = [new DialuxLuminaire { ArticleNumber = "SIZE", Name = "Fixture", Count = 1 }] }] }], path, catalogue);

            using var workbook = new XLWorkbook(path);
            Assert.Equal($"Size: {expected}", workbook.Worksheet("Lighting Schedule").Cell("I3").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Parameter_never_fabricates_cct_from_product_name_tokens()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerNoCodeCct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "schedule.xlsx");
        var catalogue = new CatalogueIndex();
        catalogue.Add(new CatalogueEntry { ArticleNumber = "OPT", Tokens = ["LED20", "CW"], Cri = "80", SourcePdf = "fixture.pdf" });
        try
        {
            var report = new DialuxReport { Rooms = [new DialuxRoom { Name = "Room", Luminaires = [new DialuxLuminaire { ArticleNumber = "OPT", Name = "LED20 CW X", Count = 1, PowerWatts = 16, LuminousFluxLumens = 2000 }] }] };
            new ExcelExporter().Save([report], path, catalogue);
            using var workbook = new XLWorkbook(path);
            Assert.Equal("CRI: >80", workbook.Worksheet("Lighting Schedule").Cell("I3").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Optional_actual_l1_and_l1m_exports_keep_report_power_flux_and_include_evidence_mapped_family_parameters()
    {
        var reports = new[]
        {
            @"D:\1_HTI_Work\DỰ ÁN\2026\04_07_RITAVO\Plaza\DiaLux\PDF\L1.pdf",
            @"D:\1_HTI_Work\DỰ ÁN\2026\04_07_RITAVO\Plaza\DiaLux\PDF\L1M.pdf"
        };
        var catalogueRoot = @"D:\Claude\DocManager\Product Family\0.EXTRA";
        if (reports.Any(path => !File.Exists(path)) || !Directory.Exists(catalogueRoot)) return;
        var extractor = new PdfTextExtractor();
        var catalogue = new CatalogueIndex();
        catalogue.Build(catalogueRoot);
        foreach (var reportPath in reports)
        {
            var output = Path.Combine(Path.GetTempPath(), $"DocManager-{Path.GetFileNameWithoutExtension(reportPath)}-{Guid.NewGuid():N}.xlsx");
            try
            {
                var report = new DialuxParser().Parse(extractor.Extract(reportPath), Path.GetFileName(reportPath));
                new ExcelExporter().Save([report], output, catalogue);
                using var workbook = new XLWorkbook(output);
                var sheet = workbook.Worksheet("Lighting Schedule");
                var columns = ColumnMap(sheet);
                var rows = Enumerable.Range(3, sheet.LastRowUsed()!.RowNumber() - 2)
                    .Where(row => sheet.Cell(row, columns["Proposed calculation model"]).GetString().Contains("BN008C LED20 CW L1200", StringComparison.OrdinalIgnoreCase)).ToArray();
                Assert.NotEmpty(rows);
                Assert.All(rows, row =>
                {
                    Assert.Equal(16, sheet.Cell(row, columns["Power (W)"]).GetDouble());
                    Assert.Equal(2001, sheet.Cell(row, columns["Luminous flux (lm)"]).GetDouble());
                    Assert.Equal("CCT: 6500 K\nCRI: >80\n25°C\nSize: 1200mm x 54mm x 36mm", sheet.Cell(row, columns["Parameter"]).GetString());
                });
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }
    }

    [Fact]
    public void Synthetic_cw_and_nw_rows_export_source_supported_brochure_parameters()
    {
        var catalogueRoot = @"D:\Claude\DocManager\Product Family\0.EXTRA";
        if (!Directory.Exists(catalogueRoot)) return;
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerBnSynthetic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, "schedule.xlsx");
        try
        {
            var catalogue = new CatalogueIndex();
            catalogue.Build(catalogueRoot);
            var report = new DialuxReport
            {
                Rooms =
                [
                    new DialuxRoom { Name = "CW", Luminaires = [new DialuxLuminaire { ArticleNumber = "BN008C", Name = "LED20 CW L1200 G1 GM X", Count = 1 }] },
                    new DialuxRoom { Name = "NW", Luminaires = [new DialuxLuminaire { ArticleNumber = "BN008C", Name = "LED20 NW L1200 G1 GM", Count = 1 }] }
                ]
            };
            new ExcelExporter().Save([report], output, catalogue);
            using var workbook = new XLWorkbook(output);
            var sheet = workbook.Worksheet("Lighting Schedule");
            var columns = ColumnMap(sheet);
            Assert.True(sheet.Cell(3, columns["Power (W)"]).IsEmpty());
            Assert.True(sheet.Cell(3, columns["Luminous flux (lm)"]).IsEmpty());
            Assert.Equal("CCT: 6500 K\nCRI: >80\n25°C\nSize: 1200mm x 54mm x 36mm", sheet.Cell(3, columns["Parameter"]).GetString());
            Assert.True(sheet.Cell(4, columns["Power (W)"]).IsEmpty());
            Assert.True(sheet.Cell(4, columns["Luminous flux (lm)"]).IsEmpty());
            Assert.Equal("CCT: 4000 K\nCRI: >80\n25°C\nSize: 1200mm x 54mm x 36mm", sheet.Cell(4, columns["Parameter"]).GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Parameter_omits_absent_catalogue_fields()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerPartialParameter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "schedule.xlsx");
        var catalogue = new CatalogueIndex();
        catalogue.Add(new CatalogueEntry { ArticleNumber = "PARTIAL", BeamAngle = "36°", SourcePdf = "fixture.pdf" });
        try
        {
            new ExcelExporter().Save([new DialuxReport { Rooms = [new DialuxRoom { Name = "Room", Luminaires = [new DialuxLuminaire { ArticleNumber = "PARTIAL", Name = "Fixture", Count = 1, PowerWatts = 10 }] }] }], path, catalogue);
            using var workbook = new XLWorkbook(path);
            Assert.Equal("Beam: 36 độ", workbook.Worksheet("Lighting Schedule").Cell("I3").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Existing_form_detects_headers_appends_style_formula_dedupes_and_saves_atomically()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerAppend-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "existing.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Lighting BOH");
                sheet.Cell("A1").Value = "No.";
                sheet.Cell("B1").Value = "AREA";
                sheet.Cell("I1").Value = "Proposed calculation model";
                sheet.Cell("J1").Value = "Parameter";
                sheet.Cell("L1").Value = "Quantity";
                sheet.Cell("M1").Value = "Power (W)";
                sheet.Cell("N1").Value = "Total power (W)";
                sheet.Cell("O1").Value = "Luminous flux (lm)";
                sheet.Cell("P1").Value = "Efficiency (lm/W)";
                sheet.Cell("A3").Value = 1;
                sheet.Cell("B3").Value = "Existing room";
                sheet.Cell("I3").Value = "EXISTING";
                sheet.Cell("L3").Value = 1;
                sheet.Cell("M3").Value = 10;
                sheet.Row(3).Style.Fill.BackgroundColor = XLColor.Yellow;
                workbook.SaveAs(path);
            }

            var report = new DialuxReport
            {
                Rooms = [new DialuxRoom { Name = "Floor · Classroom", Luminaires = [new DialuxLuminaire { ArticleNumber = "RC099V", Name = "G3 LED69 840", Count = 2, PowerWatts = 69, LuminousFluxLumens = 6900 }] }]
            };
            var exporter = new ExcelExporter();
            var form = exporter.DetectLightingForm(path);
            Assert.Equal("Lighting BOH", form.WorksheetName);
            var first = exporter.Append(path, [report]);
            var second = exporter.Append(path, [report]);

            Assert.Equal(1, first.AddedRows);
            Assert.Equal(1, second.SkippedDuplicates);
            Assert.DoesNotContain(Directory.EnumerateFiles(root), file => Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal));
            using var appended = new XLWorkbook(path);
            var appendedSheet = appended.Worksheet("Lighting BOH");
            Assert.Equal(XLColor.Yellow, appendedSheet.Cell("A4").Style.Fill.BackgroundColor);
            Assert.Equal("L4*M4", appendedSheet.Cell("N4").FormulaA1);
            Assert.Equal("O4/M4", appendedSheet.Cell("P4").FormulaA1);
            Assert.Equal("Classroom", appendedSheet.Cell("B4").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Append_dedupes_identical_two_luminaire_room_with_merged_area_cells()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerMerged-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "merged.xlsx");
        var report = new DialuxReport
        {
            Rooms =
            [
                new DialuxRoom
                {
                    Name = "Floor · Classroom",
                    Luminaires =
                    [
                        new DialuxLuminaire { ArticleNumber = "A", Name = "Model A", Count = 2, PowerWatts = 10, LuminousFluxLumens = 1000 },
                        new DialuxLuminaire { ArticleNumber = "B", Name = "Model B", Count = 3, PowerWatts = 20, LuminousFluxLumens = 2000 }
                    ]
                }
            ]
        };
        try
        {
            new ExcelExporter().Save([report], path);
            var exporter = new ExcelExporter();
            var result = exporter.Append(path, [report]);
            Assert.Equal(0, result.AddedRows);
            Assert.Equal(2, result.SkippedDuplicates);
            using var workbook = new XLWorkbook(path);
            Assert.Equal(4, workbook.Worksheet("Lighting Schedule").LastRowUsed()!.RowNumber());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Save_and_append_leave_efficiency_blank_for_empty_or_zero_power_rows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerFormula-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "formulas.xlsx");
        var report = new DialuxReport
        {
            Rooms =
            [
                new DialuxRoom { Name = "Floor · Empty" },
                new DialuxRoom { Name = "Floor · Zero", Luminaires = [new DialuxLuminaire { ArticleNumber = "ZERO", Name = "Zero", Count = 1, PowerWatts = 0, LuminousFluxLumens = 1000 }] }
            ]
        };
        try
        {
            var exporter = new ExcelExporter();
            exporter.Save([report], path);
            using (var saved = new XLWorkbook(path))
            {
                Assert.Equal(string.Empty, saved.Worksheet("Lighting Schedule").Cell("O3").FormulaA1);
                Assert.Equal(string.Empty, saved.Worksheet("Lighting Schedule").Cell("O4").FormulaA1);
            }

            var appendedReport = new DialuxReport
            {
                Rooms = [new DialuxRoom { Name = "Floor · Appended zero", Luminaires = [new DialuxLuminaire { ArticleNumber = "ZERO2", Name = "Appended zero", Count = 1, PowerWatts = 0, LuminousFluxLumens = 1200 }] }]
            };
            Assert.Equal(1, exporter.Append(path, [appendedReport]).AddedRows);
            using var appended = new XLWorkbook(path);
            Assert.Equal(string.Empty, appended.Worksheet("Lighting Schedule").Cell("O3").FormulaA1);
            Assert.Equal(string.Empty, appended.Worksheet("Lighting Schedule").Cell("O4").FormulaA1);
            Assert.Equal(string.Empty, appended.Worksheet("Lighting Schedule").Cell("O5").FormulaA1);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Total_power_includes_room_only_inventory_without_counting_report_duplicates_twice()
    {
        var report = new DialuxReport
        {
            Luminaires = [new DialuxLuminaire { ArticleNumber = "A", Name = "Main", Count = 3, PowerWatts = 10 }],
            Rooms =
            [
                new DialuxRoom
                {
                    Name = "Room 1",
                    Luminaires =
                    [
                        new DialuxLuminaire { ArticleNumber = "A", Name = "Main", Count = 1, PowerWatts = 10 },
                        new DialuxLuminaire { ArticleNumber = "B", Name = "Room only", Count = 2, PowerWatts = 20 }
                    ]
                }
            ]
        };

        Assert.Equal(70, report.TotalPowerWatts);
        Assert.Equal(50, (report with { Luminaires = [] }).TotalPowerWatts);
    }

    private static IReadOnlyDictionary<string, int> ColumnMap(IXLWorksheet sheet) =>
        Enumerable.Range(1, 16)
            .Select(column => (Header: SemanticHeader(sheet, column), Column: column))
            .Where(item => item.Header.Length > 0)
            .ToDictionary(item => item.Header, item => item.Column, StringComparer.OrdinalIgnoreCase);

    private static string SemanticHeader(IXLWorksheet sheet, int column)
    {
        var bottom = sheet.Cell(2, column);
        if (!bottom.IsMerged() && bottom.GetString() is { Length: > 0 } bottomHeader) return bottomHeader;
        return MergedCellValue(sheet.Cell(1, column));
    }

    private static string MergedCellValue(IXLCell cell) => cell.IsMerged() ? cell.MergedRange()!.FirstCell().GetString() : cell.GetString();

    private static string RoomLeaf(string name) => Regex.Replace(name.Split('·').Last().Trim(), @"\s*\([^)]*\)\s*$", string.Empty).Trim();
    private static string ParameterValue(string value) => Regex.Replace(value, @"^\s*(?:(?:Input|Operating|Supply|Mains)\s+voltage|Điện\s+áp(?:\s+(?:đầu\s+vào|hoạt\s+động|vận\s+hành|nguồn))?|Ambient\s+temperature\s+range|Nhiệt\s+độ\s+môi\s+trường(?:\s+cho\s+phép)?|Phạm\s+vi\s+nhiệt\s+độ\s+môi\s+trường\s+xung\s+quanh|Dải\s+nhiệt\s+độ)\s*:?\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
    private static string ParameterCri(string value)
    {
        var match = Regex.Match(value ?? string.Empty, @"^\s*(?<comparator>>|≥)?\s*(?<value>\d{2,3})\s*$", RegexOptions.CultureInvariant);
        return match.Success ? $"CRI: {(match.Groups["comparator"].Value is { Length: > 0 } comparator ? comparator : ">")}{match.Groups["value"].Value}" : string.Empty;
    }
    private static string ParameterBeam(string value)
    {
        var match = Regex.Match(value ?? string.Empty, @"\d[\d.,]*\s*(?:°|deg(?:ree)?s?|độ)(?:\s*(?:to|đến|[-–—])\s*\d[\d.,]*\s*(?:°|deg(?:ree)?s?|độ))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? $"Beam: {Regex.Replace(Regex.Replace(match.Value, @"°|deg(?:ree)?s?", " độ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), @"\s+", " ").Trim()}" : string.Empty;
    }
    private static string ParameterAmbient(string value)
    {
        var normalized = ParameterValue(value);
        var range = Regex.Match(normalized, @"^(?<low>[+-]?\d[\d.,]*)\s*°\s*C\s*(?:to|đến|[-–—])\s*(?<high>[+-]?\d[\d.,]*)\s*°\s*C$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return range.Success ? $"{range.Groups["low"].Value} đến {range.Groups["high"].Value}°C" : Regex.Replace(normalized, @"\s*°\s*C\b", "°C", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
    }
    private static string ParameterDimensions(string value)
    {
        var normalized = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (Regex.IsMatch(normalized, @"\b[DLWH]\s*\d[\d.,]*\s*mm\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            normalized = Regex.Replace(normalized, @"\b([DLWH])\s*(?=\d)", "$1 ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"\s*(?:x|×)\s*", " x ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return Regex.Replace(normalized, @"(?<=\d)\s*mm\b", " mm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        var components = Regex.Split(normalized, @"\s*(?:x|×)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var matches = components.Select(component => Regex.Match(component, @"^\s*(?<value>\d[\d.,]*)\s*(?<unit>mm)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
        var unitCount = matches.Count(match => match.Success && match.Groups["unit"].Success);
        if (components.Length > 1 && matches.All(match => match.Success) &&
            (unitCount == components.Length || unitCount == 1 && matches[^1].Groups["unit"].Success))
        {
            return string.Join(" x ", matches.Select(match => $"{match.Groups["value"].Value}mm"));
        }

        normalized = Regex.Replace(normalized, @"\s*([x×])\s*", "$1");
        return Regex.Replace(normalized, @"\s+mm\b", "mm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static byte[] SolidPng(int width, int height) => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+XhJeAAAAAElFTkSuQmCC");

    private static byte[] SolidPngAlt() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/lY5ZGwAAAABJRU5ErkJggg==");

    private static int DrawingCount(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Count(entry => entry.FullName.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertPicturesBoundedToCell(string path, int row, int column)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Lighting Schedule");
        var pictures = sheet.Pictures.ToArray();
        Assert.NotEmpty(pictures);
        var rowPixels = sheet.Row(row).Height * 96d / 72d;
        var columnPixels = sheet.Column(column).Width * 7d + 5d;
        Assert.All(pictures, picture =>
        {
            Assert.Equal(row, picture.TopLeftCell.Address.RowNumber);
            Assert.Equal(column, picture.TopLeftCell.Address.ColumnNumber);
            Assert.True(picture.Width <= columnPixels, $"Picture exceeds reference column: {picture.Width} > {columnPixels}");
            Assert.True(picture.Height <= rowPixels, $"Picture exceeds reference row: {picture.Height} > {rowPixels}");
        });
        Assert.True(pictures.Sum(picture => picture.Height) + Math.Max(0, pictures.Length - 1) * 6 <= rowPixels);
    }

    private static string DrawingXml(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = Assert.Single(archive.Entries, item => item.FullName.StartsWith("xl/drawings/drawing", StringComparison.OrdinalIgnoreCase) && item.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Excel_export_smoke_has_sheet_formulas_and_catalogue_cct()
    {
        var report = new DialuxReport
        {
            SourceFile = "fixture.pdf",
            Rooms =
            [
                new DialuxRoom
                {
                    Name = "Floor · Zone · Classroom (Scene 1)", AverageLux = 500, TargetLux = 500, Uniformity = .6, TargetUniformity = .4,
                    Luminaires = [new DialuxLuminaire { ArticleNumber = "RC099V", Name = "G3 LED69 840 PSU", Count = 2, PowerWatts = 70, LuminousFluxLumens = 6800 }]
                }
            ]
        };
        var catalogue = new CatalogueIndex();
        catalogue.Add(new CatalogueEntry { ArticleNumber = "RC099V", Tokens = ["G3", "LED69", "840", "PSU"], PowerWatts = 69, LuminousFluxLumens = 6900, Cct = "4000 K", Cri = "80", IpCode = "IP20", SourcePdf = "fixture.pdf" });
        var root = Path.Combine(Path.GetTempPath(), $"DocManagerExcel-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
        var output = Path.Combine(root, "schedule.xlsx");
        try
        {
            new ExcelExporter().Save([report], output, catalogue);
            using var workbook = new XLWorkbook(output);
            var sheet = workbook.Worksheet("Lighting Schedule");
            Assert.Equal("Classroom", sheet.Cell("B3").GetString());
            Assert.Equal("K3*L3", sheet.Cell("M3").FormulaA1);
            Assert.Equal("N3/L3", sheet.Cell("O3").FormulaA1);
            Assert.Contains("CCT: 4000 K", sheet.Cell("I3").GetString());
            Assert.Equal(69, sheet.Cell("L3").GetDouble());
        }
        finally { Directory.Delete(root, true); }
    }
}
