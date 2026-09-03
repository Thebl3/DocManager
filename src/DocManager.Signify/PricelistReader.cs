using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using DocManager.Core;

namespace DocManager.Signify;

public sealed record ProductRecord(string Sheet, int ExcelRow, string ProductFamily, string Material, string MaterialDescription);

public sealed class PricelistReader
{
    private const long MaximumXmlEntrySize = 128L * 1024 * 1024;

    public IReadOnlyList<ProductRecord> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var sharedStrings = ReadSharedStrings(archive);
        var sheets = ReadSheets(archive);
        var records = new List<ProductRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheet in sheets)
        {
            var document = LoadXml(archive, sheet.Path);
            var rows = document.Descendants().Where(element => element.Name.LocalName == "row");
            Dictionary<string, int>? columns = null;
            var headerRow = 0;
            foreach (var row in rows)
            {
                var rowNumber = ParseRowNumber(row, headerRow + 1);
                var values = ReadRow(row, sharedStrings);
                if (columns is null)
                {
                    var candidate = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (column, value) in values)
                    {
                        var cleaned = Clean(value);
                        if (cleaned.Length > 0 && !candidate.ContainsKey(cleaned)) candidate[cleaned] = column;
                    }

                    if (!candidate.ContainsKey("Product Family") || !candidate.ContainsKey("Material description")) continue;
                    columns = candidate;
                    headerRow = rowNumber;
                    continue;
                }

                if (rowNumber <= headerRow) continue;
                var family = Clean(GetValue(values, columns["Product Family"]));
                var description = Clean(GetValue(values, columns["Material description"]));
                if (family.Length == 0 || description.Length == 0) continue;
                var material = columns.TryGetValue("Material", out var materialColumn)
                    ? CleanMaterial(GetValue(values, materialColumn))
                    : string.Empty;
                var key = $"{family}{material}{description}";
                if (seen.Add(key)) records.Add(new ProductRecord(sheet.Name, rowNumber, family, material, description));
            }
        }

        return records;
    }

    public IReadOnlyList<ProductRecord> Search(IEnumerable<ProductRecord> records, string query, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(records);
        var raw = Clean(query);
        if (raw.Length < 2) return [];
        var folded = TextNormalization.ProductKey(raw);
        return records.Select(record => new
            {
                Record = record,
                Description = TextNormalization.ProductKey(record.MaterialDescription),
                Family = TextNormalization.ProductKey(record.ProductFamily),
                Material = TextNormalization.ProductKey(record.Material)
            })
            .Where(item => item.Description.Contains(folded, StringComparison.OrdinalIgnoreCase) || item.Family.Contains(folded, StringComparison.OrdinalIgnoreCase) || item.Material.Contains(folded, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Material.Equals(folded, StringComparison.OrdinalIgnoreCase) || item.Description.Equals(folded, StringComparison.OrdinalIgnoreCase) ? 0
                : IsTokenPrefix(item.Description, folded) ? 1
                : item.Description.Contains(folded, StringComparison.OrdinalIgnoreCase) ? 2
                : item.Family.Contains(folded, StringComparison.OrdinalIgnoreCase) ? 3 : 4)
            .ThenBy(item => item.Record.MaterialDescription, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(item => item.Record)
            .ToArray();
    }

    public ProductRecord ResolveCode(IEnumerable<ProductRecord> records, string code, string manualFamily = "Manual Downloads")
    {
        ArgumentNullException.ThrowIfNull(records);
        var cleaned = Clean(code);
        var normalized = TextNormalization.ProductKey(cleaned);
        var items = records as IReadOnlyList<ProductRecord> ?? records.ToArray();
        var exact = items.FirstOrDefault(record =>
            TextNormalization.ProductKey(record.Material).Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            TextNormalization.ProductKey(record.MaterialDescription).Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // A model prefix is safe only when it identifies one designation. Never select an arbitrary variant.
        var prefixMatches = items.Where(record => IsTokenPrefix(TextNormalization.ProductKey(record.MaterialDescription), normalized)).Take(2).ToArray();
        if (prefixMatches.Length == 1) return prefixMatches[0];
        return new ProductRecord(string.Empty, 0, manualFamily, cleaned.All(char.IsDigit) ? cleaned : string.Empty, cleaned);
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        var document = LoadXml(entry);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "si")
            .Select(item => string.Concat(item.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value)))
            .ToArray();
    }

    private static IReadOnlyList<(string Name, string Path)> ReadSheets(ZipArchive archive)
    {
        var workbook = LoadXml(archive, "xl/workbook.xml");
        var relationships = LoadXml(archive, "xl/_rels/workbook.xml.rels")
            .Descendants()
            .Where(element => element.Name.LocalName == "Relationship")
            .Select(element => new
            {
                Id = (string?)element.Attribute("Id") ?? string.Empty,
                Target = (string?)element.Attribute("Target") ?? string.Empty
            })
            .Where(item => item.Id.Length > 0 && item.Target.Length > 0)
            .ToDictionary(item => item.Id, item => item.Target, StringComparer.Ordinal);
        XNamespace officeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var result = new List<(string Name, string Path)>();
        foreach (var sheet in workbook.Descendants().Where(element => element.Name.LocalName == "sheet"))
        {
            var name = Clean((string?)sheet.Attribute("name"));
            var relationshipId = (string?)sheet.Attribute(officeRelationships + "id") ?? string.Empty;
            if (name.Length == 0 || !relationships.TryGetValue(relationshipId, out var target)) continue;
            var path = ResolveWorkbookTarget(target);
            if (archive.GetEntry(path) is not null) result.Add((name, path));
        }

        return result;
    }

    private static string ResolveWorkbookTarget(string target)
    {
        var normalized = target.Replace('\\', '/').Trim();
        var segments = new List<string>();
        if (!normalized.StartsWith('/')) segments.Add("xl");
        foreach (var segment in normalized.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) throw new InvalidDataException($"Đường dẫn worksheet không hợp lệ: {target}");
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(Uri.UnescapeDataString(segment));
        }
        var resolved = string.Join('/', segments);
        if (!resolved.StartsWith("xl/", StringComparison.Ordinal)) throw new InvalidDataException($"Đường dẫn worksheet không hợp lệ: {target}");
        return resolved;
    }

    private static XDocument LoadXml(ZipArchive archive, string path) =>
        LoadXml(archive.GetEntry(path) ?? throw new InvalidDataException($"XLSX thiếu thành phần {path}."));

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        if (entry.Length > MaximumXmlEntrySize) throw new InvalidDataException($"Thành phần XLSX quá lớn: {entry.FullName}");
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static Dictionary<int, string> ReadRow(XElement row, IReadOnlyList<string> sharedStrings)
    {
        var values = new Dictionary<int, string>();
        var fallbackColumn = 0;
        foreach (var cell in row.Elements().Where(element => element.Name.LocalName == "c"))
        {
            var column = ParseColumn((string?)cell.Attribute("r")) ?? ++fallbackColumn;
            fallbackColumn = Math.Max(fallbackColumn, column);
            var type = (string?)cell.Attribute("t") ?? string.Empty;
            string value;
            if (type.Equals("inlineStr", StringComparison.Ordinal))
            {
                value = string.Concat(cell.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value));
            }
            else
            {
                value = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value ?? string.Empty;
                if (type.Equals("s", StringComparison.Ordinal) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Count)
                    value = sharedStrings[index];
            }

            values[column] = value;
        }

        return values;
    }

    private static int ParseRowNumber(XElement row, int fallback) =>
        int.TryParse((string?)row.Attribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    private static int? ParseColumn(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var value = 0;
        foreach (var character in reference)
        {
            if (character is < 'A' or > 'Z') break;
            value = checked((value * 26) + character - 'A' + 1);
        }
        return value > 0 ? value : null;
    }

    private static string GetValue(IReadOnlyDictionary<int, string> values, int column) => values.TryGetValue(column, out var value) ? value : string.Empty;

    private static string CleanMaterial(string? value)
    {
        var cleaned = Clean(value);
        if (decimal.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == decimal.Truncate(number))
            return number.ToString("0", CultureInfo.InvariantCulture);
        return cleaned;
    }

    private static bool IsTokenPrefix(string value, string prefix) =>
        prefix.Length > 0 && (value.Equals(prefix, StringComparison.OrdinalIgnoreCase) || value.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase));

    private static string Clean(string? value) => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
