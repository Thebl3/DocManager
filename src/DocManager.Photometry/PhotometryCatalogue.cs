using System.Text.RegularExpressions;
using DocManager.Core;
using UglyToad.PdfPig;

namespace DocManager.Photometry;

public sealed partial class PhotometryCatalogue
{
    [GeneratedRegex(@"(?:Luminous\s+flux|Quang\s+thông|Light\s+output|Lumen\s+output|Total\s+flux)[^\r\n\d]*(?<value>\d[\d.,]*)\s*lm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FluxRegex();

    [GeneratedRegex(@"(?:Mức\s+tiêu\s+thụ\s+điện|Rated\s+power|System\s+power|Input\s+power|Power\s+consumption|Wattage|Công\s+suất(?:\s+tiêu\s+thụ)?)[^\r\n\d]*(?<value>\d[\d.,]*)\s*W", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerRegex();

    [GeneratedRegex(@"(?:Colour\s+rendering\s+index|Color\s+rendering\s+index|Chỉ\s+số\s+hoàn\s+màu|\bCRI\b)[^\r\n\d]*(?:>|≥)?\s*(?<value>\d{2,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CriRegex();

    [GeneratedRegex(@"\d[\d.,]*\s*lm\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LumenValueRegex();

    [GeneratedRegex(@"(?:\bfrom\b|\bto\b|\brange\b|\boptions?\b|\bavailable\b|\btừ\b|\bđến\b|tùy\s*chọn|tuỳ\s*chọn)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarketingRangeRegex();

    [GeneratedRegex(@"(?:cut[ -]?out|opening|mounting|installation|drawing|packaging|carton|shipping|quantity|khoét|lỗ\s+mở|lắp\s+đặt|bản\s+vẽ|bao\s+bì|thùng|vận\s+chuyển|số\s+lượng)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RejectedDimensionContextRegex();

    [GeneratedRegex(@"(?:Overall\s+diameter|Đường\s+kính\s+tổng\s+thể)\s*[:=\-]?\s*(?<value>\d+(?:[.,]\d+)?)\s*mm\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OverallDiameterRegex();

    [GeneratedRegex(@"(?:Overall\s+height|Chiều\s+cao\s+tổng\s+thể)\s*[:=\-]?\s*(?<value>\d+(?:[.,]\d+)?)\s*mm\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OverallHeightRegex();

    [GeneratedRegex(@"(?:Overall\s+length|Chiều\s+dài\s+tổng\s+thể)\s*[:=\-]?\s*(?<value>\d+(?:[.,]\d+)?)\s*mm\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OverallLengthRegex();

    [GeneratedRegex(@"(?:Overall\s+width|Chiều\s+rộng\s+tổng\s+thể|Chiều\s+ngang\s+tổng\s+thể)\s*[:=\-]?\s*(?<value>\d+(?:[.,]\d+)?)\s*mm\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OverallWidthRegex();

    [GeneratedRegex(@"(?:Overall\s+dimensions|Kích\s+thước\s+tổng\s+thể)\s*[:=\-]?\s*(?:L\s*[x×]\s*W\s*[x×]\s*H\s*[:=\-]?\s*)?(?<length>\d+(?:[.,]\d+)?)\s*(?:mm\s*)?[x×]\s*(?<width>\d+(?:[.,]\d+)?)\s*(?:mm\s*)?[x×]\s*(?<height>\d+(?:[.,]\d+)?)\s*mm\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OverallDimensionsRegex();

    public string? FindBestPdf(IesDocument ies, IEnumerable<string> pdfPaths, string? canonicalProductName = null)
    {
        ArgumentNullException.ThrowIfNull(ies);
        ArgumentNullException.ThrowIfNull(pdfPaths);
        var expected = DocManager.Core.ProductAssetName.CanonicalProductName(canonicalProductName ?? ies.LuminaireName);
        var resolution = DocManager.Core.ProductAssetName.Resolve(pdfPaths.Where(File.Exists), expected);
        if (resolution.HasCollision)
            throw new InvalidDataException($"name_collision: nhiều PDF khớp chính xác '{expected}'.");
        return resolution.Path;
    }

    public PhotometryMetadata Extract(string text, IesDocument ies, string? catalogueNumber = null)
    {
        ArgumentNullException.ThrowIfNull(ies);
        var flux = MatchTechnicalNumber(FluxRegex(), text, rejectMarketingFlux: true);
        var power = MatchTechnicalNumber(PowerRegex(), text, rejectMarketingFlux: false);
        var cri = MatchTechnicalInteger(CriRegex(), text);
        var cct = MatchTechnicalCct(text);
        return new PhotometryMetadata
        {
            LuminaireName = ies.LuminaireName,
            Manufacturer = ies.Manufacturer,
            // Catalogue/PDF identity must never replace the conversion context.
            CatalogueNumber = ies.GetHeader("LUMCAT") ?? string.Empty,
            Description = ies.Lamp,
            LuminousFluxLumens = flux ?? (ies.TotalLumens > 0 ? ies.TotalLumens : null),
            InputWatts = power ?? (ies.InputWatts >= 0 ? ies.InputWatts : null),
            Cri = cri,
            CctKelvin = cct,
            LuminousFluxProvenance = flux is not null ? "catalogue:technical-luminous-flux" : ies.TotalLumens > 0 ? "ies:lamp-count-times-lumens" : string.Empty,
            InputWattsProvenance = power is not null ? "catalogue:technical-input-power" : ies.InputWatts >= 0 ? "ies:input-watts" : string.Empty,
            CriProvenance = cri is not null ? "catalogue:technical-cri" : string.Empty,
            CctProvenance = cct is not null ? "catalogue:technical-single-value-cct" : string.Empty
        };
    }

    public PhotometryMetadata ExtractPdf(string path, IesDocument ies, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        using var pdf = PdfDocument.Open(path);
        // PdfPig Page.Text follows content-stream order and can flatten a whole
        // page into one marketing-first line. Reconstruct visual rows so strict
        // technical-field parsing sees each label/value row independently.
        var pages = new List<PositionedPage>(pdf.NumberOfPages);
        for (var pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(new PositionedPage(pageNumber, ReconstructPageText(pdf.GetPage(pageNumber))));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var text = string.Join('\n', pages.Select(page => page.Text));
        var geometry = ExtractOverallGeometry(pages);
        return Extract(text, ies) with
        {
            OverallGeometry = geometry.Geometry,
            GeometryWarnings = geometry.Warnings,
            CataloguePdfPath = Path.GetFullPath(path)
        };
    }

    private static GeometryExtraction ExtractOverallGeometry(IReadOnlyList<PositionedPage> pages)
    {
        var circular = new List<PhotometryGeometry>();
        var rectangular = new List<PhotometryGeometry>();
        var hasAmbiguousLabelledGeometry = false;
        foreach (var page in pages)
        {
            var lines = Lines(page.Text).ToArray();
            var diameters = FindDimensions(lines, page.PageNumber, OverallDiameterRegex());
            var heights = FindDimensions(lines, page.PageNumber, OverallHeightRegex());
            var lengths = FindDimensions(lines, page.PageNumber, OverallLengthRegex());
            var widths = FindDimensions(lines, page.PageNumber, OverallWidthRegex());

            foreach (var line in lines)
            {
                if (RejectedDimensionContextRegex().IsMatch(line)) continue;
                foreach (Match match in OverallDimensionsRegex().Matches(line))
                {
                    if (!TryDimension(match.Groups["length"].Value, out var length) ||
                        !TryDimension(match.Groups["width"].Value, out var width) ||
                        !TryDimension(match.Groups["height"].Value, out var rectangularHeight)) continue;
                    rectangular.Add(new PhotometryGeometry(
                        PhotometryGeometryShape.Rectangular,
                        length,
                        width,
                        rectangularHeight,
                        "catalogue:overall-dimensions",
                        [new(page.PageNumber, match.Value.Trim(), line)]));
                }
            }

            var labelled = diameters.Concat(heights).Concat(lengths).Concat(widths).ToArray();
            foreach (var block in DimensionBlocks(labelled))
            {
                var blockDiameters = diameters.Where(match => block.Contains(match.LineNumber)).ToArray();
                var blockHeights = heights.Where(match => block.Contains(match.LineNumber)).ToArray();
                var blockLengths = lengths.Where(match => block.Contains(match.LineNumber)).ToArray();
                var blockWidths = widths.Where(match => block.Contains(match.LineNumber)).ToArray();

                if (blockDiameters.Length > 0 && blockHeights.Length > 0)
                {
                    if (blockDiameters.Length == 1 && blockHeights.Length == 1)
                    {
                        circular.Add(new PhotometryGeometry(
                            PhotometryGeometryShape.Circular,
                            blockDiameters[0].Value,
                            0,
                            blockHeights[0].Value,
                            "catalogue:overall-dimensions",
                            [blockDiameters[0].Evidence, blockHeights[0].Evidence]));
                    }
                    else
                    {
                        hasAmbiguousLabelledGeometry = true;
                    }
                }

                if (blockLengths.Length > 0 && blockWidths.Length > 0 && blockHeights.Length > 0)
                {
                    if (blockLengths.Length == 1 && blockWidths.Length == 1 && blockHeights.Length == 1)
                    {
                        rectangular.Add(new PhotometryGeometry(
                            PhotometryGeometryShape.Rectangular,
                            blockLengths[0].Value,
                            blockWidths[0].Value,
                            blockHeights[0].Value,
                            "catalogue:overall-dimensions",
                            [blockLengths[0].Evidence, blockWidths[0].Evidence, blockHeights[0].Evidence]));
                    }
                    else
                    {
                        hasAmbiguousLabelledGeometry = true;
                    }
                }
            }
        }

        var candidates = circular.Concat(rectangular)
            .DistinctBy(candidate => $"{candidate.Shape}:{candidate.LengthOrDiameterMm:R}:{candidate.WidthMm:R}:{candidate.HeightMm:R}")
            .ToArray();
        if (hasAmbiguousLabelledGeometry || candidates.Length > 1)
            return new GeometryExtraction(null, ["dimensions catalogue overall: conflicting or ambiguous complete geometry; using IES dimensions"]);
        return candidates.Length == 1
            ? new GeometryExtraction(candidates[0], [])
            : new GeometryExtraction(null, []);
    }

    private static IReadOnlyList<DimensionMatch> FindDimensions(IReadOnlyList<string> lines, int pageNumber, Regex regex)
    {
        var matches = new List<DimensionMatch>();
        for (var lineNumber = 0; lineNumber < lines.Count; lineNumber++)
        {
            var line = lines[lineNumber];
            if (RejectedDimensionContextRegex().IsMatch(line) || !IsDimensionEvidenceLine(line)) continue;
            foreach (Match match in regex.Matches(line))
            {
                if (TryDimension(match.Groups["value"].Value, out var value))
                    matches.Add(new DimensionMatch(value, lineNumber, new(pageNumber, match.Value.Trim(), line)));
            }
        }
        return matches;
    }

    private static bool IsDimensionEvidenceLine(string line) =>
        OverallDiameterRegex().Matches(line).Count +
        OverallHeightRegex().Matches(line).Count +
        OverallLengthRegex().Matches(line).Count +
        OverallWidthRegex().Matches(line).Count +
        OverallDimensionsRegex().Matches(line).Count == 1;

    private static IReadOnlyList<DimensionBlock> DimensionBlocks(IReadOnlyList<DimensionMatch> matches)
    {
        var lines = matches.Select(match => match.LineNumber).Distinct().OrderBy(line => line).ToArray();
        if (lines.Length == 0) return [];
        var blocks = new List<DimensionBlock>();
        var start = lines[0];
        var end = start;
        foreach (var line in lines.Skip(1))
        {
            if (line == end + 1)
            {
                end = line;
                continue;
            }
            blocks.Add(new DimensionBlock(start, end));
            start = end = line;
        }
        blocks.Add(new DimensionBlock(start, end));
        return blocks;
    }
    private static bool TryDimension(string raw, out double value)
    {
        value = LocalizedNumber.ParseNullable(raw) ?? 0;
        return value is >= 1 and <= 100_000 && double.IsFinite(value);
    }

    private static string ReconstructPageText(UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords()
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .Select(word => new PositionedText(word.Text.Trim(), word.BoundingBox.Left, (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2, Math.Abs(word.BoundingBox.Top - word.BoundingBox.Bottom)))
            .ToArray();
        if (words.Length == 0) return page.Text ?? string.Empty;

        var heights = words.Select(word => word.Height).Where(height => height > 0).OrderBy(height => height).ToArray();
        var tolerance = Math.Max(1.5, (heights.Length == 0 ? 8 : heights[heights.Length / 2]) * 0.4);
        var rows = new List<TextRow>();
        foreach (var word in words.OrderByDescending(word => word.CentreY).ThenBy(word => word.Left))
        {
            var row = rows.FirstOrDefault(candidate => Math.Abs(candidate.CentreY - word.CentreY) <= tolerance);
            if (row is null)
            {
                rows.Add(new TextRow(word.CentreY, [word]));
            }
            else
            {
                row.Words.Add(word);
            }
        }

        return string.Join('\n', rows.OrderByDescending(row => row.CentreY)
            .Select(row => string.Join(' ', row.Words.OrderBy(word => word.Left).Select(word => word.Text))));
    }

    private static double? MatchTechnicalNumber(Regex regex, string? text, bool rejectMarketingFlux)
    {
        double? candidate = null;
        foreach (var line in Lines(text))
        {
            var match = regex.Match(line);
            if (!match.Success) continue;
            if (rejectMarketingFlux && (MarketingRangeRegex().IsMatch(line) || LumenValueRegex().Matches(line).Count != 1)) continue;
            var value = LocalizedNumber.ParseNullable(match.Groups["value"].Value);
            if (value is not > 0 || !double.IsFinite(value.Value)) continue;
            if (candidate is not null && candidate != value) return null;
            candidate = value;
        }
        return candidate;
    }

    private static int? MatchTechnicalInteger(Regex regex, string? text)
    {
        int? candidate = null;
        foreach (var line in Lines(text))
        {
            var match = regex.Match(line);
            if (!match.Success || !int.TryParse(match.Groups["value"].Value, out var value) || value is < 0 or > 100) continue;
            if (candidate is not null && candidate != value) return null;
            candidate = value;
        }
        return candidate;
    }

    private static int? MatchTechnicalCct(string? text)
    {
        int? candidate = null;
        foreach (var line in Lines(text))
        {
            var value = CctParser.ExtractStrict(line);
            if (value is null) continue;
            if (candidate is not null && candidate != value) return null;
            candidate = value;
        }
        return candidate;
    }

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);

    private sealed record PositionedPage(int PageNumber, string Text);
    private sealed record DimensionMatch(double Value, int LineNumber, PhotometryDimensionEvidence Evidence);
    private sealed record DimensionBlock(int StartLine, int EndLine)
    {
        public bool Contains(int lineNumber) => lineNumber >= StartLine && lineNumber <= EndLine;
    }
    private sealed record GeometryExtraction(PhotometryGeometry? Geometry, IReadOnlyList<string> Warnings);
    private sealed record PositionedText(string Text, double Left, double CentreY, double Height);
    private sealed record TextRow(double CentreY, List<PositionedText> Words);
}
