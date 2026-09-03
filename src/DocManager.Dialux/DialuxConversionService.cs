using System.IO;
using DocManager.Core;

namespace DocManager.Dialux;

public sealed class DialuxConversionService
{
    private readonly PdfTextExtractor _extractor;
    private readonly DialuxParser _parser;

    public DialuxConversionService(PdfTextExtractor? extractor = null, DialuxParser? parser = null)
    {
        _extractor = extractor ?? new PdfTextExtractor();
        _parser = parser ?? new DialuxParser();
    }

    public async Task<IReadOnlyList<DialuxReport>> ParseAsync(
        IEnumerable<string> pdfPaths,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        var paths = pdfPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return await Task.Run(() =>
        {
            var reports = new List<DialuxReport>(paths.Length);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Đang đọc {Path.GetFileName(path)}");
                var pages = _extractor.Extract(path);
                reports.Add(_parser.Parse(pages, Path.GetFileName(path)));
            }

            return (IReadOnlyList<DialuxReport>)reports;
        }, cancellationToken);
    }

    public IReadOnlyList<string> FindMissingCatalogueCodes(IEnumerable<DialuxReport> reports, CatalogueIndex catalogue)
    {
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var luminaire in reports.SelectMany(report => report.Luminaires.Concat(report.Rooms.SelectMany(room => room.Luminaires))))
        {
            foreach (var component in catalogue.FindComponents(luminaire.ArticleNumber, luminaire.Name))
            {
                if (component.Entry is null && component.ArticleNumber.Length > 0)
                {
                    missing.Add(component.ArticleNumber);
                }
            }
        }

        return missing.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
