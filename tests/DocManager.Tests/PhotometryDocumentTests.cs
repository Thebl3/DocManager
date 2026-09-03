using System.Text;
using DocManager.Photometry;

namespace DocManager.Tests;

public sealed class PhotometryDocumentTests
{
    [Fact]
    public void Load_strips_utf8_bom_and_preserves_crlf_and_final_newline()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "bom-crlf.ldt");
            var encoding = new UTF8Encoding(true);
            File.WriteAllText(path, "Maker\r\n3\r\n4\r\n", encoding);

            var document = LdtDocument.Load(path);

            Assert.Equal(3, document.LineCount);
            Assert.Equal("Maker\r\n3\r\n4\r\n", document.RawText);
            Assert.Equal("\r\n", document.NewLine);
            Assert.Equal(Encoding.UTF8.WebName, document.Encoding.WebName);
            Assert.Equal("Maker", document.Fields[0].Value);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Load_and_save_preserve_lf_without_final_newline()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "lf.ldt");
            File.WriteAllText(path, "Maker\n3\n4", new UTF8Encoding(false));
            var document = LdtDocument.Load(path);

            document.Save(createBackup: false);

            Assert.Equal("\n", document.NewLine);
            Assert.Equal("Maker\n3\n4", File.ReadAllText(path, new UTF8Encoding(false)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Replace_raw_text_updates_lines_newlines_and_final_newline()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "raw.ldt");
            File.WriteAllText(path, "one\r\ntwo\r\n", new UTF8Encoding(false));
            var document = LdtDocument.Load(path);

            document.ReplaceRawText("alpha\nbeta\n");

            Assert.Equal(2, document.LineCount);
            Assert.Equal("\n", document.NewLine);
            Assert.Equal("alpha\nbeta\n", document.RawText);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Set_value_edits_one_based_line_and_checks_both_bounds()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "set.ldt");
            File.WriteAllText(path, "one\ntwo", new UTF8Encoding(false));
            var document = LdtDocument.Load(path);

            document.SetValue(2, "changed");

            Assert.Equal("one\nchanged", document.RawText);
            Assert.Throws<ArgumentOutOfRangeException>(() => document.SetValue(0, "x"));
            Assert.Throws<ArgumentOutOfRangeException>(() => document.SetValue(3, "x"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Field_labels_match_eulumdat_lines_18_through_23_and_include_lamp_fields()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "labels.ldt");
            File.WriteAllText(path, string.Join('\n', Enumerable.Range(1, 42)), new UTF8Encoding(false));
            var fields = LdtDocument.Load(path).Fields;

            Assert.Contains("Tổng số mặt phẳng C", fields[3].Name, StringComparison.Ordinal);
            Assert.Equal("Chiều cao vùng sáng C0", fields[17].Name);
            Assert.Equal("Chiều cao vùng sáng C90", fields[18].Name);
            Assert.Equal("Chiều cao vùng sáng C180", fields[19].Name);
            Assert.Equal("Chiều cao vùng sáng C270", fields[20].Name);
            Assert.Contains("DFF", fields[21].Name, StringComparison.Ordinal);
            Assert.Contains("LORL", fields[22].Name, StringComparison.Ordinal);
            Assert.Equal("Số bộ bóng đèn", fields[25].Name);
            Assert.Equal("Công suất bộ 1", fields[31].Name);
            Assert.Equal("Tỉ số trực tiếp 90°", fields[41].Name);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Save_overwrite_creates_backup_of_previous_target_and_updates_target_atomically()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "overwrite.ldt");
            File.WriteAllText(path, "old\r\n", new UTF8Encoding(false));
            var document = LdtDocument.Load(path);
            document.ReplaceRawText("new\r\n");

            document.Save();

            Assert.Equal("new\r\n", File.ReadAllText(path));
            Assert.Equal("old\r\n", File.ReadAllText(path + ".bak"));
            Assert.Equal(Path.GetFullPath(path), document.Path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Repeated_overwrite_replaces_backup_only_after_target_replacement_succeeds()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "repeat.ldt");
            File.WriteAllText(path, "v1", new UTF8Encoding(false));
            var document = LdtDocument.Load(path);
            document.ReplaceRawText("v2");
            document.Save();
            document.ReplaceRawText("v3");

            document.Save();

            Assert.Equal("v3", File.ReadAllText(path));
            Assert.Equal("v2", File.ReadAllText(path + ".bak"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Save_as_preserves_source_and_updates_public_path()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source.ldt");
            var destination = Path.Combine(root, "nested", "saved-as.ldt");
            File.WriteAllText(source, "source", new UTF8Encoding(false));
            var document = LdtDocument.Load(source);
            document.ReplaceRawText("edited");

            document.Save(destination);

            Assert.Equal("source", File.ReadAllText(source));
            Assert.Equal("edited", File.ReadAllText(destination));
            Assert.Equal(Path.GetFullPath(destination), document.Path);
            Assert.False(File.Exists(destination + ".bak"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Utf8_bom_encoding_is_preserved_on_save_without_bom_in_first_field()
    {
        var root = PhotometryFixture.TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "encoding.ldt");
            var encoding = new UTF8Encoding(true);
            File.WriteAllText(path, "Maker\nValue\n", encoding);
            var document = LdtDocument.Load(path);
            document.SetValue(2, "Changed");

            document.Save(createBackup: false);

            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
            Assert.Equal("Maker", LdtDocument.Load(path).Fields[0].Value);
            Assert.Equal("Maker\nChanged\n", LdtDocument.Load(path).RawText);
        }
        finally { Directory.Delete(root, true); }
    }
}
