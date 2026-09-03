using System.Globalization;
using System.Text.RegularExpressions;

#pragma warning disable SYSLIB0001

namespace DocManager.Photometry;

public sealed partial class IesParser
{
    public const int MaximumCandelaSamples = 10_000_000;

    [GeneratedRegex(@"\[(?<key>[^\]]+)\]\s*(?<value>.*)", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderRegex();

    public IesDocument ParseFile(string path) => new IesSourceReader(this).ReadFile(path).Document;

    public IesDocument Parse(string text, string sourcePath = "fixture.ies")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        text = text.TrimStart('﻿');
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var tiltIndex = Array.FindIndex(lines, line => line.TrimStart().StartsWith("TILT=", StringComparison.OrdinalIgnoreCase));
        if (tiltIndex < 0)
        {
            throw new InvalidDataException("IES thiếu dòng TILT.");
        }

        var tilt = lines[tiltIndex].Trim();
        if (!tilt.Equals("TILT=NONE", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Chỉ hỗ trợ TILT=NONE; nhận được '{tilt}'.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < tiltIndex; index++)
        {
            var match = HeaderRegex().Match(lines[index].Trim());
            if (match.Success)
            {
                headers[match.Groups["key"].Value.Trim()] = match.Groups["value"].Value.Trim();
            }
        }

        var numeric = new NumericTokenReader(lines, tiltIndex + 1);
        double Next(string field) => numeric.Read(field);

        int NextCount(string field)
        {
            var value = Next(field);
            if (value < 0 || value > 100_000 || Math.Abs(value - Math.Round(value)) > 0.000001)
            {
                throw new InvalidDataException($"Giá trị {field} không hợp lệ: {value}.");
            }

            return checked((int)Math.Round(value));
        }

        var lampCount = Math.Max(1, NextCount("số bóng"));
        var lumensPerLamp = numeric.ReadLumensPerLamp();
        if (lumensPerLamp != -1 && lumensPerLamp <= 0)
        {
            throw new FormatException("Quang thông mỗi bóng phải là -1 cho absolute IES hoặc số dương hữu hạn cho relative IES.");
        }
        var multiplier = Next("hệ số candela");
        if (!double.IsFinite(multiplier) || multiplier <= 0)
        {
            throw new FormatException("Hệ số candela phải là số dương hữu hạn.");
        }
        var verticalCount = NextCount("số góc đứng");
        var horizontalCount = NextCount("số góc ngang");
        if (verticalCount is < 1 or > 10_000 || horizontalCount is < 1 or > 10_000)
        {
            throw new InvalidDataException("Số góc IES nằm ngoài giới hạn an toàn.");
        }
        var candelaSampleCount = checked((long)verticalCount * horizontalCount);
        if (candelaSampleCount > MaximumCandelaSamples)
        {
            throw new InvalidDataException($"Ma trận candela có {candelaSampleCount:N0} mẫu, vượt giới hạn an toàn {MaximumCandelaSamples:N0}.");
        }

        var photometricType = NextCount("kiểu quang trắc");
        var unitType = NextCount("đơn vị");
        if (photometricType is < 1 or > 3 || unitType is < 1 or > 2)
        {
            throw new InvalidDataException("Kiểu quang trắc hoặc đơn vị IES không hợp lệ.");
        }
        if (photometricType != 1)
        {
            throw new NotSupportedException($"Chỉ hỗ trợ photometric type C (1); type {photometricType} cần coordinate transform trước khi chuyển sang Eulumdat.");
        }

        var width = Next("chiều rộng");
        var length = Next("chiều dài");
        var height = Next("chiều cao");
        var ballast = Next("ballast factor");
        var futureUse = Next("future use");
        var inputWatts = Next("công suất");
        var verticalAngles = Enumerable.Range(0, verticalCount).Select(_ => Next("góc đứng")).ToArray();
        var horizontalAngles = Enumerable.Range(0, horizontalCount).Select(_ => Next("góc ngang")).ToArray();
        EnsureAscending(verticalAngles, "góc đứng");
        EnsureAscending(horizontalAngles, "góc ngang");
        if (verticalAngles.Any(angle => angle < 0 || angle > 180) || horizontalAngles.Any(angle => angle < 0 || angle > 360))
        {
            throw new InvalidDataException("Góc Type C phải nằm trong Gamma 0..180 và C 0..360.");
        }

        var candela = new List<IReadOnlyList<double>>(horizontalCount);
        for (var horizontal = 0; horizontal < horizontalCount; horizontal++)
        {
            var row = new double[verticalCount];
            for (var vertical = 0; vertical < verticalCount; vertical++)
            {
                var value = Next("ma trận candela") * multiplier;
                if (!double.IsFinite(value) || value < 0)
                {
                    throw new FormatException("Giá trị candela sau hệ số phải hữu hạn và không âm.");
                }
                row[vertical] = value;
            }

            candela.Add(row);
        }
        var horizontalData = CanonicalizeHorizontalPlanes(horizontalAngles, candela);
        numeric.EnsureNoTrailingTokens();

        return new IesDocument
        {
            SourcePath = sourcePath,
            Version = lines[0].Trim(),
            Headers = headers,
            LampCount = lampCount,
            LumensPerLamp = lumensPerLamp,
            CandelaMultiplier = multiplier,
            VerticalAngleCount = verticalCount,
            HorizontalAngleCount = horizontalData.Angles.Count,
            PhotometricType = photometricType,
            UnitType = unitType,
            Width = width,
            Length = length,
            Height = height,
            BallastFactor = ballast,
            FutureUse = futureUse,
            InputWatts = inputWatts,
            VerticalAngles = verticalAngles,
            HorizontalAngles = horizontalData.Angles,
            Candela = horizontalData.Candela
        };
    }

    private static (IReadOnlyList<double> Angles, IReadOnlyList<IReadOnlyList<double>> Candela) CanonicalizeHorizontalPlanes(
        IReadOnlyList<double> angles,
        IReadOnlyList<IReadOnlyList<double>> candela)
    {
        const double angleTolerance = 0.000001;
        const double candelaTolerance = 0.0001;
        var canonicalAngles = new List<double>(angles.Count);
        var canonicalCandela = new List<IReadOnlyList<double>>(candela.Count);
        for (var index = 0; index < angles.Count; index++)
        {
            var equivalent = canonicalAngles.FindIndex(angle => Math.Abs(angles[index] - angle) <= angleTolerance);
            if (equivalent < 0)
            {
                canonicalAngles.Add(angles[index]);
                canonicalCandela.Add(candela[index]);
                continue;
            }

            if (!RowsAgree(canonicalCandela[equivalent], candela[index], candelaTolerance))
                throw new InvalidDataException($"Mặt phẳng C{angles[index].ToString("0.######", CultureInfo.InvariantCulture)} bị lặp với dữ liệu candela mâu thuẫn.");
        }

        if (canonicalAngles.Count > 1 && Math.Abs(canonicalAngles[0]) <= angleTolerance && Math.Abs(canonicalAngles[^1] - 360) <= angleTolerance &&
            !RowsAgree(canonicalCandela[0], canonicalCandela[^1], candelaTolerance))
        {
            throw new InvalidDataException("Mặt phẳng C360 phải trùng C0 khi cả hai cùng xuất hiện.");
        }

        return (canonicalAngles, canonicalCandela);
    }

    private static bool RowsAgree(IReadOnlyList<double> first, IReadOnlyList<double> second, double tolerance)
    {
        for (var index = 0; index < first.Count; index++)
        {
            if (Math.Abs(first[index] - second[index]) > tolerance * Math.Max(1, Math.Abs(first[index])))
                return false;
        }
        return true;
    }

    private static double ParseInvariant(string token)
    {
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            throw new InvalidDataException($"Số IES không hợp lệ: '{token}'.");
        }

        return value;
    }

    private static void EnsureAscending(IReadOnlyList<double> values, string field)
    {
        for (var index = 1; index < values.Count; index++)
        {
            if (values[index] < values[index - 1])
            {
                throw new InvalidDataException($"Dãy {field} phải tăng dần.");
            }

            if (values[index] == values[index - 1] && field == "góc đứng")
            {
                throw new FormatException("Dãy góc đứng Gamma phải tăng nghiêm ngặt, không được có giá trị lặp.");
            }
        }
    }

    private sealed class NumericTokenReader
    {
        private const int MaximumTokenLength = 128;
        private readonly string[] _lines;
        private int _lineIndex;
        private int _characterIndex;

        public NumericTokenReader(string[] lines, int firstNumericLine)
        {
            _lines = lines;
            _lineIndex = firstNumericLine;
        }

        public double Read(string field)
        {
            if (!TryReadToken(out var token))
            {
                throw new InvalidDataException($"IES bị thiếu dữ liệu tại {field}.");
            }

            return ParseInvariant(token);
        }

        public double ReadLumensPerLamp()
        {
            if (!TryReadToken(out var token))
            {
                throw new InvalidDataException("IES bị thiếu dữ liệu tại quang thông mỗi bóng.");
            }

            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            {
                throw new FormatException($"Quang thông mỗi bóng IES không hợp lệ: '{token}'.");
            }

            return value;
        }

        public void EnsureNoTrailingTokens()
        {
            if (TryReadToken(out var token))
            {
                throw new InvalidDataException($"IES có dữ liệu số dư không được hỗ trợ: '{token}'.");
            }
        }

        private bool TryReadToken(out string token)
        {
            while (_lineIndex < _lines.Length)
            {
                var line = _lines[_lineIndex];
                while (_characterIndex < line.Length && char.IsWhiteSpace(line[_characterIndex])) _characterIndex++;
                if (_characterIndex == line.Length)
                {
                    _lineIndex++;
                    _characterIndex = 0;
                    continue;
                }

                var start = _characterIndex;
                while (_characterIndex < line.Length && !char.IsWhiteSpace(line[_characterIndex]))
                {
                    _characterIndex++;
                    if (_characterIndex - start > MaximumTokenLength)
                    {
                        throw new InvalidDataException("Mã số IES vượt giới hạn độ dài an toàn.");
                    }
                }

                token = line.Substring(start, _characterIndex - start);
                return true;
            }

            token = string.Empty;
            return false;
        }
    }

}
