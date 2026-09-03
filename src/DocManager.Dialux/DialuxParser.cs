using System.Text.RegularExpressions;
using DocManager.Core;

namespace DocManager.Dialux;

internal sealed record PositionedResultGrid(
    double? CalculatedIlluminance,
    double? TargetIlluminance,
    double? CalculatedUniformity,
    double? TargetUniformity,
    double? PowerDensityWattsPerSquareMetre);

internal sealed record PositionedRoomPage(
    int PageNumber,
    string RoomName,
    string RoomKey,
    IReadOnlyList<DialuxLuminaire> Luminaires,
    double? AreaSquareMetres,
    double? PowerDensityWattsPerSquareMetre,
    PositionedResultGrid? Grid);

public sealed partial class DialuxParser
{
    [GeneratedRegex(@"^(?<count>\d+)\s+(?<manufacturer>\S+)\s+(?<article>\S+)\s+(?<name>.+?)\s+(?:(?:\d+|[-–—])\s+)?(?<power>[-+]?\d[\d.,]*)\s*W\s+(?<flux>[-+]?\d[\d.,]*)\s*lm\s+(?<efficacy>[-+]?\d[\d.,]*)\s*lm/W\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LuminaireLineRegex();

    [GeneratedRegex(@"^(?<count>\d+)\s+(?<manufacturer>\S+)\s+(?<identity>.+?)\s+(?<power>[-+]?\d[\d.,]*)\s*W\s+(?<flux>[-+]?\d[\d.,]*)\s*lm\s+(?<efficacy>[-+]?\d[\d.,]*)\s*lm/W\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PositionedLuminaireRowRegex();

    [GeneratedRegex(@"^(?<manufacturer>[^\r\n]+?)\s+-\s+(?<article>\S+)\s+(?<name>[^\r\n]+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ProductSheetTitleRegex();

    [GeneratedRegex(@"(?m)^\s*(?<name>[^\r\n]*\S\s*·+\s*\S[^\r\n]*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoneHeadingRegex();

    [GeneratedRegex(@"(?:Ē\w*|E\s?av?\b|Em\b|E\s?perp\w*)\s*(?:\[[^\]]*\])?\s*[:=]?\s*(?<value>[-+]?\d[\d.,]*)(?:\s*lx)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AverageLuxRegex();

    [GeneratedRegex(@"Emin\s*(?:\[[^\]]*\])?\s*[:=]?\s*(?<value>[-+]?\d[\d.,]*)(?:\s*lx)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MinimumLuxRegex();

    [GeneratedRegex(@"Emax\s*(?:\[[^\]]*\])?\s*[:=]?\s*(?<value>[-+]?\d[\d.,]*)(?:\s*lx)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MaximumLuxRegex();

    [GeneratedRegex(@"\bUo(?:\s*\([^)]*\)|\s*\[[^\]]*\])?\s*[:=]?\s*(?<value>[-+]?\d[\d.,]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UniformityRegex();

    [GeneratedRegex(@"(?im)^\s*(?:Working\s+plane\s+)?(?<metric>Ē\s*\w*|E\s?av?\b|Em\b|Emin\b|Emax\b|E\s?perp\w*|U\s*(?:o|\(\s*g\s*\d*\s*\)))\s*(?:\[[^\]]*\])?\s+(?<calculated>[-+]?\d[\d.,]*)(?:\s*lx)?\s+(?:≥\s*)?(?<target>[-+]?\d[\d.,]*)(?:\s*lx)?(?:\s+\S+)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CalculatedTargetMetricRegex();

    [GeneratedRegex(@"(?im)^\s*Working\s+plane\s*\((?<name>[^)\r\n]{1,120})\)\s+(?<average>[-+]?\d[\d.,]*)\s*lx\s+(?<minimum>[-+]?\d[\d.,]*)\s*lx\s+(?<maximum>[-+]?\d[\d.,]*)\s*lx\s+(?<uniformity>[-+]?\d[\d.,]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CalculationObjectMetricRegex();

    [GeneratedRegex(@"(?im)^\s*Perpendicular\s+illuminance[^\r\n]*?\(\s*≥\s*(?<lux>[-+]?\d[\d.,]*)\s*lx\s*\)\s*\(\s*≥\s*(?<uniformity>[-+]?\d[\d.,]*)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex CalculationObjectTargetRegex();

    [GeneratedRegex(@"(?im)^\s*Ground\s+area\s*(?:\[[^\]]*\])?\s*[:=]?\s*(?<value>[-+]?\d[\d.,]*)\s*m", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AreaRegex();

    [GeneratedRegex(@"(?im)^\s*(?:Space\s+)?Lighting\s+power\s+density\s*(?:\[[^\]]*\])?\s*[:=]?\s*(?<value>[-+]?\d[\d.,]*)\s*W\s*/\s*m", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerDensityRegex();

    [GeneratedRegex(@"(?im)^\s*Power\s+density\s*(?:\[[^\]]*\])?\s*[:=]?\s*(?<value>[-+]?\d[\d.,]*)\s*(?:W\s*/\s*m)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShortPowerDensityRegex();

    [GeneratedRegex(@"(?im)^\s*(?:Ground\s+area|Area)\s*\[[^\]]*(?:m²|m2)[^\]]*\]\s*[:=]?\s*(?<value>[-+]?\d[\d.,]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketedAreaRegex();

    [GeneratedRegex(@"(?im)\b(?:Symbol\s+)?Calculated\s+Target(?:\s+Index)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryMarkerRegex();

    [GeneratedRegex(@"(?im)^\s*(?:DIALux(?:\s+evo)?|Page\s+\d+|www\.|\d+\s*/\s*\d+|Printed\s+on\b|Date\b|Project\b|Room\s+overview\b|Luminaire\s+schedule\b)", RegexOptions.CultureInvariant)]
    private static partial Regex FooterOrReportChromeRegex();

    [GeneratedRegex(@"≥\s*(?<value>[-+]?\d[\d.,]*)\s*lx", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetLuxRegex();

    [GeneratedRegex(@"\bUo[^\r\n]*?≥\s*(?<value>[-+]?\d[\d.,]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetUniformityRegex();

    public DialuxReport Parse(IReadOnlyList<PdfPageContent> pages, string sourceFile = "")
    {
        ArgumentNullException.ThrowIfNull(pages);
        var pageTexts = pages.Select(page => page.Words.Any(word => !string.IsNullOrWhiteSpace(word.Text))
            ? PdfTextExtractor.ReconstructVisualText(page.Words)
            : page.Text ?? string.Empty).ToArray();
        var text = string.Join("\n\f\n", pageTexts);
        var fallback = ParseText(text, sourceFile, pages.SelectMany(page => page.Tables).ToArray());
        var positionedRooms = ParsePositionedRooms(pages, fallback.Rooms, fallback.Luminaires);
        return positionedRooms.Count == 0 ? fallback : fallback with { Rooms = positionedRooms };
    }

    private static IReadOnlyList<DialuxRoom> ParsePositionedRooms(
        IReadOnlyList<PdfPageContent> pages,
        IReadOnlyList<DialuxRoom> fallbackRooms,
        IReadOnlyList<DialuxLuminaire> fallbackInventory)
    {
        var supplements = new Dictionary<string, DialuxRoom>(StringComparer.OrdinalIgnoreCase);
        var globalInventory = DeduplicateLuminaires(fallbackInventory.Concat(pages
            .Where(page => page.Words.Count > 0)
            .SelectMany(page => ParsePositionedLuminaireRows(page.Words))));
        var roomPages = pages
            .Where(page => page.Words.Count > 0)
            .Select(ParsePositionedRoomPage)
            .Where(page => page is not null)
            .Cast<PositionedRoomPage>()
            .ToArray();
        var detailedRooms = new Dictionary<string, DialuxRoom>(StringComparer.OrdinalIgnoreCase);
        foreach (var calculationRoom in pages
            .Where(page => page.Words.Count > 0)
            .Select(page => PdfTextExtractor.ReconstructVisualText(page.Words))
            .SelectMany(ParseCalculationObjectRooms))
        {
            var resolved = ResolveCalculationRoom(calculationRoom, roomPages);
            if (resolved is null) continue;
            var key = RoomKey(resolved.Name);
            detailedRooms[key] = detailedRooms.TryGetValue(key, out var prior)
                ? Merge(prior, resolved)
                : resolved;
        }
        var associatedLuminaires = AssociatePositionedRoomLuminaires(roomPages);
        foreach (var page in roomPages)
        {
            var pageLuminaires = associatedLuminaires.TryGetValue(page.RoomKey, out var associated)
                ? associated
                : page.Luminaires;
            var supplement = new DialuxRoom
            {
                Name = page.RoomName,
                AreaSquareMetres = page.AreaSquareMetres,
                PowerDensityWattsPerSquareMetre = page.PowerDensityWattsPerSquareMetre,
                Luminaires = pageLuminaires
            };
            supplements[page.RoomKey] = supplements.TryGetValue(page.RoomKey, out var priorSupplement)
                ? Merge(priorSupplement, supplement)
                : supplement;

            if (page.Grid is null) continue;
            var detailed = new DialuxRoom
            {
                Name = page.RoomName,
                AverageLux = page.Grid.CalculatedIlluminance,
                Uniformity = page.Grid.CalculatedUniformity,
                TargetLux = page.Grid.TargetIlluminance,
                TargetUniformity = page.Grid.TargetUniformity,
                PowerDensityWattsPerSquareMetre = page.Grid.PowerDensityWattsPerSquareMetre,
                Luminaires = pageLuminaires
            };
            detailedRooms[page.RoomKey] = detailedRooms.TryGetValue(page.RoomKey, out var priorDetailed)
                ? Merge(detailed, priorDetailed)
                : detailed;
        }

        if (detailedRooms.Count == 0) return [];
        var fallbacks = fallbackRooms
            .Where(room => IsPlausibleRoomName(room.Name))
            .GroupBy(room => RoomKey(room.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Aggregate(Merge), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, detailed) in detailedRooms.ToArray())
        {
            var room = detailed;
            if (supplements.TryGetValue(key, out var supplement)) room = Merge(room, supplement);
            if (fallbacks.TryGetValue(key, out var fallback))
            {
                room = Merge(room, fallback with { Luminaires = [] });
            }
            detailedRooms[key] = room;
        }

        if (detailedRooms.Count == 1 && detailedRooms.Values.Single().Luminaires.Count == 0 && globalInventory.Count > 0)
        {
            var pair = detailedRooms.Single();
            detailedRooms[pair.Key] = pair.Value with { Luminaires = globalInventory };
        }

        return detailedRooms.Values.ToArray();
    }

    private static PositionedRoomPage? ParsePositionedRoomPage(PdfPageContent page)
    {
        var visualText = PdfTextExtractor.ReconstructVisualText(page.Words);
        var roomName = PositionedZoneHeading(page.Words);
        if (roomName is null) return null;
        var roomKey = RoomKey(roomName);
        if (roomKey.Length == 0) return null;

        var blockLines = visualText.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var positionedLuminaires = ParsePositionedLuminaireRows(page.Words).ToArray();
        var fallbackLuminaires = ParseLuminaires(blockLines, new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase))
            .Concat(ParseTables(page.Tables));
        var luminaires = MergePageLuminaires(positionedLuminaires, fallbackLuminaires);
        return new PositionedRoomPage(
            page.PageNumber,
            roomName,
            roomKey,
            luminaires,
            MatchNumber(BracketedAreaRegex(), visualText) ?? MatchNumber(AreaRegex(), visualText),
            MatchNumber(PowerDensityRegex(), visualText) ?? MatchNumber(ShortPowerDensityRegex(), visualText),
            ParseResultGrid(page.Words));
    }

    private static string? PositionedZoneHeading(IReadOnlyList<PositionedWord> words)
    {
        var rows = PdfTextExtractor.ClusterVisualRows(words);
        var lightSceneRow = rows.FirstOrDefault(row => row.Any(word => word.Text.StartsWith("(Light", StringComparison.OrdinalIgnoreCase)) &&
            row.Any(word => word.Text.Equals("scene", StringComparison.OrdinalIgnoreCase)));
        if (lightSceneRow is not null)
        {
            var centre = lightSceneRow.Select(CentreY).Average();
            var rowHeight = lightSceneRow.Select(word => Math.Abs(word.Top - word.Bottom)).DefaultIfEmpty(8d).Max();
            var headingWords = words.Where(word => Math.Abs(CentreY(word) - centre) <= Math.Max(3d, rowHeight * .75));
            var positionedText = string.Join(' ', headingWords.OrderBy(word => word.Left).Select(word => word.Text.Trim()).Where(value => value.Length > 0));
            if (IsRoomTitle(positionedText)) return positionedText;
        }

        var visualText = PdfTextExtractor.ReconstructVisualText(words);
        var match = ZoneHeadingRegex().Match(visualText);
        return match.Success ? match.Groups["name"].Value.Trim() : null;
    }

    private static DialuxRoom? ResolveCalculationRoom(
        DialuxRoom calculationRoom,
        IReadOnlyList<PositionedRoomPage> pages)
    {
        var leaf = RoomLeafKey(calculationRoom.Name);
        var candidates = pages
            .Where(page => RoomLeafKey(page.RoomName).Equals(leaf, StringComparison.OrdinalIgnoreCase))
            .Select(page => page.RoomName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return candidates.Length switch
        {
            0 => calculationRoom,
            1 => calculationRoom with { Name = candidates[0] },
            _ => null
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<DialuxLuminaire>> AssociatePositionedRoomLuminaires(
        IReadOnlyList<PositionedRoomPage> pages)
    {
        var result = new Dictionary<string, IReadOnlyList<DialuxLuminaire>>(StringComparer.OrdinalIgnoreCase);
        foreach (var associationGroup in pages.GroupBy(page => AssociationRoomKey(page.RoomKey), StringComparer.OrdinalIgnoreCase))
        {
            foreach (var section in ConsecutivePageSections(associationGroup))
            {
                var local = DeduplicateLuminaires(section.SelectMany(page => page.Luminaires));
                if (local.Count == 0) continue;

                foreach (var roomKey in section.Select(page => page.RoomKey).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    result[roomKey] = result.TryGetValue(roomKey, out var prior)
                        ? DeduplicateLuminaires(prior.Concat(local))
                        : local;
                }
            }
        }

        return result;
    }

    private static IEnumerable<IReadOnlyList<PositionedRoomPage>> ConsecutivePageSections(IEnumerable<PositionedRoomPage> pages)
    {
        var section = new List<PositionedRoomPage>();
        int? priorPage = null;
        foreach (var page in pages.OrderBy(page => page.PageNumber))
        {
            if (priorPage.HasValue && page.PageNumber - priorPage.Value > 1)
            {
                yield return section;
                section = [];
            }
            section.Add(page);
            priorPage = page.PageNumber;
        }
        if (section.Count > 0) yield return section;
    }

    private static string AssociationRoomKey(string value)
    {
        var folded = TextNormalization.Fold(CanonicalRoomIdentity(value)).Replace('Đ', 'D').Replace('đ', 'D').ToUpperInvariant();
        folded = Regex.Replace(folded, @"\s*·\s*", " · ");
        return Regex.Replace(folded, @"\s+", " ").Trim();
    }

    internal static PositionedResultGrid? ParseResultGrid(IReadOnlyList<PositionedWord> words)
    {
        var rows = PdfTextExtractor.ClusterVisualRows(words);
        var headerIndex = -1;
        PositionedWord? calculatedHeader = null;
        PositionedWord? targetHeader = null;
        PositionedWord? indexHeader = null;
        for (var index = 0; index < rows.Count; index++)
        {
            calculatedHeader = rows[index].FirstOrDefault(word => word.Text.Equals("Calculated", StringComparison.OrdinalIgnoreCase));
            targetHeader = rows[index].FirstOrDefault(word => word.Text.Equals("Target", StringComparison.OrdinalIgnoreCase));
            if (calculatedHeader is null || targetHeader is null) continue;
            indexHeader = rows[index].FirstOrDefault(word => word.Text.Equals("Index", StringComparison.OrdinalIgnoreCase));
            headerIndex = index;
            break;
        }

        if (headerIndex < 0 || calculatedHeader is null || targetHeader is null) return null;
        var calculatedX = CentreX(calculatedHeader);
        var targetX = CentreX(targetHeader);
        if (targetX <= calculatedX) return null;
        var indexX = indexHeader is null ? targetX + (targetX - calculatedX) : CentreX(indexHeader);
        var leftBoundary = calculatedX - (targetX - calculatedX) * .5;
        var calculatedTargetBoundary = (calculatedX + targetX) / 2d;
        var targetIndexBoundary = (targetX + indexX) / 2d;

        double? calculatedIlluminance = null;
        double? targetIlluminance = null;
        double? calculatedUniformity = null;
        double? targetUniformity = null;
        double? powerDensity = null;
        var end = Math.Min(rows.Count, headerIndex + 12);
        for (var rowIndex = headerIndex + 1; rowIndex < end; rowIndex++)
        {
            var row = rows[rowIndex];
            var labels = LabelText(row, leftBoundary);
            if (!IsUniformityLabel(labels) && !IsIlluminanceLabel(labels) && !IsPowerDensityLabel(labels))
            {
                labels = NearbyLabelText(rows, rowIndex, leftBoundary);
            }
            var calculated = ColumnNumber(row, leftBoundary, calculatedTargetBoundary);
            var target = ColumnNumber(row, calculatedTargetBoundary, targetIndexBoundary);
            if (IsUniformityLabel(labels))
            {
                calculatedUniformity ??= calculated;
                targetUniformity ??= target;
            }
            else if (IsIlluminanceLabel(labels))
            {
                calculatedIlluminance ??= calculated;
                targetIlluminance ??= target;
            }
            else if (IsPowerDensityLabel(labels))
            {
                powerDensity ??= calculated;
            }
        }

        return calculatedIlluminance.HasValue || calculatedUniformity.HasValue
            ? new PositionedResultGrid(calculatedIlluminance, targetIlluminance, calculatedUniformity, targetUniformity, powerDensity)
            : null;
    }

    private static string LabelText(IReadOnlyList<PositionedWord> row, double leftBoundary) =>
        string.Join(' ', row.Where(word => CentreX(word) < leftBoundary).OrderBy(word => word.Left).Select(word => word.Text));

    private static string NearbyLabelText(IReadOnlyList<IReadOnlyList<PositionedWord>> rows, int rowIndex, double leftBoundary)
    {
        var rowHeight = rows[rowIndex].Select(word => Math.Abs(word.Top - word.Bottom)).DefaultIfEmpty(8d).Max();
        var centre = rows[rowIndex].Select(CentreY).DefaultIfEmpty(0).Average();
        return string.Join(' ', Enumerable.Range(Math.Max(0, rowIndex - 1), Math.Min(rows.Count - 1, rowIndex + 1) - Math.Max(0, rowIndex - 1) + 1)
            .SelectMany(index => rows[index])
            .Where(word => CentreX(word) < leftBoundary && Math.Abs(CentreY(word) - centre) <= rowHeight * 1.8)
            .OrderByDescending(CentreY)
            .ThenBy(word => word.Left)
            .Select(word => word.Text));
    }

    private static bool IsIlluminanceLabel(string value) =>
        value.Contains('Ē') || Regex.IsMatch(value, @"\bE\s*(?:av|m|perp)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
        value.Contains("perpendicular", StringComparison.OrdinalIgnoreCase) || value.Contains("working plane", StringComparison.OrdinalIgnoreCase);

    private static bool IsUniformityLabel(string value) =>
        Regex.IsMatch(value, @"\bU\s*(?:o|\(\s*g)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
        value.Contains("uniformity", StringComparison.OrdinalIgnoreCase);

    private static bool IsPowerDensityLabel(string value) =>
        value.Contains("power density", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Lighting power", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Space Lighting", StringComparison.OrdinalIgnoreCase);

    private static double? ColumnNumber(IReadOnlyList<PositionedWord> row, double minimumX, double maximumX) => row
        .Where(word => CentreX(word) >= minimumX && CentreX(word) < maximumX)
        .Select(word => Regex.Match(word.Text.Trim(), @"^[-+]?\d[\d.,]*$", RegexOptions.CultureInvariant))
        .Where(match => match.Success)
        .Select(match => Number(match.Value))
        .FirstOrDefault(value => value.HasValue);

    private static double CentreX(PositionedWord word) => (word.Left + word.Right) / 2d;
    private static double CentreY(PositionedWord word) => (word.Bottom + word.Top) / 2d;

    public DialuxReport ParseText(
        string text,
        string sourceFile = "fixture.pdf",
        IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>>? tables = null)
    {
        text ??= string.Empty;
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var products = ProductSheetTitleRegex().Matches(text)
            .Cast<Match>()
            .GroupBy(match => match.Groups["article"].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(match => match.Groups["name"].Value.Trim()).ToArray(), StringComparer.OrdinalIgnoreCase);
        var luminaires = ParseLuminaires(lines, products);
        if (tables is not null)
        {
            luminaires.AddRange(ParseTables(tables));
        }
        luminaires = DeduplicateLuminaires(luminaires);

        var rooms = ParseRooms(text, lines, products).ToArray();
        if (rooms.Length == 1 && rooms[0].Luminaires.Count == 0 && luminaires.Count > 0)
        {
            rooms[0] = rooms[0] with { Luminaires = luminaires };
        }
        else if (rooms.Length > 1)
        {
            rooms = rooms.Select(room => room.Luminaires.Count == 0
                ? room
                : room with { Luminaires = DeduplicateLuminaires(room.Luminaires) }).ToArray();
        }

        return new DialuxReport
        {
            SourceFile = sourceFile,
            ProjectName = lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty,
            Operator = Header(text, "Operator", "Bearbeiter"),
            Date = Header(text, "Date", "Datum"),
            Luminaires = luminaires,
            Rooms = rooms
        };
    }

    private static List<DialuxLuminaire> ParseLuminaires(IReadOnlyList<string> lines, IReadOnlyDictionary<string, string[]> products)
    {
        var result = new List<DialuxLuminaire>();
        for (var index = 0; index < lines.Count; index++)
        {
            var match = LuminaireLineRegex().Match(lines[index].Trim());
            if (!match.Success)
            {
                continue;
            }

            var article = match.Groups["article"].Value.Trim();
            var name = match.Groups["name"].Value.Trim().TrimEnd('-', '–', '—').Trim();
            for (var continuationIndex = index + 1;
                 continuationIndex < lines.Count && IsContinuation(lines[continuationIndex]);
                 continuationIndex++)
            {
                name = JoinWhitespace(name, lines[continuationIndex]);
            }

            name = BestProductName(article, name, products);
            result.Add(new DialuxLuminaire
            {
                Manufacturer = match.Groups["manufacturer"].Value.Trim(),
                ArticleNumber = article,
                Name = name,
                Count = Number(match.Groups["count"].Value),
                PowerWatts = Number(match.Groups["power"].Value),
                LuminousFluxLumens = Number(match.Groups["flux"].Value),
                EfficacyLumensPerWatt = Number(match.Groups["efficacy"].Value)
            });
        }

        return result;
    }

    private static IReadOnlyList<DialuxRoom> ParseRooms(string text, IReadOnlyList<string> lines, IReadOnlyDictionary<string, string[]> products)
    {
        var headings = ZoneHeadingRegex().Matches(text).Cast<Match>().ToArray();
        if (headings.Length > 0)
        {
            var rooms = ParseCalculationObjectRooms(text)
                .ToDictionary(room => RoomKey(room.Name), StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < headings.Length; index++)
            {
                var heading = headings[index];
                var end = index + 1 < headings.Length ? headings[index + 1].Index : text.Length;
                var block = text[heading.Index..end];
                var parsed = ParseRoom(heading.Groups["name"].Value.Trim(), block, products);
                var calculationObject = CalculationObjectMetricRegex().Match(block);
                if (!HasResult(parsed) || calculationObject.Success &&
                    !RoomKey(calculationObject.Groups["name"].Value).Equals(RoomKey(parsed.Name), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var roomKey = RoomKey(parsed.Name);
                if (!rooms.TryGetValue(roomKey, out var existing))
                {
                    rooms[roomKey] = parsed;
                }
                else
                {
                    rooms[roomKey] = Merge(existing, parsed);
                }
            }

            if (rooms.Count > 0)
            {
                return rooms.Values.ToArray();
            }
        }

        var summary = ParseSummaryRooms(text, lines, products);
        return summary.Count > 0 ? summary : ParseLegacyRooms(lines);
    }

    private static DialuxRoom ParseRoom(string name, string block, IReadOnlyDictionary<string, string[]> products)
    {
        var blockLines = block.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        return new DialuxRoom
        {
            Name = name,
            AverageLux = MetricCalculated(block, false) ?? MatchNumber(AverageLuxRegex(), block),
            MinimumLux = CalculatedMetric(block, "Emin") ?? MatchNumber(MinimumLuxRegex(), block),
            MaximumLux = CalculatedMetric(block, "Emax") ?? MatchNumber(MaximumLuxRegex(), block),
            Uniformity = MetricCalculated(block, true) ?? MatchNumber(UniformityRegex(), block),
            TargetLux = MetricTarget(block, false) ?? MatchNumber(TargetLuxRegex(), block),
            TargetUniformity = MetricTarget(block, true) ?? MatchNumber(TargetUniformityRegex(), block),
            AreaSquareMetres = MatchNumber(BracketedAreaRegex(), block) ?? MatchNumber(AreaRegex(), block),
            PowerDensityWattsPerSquareMetre = MatchNumber(PowerDensityRegex(), block) ?? MatchNumber(ShortPowerDensityRegex(), block),
            Luminaires = DeduplicateLuminaires(ParseLuminaires(blockLines, products))
        };
    }

    private static List<DialuxRoom> ParseSummaryRooms(string text, IReadOnlyList<string> lines, IReadOnlyDictionary<string, string[]> products)
    {
        var rooms = ParseCalculationObjectRooms(text);
        var offsets = SummaryMarkerRegex().Matches(text).Select(match => match.Index).ToArray();
        foreach (var offset in offsets)
        {
            var lineIndex = text[..offset].Count(character => character == '\n');
            var name = Enumerable.Range(Math.Max(0, lineIndex - 16), Math.Min(16, lineIndex))
                .Reverse()
                .Select(index => lines[index].Trim())
                .FirstOrDefault(IsRoomTitle) ?? "Room";
            name = Regex.Replace(name, @"\s*\([^)]*\)\s*$", string.Empty).Trim();
            var next = offsets.FirstOrDefault(candidate => candidate > offset);
            var end = next > offset ? next : text.Length;
            var room = ParseRoom(name, text[offset..end], products);
            if (HasResult(room) && !rooms.Any(existing => SameRoom(existing, room)))
            {
                rooms.Add(room);
            }
        }

        return rooms;
    }

    private static List<DialuxRoom> ParseCalculationObjectRooms(string text)
    {
        var rooms = new List<DialuxRoom>();
        foreach (Match match in CalculationObjectMetricRegex().Matches(text))
        {
            var pageEnd = text.IndexOf('\f', match.Index);
            var blockEnd = pageEnd >= 0 ? pageEnd : text.Length;
            var block = text[match.Index..blockEnd];
            var target = CalculationObjectTargetRegex().Match(block);
            var room = new DialuxRoom
            {
                Name = match.Groups["name"].Value.Trim(),
                AverageLux = Number(match.Groups["average"].Value),
                MinimumLux = Number(match.Groups["minimum"].Value),
                MaximumLux = Number(match.Groups["maximum"].Value),
                Uniformity = Number(match.Groups["uniformity"].Value),
                TargetLux = target.Success ? Number(target.Groups["lux"].Value) : null,
                TargetUniformity = target.Success ? Number(target.Groups["uniformity"].Value) : null
            };
            var existingIndex = rooms.FindIndex(existing => RoomKey(existing.Name).Equals(RoomKey(room.Name), StringComparison.OrdinalIgnoreCase));
            if (existingIndex < 0) rooms.Add(room);
            else rooms[existingIndex] = Merge(rooms[existingIndex], room);
        }

        return rooms;
    }

    private static List<DialuxRoom> ParseLegacyRooms(IReadOnlyList<string> lines)
    {
        var rooms = new List<DialuxRoom>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (!AverageLuxRegex().IsMatch(line) && !MinimumLuxRegex().IsMatch(line) && !MaximumLuxRegex().IsMatch(line) && !UniformityRegex().IsMatch(line))
            {
                continue;
            }

            var metricPosition = Regex.Match(line, @"\b(?:Eav|Emin|Emax|Uo)\b", RegexOptions.IgnoreCase).Index;
            var name = metricPosition > 0 ? line[..metricPosition].Trim(" :-\t".ToCharArray()) : string.Empty;
            if (name.Length == 0)
            {
                name = lines.Take(index).Reverse().Select(value => value.Trim()).FirstOrDefault(IsRoomTitle) ?? "Room";
            }

            var room = new DialuxRoom
            {
                Name = name.Length > 80 ? name[..80] : name,
                AverageLux = MatchNumber(AverageLuxRegex(), line),
                MinimumLux = MatchNumber(MinimumLuxRegex(), line),
                MaximumLux = MatchNumber(MaximumLuxRegex(), line),
                Uniformity = MatchNumber(UniformityRegex(), line)
            };
            if (!rooms.Any(existing => SameRoom(existing, room)))
            {
                rooms.Add(room);
            }
        }

        return rooms;
    }

    private static IEnumerable<DialuxLuminaire> ParsePositionedLuminaireRows(IReadOnlyList<PositionedWord> words)
    {
        var rows = PdfTextExtractor.ClusterVisualRows(words);
        var result = new List<DialuxLuminaire>();
        var inLuminaireList = false;
        ArticleNameColumn? articleNameColumn = null;
        IReadOnlyList<PositionedWord>? priorRow = null;
        foreach (var row in rows)
        {
            var text = string.Join(' ', row.OrderBy(word => word.Left).Select(word => word.Text.Trim()).Where(value => value.Length > 0));
            if (Regex.IsMatch(text, @"\bLuminaire\s+list\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                inLuminaireList = true;
                articleNameColumn = null;
                priorRow = null;
                continue;
            }

            if (!inLuminaireList || text.Length == 0) continue;
            if (FooterOrReportChromeRegex().IsMatch(text) || Regex.IsMatch(text, @"^(?:Summary|Results?|Calculation|Room|Working\s+plane)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                inLuminaireList = false;
                articleNameColumn = null;
                priorRow = null;
                continue;
            }

            if (TryArticleNameColumn(row, out var detectedColumn))
            {
                articleNameColumn = detectedColumn;
                priorRow = null;
                continue;
            }

            if (articleNameColumn is not null && priorRow is not null &&
                IsPositionedArticleNameContinuation(row, priorRow, articleNameColumn))
            {
                result[^1] = result[^1] with { Name = JoinWhitespace(result[^1].Name, text) };
                priorRow = row;
                continue;
            }

            var match = PositionedLuminaireRowRegex().Match(text);
            if (!match.Success)
            {
                priorRow = null;
                continue;
            }

            var identity = match.Groups["identity"].Value.Trim();
            var rug = Regex.Match(identity, @"\s+(?:[-–—]|\d+(?:[.,]\d+)?)\s*$", RegexOptions.CultureInvariant);
            if (rug.Success) identity = identity[..rug.Index].TrimEnd();
            var split = SplitArticleAndDesignation(identity);
            var count = Number(match.Groups["count"].Value);
            var power = Number(match.Groups["power"].Value);
            var flux = Number(match.Groups["flux"].Value);
            var efficacy = Number(match.Groups["efficacy"].Value);
            if (!IsProductArticle(split.Article) || !IsProductName(split.Name) ||
                count is not > 0 or > 100000 || power is not > 0 or > 100000 || flux is not > 0 or > 10000000 ||
                efficacy is not > 0 or > 1000)
            {
                priorRow = null;
                continue;
            }

            result.Add(new DialuxLuminaire
            {
                Manufacturer = match.Groups["manufacturer"].Value.Trim(),
                ArticleNumber = split.Article,
                Name = split.Name,
                Count = count,
                PowerWatts = power,
                LuminousFluxLumens = flux,
                EfficacyLumensPerWatt = efficacy
            });
            priorRow = row;
        }

        return result;
    }

    private static IEnumerable<DialuxLuminaire> ParseTables(IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> tables)
    {
        foreach (var table in tables.Where(table => table.Count > 1))
        {
            var headers = table[0].Select(NormalizeHeader).ToArray();
            int Find(params string[] aliases) => Array.FindIndex(headers, header => aliases.Any(alias => HeaderContains(header, alias)));
            var manufacturer = Find("manufacturer", "hersteller");
            var article = Find("article no", "article number", "article", "artikel", "art", "product code");
            var name = Find("article name", "name", "luminaire", "leuchte", "designation", "description");
            var count = Find("count", "quantity", "anzahl", "qty", "pcs", "pieces");
            var power = Find("power", "p w", "p", "leistung", "watt");
            var flux = Find("flux", "fluss", "lumen", "lm", "phi", "φ");
            var efficacy = Find("luminous efficacy", "efficacy", "lm w");
            if (article < 0 || (name < 0 && !HeaderContains(headers[article], "article name")) || count < 0 || power < 0 || flux < 0) continue;

            foreach (var row in table.Skip(1))
            {
                string Cell(int column) => column >= 0 && column < row.Count ? row[column].Trim() : string.Empty;
                var merged = SplitMergedArticleAndName(Cell(article));
                var articleValue = merged.Article;
                var nameValue = name >= 0 && name != article ? Cell(name) : merged.Name;
                if (!IsProductArticle(articleValue) && string.IsNullOrWhiteSpace(articleValue) && nameValue.Length > 0)
                {
                    var split = SplitArticleAndDesignation(nameValue);
                    if (split.Article.Any(char.IsDigit))
                    {
                        articleValue = split.Article;
                        nameValue = split.Name;
                    }
                }
                else if (!IsProductArticle(articleValue) && nameValue.Length > 0)
                {
                    var split = SplitArticleAndDesignation($"{articleValue} {nameValue}");
                    if (split.Article.Any(char.IsDigit))
                    {
                        articleValue = split.Article;
                        nameValue = split.Name;
                    }
                }
                var parsedCount = Number(Cell(count));
                var parsedPower = Number(Cell(power));
                var parsedFlux = Number(Cell(flux));
                var parsedEfficacy = Number(Cell(efficacy));
                if (!IsProductArticle(articleValue) || !IsProductName(nameValue) ||
                    parsedCount is not > 0 or > 100000 || parsedPower is not > 0 or > 100000 || parsedFlux is not > 0 or > 10000000 ||
                    (parsedEfficacy.HasValue && parsedEfficacy is <= 0 or > 1000)) continue;
                yield return new DialuxLuminaire
                {
                    Manufacturer = Cell(manufacturer),
                    ArticleNumber = articleValue,
                    Name = nameValue,
                    Count = parsedCount,
                    PowerWatts = parsedPower,
                    LuminousFluxLumens = parsedFlux,
                    EfficacyLumensPerWatt = parsedEfficacy ?? Math.Round(parsedFlux.Value / parsedPower.Value, 1)
                };
            }
        }
    }

    private static bool TryArticleNameColumn(IReadOnlyList<PositionedWord> row, out ArticleNameColumn? column)
    {
        var articles = row.Where(word => word.Text.Equals("Article", StringComparison.OrdinalIgnoreCase)).OrderBy(word => word.Left).ToArray();
        var name = row.FirstOrDefault(word => word.Text.Equals("name", StringComparison.OrdinalIgnoreCase));
        if (articles.Length < 2 || name is null || name.Left < articles[^1].Left)
        {
            column = null;
            return false;
        }

        var nextColumn = row.Where(word => word.Left > name.Right).OrderBy(word => word.Left).FirstOrDefault();
        column = new ArticleNameColumn(articles[^1].Left, nextColumn?.Left ?? Math.Max(articles[^1].Right, name.Right));
        return true;
    }

    private static bool IsPositionedArticleNameContinuation(
        IReadOnlyList<PositionedWord> candidate,
        IReadOnlyList<PositionedWord> prior,
        ArticleNameColumn articleNameColumn)
    {
        if (candidate.Count == 0 || candidate.Any(word => string.IsNullOrWhiteSpace(word.Text))) return false;
        var candidateLeft = candidate.Min(word => word.Left);
        var candidateRight = candidate.Max(word => word.Right);
        var articleWidth = Math.Max(1d, articleNameColumn.Right - articleNameColumn.Left);
        var horizontalTolerance = Math.Max(8d, articleWidth * .35d);
        if (candidateLeft < articleNameColumn.Left - horizontalTolerance || candidateRight > articleNameColumn.Right + horizontalTolerance) return false;

        var priorCentre = prior.Select(CentreY).Average();
        var candidateCentre = candidate.Select(CentreY).Average();
        var lineHeight = Math.Max(MedianWordHeight(prior), MedianWordHeight(candidate));
        var verticalGap = priorCentre - candidateCentre;
        return verticalGap >= lineHeight * .5d && verticalGap <= lineHeight * 2.5d &&
            !FooterOrReportChromeRegex().IsMatch(string.Join(' ', candidate.Select(word => word.Text))) &&
            !IsMetricOrSectionLine(string.Join(' ', candidate.Select(word => word.Text)));
    }

    private static double MedianWordHeight(IReadOnlyList<PositionedWord> words)
    {
        var heights = words.Select(word => Math.Abs(word.Top - word.Bottom)).Where(height => height > 0).OrderBy(height => height).ToArray();
        return heights.Length == 0 ? 8d : heights[heights.Length / 2];
    }

    private static string JoinWhitespace(string first, string second) =>
        Regex.Replace($"{first} {second}", @"\s+", " ").Trim();

    private sealed record ArticleNameColumn(double Left, double Right);

    private static List<DialuxLuminaire> DeduplicateLuminaires(IEnumerable<DialuxLuminaire> luminaires)
    {
        var result = new List<DialuxLuminaire>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var luminaire in luminaires)
        {
            if (!IsProductArticle(luminaire.ArticleNumber) || !IsProductName(luminaire.Name)) continue;
            var key = string.Join('|', new object?[]
            {
                luminaire.ArticleNumber, luminaire.Name, luminaire.PowerWatts, luminaire.LuminousFluxLumens
            }.Select(value => NormalizeValue(value?.ToString())));
            if (seen.Add(key)) result.Add(luminaire);
        }
        return result;
    }

    private static List<DialuxLuminaire> MergePageLuminaires(
        IReadOnlyList<DialuxLuminaire> positioned,
        IEnumerable<DialuxLuminaire> fallbacks)
    {
        var result = DeduplicateLuminaires(positioned);
        foreach (var fallback in fallbacks)
        {
            var duplicate = result.Any(item =>
                item.ArticleNumber.Equals(fallback.ArticleNumber, StringComparison.OrdinalIgnoreCase) &&
                item.Count == fallback.Count && item.PowerWatts == fallback.PowerWatts &&
                item.LuminousFluxLumens == fallback.LuminousFluxLumens &&
                (NormalizeValue(item.Name).Contains(NormalizeValue(fallback.Name), StringComparison.OrdinalIgnoreCase) ||
                 NormalizeValue(fallback.Name).Contains(NormalizeValue(item.Name), StringComparison.OrdinalIgnoreCase)));
            if (!duplicate) result.Add(fallback);
        }
        return DeduplicateLuminaires(result);
    }

    private static string NormalizeHeader(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
    private static bool HeaderContains(string header, string alias) => Regex.IsMatch(header, $@"(?:^|\s){Regex.Escape(alias)}(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string NormalizeValue(string? value) => Regex.Replace(value?.Trim().ToUpperInvariant() ?? string.Empty, @"\s+", " ");

    private static (string Article, string Name) SplitMergedArticleAndName(string value) => SplitArticleAndDesignation(value);

    private static (string Article, string Name) SplitArticleAndDesignation(string value)
    {
        var match = Regex.Match(NormalizeValue(value), @"^(?<article>[A-Z0-9][A-Z0-9_.\-/]{1,39})\s+(?<name>.+)$", RegexOptions.CultureInvariant);
        return match.Success ? (match.Groups["article"].Value, match.Groups["name"].Value.Trim()) : (value.Trim(), string.Empty);
    }

    private static bool IsProductArticle(string value) => Regex.IsMatch(value.Trim(), @"^(?:[A-Za-z][A-Za-z0-9_.\-/]{1,39}|\d{6,40})$", RegexOptions.CultureInvariant);
    private static bool IsProductName(string value) => value.Trim().Length is >= 2 and <= 300 &&
        !FooterOrReportChromeRegex().IsMatch(value.Trim()) &&
        !Regex.IsMatch(value.Trim(), @"^(?:name|article name|luminaire|designation|description|article(?: no| number)?|manufacturer|quantity|power|p|flux|phi)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string BestProductName(string article, string original, IReadOnlyDictionary<string, string[]> products)
    {
        if (!products.TryGetValue(article, out var candidates) || candidates.Length == 0)
        {
            return original;
        }

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        var originalTokens = TextNormalization.ProductTokens(original).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.OrderByDescending(candidate => TextNormalization.ProductTokens(candidate).Count(originalTokens.Contains)).First();
    }

    private static string Header(string text, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = Regex.Match(text, $@"(?im)^\s*{Regex.Escape(label)}\s*[:\s]+(?<value>[^\r\n]+)");
            if (match.Success)
            {
                return match.Groups["value"].Value.Trim();
            }
        }

        return string.Empty;
    }

    private static bool IsContinuation(string line)
    {
        var value = line.Trim();
        return value.Length > 0 && !LuminaireLineRegex().IsMatch(value) && !value.Contains("lm/W", StringComparison.OrdinalIgnoreCase) &&
               !ZoneHeadingRegex().IsMatch(value) &&
               !IsMetricOrSectionLine(value) &&
               !FooterOrReportChromeRegex().IsMatch(value) &&
               !Regex.IsMatch(value, @"^[\d\s]+$") &&
               !Regex.IsMatch(value, @"^(luminaire|pcs|summary|results|symbol)\b", RegexOptions.IgnoreCase);
    }

    private static bool IsMetricOrSectionLine(string value) =>
        AverageLuxRegex().IsMatch(value) || MinimumLuxRegex().IsMatch(value) || MaximumLuxRegex().IsMatch(value) ||
        UniformityRegex().IsMatch(value) || TargetLuxRegex().IsMatch(value) || TargetUniformityRegex().IsMatch(value) ||
        AreaRegex().IsMatch(value) || BracketedAreaRegex().IsMatch(value) || PowerDensityRegex().IsMatch(value) || ShortPowerDensityRegex().IsMatch(value) ||
        Regex.IsMatch(value, @"^(Summary|Results|Result overview|Room summary|Luminaire(?:s| list)?|Product data sheet|Calculation surfaces?|Workplane)\b", RegexOptions.IgnoreCase);

    private static bool IsRoomTitle(string value) => value.Length is > 0 and <= 120 &&
        !FooterOrReportChromeRegex().IsMatch(value) &&
        !Regex.IsMatch(value, @"^(Summary|Results|Symbol|Calculated|Target|Index|Ground area|Area|Power density|Lighting power|Mounting height|Maintenance factor|\d+)$", RegexOptions.IgnoreCase) &&
        !AverageLuxRegex().IsMatch(value) && !MinimumLuxRegex().IsMatch(value) && !MaximumLuxRegex().IsMatch(value) && !UniformityRegex().IsMatch(value) &&
        !Regex.IsMatch(value, @"^(?:\d+\s+)?(?:Signify|Philips)\b.*\b(?:W|lm|lm/W)\b", RegexOptions.IgnoreCase);

    private static double? MetricCalculated(string text, bool uniformity) => CalculatedMetric(text, metric =>
        metric.Equals("Uo", StringComparison.OrdinalIgnoreCase) == uniformity, "calculated");

    private static double? CalculatedMetric(string text, string metric) => CalculatedMetric(text,
        value => value.Equals(metric, StringComparison.OrdinalIgnoreCase), "calculated");

    private static double? MetricTarget(string text, bool uniformity) => CalculatedMetric(text, metric =>
        metric.Equals("Uo", StringComparison.OrdinalIgnoreCase) == uniformity, "target");

    private static double? CalculatedMetric(string text, Func<string, bool> matchesMetric, string valueGroup) => CalculatedTargetMetricRegex().Matches(text)
        .Cast<Match>()
        .Where(match => matchesMetric(match.Groups["metric"].Value))
        .Select(match => Number(match.Groups[valueGroup].Value))
        .FirstOrDefault(value => value.HasValue);

    private static bool HasResult(DialuxRoom room) => IsPlausibleRoomName(room.Name) &&
        (room.AverageLux.HasValue || room.Uniformity.HasValue || room.AreaSquareMetres.HasValue || room.PowerDensityWattsPerSquareMetre.HasValue);

    private static DialuxRoom Merge(DialuxRoom first, DialuxRoom second) => first with
    {
        Name = PreferredRoomName(first.Name, second.Name),
        AverageLux = first.AverageLux ?? second.AverageLux,
        MinimumLux = first.MinimumLux ?? second.MinimumLux,
        MaximumLux = first.MaximumLux ?? second.MaximumLux,
        Uniformity = first.Uniformity ?? second.Uniformity,
        TargetLux = first.TargetLux ?? second.TargetLux,
        TargetUniformity = first.TargetUniformity ?? second.TargetUniformity,
        AreaSquareMetres = first.AreaSquareMetres ?? second.AreaSquareMetres,
        PowerDensityWattsPerSquareMetre = first.PowerDensityWattsPerSquareMetre ?? second.PowerDensityWattsPerSquareMetre,
        Luminaires = DeduplicateLuminaires(first.Luminaires.Concat(second.Luminaires))
    };

    private static string RoomKey(string name) => CanonicalRoomIdentity(name);
    private static string RoomLeafKey(string name) => CanonicalRoomIdentity(name.Split('·').Last());
    private static string CanonicalRoomIdentity(string name)
    {
        var value = Regex.Replace(name.Trim(), @"\s*\([^)]*\)\s*$", string.Empty);
        value = Regex.Replace(value, @"\s*·\s*", " · ");
        return Regex.Replace(value, @"\s+", " ").Trim();
    }
    private static string PreferredRoomName(string first, string second) => first.Count(character => character == '·') >= second.Count(character => character == '·') ? first : second;
    internal static bool IsPlausibleRoomName(string? name)
    {
        var value = name?.Trim() ?? string.Empty;
        return value.Length is > 0 and <= 160 && value.Count(character => character == '\n' || character == '\r' || character == '\f') == 0 &&
            value.Count(character => character == '·') <= 8 && !IsMetricOrSectionLine(value) && !FooterOrReportChromeRegex().IsMatch(value);
    }

    private static bool SameRoom(DialuxRoom left, DialuxRoom right) => RoomKey(left.Name).Equals(RoomKey(right.Name), StringComparison.OrdinalIgnoreCase) && left.AverageLux == right.AverageLux && left.MinimumLux == right.MinimumLux && left.MaximumLux == right.MaximumLux;
    private static double? MatchNumber(Regex regex, string text) => Number(regex.Match(text).Groups["value"].Value);
    private static double? Number(string value) => LocalizedNumber.ParseNullable(value);

    private static IEnumerable<int> AllIndexes(string text, string value)
    {
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            yield return index;
        }
    }
}
