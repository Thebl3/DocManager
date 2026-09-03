using System.Text;
using DocManager.Photometry;

namespace DocManager.Tests;

public sealed class IesPreviewTests
{
    [Fact]
    public void Snapshot_preserves_utf8_bom_exact_text_bytes_and_mixed_newlines()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "mixed.ies");
            var text = PhotometryFixture.Ies()
                .Replace("\n[TEST]", "\r\n[TEST]", StringComparison.Ordinal)
                .Replace("\n[MANUFAC]", "\r[MANUFAC]", StringComparison.Ordinal);
            var payload = new UTF8Encoding(false).GetBytes(text);
            var bytes = Encoding.UTF8.GetPreamble().Concat(payload).ToArray();
            File.WriteAllBytes(path, bytes);

            var snapshot = new IesSourceReader().ReadFile(path);

            Assert.Equal(bytes, snapshot.OriginalBytes);
            Assert.Equal(text, snapshot.DecodedText);
            Assert.Equal(IesSourceEncodingKind.Utf8Bom, snapshot.EncodingKind);
            Assert.True(snapshot.HasBom);
            Assert.Equal(IesNewLineKind.Mixed, snapshot.NewLines.Kind);
            Assert.True(snapshot.HasFinalNewLine);
            Assert.Equal(bytes.Length, snapshot.ByteCount);
            Assert.Equal(Path.GetFullPath(path), snapshot.Document.SourcePath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Snapshot_identifies_utf8_without_bom_crlf_and_no_final_newline()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "crlf.ies");
            var text = PhotometryFixture.Ies().TrimEnd('\n').Replace("\n", "\r\n", StringComparison.Ordinal);
            File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));

            var snapshot = new IesSourceReader().ReadFile(path);

            Assert.Equal(text, snapshot.DecodedText);
            Assert.Equal(IesSourceEncodingKind.Utf8NoBom, snapshot.EncodingKind);
            Assert.Equal(IesNewLineKind.CrLf, snapshot.NewLines.Kind);
            Assert.False(snapshot.HasFinalNewLine);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Snapshot_falls_back_to_windows1252_and_preserves_non_utf8_bytes()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            var path = Path.Combine(root, "cp1252.ies");
            var text = PhotometryFixture.Ies().Replace("Acme", "Café €", StringComparison.Ordinal);
            var encoding = Encoding.GetEncoding(1252);
            var bytes = encoding.GetBytes(text);
            File.WriteAllBytes(path, bytes);

            var snapshot = new IesSourceReader().ReadFile(path);

            Assert.Equal(bytes, snapshot.OriginalBytes);
            Assert.Equal(text, snapshot.DecodedText);
            Assert.Equal(IesSourceEncodingKind.Windows1252, snapshot.EncodingKind);
            Assert.Equal("Café €", snapshot.Document.Manufacturer);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Snapshot_falls_back_to_latin1_when_cp1252_rejects_undefined_byte()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "latin1.ies");
            var bytes = Encoding.Latin1.GetBytes(PhotometryFixture.Ies().Replace("Acme", "Latin", StringComparison.Ordinal));
            var marker = Encoding.ASCII.GetBytes("Latin");
            var markerIndex = bytes.AsSpan().IndexOf(marker);
            Assert.True(markerIndex >= 0);
            bytes[markerIndex] = 0x81;
            File.WriteAllBytes(path, bytes);

            var snapshot = new IesSourceReader().ReadFile(path);

            Assert.Equal(IesSourceEncodingKind.Latin1, snapshot.EncodingKind);
            Assert.Equal(bytes, snapshot.OriginalBytes);
            Assert.Contains('', snapshot.DecodedText);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Projection_contains_headers_numeric_derived_angle_and_candela_summaries()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "projection.ies");
            File.WriteAllText(path, PhotometryFixture.Ies(
                horizontal: [0, 90],
                vertical: [0, 30, 90],
                candela: [[0, 10, 20], [5, 50, 15]],
                lampCount: 2,
                lumensPerLamp: 1000), new UTF8Encoding(false));
            var projection = IesPreviewProjection.Create(new IesSourceReader().ReadFile(path));

            AssertRow(projection, "[MANUFAC]", "Acme");
            AssertRow(projection, "Số bóng", "2");
            AssertRow(projection, "Chế độ quang trắc", "Relative");
            AssertRow(projection, "Tổng quang thông IES", "2000 lm");
            AssertRow(projection, "Góc Gamma: bước", "Không đều");
            AssertRow(projection, "Góc C: bước", "Đều 90°");
            AssertRow(projection, "Kích thước ma trận", "2 C × 3 Gamma");
            AssertRow(projection, "Tổng số mẫu", "6");
            AssertRow(projection, "Đỉnh", "50 cd tại C 90° / Gamma 30°");
            AssertRow(projection, "Mẫu âm / bằng 0", "0 / 1");
            Assert.True(projection.Rows.Count < 60);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Projection_explains_absolute_catalogue_readiness()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "absolute.ies");
            File.WriteAllText(path, PhotometryFixture.Ies(lumensPerLamp: -1), new UTF8Encoding(false));

            var projection = IesPreviewProjection.Create(new IesSourceReader().ReadFile(path));

            AssertRow(projection, "Chế độ quang trắc", "Absolute");
            AssertRow(projection, "Tổng quang thông IES", "Không xác định từ IES");
            Assert.Contains(projection.Rows, row => row.Name == "Sẵn sàng chuyển" && row.Value.Contains("cần catalogue PDF", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Parser_rejects_large_trailing_payload_after_bounded_expected_data()
    {
        var text = PhotometryFixture.Ies() + string.Join(' ', Enumerable.Repeat("0", 200_000));

        var exception = Assert.Throws<InvalidDataException>(() => new IesParser().Parse(text));

        Assert.Contains("dữ liệu số dư", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_rejects_matrix_product_above_cap_before_reading_angles_or_allocating_matrix()
    {
        var numericHeader = $"1 1000 1 10000 1001 1 2 1 1 1 1 1 10";
        var text = $"IESNA:LM-63-2002\nTILT=NONE\n{numericHeader}\n";

        var exception = Assert.Throws<InvalidDataException>(() => new IesParser().Parse(text));

        Assert.Contains("vượt giới hạn an toàn", exception.Message, StringComparison.Ordinal);
        Assert.Contains(IesParser.MaximumCandelaSamples.ToString("N0"), exception.Message, StringComparison.Ordinal);
    }

    private static void AssertRow(IesPreviewProjection projection, string name, string expected) =>
        Assert.Contains(projection.Rows, row => row.Name == name && row.Value == expected);
}
