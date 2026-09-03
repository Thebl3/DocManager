using System.Text;

#pragma warning disable SYSLIB0001

namespace DocManager.Photometry;

public sealed class LdtDocument
{
    private static readonly string[] KnownFields =
    [
        "Định danh công ty",                    // 1
        "Loại bộ đèn (Ityp)",                  // 2
        "Đối xứng (Isym)",                     // 3
        "Tổng số mặt phẳng C 0..<360 (Mc)",   // 4
        "Bước mặt phẳng C (Dc)",               // 5
        "Số góc Gamma mỗi mặt phẳng (Ng)",     // 6
        "Bước góc Gamma (Dg)",                 // 7
        "Mã báo cáo đo",                       // 8
        "Tên bộ đèn",                          // 9
        "Mã bộ đèn",                           // 10
        "Tên tệp",                             // 11
        "Ngày/người tạo",                      // 12
        "Chiều dài bộ đèn",                    // 13
        "Chiều rộng bộ đèn",                   // 14
        "Chiều cao bộ đèn",                    // 15
        "Chiều dài vùng sáng",                 // 16
        "Chiều rộng vùng sáng",                // 17
        "Chiều cao vùng sáng C0",              // 18
        "Chiều cao vùng sáng C90",             // 19
        "Chiều cao vùng sáng C180",            // 20
        "Chiều cao vùng sáng C270",            // 21
        "Hệ số quang thông xuống dưới (DFF)",   // 22
        "Hiệu suất bộ đèn (LORL)",              // 23
        "Hệ số chuyển đổi cường độ",            // 24
        "Góc nghiêng bộ đèn",                   // 25
        "Số bộ bóng đèn",                      // 26
        "Số bóng trong bộ 1",                  // 27
        "Loại bóng bộ 1",                      // 28
        "Tổng quang thông bóng bộ 1",           // 29
        "Nhiệt độ màu bộ 1",                   // 30
        "Chỉ số hoàn màu bộ 1",                // 31
        "Công suất bộ 1",                      // 32
        "Tỉ số trực tiếp 0°",                  // 33
        "Tỉ số trực tiếp 10°",                 // 34
        "Tỉ số trực tiếp 20°",                 // 35
        "Tỉ số trực tiếp 30°",                 // 36
        "Tỉ số trực tiếp 40°",                 // 37
        "Tỉ số trực tiếp 50°",                 // 38
        "Tỉ số trực tiếp 60°",                 // 39
        "Tỉ số trực tiếp 70°",                 // 40
        "Tỉ số trực tiếp 80°",                 // 41
        "Tỉ số trực tiếp 90°"                  // 42
    ];

    private readonly List<string> _lines;
    private bool _hasFinalNewLine;

    private LdtDocument(string path, List<string> lines, Encoding encoding, string newLine, bool hasFinalNewLine)
    {
        Path = path;
        _lines = lines;
        Encoding = encoding;
        NewLine = newLine;
        _hasFinalNewLine = hasFinalNewLine;
    }

    public string Path { get; private set; }
    public Encoding Encoding { get; }
    public string NewLine { get; private set; }
    public int LineCount => _lines.Count;
    public string RawText => string.Join(NewLine, _lines) + (_hasFinalNewLine ? NewLine : string.Empty);
    public IReadOnlyList<LdtField> Fields => _lines.Select((value, index) => new LdtField(index + 1, FieldName(index), value)).ToArray();

    public static LdtDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(fullPath);
        var (encoding, text) = Decode(bytes);
        text = text.TrimStart('﻿');
        var parsed = ParseText(text, null);
        return new LdtDocument(fullPath, parsed.Lines, encoding, parsed.NewLine, parsed.HasFinalNewLine);
    }

    internal static LdtDocument CreateForSave(string path, string rawText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var parsed = ParseText((rawText ?? string.Empty).TrimStart('﻿'), "\r\n");
        return new LdtDocument(fullPath, parsed.Lines, new UTF8Encoding(false), parsed.NewLine, parsed.HasFinalNewLine);
    }

    public void ReplaceRawText(string rawText)
    {
        var parsed = ParseText((rawText ?? string.Empty).TrimStart('﻿'), NewLine);
        _lines.Clear();
        _lines.AddRange(parsed.Lines);
        NewLine = parsed.NewLine;
        _hasFinalNewLine = parsed.HasFinalNewLine;
    }

    public void SetValue(int oneBasedIndex, string value)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > _lines.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(oneBasedIndex));
        }

        _lines[oneBasedIndex - 1] = value ?? string.Empty;
    }

    public void Save(string? destinationPath = null, bool createBackup = true, bool overwrite = true)
    {
        var target = System.IO.Path.GetFullPath(destinationPath ?? Path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        if (!overwrite && File.Exists(target))
        {
            throw new IOException($"Tệp đích đã tồn tại: {target}");
        }

        var temporary = $"{target}.{Guid.NewGuid():N}.part";
        string? pendingBackup = null;
        try
        {
            File.WriteAllText(temporary, RawText, Encoding);
            if (!File.Exists(target))
            {
                File.Move(temporary, target, false);
            }
            else if (!overwrite)
            {
                throw new IOException($"Tệp đích đã tồn tại: {target}");
            }
            else if (createBackup)
            {
                // File.Replace changes the target and captures its old contents in one
                // filesystem operation. The existing .bak is untouched if this fails.
                pendingBackup = $"{target}.{Guid.NewGuid():N}.backup";
                File.Replace(temporary, target, pendingBackup);
                try
                {
                    File.Move(pendingBackup, $"{target}.bak", true);
                    pendingBackup = null;
                }
                catch (Exception promotionException)
                {
                    // The target was already replaced. Restore it from the captured old
                    // target and leave the prior .bak untouched before surfacing failure.
                    try
                    {
                        File.Replace(pendingBackup!, target, null);
                        pendingBackup = null;
                    }
                    catch (Exception restoreException)
                    {
                        throw new IOException(
                            $"Không thể khôi phục '{target}'. Bản sao lưu phục hồi còn tại '{pendingBackup}'.",
                            new AggregateException(promotionException, restoreException));
                    }
                    throw;
                }
            }
            else
            {
                File.Move(temporary, target, true);
            }

            Path = target;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (pendingBackup is not null && File.Exists(pendingBackup))
            {
                // This is only reachable if a filesystem operation failed before the
                // backup could be promoted or restored. Preserve the captured target.
            }
        }
    }

    private static (Encoding Encoding, string Text) Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
        {
            var encoding = new UTF8Encoding(true, true);
            return (encoding, encoding.GetString(bytes));
        }

        try
        {
            var encoding = new UTF8Encoding(false, true);
            return (encoding, encoding.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try
            {
                var encoding = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return (encoding, encoding.GetString(bytes));
            }
            catch (DecoderFallbackException)
            {
                var encoding = Encoding.GetEncoding(28591);
                return (encoding, encoding.GetString(bytes));
            }
        }
    }

    private static (List<string> Lines, string NewLine, bool HasFinalNewLine) ParseText(string text, string? fallbackNewLine)
    {
        var newLine = DetectNewLine(text) ?? fallbackNewLine ?? Environment.NewLine;
        var hasFinalNewLine = text.EndsWith("\r\n", StringComparison.Ordinal) || text.EndsWith('\n') || text.EndsWith('\r');
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (hasFinalNewLine && lines.Count > 1) lines.RemoveAt(lines.Count - 1);
        return (lines, newLine, hasFinalNewLine);
    }

    private static string? DetectNewLine(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r') return index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
            if (text[index] == '\n') return "\n";
        }
        return null;
    }

    private static string FieldName(int zeroBasedIndex) => zeroBasedIndex < KnownFields.Length ? KnownFields[zeroBasedIndex] : $"Dòng {zeroBasedIndex + 1}";
}

public sealed record LdtField(int Index, string Name, string Value);
