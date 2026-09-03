using System.IO;
using UglyToad.PdfPig;

namespace DocManager.Dialux;

public sealed record PositionedWord(string Text, double Left, double Bottom, double Right, double Top);

public sealed record PdfPageContent(
    int PageNumber,
    string Text,
    IReadOnlyList<PositionedWord> Words,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> Tables);

public sealed class PdfTextExtractor
{
    private static readonly string[] HeaderTerms = ["manufacturer", "hersteller", "article", "artikel", "luminaire", "leuchte", "designation", "productdescription", "product description", "12nc", "sku", "markets", "quantity", "count", "anzahl", "power", "leistung", "watt", "flux", "fluss", "lumen"];

    public IReadOnlyList<PdfPageContent> Extract(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || !Path.GetExtension(fullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("Tệp PDF không tồn tại hoặc không đúng định dạng.", fullPath);
        }

        using var document = PdfDocument.Open(fullPath);
        var pages = new List<PdfPageContent>(document.NumberOfPages);
        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            var words = page.GetWords()
                .Select(word => new PositionedWord(word.Text, word.BoundingBox.Left, word.BoundingBox.Bottom, word.BoundingBox.Right, word.BoundingBox.Top))
                .OrderByDescending(word => word.Top)
                .ThenBy(word => word.Left)
                .ToArray();
            pages.Add(new PdfPageContent(pageNumber, page.Text ?? string.Empty, words, ReconstructTables(words)));
        }

        return pages;
    }

    public string ExtractText(string path) => string.Join('\n', Extract(path).Select(page => page.Text));

    internal static string ReconstructVisualText(IReadOnlyList<PositionedWord> words) =>
        string.Join('\n', ClusterVisualRows(words).Select(row => string.Join(' ', row.Select(word => word.Text.Trim()).Where(text => text.Length > 0))));

    // Best effort only: text PDFs with a recognizable luminaire header and aligned columns.
    // Wrapped Article name cells are joined when their geometry remains within that column.
    // Borderless prose layouts and scanned/image PDFs are intentionally left unchanged.
    internal static IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> ReconstructTables(IReadOnlyList<PositionedWord> words)
    {
        if (words.Count == 0) return [];
        var rows = ClusterVisualRows(words);
        var tables = new List<IReadOnlyList<IReadOnlyList<string>>>();
        for (var headerIndex = 0; headerIndex < rows.Count; headerIndex++)
        {
            var header = rows[headerIndex];
            var headerText = string.Join(' ', header.Select(word => word.Text)).ToLowerInvariant();
            if (HeaderTerms.Count(term => headerText.Contains(term, StringComparison.Ordinal)) < 2) continue;

            var boundaries = DetectColumnStarts(header);
            if (boundaries.Length == 0) continue;

            var table = new List<IReadOnlyList<string>> { Cells(header, boundaries) };
            var articleNameGeometry = DetectLuminaireArticleNameGeometry(header);
            var articleNameColumn = articleNameGeometry is { } geometry ? ColumnAt(geometry.Left, boundaries) : -1;
            IReadOnlyList<PositionedWord>? priorDataRow = null;
            for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                if (row.Count == 0) continue;
                var rowText = string.Join(' ', row.Select(word => word.Text)).ToLowerInvariant();
                if (HeaderTerms.Count(term => rowText.Contains(term, StringComparison.Ordinal)) >= 2) break;
                var cells = Cells(row, boundaries);
                var populated = cells.Count(cell => cell.Length > 0);
                if (articleNameGeometry is { } articleName && articleNameColumn >= 0 && table.Count > 1 && priorDataRow is not null &&
                    IsArticleNameContinuation(row, priorDataRow, articleName))
                {
                    var merged = table[^1].ToArray();
                    merged[articleNameColumn] = JoinWhitespace(merged[articleNameColumn], VisualText(row));
                    table[^1] = merged;
                    priorDataRow = row;
                    continue;
                }

                if (populated < 2)
                {
                    if (table.Count > 1) break;
                    continue;
                }

                table.Add(cells);
                priorDataRow = row;
            }

            if (table.Count > 1)
            {
                tables.Add(table);
                headerIndex += table.Count - 1;
            }
        }

        return tables;
    }

    private static double[] DetectColumnStarts(IReadOnlyList<PositionedWord> header)
    {
        var threshold = Math.Max(8d, MedianWordHeight(header) * 1.25);
        var groups = new List<List<PositionedWord>>();
        foreach (var word in header)
        {
            if (groups.Count == 0 || word.Left - groups[^1][^1].Right >= threshold)
            {
                groups.Add([word]);
            }
            else
            {
                groups[^1].Add(word);
            }
        }

        return groups.Skip(1).Select(group => group[0].Left).ToArray();
    }

    internal static IReadOnlyList<IReadOnlyList<PositionedWord>> ClusterVisualRows(IReadOnlyList<PositionedWord> words)
    {
        var tolerance = Math.Max(2d, MedianWordHeight(words) * .45);
        var rows = new List<WordRow>();
        foreach (var word in words.Where(word => !string.IsNullOrWhiteSpace(word.Text)).OrderByDescending(word => (word.Top + word.Bottom) / 2d).ThenBy(word => word.Left))
        {
            var centre = (word.Top + word.Bottom) / 2d;
            var row = rows.FirstOrDefault(candidate => Math.Abs(candidate.Centre - centre) <= tolerance);
            if (row is null)
            {
                rows.Add(new WordRow(centre, [word]));
            }
            else
            {
                row.Words.Add(word);
            }
        }

        return rows.OrderByDescending(row => row.Centre).Select(row => (IReadOnlyList<PositionedWord>)row.Words.OrderBy(word => word.Left).ToArray()).ToArray();
    }

    private static IReadOnlyList<string> Cells(IReadOnlyList<PositionedWord> row, IReadOnlyList<double> boundaries)
    {
        var cells = Enumerable.Range(0, boundaries.Count + 1).Select(_ => new List<string>()).ToArray();
        foreach (var word in row)
        {
            cells[ColumnAt(word.Left, boundaries)].Add(word.Text.Trim());
        }

        return cells.Select(cell => string.Join(' ', cell.Where(value => value.Length > 0))).ToArray();
    }

    private static (double Left, double Right)? DetectLuminaireArticleNameGeometry(IReadOnlyList<PositionedWord> header)
    {
        var articleWords = header.Where(word => word.Text.Equals("Article", StringComparison.OrdinalIgnoreCase)).OrderBy(word => word.Left).ToArray();
        var name = header.FirstOrDefault(word => word.Text.Equals("name", StringComparison.OrdinalIgnoreCase));
        if (articleWords.Length < 2 || name is null) return null;
        var articleName = articleWords[^1];
        if (name.Left < articleName.Left) return null;
        var nextColumn = header.Where(word => word.Left > name.Right).OrderBy(word => word.Left).FirstOrDefault();
        return (articleName.Left, nextColumn?.Left ?? Math.Max(articleName.Right, name.Right));
    }

    private static bool IsArticleNameContinuation(
        IReadOnlyList<PositionedWord> candidate,
        IReadOnlyList<PositionedWord> prior,
        (double Left, double Right) articleNameGeometry)
    {
        if (candidate.Count == 0 || candidate.Any(word => string.IsNullOrWhiteSpace(word.Text))) return false;
        var candidateLeft = candidate.Min(word => word.Left);
        var candidateRight = candidate.Max(word => word.Right);
        var articleWidth = Math.Max(1d, articleNameGeometry.Right - articleNameGeometry.Left);
        var horizontalTolerance = Math.Max(8d, articleWidth * .35d);
        if (candidateLeft < articleNameGeometry.Left - horizontalTolerance || candidateRight > articleNameGeometry.Right + horizontalTolerance) return false;

        var priorCentre = prior.Select(word => (word.Top + word.Bottom) / 2d).Average();
        var candidateCentre = candidate.Select(word => (word.Top + word.Bottom) / 2d).Average();
        var lineHeight = Math.Max(MedianWordHeight(prior), MedianWordHeight(candidate));
        var verticalGap = priorCentre - candidateCentre;
        return verticalGap >= lineHeight * .5d && verticalGap <= lineHeight * 2.5d;
    }

    private static int ColumnAt(double left, IReadOnlyList<double> boundaries)
    {
        var column = 0;
        while (column < boundaries.Count && left >= boundaries[column]) column++;
        return column;
    }

    private static string VisualText(IReadOnlyList<PositionedWord> row) =>
        string.Join(' ', row.OrderBy(word => word.Left).Select(word => word.Text.Trim()).Where(text => text.Length > 0));

    private static string JoinWhitespace(string first, string second) =>
        string.Join(' ', new[] { first, second }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

    private static double MedianWordHeight(IReadOnlyList<PositionedWord> words)
    {
        var heights = words.Select(word => Math.Abs(word.Top - word.Bottom)).Where(height => height > 0).OrderBy(height => height).ToArray();
        return heights.Length == 0 ? 8d : heights[heights.Length / 2];
    }

    private sealed record WordRow(double Centre, List<PositionedWord> Words);
}
