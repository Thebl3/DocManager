using System.Globalization;

namespace DocManager.Photometry;

public interface IPhotometryViewRow
{
    int Index { get; }
    string Section { get; }
    string Name { get; }
    string Value { get; }
}

public sealed record IesPreviewRow(int Index, string Section, string Name, string Value) : IPhotometryViewRow;

public sealed record IesPreviewProjection(IReadOnlyList<IesPreviewRow> Rows)
{
    private const double Tolerance = 0.000001;
    private static readonly string[] PromotedHeaders = ["MANUFAC", "LUMINAIRE", "LUMCAT", "LAMP", "TEST"];

    public static IesPreviewProjection Create(IesSourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var ies = snapshot.Document;
        var rows = new List<IesPreviewRow>();
        void Add(string section, string name, object? value) =>
            rows.Add(new IesPreviewRow(rows.Count + 1, section, name, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));

        Add("Nguồn", "Tên tệp", Path.GetFileName(snapshot.SourcePath));
        Add("Nguồn", "Đường dẫn", snapshot.SourcePath);
        Add("Nguồn", "Phiên bản LM-63", ies.Version);
        Add("Nguồn", "Mã hóa", snapshot.EncodingDisplayName);
        Add("Nguồn", "BOM", snapshot.HasBom ? "Có" : "Không");
        Add("Nguồn", "Kiểu xuống dòng", snapshot.NewLines.DisplayName);
        Add("Nguồn", "Xuống dòng cuối", snapshot.HasFinalNewLine ? "Có" : "Không");
        Add("Nguồn", "Kích thước nguồn", $"{snapshot.ByteCount.ToString("N0", CultureInfo.InvariantCulture)} byte");
        Add("Nguồn", "TILT", "NONE (được hỗ trợ)");
        Add("Nguồn", "Sẵn sàng chuyển", ies.LumensPerLamp == -1
            ? "IES absolute: cần catalogue PDF có quang thông kỹ thuật dương"
            : "Sẵn sàng chuyển sang LDT (IES relative)");

        var emittedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in PromotedHeaders)
        {
            if (!ies.Headers.TryGetValue(key, out var value)) continue;
            Add("Header", $"[{key}]", value);
            emittedHeaders.Add(key);
        }
        foreach (var header in ies.Headers.Where(header => !emittedHeaders.Contains(header.Key)))
            Add("Header", $"[{header.Key}]", header.Value);

        Add("Số liệu", "Số bóng", Number(ies.LampCount));
        Add("Số liệu", "Quang thông mỗi bóng", Number(ies.LumensPerLamp));
        Add("Số liệu", "Hệ số candela", Number(ies.CandelaMultiplier));
        Add("Số liệu", "Số góc đứng (Gamma)", Number(ies.VerticalAngleCount));
        Add("Số liệu", "Số góc ngang (C)", Number(ies.HorizontalAngleCount));
        Add("Số liệu", "Kiểu quang trắc", ies.PhotometricType == 1 ? "1 — Type C" : Number(ies.PhotometricType));
        Add("Số liệu", "Đơn vị kích thước", ies.UnitType == 1 ? "1 — feet" : ies.UnitType == 2 ? "2 — mét" : Number(ies.UnitType));
        Add("Số liệu", "Chiều rộng", Number(ies.Width));
        Add("Số liệu", "Chiều dài", Number(ies.Length));
        Add("Số liệu", "Chiều cao", Number(ies.Height));
        Add("Số liệu", "Ballast factor", Number(ies.BallastFactor));
        Add("Số liệu", "Ballast-lamp factor / FutureUse", Number(ies.FutureUse));
        Add("Số liệu", "Công suất đầu vào", $"{Number(ies.InputWatts)} W");

        var isAbsolute = ies.LumensPerLamp == -1;
        Add("Suy ra", "Chế độ quang trắc", isAbsolute ? "Absolute" : "Relative");
        Add("Suy ra", "Tổng quang thông IES", !isAbsolute && ies.TotalLumens > 0 && double.IsFinite(ies.TotalLumens)
            ? $"{Number(ies.TotalLumens)} lm"
            : "Không xác định từ IES");
        var unitToMillimetres = ies.UnitType == 1 ? 304.8 : 1000d;
        Add("Suy ra", "Kích thước (D × R × C)",
            $"{Number(Math.Abs(ies.Length) * unitToMillimetres)} × {Number(Math.Abs(ies.Width) * unitToMillimetres)} × {Number(Math.Abs(ies.Height) * unitToMillimetres)} mm");

        AddAngleRows(Add, "Gamma", ies.VerticalAngles);
        AddAngleRows(Add, "C", ies.HorizontalAngles);
        AddCandelaRows(Add, ies);
        return new IesPreviewProjection(rows);
    }

    private static void AddAngleRows(Action<string, string, object?> add, string name, IReadOnlyList<double> angles)
    {
        if (angles.Count == 0)
        {
            add("Góc", $"Góc {name}", "Không có");
            return;
        }

        var step = UniformStep(angles);
        add("Góc", $"Góc {name}: số lượng / khoảng",
            $"{angles.Count.ToString(CultureInfo.InvariantCulture)} / {Number(angles[0])}°..{Number(angles[^1])}°");
        add("Góc", $"Góc {name}: bước", step is null ? "Không đều" : angles.Count == 1 ? "Một mẫu" : $"Đều {Number(step.Value)}°");
        add("Góc", $"Góc {name}: mẫu", Sample(angles, value => $"{Number(value)}°"));
    }

    private static void AddCandelaRows(Action<string, string, object?> add, IesDocument ies)
    {
        var count = 0L;
        var negative = 0L;
        var zero = 0L;
        var minimum = double.PositiveInfinity;
        var peak = (Value: double.NegativeInfinity, Horizontal: 0, Vertical: 0);
        var first = new List<(double Value, int Horizontal, int Vertical)>(6);
        var last = new Queue<(double Value, int Horizontal, int Vertical)>(3);
        for (var horizontal = 0; horizontal < ies.Candela.Count; horizontal++)
        {
            var row = ies.Candela[horizontal];
            for (var vertical = 0; vertical < row.Count; vertical++)
            {
                var sample = (row[vertical], horizontal, vertical);
                count++;
                if (sample.Item1 < minimum) minimum = sample.Item1;
                if (sample.Item1 > peak.Value) peak = sample;
                if (sample.Item1 < 0) negative++;
                if (Math.Abs(sample.Item1) <= Tolerance) zero++;
                if (first.Count < 6) first.Add(sample);
                last.Enqueue(sample);
                if (last.Count > 3) last.Dequeue();
            }
        }

        add("Candela", "Kích thước ma trận", $"{ies.HorizontalAngleCount.ToString(CultureInfo.InvariantCulture)} C × {ies.VerticalAngleCount.ToString(CultureInfo.InvariantCulture)} Gamma");
        add("Candela", "Tổng số mẫu", count.ToString("N0", CultureInfo.InvariantCulture));
        if (count == 0)
        {
            add("Candela", "Thống kê", "Không có mẫu");
            return;
        }

        var c = peak.Horizontal < ies.HorizontalAngles.Count ? ies.HorizontalAngles[peak.Horizontal] : double.NaN;
        var gamma = peak.Vertical < ies.VerticalAngles.Count ? ies.VerticalAngles[peak.Vertical] : double.NaN;
        add("Candela", "Nhỏ nhất / lớn nhất", $"{Number(minimum)} / {Number(peak.Value)} cd");
        add("Candela", "Đỉnh", $"{Number(peak.Value)} cd tại C {Number(c)}° / Gamma {Number(gamma)}°");
        add("Candela", "Mẫu âm / bằng 0", $"{negative.ToString("N0", CultureInfo.InvariantCulture)} / {zero.ToString("N0", CultureInfo.InvariantCulture)}");
        var representatives = count <= 6
            ? first.ToArray()
            : first.Take(3).Concat(last).ToArray();
        var separator = count > 6 ? "; …; " : "; ";
        var formatted = representatives.Select(sample =>
            $"C{Number(ies.HorizontalAngles[sample.Horizontal])}/G{Number(ies.VerticalAngles[sample.Vertical])}: {Number(sample.Value)} cd").ToArray();
        add("Candela", "Mẫu đại diện", count > 6
            ? string.Join("; ", formatted[..3]) + separator + string.Join("; ", formatted[3..])
            : string.Join(separator, formatted));
    }

    private static double? UniformStep(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        var step = values[1] - values[0];
        if (step <= Tolerance) return null;
        for (var index = 2; index < values.Count; index++)
        {
            if (Math.Abs(values[index] - values[index - 1] - step) > Tolerance) return null;
        }
        return step;
    }

    private static string Sample<T>(IReadOnlyList<T> values, Func<T, string> format)
    {
        const int maximum = 6;
        if (values.Count <= maximum) return string.Join("; ", values.Select(format));
        return string.Join("; ", values.Take(3).Select(format)) + "; …; " + string.Join("; ", values.TakeLast(3).Select(format));
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
