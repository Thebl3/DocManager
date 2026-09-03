using System.Globalization;
using System.Text;
using DocManager.Photometry;
using DocManager.Signify;
using UglyToad.PdfPig.Writer;

namespace DocManager.Tests;

public sealed class PhotometryTests
{
    [Fact]
    public void Parser_strips_bom_coerces_zero_lamps_and_supplies_canonical_fallbacks()
    {
        var text = "﻿" + PhotometryFixture.Ies(lampCount: 0, manufacturer: null, lamp: null);

        var ies = new IesParser().Parse(text, "fallback-product.ies");

        Assert.Equal("IESNA:LM-63-2002", ies.Version);
        Assert.Equal(1, ies.LampCount);
        Assert.Equal("Unknown", ies.Manufacturer);
        Assert.Equal("LED", ies.Lamp);
    }

    [Fact]
    public void Tilt_other_than_none_fails()
    {
        var text = PhotometryFixture.Ies().Replace("TILT=NONE", "TILT=INCLUDE", StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => new IesParser().Parse(text));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Parser_rejects_photometric_types_without_coordinate_transform(int photometricType)
    {
        var exception = Assert.Throws<NotSupportedException>(() => new IesParser().Parse(PhotometryFixture.Ies(photometricType: photometricType)));
        Assert.Contains("coordinate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Parser_rejects_invalid_lumens_per_lamp(double lumensPerLamp)
    {
        var exception = Assert.Throws<FormatException>(() => new IesParser().Parse(PhotometryFixture.Ies(lumensPerLamp: lumensPerLamp)));

        Assert.Contains("Quang thông", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Parser_rejects_nonfinite_lumens_per_lamp(string value)
    {
        var text = PhotometryFixture.Ies().Replace("1 1000 1 3", $"1 {value} 1 3", StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => new IesParser().Parse(text));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Parser_rejects_nonpositive_candela_multiplier(double multiplier)
    {
        var text = PhotometryFixture.Ies().Replace("1 1000 1 3", $"1 1000 {multiplier.ToString(CultureInfo.InvariantCulture)} 3", StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => new IesParser().Parse(text));
    }

    [Fact]
    public void Parser_rejects_negative_candela_after_multiplier()
    {
        var exception = Assert.Throws<FormatException>(() => new IesParser().Parse(PhotometryFixture.Ies(candela: [[-1, 0, 1]])));

        Assert.Contains("candela", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Absolute_photometry_requires_catalogue_flux_and_uses_official_marker_and_normalization()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies(lumensPerLamp: -1, horizontal: [0], vertical: [0, 90, 180], candela: [[100, 100, 100]]));
        var converter = new LdtConverter();

        var exception = Assert.Throws<InvalidDataException>(() => converter.Convert(ies, null, out _, out _, out _));
        Assert.Contains("catalogue", exception.Message, StringComparison.OrdinalIgnoreCase);

        var ldt = PhotometryFixture.ParseLdt(converter.Convert(
            ies,
            new PhotometryMetadata { LuminousFluxLumens = 2_000 },
            out _, out var flux, out _));
        Assert.Equal(2_000, flux);
        Assert.Equal("1", ldt.Lines[25]);
        Assert.Equal("-1", ldt.Lines[26]);
        Assert.Equal("2000", ldt.Lines[28]);
        Assert.Equal([50d, 50d, 50d], ldt.Intensities);
    }

    [Theory]
    [InlineData("Lumen output: 2,000 lm", 2000)]
    [InlineData("Luminous flux: 2,000 lm", 2000)]
    public void Catalogue_recognizes_flux_aliases(string text, double expected)
    {
        var metadata = new PhotometryCatalogue().Extract(text, new IesParser().Parse(PhotometryFixture.Ies()));
        Assert.Equal(expected, metadata.LuminousFluxLumens);
    }

    [Fact]
    public void Catalogue_rejects_conflicting_multi_sku_technical_values()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies());
        var metadata = new PhotometryCatalogue().Extract(
            "Luminous flux: 1000 lm\nLuminous flux: 2000 lm\nInput power: 10 W\nInput power: 20 W\nCRI: 80\nCRI: 90\nCCT: 3000 K\nCCT: 4000 K",
            ies);

        Assert.Equal(ies.TotalLumens, metadata.LuminousFluxLumens);
        Assert.Equal(ies.InputWatts, metadata.InputWatts);
        Assert.Null(metadata.Cri);
        Assert.Null(metadata.CctKelvin);
        Assert.DoesNotContain("catalogue:", metadata.LuminousFluxProvenance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("catalogue:", metadata.InputWattsProvenance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalogue_ignores_out_of_range_cri_and_converter_serializes_fallback()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies());
        var metadata = new PhotometryCatalogue().Extract("CRI: 999", ies);
        var ldt = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, metadata, out _, out _, out _));

        Assert.Null(metadata.Cri);
        Assert.Equal(string.Empty, metadata.CriProvenance);
        Assert.Equal("0", ldt.Lines[30]);
    }

    [Theory]
    [InlineData("Wattage: 25 W", 25)]
    [InlineData("Mức tiêu thụ điện: 26 W", 26)]
    public void Catalogue_recognizes_power_aliases(string text, double expected)
    {
        var metadata = new PhotometryCatalogue().Extract(text, new IesParser().Parse(PhotometryFixture.Ies()));
        Assert.Equal(expected, metadata.InputWatts);
    }

    [Fact]
    public void Catalogue_overrides_only_technical_fields_and_canonical_identity_uses_lines_9_and_11_with_blank_10()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies(lumcat: "IES-CAT", manufacturer: "IES Maker", lamp: "IES LED"));
        var extracted = new PhotometryCatalogue().Extract(
            "Lumen output: 2,000 lm\nWattage: 25 W\nCRI: 90\nCCT: 4000 K",
            ies,
            "PDF-CAT");
        var metadata = extracted with
        {
            Manufacturer = "PDF Maker",
            LuminaireName = "PDF Name",
            CatalogueNumber = "PDF-CAT",
            Description = "PDF Lamp"
        };

        var ldt = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, metadata, "Đèn chính/840", out _, out _, out _));

        Assert.Equal("IES Maker", ldt.Lines[0]);
        Assert.Equal("Đèn chính 840", ldt.Lines[8]);
        Assert.Equal(string.Empty, ldt.Lines[9]);
        Assert.Equal("Đèn chính 840", ldt.Lines[10]);
        Assert.Equal("IES LED", ldt.Lines[27]);
        Assert.Equal("2000", ldt.Lines[28]);
        Assert.Equal("4000", ldt.Lines[29]);
        Assert.Equal("90", ldt.Lines[30]);
        Assert.Equal("25", ldt.Lines[31]);
    }

    [Fact]
    public void Ies_fallback_dimensions_mirror_to_luminous_area_without_shifting_photometry()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies(
            horizontal: [0],
            vertical: [0, 90, 180],
            candela: [[100, 100, 100]],
            width: 0.1,
            length: 1.2,
            height: 0.05));
        var ldt = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, null, out _, out _, out _));

        Assert.Equal(["1200", "100", "50"], ldt.Lines[12..15]);
        Assert.Equal(["1200", "100", "50"], ldt.Lines[15..18]);
        Assert.Equal(["0", "0", "0"], ldt.Lines[18..21]);
        Assert.Equal(["50.0", "125.7"], ldt.Lines[21..23]);
        Assert.Equal([0d], ldt.CPlanes);
        Assert.Equal([0d, 90d, 180d], ldt.GammaAngles);
        Assert.Equal([100d, 100d, 100d], ldt.Intensities);
        Assert.Contains("dimensions=IES fallback", ldt.Lines[11], StringComparison.Ordinal);
    }

    [Fact]
    public void Catalogue_rectangular_geometry_mirrors_physical_dimensions_to_luminous_area()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies(width: 0.1, length: 1.2, height: 0.05));
        var metadata = new PhotometryMetadata
        {
            OverallGeometry = new(
                PhotometryGeometryShape.Rectangular,
                480,
                320,
                109,
                "catalogue:overall-dimensions",
                [new(2, "Overall dimensions", "Overall dimensions L x W x H: 480 x 320 x 109 mm")])
        };
        var lines = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, metadata, out _, out _, out _)).Lines;

        Assert.Equal(["480", "320", "109"], lines[12..15]);
        Assert.Equal(["480", "320", "109"], lines[15..18]);
        Assert.Equal(["0", "0", "0"], lines[18..21]);
        Assert.Contains("dimensions=catalogue overall", lines[11], StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_public_geometry_and_luminous_axis_heights_fall_back_safely()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies());
        var metadata = new PhotometryMetadata
        {
            OverallGeometry = new(PhotometryGeometryShape.Rectangular, double.NaN, -1, double.PositiveInfinity, "test", []),
            LuminousArea = new(PhotometryGeometryShape.Rectangular, 450, 320, 10, double.NaN, -1, double.PositiveInfinity, "test", [])
        };

        var lines = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, metadata, out _, out _, out _)).Lines;

        Assert.Equal(["1000", "100", "50"], lines[12..15]);
        Assert.Equal(["0", "0", "0"], lines[18..21]);
    }

    [Fact]
    public void Explicit_luminous_area_values_win_while_missing_values_mirror_physical_dimensions()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies());
        var metadata = new PhotometryMetadata
        {
            OverallGeometry = new(
                PhotometryGeometryShape.Rectangular,
                480,
                320,
                109,
                "catalogue:overall-dimensions",
                [new(1, "Overall dimensions", "Overall dimensions: 480 x 320 x 109 mm")]),
            LuminousArea = new(
                PhotometryGeometryShape.Rectangular,
                450,
                0,
                0,
                12,
                13,
                14,
                "catalogue:luminous-area",
                [new(1, "Luminous area", "Luminous area: 450 mm")])
        };
        var lines = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, metadata, out _, out _, out _)).Lines;

        Assert.Equal(["450", "320", "109"], lines[15..18]);
        Assert.Equal(["12", "13", "14"], lines[18..21]);
    }

    [Fact]
    public void Circular_luminous_width_falls_back_to_overall_width_exactly_like_line_14()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies());
        var metadata = new PhotometryMetadata
        {
            OverallGeometry = new(
                PhotometryGeometryShape.Rectangular,
                480,
                320,
                109,
                "catalogue:overall-dimensions",
                []),
            LuminousArea = new(
                PhotometryGeometryShape.Circular,
                225,
                0,
                20,
                0,
                0,
                0,
                "catalogue:luminous-area",
                [])
        };

        var lines = PhotometryFixture.ParseLdt(
            new LdtConverter().Convert(ies, metadata, out _, out _, out _)).Lines;

        Assert.Equal("320", lines[13]);
        Assert.Equal("320", lines[16]);
    }

    [Fact]
    public void Circular_catalogue_geometry_serializes_dimensions_without_forcing_axial_type()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies(horizontal: [0, 90, 180, 270], width: 0.2, length: 0.2, height: 0.05));
        var metadata = new PhotometryMetadata
        {
            OverallGeometry = new(
                PhotometryGeometryShape.Circular,
                225,
                0,
                35,
                "catalogue:overall-dimensions",
                [new(1, "Overall diameter", "Overall diameter: 225 mm"), new(1, "Overall height", "Overall height: 35 mm")])
        };
        var lines = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, metadata, out var symmetry, out _, out _)).Lines;

        Assert.Equal(0, symmetry);
        Assert.Equal("3", lines[1]);
        Assert.Equal(["225", "0", "35"], lines[12..15]);
        Assert.Equal(["225", "0", "35"], lines[15..18]);
        Assert.Equal(["0", "0", "0"], lines[18..21]);
    }

    [Theory]
    [InlineData("Overall diameter: 120 mm\nOverall height: 68 mm", PhotometryGeometryShape.Circular, 120, 0, 68)]
    [InlineData("Overall dimensions L x W x H: 480 x 320 x 109 mm", PhotometryGeometryShape.Rectangular, 480, 320, 109)]
    [InlineData("Overall length: 600 mm\nOverall width: 80 mm\nOverall height: 25 mm", PhotometryGeometryShape.Rectangular, 600, 80, 25)]
    public void Catalogue_geometry_accepts_only_complete_labelled_overall_dimensions(
        string geometryText,
        PhotometryGeometryShape shape,
        double length,
        double width,
        double height)
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var pdf = Path.Combine(root, "Fixture.pdf");
            WriteGeometryPdf(pdf, geometryText.Split('\n'));
            var metadata = new PhotometryCatalogue().ExtractPdf(pdf, new IesParser().Parse(PhotometryFixture.Ies()));

            Assert.NotNull(metadata.OverallGeometry);
            Assert.Equal(shape, metadata.OverallGeometry!.Shape);
            Assert.Equal(length, metadata.OverallGeometry.LengthOrDiameterMm);
            Assert.Equal(width, metadata.OverallGeometry.WidthMm);
            Assert.Equal(height, metadata.OverallGeometry.HeightMm);
            Assert.All(metadata.OverallGeometry.Evidence, evidence => Assert.True(evidence.PageNumber > 0));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("Cut-out Overall diameter: 120 mm\nOverall height: 68 mm")]
    [InlineData("Overall diameter: 120 mm")]
    [InlineData("Fixture D100 W30L120 L5000")]
    [InlineData("Packaging Overall dimensions: 480 x 320 x 109 mm")]
    public void Catalogue_geometry_rejects_forbidden_incomplete_and_product_token_dimensions(string geometryText)
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var pdf = Path.Combine(root, "Fixture.pdf");
            WriteGeometryPdf(pdf, geometryText.Split('\n'));
            var metadata = new PhotometryCatalogue().ExtractPdf(pdf, new IesParser().Parse(PhotometryFixture.Ies()));
            Assert.Null(metadata.OverallGeometry);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Catalogue_geometry_conflict_falls_back_to_ies_with_warning()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var pdf = Path.Combine(root, "Fixture.pdf");
            WriteGeometryPdf(pdf, ["Overall diameter: 120 mm", "Overall height: 68 mm", "Overall diameter: 225 mm", "Overall height: 35 mm"]);
            var metadata = new PhotometryCatalogue().ExtractPdf(pdf, new IesParser().Parse(PhotometryFixture.Ies()));

            Assert.Null(metadata.OverallGeometry);
            Assert.Contains(metadata.GeometryWarnings, warning => warning.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Catalogue_geometry_enumerates_multiple_complete_dimensions_on_one_visual_row()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var pdf = Path.Combine(root, "Fixture.pdf");
            WriteGeometryPdf(pdf, ["Overall dimensions: 480 x 320 x 109 mm Overall dimensions: 600 x 80 x 25 mm"]);

            var metadata = new PhotometryCatalogue().ExtractPdf(pdf, new IesParser().Parse(PhotometryFixture.Ies()));

            Assert.Null(metadata.OverallGeometry);
            Assert.Contains(metadata.GeometryWarnings, warning => warning.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Catalogue_geometry_does_not_cross_pair_nearby_variant_label_sets()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var pdf = Path.Combine(root, "Fixture.pdf");
            WriteGeometryPdf(pdf,
            [
                "Variant A",
                "Overall length: 600 mm",
                "Overall width: 80 mm",
                "Overall height: 25 mm",
                "Variant B",
                "Overall length: 1200 mm",
                "Overall width: 100 mm",
                "Overall height: 50 mm"
            ]);

            var metadata = new PhotometryCatalogue().ExtractPdf(pdf, new IesParser().Parse(PhotometryFixture.Ies()));

            Assert.Null(metadata.OverallGeometry);
            Assert.Contains(metadata.GeometryWarnings, warning => warning.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Irregular_grids_report_zero_steps_and_preserve_declared_structure()
    {
        var horizontal = new[] { 0d, 100d, 230d };
        var vertical = new[] { 0d, 30d, 75d, 180d };
        var ies = new IesParser().Parse(PhotometryFixture.Ies(horizontal: horizontal, vertical: vertical));

        var ldt = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, null, out var symmetry, out _, out _));

        Assert.Equal(0, symmetry);
        Assert.Equal("0", ldt.Lines[4]);
        Assert.Equal("0", ldt.Lines[6]);
        Assert.Equal(horizontal, ldt.CPlanes);
        Assert.Equal(vertical, ldt.GammaAngles);
        Assert.Equal(horizontal.Length * vertical.Length, ldt.Intensities.Length);
    }

    [Theory]
    [InlineData(new double[] { 0, 0, 180 })]
    [InlineData(new double[] { 0, 90, 90, 180 })]
    [InlineData(new double[] { 0, 90, 180, 180 })]
    public void Parser_rejects_duplicate_gamma_angles_before_conversion(double[] vertical)
    {
        var exception = Assert.Throws<FormatException>(() => new IesParser().Parse(PhotometryFixture.Ies(vertical: vertical)));

        Assert.Contains("Gamma", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nghiêm ngặt", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_rejects_conflicting_duplicate_non_closure_c_planes()
    {
        var exception = Assert.Throws<InvalidDataException>(() => new IesParser().Parse(PhotometryFixture.Ies(
            horizontal: [0, 90, 90, 180],
            candela: [[100, 90, 80], [90, 80, 70], [91, 80, 70], [80, 70, 60]])));

        Assert.Contains("lặp", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_canonicalizes_identical_duplicate_non_closure_c_planes()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies(
            horizontal: [0, 90, 90, 180],
            candela: [[100, 90, 80], [90, 80, 70], [90, 80, 70], [80, 70, 60]]));

        Assert.Equal([0d, 90d, 180d], ies.HorizontalAngles);
        Assert.Equal(3, ies.HorizontalAngleCount);
        Assert.Equal(3, ies.Candela.Count);
    }

    [Fact]
    public void Full_uniform_grid_reports_step_only_when_cyclic_closure_is_uniform()
    {
        var horizontal = new[] { 0d, 90d, 180d, 270d, 360d };
        var vertical = new[] { 0d, 90d, 180d };
        var candela = horizontal.Select((_, row) => vertical.Select((_, column) => 100d - (5d * (row % 4)) - (20d * column)).ToArray()).ToArray();
        candela[^1] = candela[0].ToArray();
        var ies = new IesParser().Parse(PhotometryFixture.Ies(horizontal: horizontal, vertical: vertical, candela: candela));
        var ldt = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, null, out var symmetry, out _, out _));

        Assert.Equal(0, symmetry);
        Assert.Equal("90", ldt.Lines[4]);
        Assert.Equal([0d, 90d, 180d, 270d], ldt.CPlanes);
        Assert.Equal(4 * vertical.Length, ldt.Intensities.Length);
    }

    [Fact]
    public void Converter_public_boundary_accepts_matching_C0_C360_and_omits_C360_from_mc()
    {
        var vertical = new[] { 0d, 90d, 180d };
        var first = new[] { 100d, 80d, 20d };
        var ies = CoreAndCatalogueTests.BasicIes() with
        {
            VerticalAngleCount = vertical.Length,
            HorizontalAngleCount = 5,
            VerticalAngles = vertical,
            HorizontalAngles = [0d, 90d, 180d, 270d, 360d],
            Candela = [first, [90d, 70d, 10d], [80d, 60d, 5d], [90d, 70d, 10d], first.ToArray()]
        };

        var ldt = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, null, out var symmetry, out _, out _));

        Assert.Equal(0, symmetry);
        Assert.Equal([0d, 90d, 180d, 270d], ldt.CPlanes);
        Assert.Equal(4 * vertical.Length, ldt.Intensities.Length);
    }

    [Fact]
    public void Converter_public_boundary_rejects_conflicting_C0_C360_rows()
    {
        var ies = CoreAndCatalogueTests.BasicIes() with
        {
            HorizontalAngleCount = 5,
            HorizontalAngles = [0d, 90d, 180d, 270d, 360d],
            Candela =
            [
                [100d, 80d, 20d],
                [90d, 70d, 10d],
                [80d, 60d, 5d],
                [90d, 70d, 10d],
                [101d, 80d, 20d]
            ]
        };

        var exception = Assert.Throws<InvalidDataException>(() =>
            new LdtConverter().Convert(ies, null, out _, out _, out _));

        Assert.Contains("C360", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C0", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(new double[] { 0, 90, 90 })]
    [InlineData(new double[] { 0, 90, double.NaN })]
    public void Converter_public_boundary_rejects_non_increasing_or_non_finite_angle_grids(double[] horizontal)
    {
        var ies = CoreAndCatalogueTests.BasicIes() with
        {
            HorizontalAngleCount = horizontal.Length,
            HorizontalAngles = horizontal,
            Candela = horizontal.Select(_ => (IReadOnlyList<double>)[100d, 50d, 0d]).ToArray()
        };

        Assert.Throws<InvalidDataException>(() =>
            new LdtConverter().Convert(ies, null, out _, out _, out _));
    }

    [Fact]
    public void Non_divisor_quarter_grid_is_rejected_when_it_cannot_form_official_full_mc()
    {
        var horizontal = new[] { 0d, 7d, 14d, 21d, 28d, 35d, 42d, 49d, 56d, 63d, 70d, 77d, 84d, 90d };
        var ies = new IesParser().Parse(PhotometryFixture.Ies(horizontal: horizontal));

        var exception = Assert.Throws<InvalidDataException>(() => new LdtConverter().Convert(ies, null, out _, out _, out _));

        Assert.Contains("full C planes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SymmetryCases))]
    public void Symmetry_serializes_full_mc_angles_and_reduced_candela_rows(double[] horizontal, int expectedIsym, double[] expectedFullPlanes, int expectedPayloadRows)
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies(horizontal: horizontal));

        var ldt = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, null, out var symmetry, out _, out _));

        Assert.Equal(expectedIsym, symmetry);
        Assert.Equal(expectedIsym.ToString(CultureInfo.InvariantCulture), ldt.Lines[2]);
        Assert.Equal(expectedFullPlanes.Length, int.Parse(ldt.Lines[3], CultureInfo.InvariantCulture));
        Assert.Equal(expectedFullPlanes, ldt.CPlanes);
        Assert.Equal(expectedPayloadRows, ldt.PayloadPlaneCount);
        Assert.Equal(expectedPayloadRows * ldt.GammaAngles.Length, ldt.Intensities.Length);
        Assert.Equal(42 + expectedFullPlanes.Length + ldt.GammaAngles.Length + ldt.Intensities.Length, ldt.Lines.Length);
    }

    public static TheoryData<double[], int, double[], int> SymmetryCases => new()
    {
        { [0], 1, [0], 1 },
        { [0, 45, 90], 4, [0, 45, 90, 135, 180, 225, 270, 315], 3 },
        { [0, 90, 180], 2, [0, 90, 180, 270], 3 },
        { [0, 90, 180, 270], 0, [0, 90, 180, 270], 4 }
    };

    [Fact]
    public void C90_C270_source_uses_isym3_wrapped_payload_order()
    {
        var horizontal = new[] { 90d, 180d, 270d };
        var vertical = new[] { 0d, 90d, 180d };
        var candela = new[]
        {
            new[] { 90d, 90d, 90d },
            new[] { 180d, 180d, 180d },
            new[] { 270d, 270d, 270d }
        };
        var ies = new IesParser().Parse(PhotometryFixture.Ies(horizontal: horizontal, vertical: vertical, candela: candela));

        var ldt = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, null, out var symmetry, out _, out _));

        Assert.Equal(3, symmetry);
        Assert.Equal([0d, 90d, 180d, 270d], ldt.CPlanes);
        Assert.Equal(3, ldt.PayloadPlaneCount);
        Assert.Equal(270, ldt.Intensities[0]);
        Assert.Equal(180, ldt.Intensities[3]);
        Assert.Equal(90, ldt.Intensities[6]);
    }

    [Fact]
    public void Catalogue_geometry_controls_type_indicator_from_finalized_dimensions()
    {
        var converter = new LdtConverter();
        var rotational = new IesParser().Parse(PhotometryFixture.Ies(horizontal: [0], width: 0.1, length: 1.2));
        var circular = new PhotometryMetadata
        {
            OverallGeometry = new(PhotometryGeometryShape.Circular, 225, 0, 35, "catalogue:overall-dimensions", [])
        };
        var rectangular = new PhotometryMetadata
        {
            OverallGeometry = new(PhotometryGeometryShape.Rectangular, 1200, 100, 35, "catalogue:overall-dimensions", [])
        };

        var circularOutput = PhotometryFixture.ParseLdt(converter.Convert(rotational, circular, out _, out _, out _));
        var linearOutput = PhotometryFixture.ParseLdt(converter.Convert(rotational, rectangular, out _, out _, out _));
        var asymmetric = new IesParser().Parse(PhotometryFixture.Ies(horizontal: [0, 90, 180, 270], width: 1.2, length: 0.1));
        var asymmetricOutput = PhotometryFixture.ParseLdt(converter.Convert(asymmetric, circular, out _, out _, out _));

        Assert.Equal("1", circularOutput.Lines[1]);
        Assert.Equal("2", linearOutput.Lines[1]);
        Assert.Equal("3", asymmetricOutput.Lines[1]);
    }

    [Fact]
    public void Type_indicator_does_not_claim_axial_point_source_for_asymmetric_distribution()
    {
        var axial = PhotometryFixture.ParseLdt(new LdtConverter().Convert(
            new IesParser().Parse(PhotometryFixture.Ies(horizontal: [0], width: 0.2, length: 0.2)), null, out _, out _, out _));
        var fullHorizontal = new[] { 0d, 90d, 180d, 270d, 360d };
        var fullVertical = new[] { 0d, 90d, 180d };
        var fullCandela = fullHorizontal.Select((_, row) => fullVertical.Select((_, column) => 100d - (5d * (row % 4)) - (20d * column)).ToArray()).ToArray();
        fullCandela[^1] = fullCandela[0].ToArray();
        var asymmetric = PhotometryFixture.ParseLdt(new LdtConverter().Convert(
            new IesParser().Parse(PhotometryFixture.Ies(horizontal: fullHorizontal, vertical: fullVertical, candela: fullCandela, width: 0.2, length: 0.2)), null, out _, out _, out _));
        var linear = PhotometryFixture.ParseLdt(new LdtConverter().Convert(
            new IesParser().Parse(PhotometryFixture.Ies(horizontal: [0, 90, 180], width: 0.1, length: 1.2)), null, out _, out _, out _));

        Assert.Equal("1", axial.Lines[1]);
        Assert.Equal("3", asymmetric.Lines[1]);
        Assert.Equal("2", linear.Lines[1]);
    }

    [Fact]
    public void Dff_and_lorl_are_derived_from_complete_spherical_distribution()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies(
            horizontal: [0],
            vertical: [0, 90, 180],
            candela: [[100, 100, 100]],
            lumensPerLamp: 1_000));
        var lines = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, null, out _, out _, out _)).Lines;

        Assert.Equal("50.0", lines[21]);
        Assert.Equal("125.7", lines[22]);
        Assert.Contains("DFF/LORL=Type C candela", lines[11], StringComparison.Ordinal);
    }

    [Fact]
    public void Gamma_zero_to_ninety_is_extended_with_explicit_zero_uplight()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies(
            horizontal: [0], vertical: [0, 45, 90], candela: [[100, 50, 0]]));

        var ldt = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, null, out _, out _, out _));

        Assert.Equal([0d, 45d, 90d, 135d, 180d], ldt.GammaAngles);
        Assert.Equal([100d, 50d, 0d, 0d, 0d], ldt.Intensities);
        Assert.Equal("100.0", ldt.Lines[21]);
        Assert.InRange(double.Parse(ldt.Lines[22], CultureInfo.InvariantCulture), 0.1, 1000);
    }

    [Theory]
    [InlineData(new double[] { 0, 80 })]
    [InlineData(new double[] { 10, 90 })]
    public void Arbitrary_partial_gamma_coverage_is_rejected(double[] gamma)
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies(vertical: gamma));
        var exception = Assert.Throws<InvalidDataException>(() => new LdtConverter().Convert(ies, null, out _, out _, out _));
        Assert.Contains("Gamma", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalogue_pdf_matching_is_exact_and_rejects_ambiguous_legacy_assets()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var ies = new IesParser().Parse(PhotometryFixture.Ies(), "legacy.ies");
            var exact = Path.Combine(root, "Fixture.pdf");
            var sibling = Path.Combine(root, "Fixture Extra.pdf");
            File.WriteAllText(exact, "x");
            File.WriteAllText(sibling, "x");
            Assert.Equal(exact, new PhotometryCatalogue().FindBestPdf(ies, [sibling, exact], "Fixture"));

            File.Delete(exact);
            var firstLegacy = Path.Combine(root, "911401 - Fixture.pdf");
            var secondLegacy = Path.Combine(root, "999999 - Fixture.pdf");
            File.WriteAllText(firstLegacy, "x");
            File.WriteAllText(secondLegacy, "x");
            var exception = Assert.Throws<InvalidDataException>(() => new PhotometryCatalogue().FindBestPdf(ies, [firstLegacy, secondLegacy], "Fixture"));
            Assert.Contains("name_collision", exception.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Catalogue_technical_overrides_carry_explicit_provenance_and_keep_ies_identity_rules()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies(manufacturer: "IES Maker", lamp: "IES Lamp"));
        var metadata = new PhotometryCatalogue().Extract("Luminous flux: 2000 lm\nInput power: 20 W\nCRI: 90\nCCT: 4000 K", ies);
        var lines = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, metadata, "Canonical Product", out _, out _, out _)).Lines;

        Assert.Equal("catalogue:technical-luminous-flux", metadata.LuminousFluxProvenance);
        Assert.Equal("catalogue:technical-input-power", metadata.InputWattsProvenance);
        Assert.Equal("catalogue:technical-cri", metadata.CriProvenance);
        Assert.Equal("catalogue:technical-single-value-cct", metadata.CctProvenance);
        Assert.Equal("IES Maker", lines[0]);
        Assert.Equal("IES Lamp", lines[27]);
        Assert.Equal(["1000", "100", "50"], lines[12..15]);
        Assert.Contains("technical provenance", lines[11], StringComparison.Ordinal);
        Assert.Contains("manufacturer/lamp=IES", lines[11], StringComparison.Ordinal);
        Assert.Contains("dimensions=IES fallback", lines[11], StringComparison.Ordinal);
    }

    [Fact]
    public void Catalogue_rejects_marketing_ranges_before_technical_rows()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies());
        var metadata = new PhotometryCatalogue().Extract(
            "dải nhiệt độ màu (CCT) đầy đủ gồm 3000K, 4000K và 6500K, mức quang thông từ 400lm đến 2.000 lm\n" +
            "Quang thông 1.300 lm\nNhiệt độ màu (CCT) 4000 K\nChỉ số hoàn màu (CRI) 80\nCông suất 12 W",
            ies);

        Assert.Equal(1300, metadata.LuminousFluxLumens);
        Assert.Equal(4000, metadata.CctKelvin);
        Assert.Equal(80, metadata.Cri);
        Assert.Equal(12, metadata.InputWatts);
    }

    [Theory]
    [InlineData("DN068B G2 LED12_830 PSU D125", "911401862487 - DN068B G2 LED12_830 PSU D125.pdf", 1235, 3000)]
    [InlineData("DN068B G2 LED13_840 PSU D125", "911401862587 - DN068B G2 LED13_840 PSU D125.pdf", 1300, 4000)]
    [InlineData("DN068B G2 LED13_865 PSU D125", "911401862687 - DN068B G2 LED13_865 PSU D125.pdf", 1300, 6500)]
    public void Actual_dn068b_pdf_extracts_authoritative_technical_values_when_available(string iesStem, string pdfName, double flux, int cct)
    {
        const string family = @"D:\Claude\DocManager\Product Family\Smartbright Pro DN068B G2";
        var iesPath = Path.Combine(family, "IES", iesStem + ".ies");
        var pdfPath = Path.Combine(family, pdfName);
        if (!File.Exists(iesPath) || !File.Exists(pdfPath)) return;

        var ies = new IesParser().ParseFile(iesPath);
        var metadata = new PhotometryCatalogue().ExtractPdf(pdfPath, ies);

        Assert.Equal(flux, metadata.LuminousFluxLumens);
        Assert.Equal(cct, metadata.CctKelvin);
        Assert.Equal(80, metadata.Cri);
        Assert.Equal(12, metadata.InputWatts);
    }

    [Fact]
    public void Actual_dn068b_variants_convert_to_distinct_structurally_valid_outputs_when_available()
    {
        const string family = @"D:\Claude\DocManager\Product Family\Smartbright Pro DN068B G2";
        var variants = new[]
        {
            (Stem: "DN068B G2 LED12_830 PSU D125", Cct: 3000),
            (Stem: "DN068B G2 LED13_840 PSU D125", Cct: 4000),
            (Stem: "DN068B G2 LED13_865 PSU D125", Cct: 6500)
        };
        var iesPaths = variants.Select(variant => Path.Combine(family, "IES", variant.Stem + ".ies")).ToArray();
        var pdfs = Directory.Exists(family) ? Directory.EnumerateFiles(family, "*.pdf").ToArray() : [];
        if (iesPaths.Any(path => !File.Exists(path)) || pdfs.Length == 0) return;

        var output = PhotometryFixture.TemporaryDirectory();
        try
        {
            var converter = new LdtConverter();
            var results = variants.Select((variant, index) =>
            {
                var productName = Path.GetFileNameWithoutExtension(iesPaths[index]);
                var selectedPdf = new PhotometryCatalogue().FindBestPdf(new IesParser().ParseFile(iesPaths[index]), pdfs, productName);
                Assert.NotNull(selectedPdf);
                var result = converter.ConvertFile(iesPaths[index], selectedPdf, output, productName);
                var ldt = PhotometryFixture.ParseLdt(File.ReadAllText(result.OutputPath));
                Assert.Equal(variant.Cct.ToString(CultureInfo.InvariantCulture), ldt.Lines[29]);
                Assert.Equal("100.0", ldt.Lines[21]);
                Assert.True(double.TryParse(ldt.Lines[22], NumberStyles.Float, CultureInfo.InvariantCulture, out var lorl));
                Assert.InRange(lorl, 0.1, 1000);
                Assert.Equal(0, result.SymmetryIndicator);
                Assert.Equal(16, ldt.CPlanes.Length);
                Assert.Equal(181, ldt.GammaAngles.Length);
                Assert.Equal(180, ldt.GammaAngles[^1]);
                Assert.All(ldt.Intensities.Chunk(ldt.GammaAngles.Length), row => Assert.All(row.Skip(91), value => Assert.Equal(0, value)));
                return result.OutputPath;
            }).ToArray();

            Assert.Equal(3, results.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            Directory.Delete(output, true);
        }
    }

    [Fact]
    public void Zero_lamp_count_and_catalogue_flux_have_correct_per_1000_lumen_scaling()
    {
        var ies = new IesParser().Parse(PhotometryFixture.Ies(lampCount: 0, horizontal: [0], vertical: [0, 90, 180], candela: [[100, 100, 100]]));
        Assert.Equal(1, ies.LampCount);

        var iesFlux = PhotometryFixture.ParseLdt(new LdtConverter().Convert(ies, null, out _, out _, out _));
        var catalogueFlux = PhotometryFixture.ParseLdt(new LdtConverter().Convert(
            ies, new PhotometryMetadata { LuminousFluxLumens = 2_000 }, out _, out _, out _));

        Assert.Equal("1", iesFlux.Lines[26]);
        Assert.Equal([100d, 100d, 100d], iesFlux.Intensities);
        Assert.Equal([50d, 50d, 50d], catalogueFlux.Intensities);
    }

    [Fact]
    public void Relative_ies_converts_without_catalogue_pdf_using_ies_metadata_warning()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var iesPath = Path.Combine(root, "Fixture 840.ies");
            File.WriteAllText(iesPath, PhotometryFixture.Ies(), new UTF8Encoding(false));

            var result = new LdtConverter().ConvertFileWithoutCatalogue(iesPath, Path.Combine(root, "LDT"), "Fixture 840");

            Assert.True(File.Exists(result.OutputPath));
            Assert.Equal(string.Empty, result.CataloguePdfPath);
            Assert.Equal("ies:lamp-count-times-lumens", result.Provenance!["flux"]);
            Assert.Equal("ies:input-watts", result.Provenance["power"]);
            Assert.Contains(result.Warnings!, warning => warning.Contains("Không có catalogue PDF", StringComparison.Ordinal));
            Assert.Contains(result.Warnings!, warning => warning.Contains("quang thông", StringComparison.Ordinal));
            Assert.Contains(result.Warnings!, warning => warning.Contains("công suất", StringComparison.Ordinal));
            Assert.Contains(result.Warnings!, warning => warning.Contains("CCT", StringComparison.Ordinal));
            Assert.Contains(result.Warnings!, warning => warning.Contains("CRI", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Catalogue_list_conversion_uses_one_immutable_ies_snapshot_for_matching_and_result()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var iesPath = Path.Combine(root, "Fixture.ies");
            var pdfPath = Path.Combine(root, "Fixture.pdf");
            var output = Path.Combine(root, "LDT");
            var original = PhotometryFixture.Ies(lumensPerLamp: 1000);
            File.WriteAllText(iesPath, original, new UTF8Encoding(false));
            WriteGeometryPdf(pdfPath, ["Fixture"]);
            var snapshot = new IesSourceReader().ReadFile(iesPath);

            var result = new LdtConverter().ConvertFile(
                iesPath,
                MutatingCataloguePaths(iesPath, PhotometryFixture.Ies(lumensPerLamp: 2000), pdfPath),
                output);
            var ldt = PhotometryFixture.ParseLdt(File.ReadAllText(result.OutputPath));

            Assert.Equal(snapshot.Fingerprint, result.SourceFingerprint);
            Assert.Equal("1000", ldt.Lines[28]);
            Assert.Equal(2000, new IesParser().ParseFile(iesPath).TotalLumens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Actual_wt198c_uses_canonical_output_identity_and_catalogue_scaling_when_available()
    {
        const string family = @"D:\Claude\DocManager\Product Family\GreenPerform Waterproof G3";
        const string product = "WT198C LED40S 840 PSU L1200";
        var iesPath = Path.Combine(family, "IES", "911401504347 - WT198C LED40S_840 PSU L1200.ies");
        var pdfPath = Path.Combine(family, "911401504347 - WT198C LED40S_840 PSU L1200.pdf");
        if (!File.Exists(iesPath) || !File.Exists(pdfPath)) return;
        var output = PhotometryFixture.TemporaryDirectory();
        try
        {
            var ies = new IesParser().ParseFile(iesPath);
            var result = new LdtConverter().ConvertFile(iesPath, pdfPath, output, product);
            var ldt = PhotometryFixture.ParseLdt(File.ReadAllText(result.OutputPath));

            Assert.Equal("WT198C LED40S 840 PSU L1200.ldt", Path.GetFileName(result.OutputPath));
            Assert.Equal(product, ldt.Lines[8]);
            Assert.Equal(string.Empty, ldt.Lines[9]);
            Assert.Equal(product, ldt.Lines[10]);
            Assert.Equal(4000, double.Parse(ldt.Lines[28], CultureInfo.InvariantCulture));
            Assert.Equal("4000", ldt.Lines[29]);
            Assert.Equal("80", ldt.Lines[30]);
            Assert.Equal("28.6", ldt.Lines[31]);
            Assert.Equal(ies.Candela[0][0] * 1000 / 4000, ldt.Intensities[0], 4);
        }
        finally { Directory.Delete(output, true); }
    }

    [Fact]
    public void Actual_wt198c_canonical_batch_resolves_legacy_sku_assets_and_uses_catalogue_technical_values_when_available()
    {
        const string sourceRoot = @"D:\Claude\DocManager\Product Family";
        const string familyName = "GreenPerform Waterproof G3";
        const string product = "WT198C LED40S 840 PSU L1200";
        var sourceFamily = Path.Combine(sourceRoot, familyName);
        var sourceIes = Path.Combine(sourceFamily, "IES", "911401504347 - WT198C LED40S_840 PSU L1200.ies");
        var sourcePdf = Path.Combine(sourceFamily, "911401504347 - WT198C LED40S_840 PSU L1200.pdf");
        if (!File.Exists(sourceIes) || !File.Exists(sourcePdf)) return;
        var output = PhotometryFixture.TemporaryDirectory();
        try
        {
            var family = Path.Combine(output, familyName);
            var iesPath = Path.Combine(family, "IES", Path.GetFileName(sourceIes));
            var pdfPath = Path.Combine(family, Path.GetFileName(sourcePdf));
            Directory.CreateDirectory(Path.GetDirectoryName(iesPath)!);
            File.Copy(sourceIes, iesPath);
            File.Copy(sourcePdf, pdfPath);
            using var http = new HttpClient();
            var result = Assert.Single(new CatalogueDownloadService(new SignifyClient(http)).ConvertProducts(
                [new ProductRecord("Fixture", 1, familyName, "911401504347", product)], output));
            var lines = File.ReadAllLines(result.Result.OutputPath);

            Assert.Equal("WT198C LED40S 840 PSU L1200.ldt", Path.GetFileName(result.Result.OutputPath));
            Assert.Equal(product, lines[8]);
            Assert.Equal(string.Empty, lines[9]);
            Assert.Equal(product, lines[10]);
            Assert.Equal(["4000", "4000", "80", "28.6"], lines[28..32]);
            Assert.Equal(Path.GetFullPath(iesPath), result.Context.IesPath);
            Assert.Equal(Path.GetFullPath(pdfPath), result.Context.PdfPath);
        }
        finally { Directory.Delete(output, true); }
    }

    [Fact]
    public void Product_context_840_and_865_with_same_header_keep_distinct_names_and_strict_cct()
    {
        var converter = new LdtConverter();
        var source = PhotometryFixture.Ies(lumcat: "SHARED");
        var ies840 = new IesParser().Parse(source, "legacy-one.ies");
        var ies865 = new IesParser().Parse(source, "legacy-two.ies");
        var catalogue = new PhotometryCatalogue();
        Assert.Null(catalogue.Extract("CCT: 3000 K, 4000 K, 6500 K", ies840).CctKelvin);
        var metadata840 = catalogue.Extract("CCT: 3000 K, 4000 K, 6500 K\nCorrelated colour temperature (Nom.): 4000 K", ies840);
        var metadata865 = catalogue.Extract("CCT: 3000 K, 4000 K, 6500 K\nCorrelated colour temperature (Nom.): 6500 K", ies865);
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var out840 = LdtIdentity.ChooseOutputPath("RC099V G3 LED69/840", root);
            File.WriteAllText(out840, converter.Convert(ies840, metadata840, "RC099V G3 LED69/840", out _, out _, out _));
            var out865 = LdtIdentity.ChooseOutputPath("RC099V G3 LED69/865", root);
            File.WriteAllText(out865, converter.Convert(ies865, metadata865, "RC099V G3 LED69/865", out _, out _, out _));

            Assert.NotEqual(Path.GetFileName(out840), Path.GetFileName(out865));
            Assert.Equal("4000", File.ReadAllLines(out840)[29]);
            Assert.Equal("6500", File.ReadAllLines(out865)[29]);
        }
        finally { Directory.Delete(root, true); }
    }

    private static IEnumerable<string> MutatingCataloguePaths(string iesPath, string mutatedIes, string pdfPath)
    {
        File.WriteAllText(iesPath, mutatedIes, new UTF8Encoding(false));
        yield return pdfPath;
    }

    private static void WriteGeometryPdf(string path, IReadOnlyList<string> lines)
    {
        using var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(600, 800);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        for (var index = 0; index < lines.Count; index++)
            page.AddText(lines[index], 10, new UglyToad.PdfPig.Core.PdfPoint(40, 740 - (20 * index)), font);
        File.WriteAllBytes(path, builder.Build());
    }

    [Theory]
    [InlineData("Product.ldt", true)]
    [InlineData("911401 - Product.ldt", true)]
    [InlineData("Product_auto.ldt", true)]
    [InlineData("Product_auto2.ldt", true)]
    [InlineData("Product_auto01.ldt", false)]
    [InlineData("Product_auto_backup.ldt", false)]
    [InlineData("Product backup.ldt", false)]
    [InlineData("Sản phẩm.ldt", false)]
    public void Ldt_identity_accepts_canonical_and_conservative_legacy_product_names(string candidate, bool expected)
    {
        Assert.Equal(expected, LdtIdentity.IsIdentityMatch("Product", "Product.ies", candidate));
    }

    [Fact]
    public void Choose_output_path_is_exact_canonical_and_fails_closed_if_it_exists()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            Assert.Equal("Đèn Product 840.ldt", Path.GetFileName(LdtIdentity.ChooseOutputPath("Đèn Product/840", root)));
            File.WriteAllText(Path.Combine(root, "Đèn Product 840.ldt"), string.Empty);
            Assert.Throws<IOException>(() => LdtIdentity.ChooseOutputPath("Đèn Product/840", root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Legacy_header_fallback_is_disabled_if_any_sibling_fails_but_exact_filename_still_works()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        var ldtDirectory = Path.Combine(root, "LDT");
        Directory.CreateDirectory(ldtDirectory);
        try
        {
            var source = Path.Combine(root, "variant-840.ies");
            var broken = Path.Combine(root, "broken.ies");
            File.WriteAllText(source, "x");
            File.WriteAllText(broken, "x");
            var legacy = Path.Combine(ldtDirectory, "Shared header.ldt");
            File.WriteAllText(legacy, "x");
            string? Reader(string path) => path.Contains("broken", StringComparison.OrdinalIgnoreCase) ? throw new InvalidDataException() : "Shared header";

            Assert.Null(LdtIdentity.FindExisting(source, ldtDirectory, Reader));

            var exact = Path.Combine(ldtDirectory, "variant-840.ldt");
            File.WriteAllText(exact, "x");
            Assert.Equal(exact, LdtIdentity.FindExisting(source, ldtDirectory, _ => throw new InvalidDataException()));
        }
        finally { Directory.Delete(root, true); }
    }
}

internal static class PhotometryFixture
{
    public static string Ies(
        double[]? horizontal = null,
        double[]? vertical = null,
        double[][]? candela = null,
        int lampCount = 1,
        double lumensPerLamp = 1_000,
        int photometricType = 1,
        double width = 0.1,
        double length = 1,
        double height = 0.05,
        string? manufacturer = "Acme",
        string? lamp = "LED 840",
        string? lumcat = "IES-CAT")
    {
        horizontal ??= [0];
        vertical ??= [0, 90, 180];
        candela ??= horizontal.Select((_, row) => vertical.Select((_, column) => 50d - row - (10d * column)).ToArray()).ToArray();
        if (candela.Length != horizontal.Length || candela.Any(row => row.Length != vertical.Length)) throw new ArgumentException("Fixture candela dimensions are invalid.");
        static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        var lines = new List<string> { "IESNA:LM-63-2002", "[TEST] TEST-1" };
        if (manufacturer is not null) lines.Add($"[MANUFAC] {manufacturer}");
        lines.Add("[LUMINAIRE] Fixture");
        if (lumcat is not null) lines.Add($"[LUMCAT] {lumcat}");
        if (lamp is not null) lines.Add($"[LAMP] {lamp}");
        lines.Add("TILT=NONE");
        lines.Add(string.Join(' ',
            Number(lampCount), Number(lumensPerLamp), "1", Number(vertical.Length), Number(horizontal.Length), Number(photometricType),
            "2", Number(width), Number(length), Number(height), "1", "1", "10"));
        lines.Add(string.Join(' ', vertical.Select(Number)));
        lines.Add(string.Join(' ', horizontal.Select(Number)));
        lines.AddRange(candela.Select(row => string.Join(' ', row.Select(Number))));
        return string.Join('\n', lines) + "\n";
    }

    public static ParsedLdt ParseLdt(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        Assert.True(lines.Length >= 42, $"LDT header has only {lines.Length} lines.");
        var symmetry = int.Parse(lines[2], CultureInfo.InvariantCulture);
        var cCount = int.Parse(lines[3], CultureInfo.InvariantCulture);
        var gammaCount = int.Parse(lines[5], CultureInfo.InvariantCulture);
        var payloadPlaneCount = symmetry switch
        {
            0 => cCount,
            1 => 1,
            2 or 3 => (cCount / 2) + 1,
            4 => (cCount / 4) + 1,
            _ => throw new InvalidDataException($"Invalid Isym {symmetry}.")
        };
        var expected = 42 + cCount + gammaCount + (payloadPlaneCount * gammaCount);
        Assert.Equal(expected, lines.Length);
        var cPlanes = lines.Skip(42).Take(cCount).Select(Parse).ToArray();
        var gamma = lines.Skip(42 + cCount).Take(gammaCount).Select(Parse).ToArray();
        var intensities = lines.Skip(42 + cCount + gammaCount).Select(Parse).ToArray();
        Assert.Equal(payloadPlaneCount * gammaCount, intensities.Length);
        return new ParsedLdt(lines, cPlanes, gamma, intensities, payloadPlaneCount);
    }

    public static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DocManagerTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static double Parse(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
}

internal sealed record ParsedLdt(string[] Lines, double[] CPlanes, double[] GammaAngles, double[] Intensities, int PayloadPlaneCount);
