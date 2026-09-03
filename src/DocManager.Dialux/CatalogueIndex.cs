using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DocManager.Core;

namespace DocManager.Dialux;

public sealed partial class CatalogueIndex
{
    private readonly Dictionary<string, List<CatalogueEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly PdfTextExtractor _extractor;
    private readonly IPdfProductImageExtractor _imageExtractor;

    public CatalogueIndex(PdfTextExtractor? extractor = null, IPdfProductImageExtractor? imageExtractor = null)
    {
        _extractor = extractor ?? new PdfTextExtractor();
        _imageExtractor = imageExtractor ?? new PdfPigProductImageExtractor();
    }

    public IReadOnlyCollection<CatalogueEntry> Entries => _entries.Values.SelectMany(value => value).ToArray();

    public void Add(CatalogueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.ArticleNumber)) throw new ArgumentException("Catalogue entry phải có article number.", nameof(entry));
        if (!_entries.TryGetValue(entry.ArticleNumber.Trim(), out var variants))
        {
            variants = [];
            _entries[entry.ArticleNumber.Trim()] = variants;
        }
        variants.Add(entry);
    }

    public void Build(string rootPath, CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = FileSystemSafety.NormalizeDirectoryPath(rootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }
        if (!FileSystemSafety.HasNoReparsePointsInExistingPath(root))
            throw new InvalidDataException("Catalogue root chứa symbolic link, junction hoặc reparse point.");

        _entries.Clear();
        var logicalInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in FileSystemSafety.EnumerateFilesRecursivelyWithoutReparsePoints(root, "*.pdf", cancellationToken)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string hash;
            try
            {
                using var stream = File.OpenRead(path);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch { continue; }

            IReadOnlyList<PdfPageContent> pages;
            try { pages = _extractor.Extract(path); }
            catch { pages = []; }
            var identity = IdentityFromFileName(path);
            var descriptors = ExtractProductDescriptors(pages);
            var document = new CatalogueDocument
            {
                SourcePdf = path,
                ContentHash = hash,
                FileIdentity = identity,
                ProductDescriptors = descriptors,
                IsFamilyDocument = IsFamilyDocument(identity, descriptors)
            };
            if (string.IsNullOrWhiteSpace(identity.Article) && descriptors.Count == 0) continue;
            if (!logicalInputs.Add(LogicalInputKey(document)))
            {
                progress?.Report(Path.GetFileName(path));
                continue;
            }

            var text = document.IsFamilyDocument ? string.Empty : string.Join('\n', pages.Select(page => PdfTextExtractor.ReconstructVisualText(page.Words)));
            PdfProductImage? image;
            try { image = _imageExtractor.Extract(path, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { image = null; }

            var entries = BuildEntries(document, pages, text, image);
            foreach (var entry in entries) Add(entry);
            progress?.Report(Path.GetFileName(path));
        }
    }

    private static string LogicalInputKey(CatalogueDocument document)
    {
        var identities = document.ProductDescriptors.Count > 0
            ? document.ProductDescriptors.Select(descriptor => $"{descriptor.Sku}|{TextNormalization.ProductKey(descriptor.Description)}")
            : [$"{document.FileIdentity.Article}|{string.Join(' ', document.FileIdentity.Tokens)}"];
        return $"{document.ContentHash}|{string.Join(';', identities.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}";
    }

    private static IReadOnlyList<CatalogueEntry> BuildEntries(CatalogueDocument document, IReadOnlyList<PdfPageContent> pages, string text, PdfProductImage? image)
    {
        CatalogueEntry WithDocument(CatalogueEntry entry) => entry with
        {
            ImagePng = image?.Png,
            ImageAspect = image?.Aspect,
            IsFamilyDocument = document.IsFamilyDocument,
            DocumentHash = document.ContentHash
        };

        if (!document.IsFamilyDocument)
        {
            if (document.FileIdentity.Article.Length > 0)
            {
                return [WithDocument(Parse(document.FileIdentity.Article, document.FileIdentity.Tokens, document.SourcePdf, text))];
            }

            var descriptor = document.ProductDescriptors[0];
            return [WithDocument(Parse(descriptor.ArticleNumber, descriptor.Tokens, document.SourcePdf, text) with
            {
                Sku = descriptor.Sku,
                VariantDescription = descriptor.Description,
                HasVariantEvidence = true
            })];
        }

        var sections = ExtractFamilyTechnicalSections(document.FileIdentity.Article, document.ProductDescriptors, pages);
        var descriptorSections = document.ProductDescriptors
            .Select(descriptor => new DescriptorTechnicalSection(descriptor, SelectFamilyTechnicalSection(descriptor, sections)))
            .ToArray();
        var commonSections = document.ProductDescriptors.Count == 0
            ? sections
            : descriptorSections
                .Where(item => item.Section is not null)
                .Select(item => item.Section!)
                .Distinct()
                .ToArray();
        var common = ParseCommonFamilyFacts(
            document.FileIdentity.Article,
            [],
            document.SourcePdf,
            commonSections.SelectMany(section => section.Columns).ToArray());
        if (document.ProductDescriptors.Count == 0)
        {
            return document.FileIdentity.Article.Length == 0 ? [] : [WithDocument(common)];
        }

        return descriptorSections.Select(item =>
        {
            var descriptor = item.Descriptor;
            var mapped = ParseVariantFamilyFacts(common, descriptor, item.Section?.Columns ?? []);
            return WithDocument(mapped with
            {
                ArticleNumber = descriptor.ArticleNumber,
                Tokens = descriptor.Tokens,
                Sku = descriptor.Sku,
                VariantDescription = descriptor.Description,
                HasVariantEvidence = true
            });
        }).ToArray();
    }

    public CatalogueEntry? Find(string? articleNumber, string? designation = null)
    {
        var components = Components(articleNumber, designation);
        var main = components.Count > 0 ? components[^1] : string.Empty;
        var lookupArticle = components.Count > 1
            ? TextNormalization.ProductTokens(main).FirstOrDefault() ?? string.Empty
            : TextNormalization.ProductTokens(articleNumber).FirstOrDefault() ?? articleNumber?.Trim() ?? string.Empty;
        return FindComponent(lookupArticle, main);
    }

    public IReadOnlyList<CatalogueComponentMatch> FindComponents(string? articleNumber, string? designation = null)
    {
        var components = Components(articleNumber, designation);
        return components.Select(component =>
        {
            var tokens = TextNormalization.ProductTokens(component);
            var article = tokens.FirstOrDefault() ?? string.Empty;
            return new CatalogueComponentMatch(article, component, article.Length == 0 ? null : FindComponent(article, component));
        }).ToArray();
    }

    public CatalogueEntry? FindAccessory(string? articleNumber, string? designation = null)
    {
        var matches = FindComponents(articleNumber, designation);
        return matches.Count > 1 ? matches[0].Entry : null;
    }

    public CatalogueEntry? FindAccessory(string? designation) => FindAccessory(null, designation);

    private CatalogueEntry? FindComponent(string lookupArticle, string lookupDesignation)
    {
        var article = TextNormalization.ProductTokens(lookupArticle).FirstOrDefault() ?? lookupArticle;
        if (!_entries.TryGetValue(article, out var candidates) || candidates.Count == 0)
        {
            var designationTokens = TextNormalization.ProductTokens(lookupDesignation);
            article = designationTokens.FirstOrDefault() ?? article;
            if (!_entries.TryGetValue(article, out candidates) || candidates.Count == 0)
            {
                var bridgedArticle = designationTokens.Count > 1 && IsNumericCode(designationTokens[0]) ? designationTokens[1] : string.Empty;
                if (bridgedArticle.Length == 0 || !_entries.TryGetValue(bridgedArticle, out candidates) || candidates.Count == 0) return null;
                article = bridgedArticle;
            }
        }

        if (string.IsNullOrWhiteSpace(lookupDesignation)) return candidates.Count == 1 ? candidates[0] : null;
        var requested = TextNormalization.ProductTokens(lookupDesignation).ToHashSet(StringComparer.OrdinalIgnoreCase);
        requested.Remove(article);
        var discriminators = DynamicDiscriminators(candidates);
        var cohortVocabulary = candidates.SelectMany(candidate => candidate.Tokens).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasExplicitVariantRequest = requested.Any(IsExplicitVariantDiscriminator);
        var explicitUnknownDiscriminator = requested.Any(token => IsExplicitVariantDiscriminator(token) && !cohortVocabulary.Contains(token));
        var unmatchedKnownShape = requested.Any(token => LooksLikeCohortToken(token) && !cohortVocabulary.Contains(token));
        if (explicitUnknownDiscriminator || unmatchedKnownShape) return hasExplicitVariantRequest ? null : FamilyFallback(candidates);
        var requestedDiscriminators = requested.Where(discriminators.Contains).ToArray();
        if (requestedDiscriminators.Length == 0 && !hasExplicitVariantRequest)
        {
            if (candidates.Any(candidate => candidate.IsFamilyDocument)) return FamilyFallback(candidates);
            return RankCandidates(candidates, requested)[0].Candidate;
        }
        var compatible = candidates.Where(candidate => HasCompatibleDiscriminators(requested, candidate.Tokens, discriminators) &&
            (!hasExplicitVariantRequest || requested.Where(IsExplicitVariantDiscriminator).All(token => candidate.Tokens.Contains(token, StringComparer.OrdinalIgnoreCase)))).ToArray();
        if (hasExplicitVariantRequest) return compatible.Length == 1 ? compatible[0] : null;
        if (compatible.Length == 0 || compatible.Length > 1 && candidates.Any(candidate => candidate.IsFamilyDocument)) return FamilyFallback(candidates);

        var ranked = RankCandidates(compatible, requested);
        return ranked[0].Candidate;
    }

    private static IReadOnlyList<CandidateRank> RankCandidates(IReadOnlyList<CatalogueEntry> candidates, IReadOnlySet<string> requested) => candidates
        .Select(candidate => new CandidateRank(
            candidate,
            candidate.Tokens.Count(requested.Contains),
            requested.Count(token => !candidate.Tokens.Contains(token, StringComparer.OrdinalIgnoreCase)),
            candidate.Tokens.Count(token => !requested.Contains(token))))
        .OrderByDescending(item => item.Matches)
        .ThenBy(item => item.Missing)
        .ThenBy(item => item.Extra)
        .ThenBy(item => item.Candidate.SourcePdf, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static CatalogueEntry? FamilyFallback(IReadOnlyList<CatalogueEntry> candidates)
    {
        var family = candidates.Where(candidate => candidate.IsFamilyDocument).ToArray();
        if (family.Length == 0) return null;
        string Common(Func<CatalogueEntry, string> selector)
        {
            var values = family.Select(selector).Select(value => value.Trim()).ToArray();
            if (values.Any(string.IsNullOrWhiteSpace)) return string.Empty;
            var distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return distinct.Length == 1 ? distinct[0] : string.Empty;
        }

        var first = family[0];
        return first with
        {
            SourcePdf = string.Empty,
            DocumentHash = string.Empty,
            Tokens = [],
            Sku = string.Empty,
            VariantDescription = string.Empty,
            HasVariantEvidence = false,
            PowerWatts = null,
            LuminousFluxLumens = null,
            Cct = string.Empty,
            CctProvenance = string.Empty,
            Cri = Common(candidate => candidate.Cri),
            IpCode = Common(candidate => candidate.IpCode),
            IkCode = Common(candidate => candidate.IkCode),
            Voltage = Common(candidate => candidate.Voltage),
            BeamAngle = Common(candidate => candidate.BeamAngle),
            AmbientTemperature = Common(candidate => candidate.AmbientTemperature),
            Dimensions = Common(candidate => candidate.Dimensions),
            ImagePng = null,
            ImageAspect = null
        };
    }

    private static IReadOnlyList<string> Components(string? articleNumber, string? designation)
    {
        var combined = string.Join(' ', new[] { articleNumber, designation }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return combined.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static HashSet<string> DynamicDiscriminators(IReadOnlyList<CatalogueEntry> candidates)
    {
        var discriminators = candidates.SelectMany(candidate => candidate.Tokens)
            .GroupBy(token => token, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() < candidates.Count)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var token in candidates.SelectMany(candidate => candidate.Tokens).Where(IsExplicitVariantDiscriminator)) discriminators.Add(token);
        return discriminators;
    }

    private static bool HasCompatibleDiscriminators(IReadOnlySet<string> requested, IReadOnlyList<string> candidate, IReadOnlySet<string> discriminators)
    {
        var candidateSet = candidate.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return requested.Where(discriminators.Contains).All(candidateSet.Contains);
    }

    private static bool IsExplicitVariantDiscriminator(string token) =>
        Regex.IsMatch(token, @"^[789][2-6][057](?:PC)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool LooksLikeCohortToken(string token) =>
        Regex.IsMatch(token, @"^(?:LED|L|W|D|P)\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsNumericCode(string token) =>
        Regex.IsMatch(token, @"^\d{12}$", RegexOptions.CultureInvariant);

    public static CatalogueEntry Parse(string article, IReadOnlyList<string> tokens, string sourcePath, string? text)
    {
        text ??= string.Empty;
        var cct = CctParser.ExtractStrict(text);
        return new CatalogueEntry
        {
            ArticleNumber = article,
            Tokens = tokens,
            SourcePdf = sourcePath,
            PowerWatts = MatchNumber(PowerRegex(), text),
            LuminousFluxLumens = MatchTechnicalFlux(text),
            IpCode = MatchValue(LabeledIpRegex(), text) ?? MatchValue(GenericIpRegex(), text) ?? string.Empty,
            IkCode = MatchValue(LabeledIkRegex(), text) ?? MatchValue(GenericIkRegex(), text) ?? string.Empty,
            Cct = cct.HasValue ? $"{cct.Value} K" : string.Empty,
            CctProvenance = cct.HasValue ? "source-technical-single-value" : string.Empty,
            Cri = MatchTextValue(CriRegex(), text),
            Voltage = MatchLine(VoltageRegex(), text),
            BeamAngle = MatchBeamAngle(text),
            AmbientTemperature = AmbientTemperatureNormalizer.Normalize(MatchLine(TemperatureRegex(), text)),
            Dimensions = ExtractDimensions(text)
        };
    }

    public static (string Article, IReadOnlyList<string> Tokens) IdentityFromFileName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var delimiter = stem.IndexOf(" - ", StringComparison.Ordinal);
        if (delimiter >= 0) stem = stem[(delimiter + 3)..];
        var chunks = stem.Split([' ', '_', '/'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var article = chunks.FirstOrDefault()?.Trim().ToUpperInvariant() ?? string.Empty;
        return (article, chunks.Length > 1 ? TextNormalization.ProductTokens(chunks[1]) : []);
    }

    internal static IReadOnlyList<CatalogueProductDescriptor> ExtractProductDescriptors(IReadOnlyList<PdfPageContent> pages)
    {
        var descriptors = new List<CatalogueProductDescriptor>();
        foreach (var page in pages)
        {
            var rows = PdfTextExtractor.ClusterVisualRows(page.Words);
            for (var index = 0; index < rows.Count; index++)
            {
                var header = RowText(rows[index]);
                if (!Regex.IsMatch(header, @"\b(?:12NC|SKU|Order\s+code|Material(?:\s+Nr\.?)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                    !Regex.IsMatch(header, @"\b(?:Product\s*Description|Order\s+product\s+name|Product\s+name)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;

                var headerRow = rows[index];
                var productHeaderWords = headerRow.Where(word => Regex.IsMatch(word.Text, @"^(?:Product(?:Description)?|Description)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
                var marketHeader = headerRow.FirstOrDefault(word => word.Text.Equals("Markets", StringComparison.OrdinalIgnoreCase));
                if (productHeaderWords.Length == 0) continue;
                var productLeft = productHeaderWords.Min(word => word.Left);
                var productRight = productHeaderWords.Max(word => word.Right);
                var previousHeader = headerRow.Where(word => word.Right < productLeft).OrderByDescending(word => word.Right).FirstOrDefault();
                var leftBoundary = previousHeader is null ? productLeft : (previousHeader.Right + productLeft) / 2d;
                var rightBoundary = marketHeader is null ? double.PositiveInfinity : (productRight + marketHeader.Left) / 2d;
                for (var rowIndex = index + 1; rowIndex < rows.Count; rowIndex++)
                {
                    var row = rows[rowIndex];
                    var text = RowText(row);
                    var sku = SkuRegex().Match(text);
                    var product = ProductDescriptorRegex().Match(RowText(row.Where(word => word.Left >= leftBoundary && word.Left < rightBoundary)));
                    if (!sku.Success || !product.Success)
                    {
                        if (descriptors.Count > 0 && text.Length > 0 && !SkuRegex().IsMatch(text)) break;
                        continue;
                    }
                    var article = product.Groups["article"].Value.ToUpperInvariant();
                    var description = product.Groups["description"].Value.Trim();
                    descriptors.Add(new CatalogueProductDescriptor(sku.Value, article, TextNormalization.ProductTokens(description), $"{article} {description}".Trim(), page.PageNumber));
                }
            }
        }

        return descriptors
            .DistinctBy(descriptor => $"{descriptor.Sku}|{TextNormalization.ProductKey(descriptor.Description)}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsFamilyDocument((string Article, IReadOnlyList<string> Tokens) fileIdentity, IReadOnlyList<CatalogueProductDescriptor> descriptors)
    {
        if (descriptors.Count > 1) return true;
        if (descriptors.Count == 0) return false;
        var descriptor = descriptors[0];
        return !descriptor.ArticleNumber.Equals(fileIdentity.Article, StringComparison.OrdinalIgnoreCase) ||
            fileIdentity.Tokens.Any(token => !descriptor.Tokens.Contains(token, StringComparer.OrdinalIgnoreCase));
    }

    private static CatalogueEntry ParseVariantFamilyFacts(
        CatalogueEntry common,
        CatalogueProductDescriptor descriptor,
        IReadOnlyList<FamilyTechnicalColumn> columns)
    {
        var descriptorTokens = descriptor.Tokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = SelectFamilyTechnicalColumn(columns, descriptorTokens);
        var selectedFacts = selected?.Facts ?? EmptyFacts;
        if (selected is not null) common = ApplyFamilyFacts(common with { Tokens = descriptor.Tokens }, selectedFacts) with
        {
            Tokens = common.Tokens,
            Voltage = common.Voltage,
            BeamAngle = common.BeamAngle
        };

        var cctCandidates = CandidateCcts(selectedFacts);
        if (TryMapVariantCodeCct(descriptor.Tokens, cctCandidates, out var mappedCct, out var variantCode))
        {
            common = common with
            {
                Cct = $"{mappedCct} K",
                CctProvenance = $"derived-from-variant-code:{variantCode};source-supported-candidate:{mappedCct}K"
            };
        }

        var dimensionSource = selected is null ? string.Empty : FamilyDimensionSource(selected, columns);
        var dimensionAlternatives = ParseDimensionAlternatives(dimensionSource);
        if (TrySelectDimension(descriptor.Tokens, dimensionAlternatives, out var dimensions))
        {
            common = common with { Dimensions = dimensions };
        }
        return common;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyFacts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal static CatalogueEntry MapFamilyTechnicalFacts(
        CatalogueEntry common,
        CatalogueProductDescriptor descriptor,
        string header,
        IReadOnlyDictionary<string, string> facts) =>
        ParseVariantFamilyFacts(common, descriptor, [new FamilyTechnicalColumn(header, facts)]);


    private static FamilyTechnicalColumn? SelectFamilyTechnicalColumn(
        IReadOnlyList<FamilyTechnicalColumn> columns,
        IReadOnlySet<string> descriptorTokens)
    {
        if (descriptorTokens.Contains("TW"))
        {
            var matches = columns.Where(column => column.Header.Contains("Taiwan", StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        if (descriptorTokens.Contains("GM"))
        {
            var matches = columns.Where(column => column.Header.Contains("GM", StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        if (columns.Count == 1) return columns[0];

        var ranked = columns
            .Select(column => new { Column = column, Matches = TextNormalization.ProductTokens(column.Header).Count(descriptorTokens.Contains) })
            .Where(item => item.Matches > 0)
            .OrderByDescending(item => item.Matches)
            .ToArray();
        return ranked.Length > 0 && (ranked.Length == 1 || ranked[0].Matches > ranked[1].Matches) ? ranked[0].Column : null;
    }

    private static IReadOnlyList<int> CandidateCcts(IReadOnlyDictionary<string, string> facts)
    {
        var source = FactValue(facts, @"^(?:Color|Colour)\s*&\s*CCT$");
        if (source.Length == 0) return [];
        return KelvinCandidateRegex().Matches(source)
            .Select(match => LocalizedNumber.ParseNullable(match.Groups["value"].Value))
            .Where(value => value is >= 1000 and <= 50000)
            .Select(value => (int)Math.Round(value!.Value))
            .Distinct()
            .ToArray();
    }

    private static bool TryMapVariantCodeCct(
        IReadOnlyList<string> tokens,
        IReadOnlyCollection<int> candidates,
        out int kelvin,
        out string variantCode)
    {
        kelvin = 0;
        variantCode = string.Empty;
        if (candidates.Count == 0) return false;
        var matches = tokens
            .Select(token => (Token: token.ToUpperInvariant(), Kelvin: VariantCodeKelvin(token)))
            .Where(item => item.Kelvin.HasValue && candidates.Contains(item.Kelvin.Value))
            .Distinct()
            .ToArray();
        if (matches.Length != 1) return false;
        variantCode = matches[0].Token;
        kelvin = matches[0].Kelvin!.Value;
        return true;
    }

    private static int? VariantCodeKelvin(string token) => token.ToUpperInvariant() switch
    {
        "WW" => 3000,
        "NW" => 4000,
        "CW" => 6500,
        _ => null
    };

    private static string FamilyDimensionSource(
        FamilyTechnicalColumn column,
        IReadOnlyList<FamilyTechnicalColumn> columns)
    {
        var sources = columns
            .SelectMany(candidate => candidate.Facts
                .Where(item => Regex.IsMatch(item.Key, @"^Dimension(?:s|\.)?\s*(?:\(\s*mm\s*\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                    Regex.IsMatch(item.Key, @"^\d[\d.,]*\s*mm\s*/\s*\d[\d.,]*\s*mm\s*(?:\*|x|×)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .Select(item => item.Value))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var fullSources = sources.Where(value => ParseDimensionAlternatives(value).Count > 0).ToArray();
        if (fullSources.Length == 1 && sources.All(source => IsContainedDimensionSource(source, fullSources[0]))) return fullSources[0];
        return column.Facts.FirstOrDefault(item =>
            Regex.IsMatch(item.Key, @"^Dimension(?:s|\.)?\s*(?:\(\s*mm\s*\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(item.Key, @"^\d[\d.,]*\s*mm\s*/\s*\d[\d.,]*\s*mm\s*(?:\*|x|×)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).Value ?? string.Empty;
    }

    private static bool IsContainedDimensionSource(string source, string fullSource)
    {
        var sourceValues = MillimetreValueRegex().Matches(source)
            .Select(match => NormalizeMillimetres(match.Groups["value"].Value)).Where(value => value > 0).ToArray();
        var fullValues = MillimetreValueRegex().Matches(fullSource)
            .Select(match => NormalizeMillimetres(match.Groups["value"].Value)).Where(value => value > 0).ToHashSet();
        return sourceValues.Length > 0 && sourceValues.All(fullValues.Contains);
    }

    private static IReadOnlyList<FamilyDimensionAlternative> ParseDimensionAlternatives(string source)
    {
        var normalized = Regex.Replace(source, @"\s+", " ").Trim();
        if (normalized.Length == 0) return [];
        var parts = Regex.Split(normalized, @"\s*(?:\*|x|×)\s*");
        if (parts.Length == 3 && parts[2].EndsWith("mm", StringComparison.OrdinalIgnoreCase))
        {
            var lengths = DimensionNumberRegex().Matches(parts[0])
                .Select(item => NormalizeMillimetres(item.Groups["value"].Value)).Where(value => value > 0).Distinct().ToArray();
            var widths = DimensionNumberRegex().Matches(parts[1])
                .Select(item => NormalizeMillimetres(item.Groups["value"].Value)).Where(value => value > 0).Distinct().ToArray();
            var heights = DimensionNumberRegex().Matches(parts[2])
                .Select(item => NormalizeMillimetres(item.Groups["value"].Value)).Where(value => value > 0).Distinct().ToArray();
            if (widths.Length == 1 && heights.Length == 1)
            {
                return lengths.Select(length => new FamilyDimensionAlternative(length, widths[0], heights[0])).ToArray();
            }
        }
        var match = FamilyDimensionMatrixRegex().Match(normalized);
        if (!match.Success) return [];
        var matrixWidths = MillimetreValueRegex().Matches(match.Groups["widths"].Value)
            .Select(item => NormalizeMillimetres(item.Groups["value"].Value)).Where(value => value > 0).Distinct().ToArray();
        if (matrixWidths.Length != 1) return [];
        var matrixHeights = MillimetreValueRegex().Matches(match.Groups["heights"].Value)
            .Select(item => NormalizeMillimetres(item.Groups["value"].Value)).Where(value => value > 0).Distinct().ToArray();
        if (matrixHeights.Length != 1) return [];
        return MillimetreValueRegex().Matches(match.Groups["lengths"].Value)
            .Select(item => NormalizeMillimetres(item.Groups["value"].Value))
            .Where(value => value > 0)
            .Distinct()
            .Select(length => new FamilyDimensionAlternative(length, matrixWidths[0], matrixHeights[0]))
            .ToArray();
    }


    private static bool TrySelectDimension(
        IReadOnlyList<string> tokens,
        IReadOnlyList<FamilyDimensionAlternative> alternatives,
        out string dimensions)
    {
        dimensions = string.Empty;
        var lengthTokens = tokens
            .Select(token => LengthTokenRegex().Match(token))
            .Where(match => match.Success)
            .Select(match => NormalizeMillimetres(match.Groups["length"].Value))
            .Where(value => value > 0)
            .Distinct()
            .ToArray();
        if (lengthTokens.Length != 1) return false;
        var matches = alternatives.Where(item => item.LengthMillimetres == lengthTokens[0]).Distinct().ToArray();
        if (matches.Length != 1) return false;
        dimensions = $"{Format(matches[0].LengthMillimetres)}x{Format(matches[0].WidthMillimetres)}x{Format(matches[0].HeightMillimetres)}mm";
        return true;
    }

    private static double NormalizeMillimetres(string value) => LocalizedNumber.ParseNullable(value) ?? 0;

    private static string FactValue(IReadOnlyDictionary<string, string> facts, string pattern) =>
        facts.FirstOrDefault(item => Regex.IsMatch(item.Key, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).Value ?? string.Empty;

    private static CatalogueEntry ParseCommonFamilyFacts(string article, IReadOnlyList<string> tokens, string sourcePath, IReadOnlyList<FamilyTechnicalColumn> columns)
    {
        var common = new CatalogueEntry { ArticleNumber = article, Tokens = tokens, SourcePdf = sourcePath };
        if (columns.Count == 0) return common;
        var commonFacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in columns.SelectMany(column => column.Facts.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var values = columns
                .Select(column => column.Facts.GetValueOrDefault(label, string.Empty))
                .Select(value => string.IsNullOrWhiteSpace(value)
                    ? string.Empty
                    : Regex.Replace(value.Trim(), @"\s+", " "))
                .ToArray();
            if (values.All(value => value.Length > 0) &&
                values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            {
                commonFacts[label] = values[0];
            }
        }
        return ApplyFamilyFacts(common, commonFacts) with { PowerWatts = null, LuminousFluxLumens = null };
    }

    private static IReadOnlyList<FamilyTechnicalSection> ExtractFamilyTechnicalSections(
        string fileArticle,
        IReadOnlyList<CatalogueProductDescriptor> descriptors,
        IReadOnlyList<PdfPageContent> pages)
    {
        var descriptorArticles = descriptors.Select(descriptor => descriptor.ArticleNumber).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sections = descriptorArticles.SelectMany(article => ExtractFamilyTechnicalSections(article, pages)).ToList();
        if (sections.Count == 0 && !string.IsNullOrWhiteSpace(fileArticle))
        {
            var fileSections = ExtractFamilyTechnicalSections(fileArticle, pages);
            if (fileSections.Count > 0)
            {
                sections.AddRange(descriptorArticles.Length == 1
                    ? fileSections.Select(section => section with { Article = descriptorArticles[0] })
                    : fileSections);
            }
        }
        return sections;
    }

    private static FamilyTechnicalSection? SelectFamilyTechnicalSection(
        CatalogueProductDescriptor descriptor,
        IReadOnlyList<FamilyTechnicalSection> sections)
    {
        var candidates = sections.Where(section => section.Article.Equals(descriptor.ArticleNumber, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0) return null;
        var samePage = candidates.Where(section => section.PageNumber == descriptor.PageNumber).ToArray();
        if (samePage.Length == 1) return samePage[0];
        if (samePage.Length > 1) return null;
        var preceding = candidates.Where(section => section.PageNumber < descriptor.PageNumber).OrderByDescending(section => section.PageNumber).ToArray();
        if (preceding.Length > 0) return preceding[0];
        var following = candidates.Where(section => section.PageNumber > descriptor.PageNumber).OrderBy(section => section.PageNumber).ToArray();
        return following.Length > 0 ? following[0] : null;
    }

    private static IReadOnlyList<FamilyTechnicalSection> ExtractFamilyTechnicalSections(string article, IReadOnlyList<PdfPageContent> pages)
    {
        var sections = new List<FamilyTechnicalSection>();
        foreach (var page in pages)
        {
            var rows = PdfTextExtractor.ClusterVisualRows(page.Words);
            bool IsTechnicalHeader((IReadOnlyList<PositionedWord> Row, int Index) item)
            {
                if (item.Row.Any(word => word.Text.Equals("GM", StringComparison.OrdinalIgnoreCase) ||
                    word.Text.Equals("Taiwan", StringComparison.OrdinalIgnoreCase) ||
                    word.Text.Equals("China", StringComparison.OrdinalIgnoreCase))) return true;
                if (item.Index > 0 && RowText(rows[item.Index - 1]).Contains("Specification", StringComparison.OrdinalIgnoreCase)) return true;
                return item.Index + 1 < rows.Count && !SkuRegex().IsMatch(RowText(rows[item.Index + 1])) &&
                    rows[item.Index + 1].Any(word => word.Text.Equals("GM", StringComparison.OrdinalIgnoreCase) ||
                        word.Text.Equals("Taiwan", StringComparison.OrdinalIgnoreCase));
            }
            var header = rows.Select((row, index) => (Row: row, Index: index))
                .FirstOrDefault(item => Regex.IsMatch(RowText(item.Row), $@"^\s*{Regex.Escape(article)}\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                    IsTechnicalHeader(item));
            if (header.Row is null)
            {
                header = rows.Select((row, index) => (Row: row, Index: index))
                    .FirstOrDefault(item => item.Row.Any(word => word.Text.Equals(article, StringComparison.OrdinalIgnoreCase)) &&
                        !SkuRegex().IsMatch(RowText(item.Row)) &&
                        !RowText(item.Row).Contains("Specification", StringComparison.OrdinalIgnoreCase) &&
                        IsTechnicalHeader(item));
            }
            if (header.Row is null || header.Index + 1 >= rows.Count) continue;
            var hasInlineColumnHeaders = header.Row.Any(word => word.Left > 120 && !word.Text.Equals(article, StringComparison.OrdinalIgnoreCase));
            var headerRowIndex = hasInlineColumnHeaders ? header.Index : header.Index + 1;
            while (headerRowIndex < rows.Count && RowText(rows[headerRowIndex]).Contains("Specification", StringComparison.OrdinalIgnoreCase)) headerRowIndex++;
            if (headerRowIndex + 1 >= rows.Count) continue;
            var headerWords = rows[headerRowIndex].Where(word => word.Left > 120).OrderBy(word => word.Left).ToArray();
            var labelStartIndex = headerRowIndex + 1;
            if (!hasInlineColumnHeaders && headerWords.All(word => SkuRegex().IsMatch(word.Text)))
            {
                headerRowIndex++;
                if (headerRowIndex + 1 >= rows.Count) continue;
                headerWords = rows[headerRowIndex].OrderBy(word => word.Left).ToArray();
                labelStartIndex = headerRowIndex + 1;
            }
            else if (hasInlineColumnHeaders && headerWords.All(word => SkuRegex().IsMatch(word.Text)))
            {
                var marketHeaderIndex = headerRowIndex + 1;
                if (marketHeaderIndex + 1 >= rows.Count) continue;
                headerWords = rows[marketHeaderIndex].OrderBy(word => word.Left).ToArray();
                labelStartIndex = marketHeaderIndex + 1;
            }
            if (headerWords.Length == 0) continue;
            var headerGroups = new List<List<PositionedWord>>();
            if (headerWords.Any(word => word.Text.Equals("Taiwan", StringComparison.OrdinalIgnoreCase)) &&
                headerWords.Any(word => word.Text.Equals("GM", StringComparison.OrdinalIgnoreCase)))
            {
                var taiwan = headerWords.First(word => word.Text.Equals("Taiwan", StringComparison.OrdinalIgnoreCase));
                var gm = headerWords.First(word => word.Text.Equals("GM", StringComparison.OrdinalIgnoreCase));
                headerGroups.Add(headerWords.Where(word => word.Left < (taiwan.Left + gm.Left) / 2d).ToList());
                headerGroups.Add(headerWords.Where(word => word.Left >= (taiwan.Left + gm.Left) / 2d).ToList());
            }
            else foreach (var word in headerWords)
            {
                if (headerGroups.Count == 0 || word.Left - headerGroups[^1][^1].Right > 24) headerGroups.Add([word]);
                else headerGroups[^1].Add(word);
            }
            var starts = headerGroups.Select(group => group[0].Left).ToArray();
            var facts = starts.Select(_ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)).ToArray();
            var labelRows = rows.Skip(labelStartIndex)
                .Select(row => new { Row = row, Label = RowText(row.Where(word => word.Left < starts[0])), Centre = row.Average(word => (word.Top + word.Bottom) / 2d) })
                .Where(item => item.Label.Length > 0).ToList();
            var dimensionRow = rows.Select((row, index) => new { Row = row, Index = index, Text = RowText(row) })
                .FirstOrDefault(item => Regex.IsMatch(item.Text, @"^\d[\d.,]*\s*mm\s*/\s*\d[\d.,]*\s*mm\s*(?:\*|x|×)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            if (dimensionRow is not null && labelRows.All(item => !item.Label.StartsWith("Dimension", StringComparison.OrdinalIgnoreCase)))
            {
                labelRows.Add(new { Row = dimensionRow.Row, Label = "Dimension. (mm)", Centre = dimensionRow.Row.Average(word => (word.Top + word.Bottom) / 2d) });
                labelRows.Sort((left, right) => right.Centre.CompareTo(left.Centre));
            }
            var pageTop = headerWords.Average(word => (word.Top + word.Bottom) / 2d);
            for (var labelIndex = 0; labelIndex < labelRows.Count; labelIndex++)
            {
                var label = labelRows[labelIndex];
                var upper = labelIndex == 0 ? pageTop : (labelRows[labelIndex - 1].Centre + label.Centre) / 2d;
                var lower = labelIndex == labelRows.Count - 1 ? double.NegativeInfinity : (label.Centre + labelRows[labelIndex + 1].Centre) / 2d;
                for (var column = 0; column < starts.Length; column++)
                {
                    var value = RowText(page.Words.Where(word =>
                    {
                        var centre = (word.Top + word.Bottom) / 2d;
                        return centre <= upper && centre > lower && word.Left >= starts[column] && (column == starts.Length - 1 || word.Left < starts[column + 1]);
                    }));
                    if (value.Length > 0) facts[column][label.Label] = value;
                }
                if (label.Label.Equals("Dimension. (mm)", StringComparison.OrdinalIgnoreCase) &&
                    !facts.Any(fact => fact.ContainsKey(label.Label)))
                {
                    var value = RowText(label.Row);
                    if (value.Length > 0)
                    {
                        foreach (var fact in facts) fact[label.Label] = value;
                    }
                }
            }
            if (dimensionRow is not null)
            {
                var dimension = RowText(dimensionRow.Row);
                if (ParseDimensionAlternatives(dimension).Count > 0)
                {
                    foreach (var fact in facts) fact["Dimension. (mm)"] = dimension;
                }
            }
            sections.Add(new FamilyTechnicalSection(article, page.PageNumber,
                starts.Select((_, index) => new FamilyTechnicalColumn(RowText(headerGroups[index]), facts[index])).ToArray()));
        }
        return sections;
    }

    private static CatalogueEntry ApplyFamilyFacts(CatalogueEntry entry, IReadOnlyDictionary<string, string> facts)
    {
        string Fact(string pattern) => facts.FirstOrDefault(item => Regex.IsMatch(item.Key, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).Value ?? string.Empty;
        var cri = Fact(@"^CRI$");
        var ip = Fact(@"^IP\s+rating$");
        var ik = Fact(@"^(?:Mechanical\s+impact(?:\s+(?:protection\s+)?code)?|IK\s+rating)$");
        var voltage = Fact(@"^Input\s+Voltage$");
        var ambient = Fact(@"^Operating\s+temperature$");
        var photometric = Fact(@"^Photometric$");
        var wattage = Fact(@"^System\s+Wattage$");
        var output = Fact(@"^System\s+output$");
        return entry with
        {
            Cri = Regex.Match(cri, @"(?:>|≥)?\s*\d{2,3}").Value.Trim(),
            IpCode = Regex.Match(ip, @"IP\s*\d{2}[A-Z]?").Value.Replace(" ", string.Empty),
            IkCode = Regex.Match(ik, @"IK\s*\d{2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Value.Replace(" ", string.Empty),
            Voltage = voltage.Length > 0 ? $"Input voltage {voltage}" : string.Empty,
            AmbientTemperature = AmbientTemperatureNormalizer.Normalize(ambient),
            BeamAngle = Regex.Match(photometric, @"\d[\d.,]*\s*(?:°|deg(?:ree)?s?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Value,
            PowerWatts = SingleValueForLength(wattage, entry.Tokens, WattValueRegex()) ?? entry.PowerWatts,
            LuminousFluxLumens = SingleValueForLength(output, entry.Tokens, FluxValueRegex()) ?? entry.LuminousFluxLumens
        };
    }

    private static double? SingleValueForLength(string value, IReadOnlyList<string> tokens, Regex regex)
    {
        if (value.Length == 0) return null;
        var lengthTokens = tokens.Select(token => LengthTokenRegex().Match(token)).Where(match => match.Success).ToArray();
        if (lengthTokens.Length != 1) return null;
        var millimetres = NormalizeMillimetres(lengthTokens[0].Groups["length"].Value);
        if (millimetres <= 0) return null;
        var metres = millimetres / 1000d;
        var markerText = Math.Abs(metres - Math.Round(metres)) < .0001
            ? ((int)Math.Round(metres)).ToString(CultureInfo.InvariantCulture)
            : metres.ToString("0.0", CultureInfo.InvariantCulture);
        var marker = Regex.Match(value, $@"(?:^|\s){Regex.Escape(markerText)}m:\s*(?<segment>.*?)(?=(?:\s+\d+(?:\.\d+)?m:)|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!marker.Success) return null;
        var matches = regex.Matches(marker.Groups["segment"].Value)
            .Select(match => LocalizedNumber.ParseNullable(match.Groups["value"].Value)).Where(number => number.HasValue).Select(number => number!.Value).Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string RowText(IEnumerable<PositionedWord> row) =>
        string.Join(' ', row.OrderBy(word => word.Left).Select(word => word.Text.Trim()).Where(value => value.Length > 0));

    private static string ExtractDimensions(string? text)
    {
        text ??= string.Empty;
        var diameter = MatchNumber(DiameterRegex(), text);
        var height = MatchNumber(HeightRegex(), text);
        if (diameter.HasValue && height.HasValue) return $"D {Format(diameter.Value)} mm x H {Format(height.Value)} mm";
        var hwd = HwdRegex().Match(text);
        if (hwd.Success) return $"L {hwd.Groups["depth"].Value} mm x W {hwd.Groups["width"].Value} mm x H {hwd.Groups["height"].Value} mm";
        var labeled = LabeledDimensionsRegex().Match(text);
        if (labeled.Success) return $"L {labeled.Groups["length"].Value} mm x W {labeled.Groups["width"].Value} mm x H {labeled.Groups["height"].Value} mm";
        var length = MatchNumber(LengthRegex(), text);
        var width = MatchNumber(WidthRegex(), text);
        return length.HasValue && width.HasValue && height.HasValue
            ? $"L {Format(length.Value)} mm x W {Format(width.Value)} mm x H {Format(height.Value)} mm"
            : string.Empty;
    }

    private static string? MatchValue(Regex regex, string text)
    {
        var match = regex.Match(text ?? string.Empty);
        return match.Success ? Regex.Replace(match.Groups["value"].Value.Trim().ToUpperInvariant(), @"\s+", string.Empty) : null;
    }

    private static string MatchTextValue(Regex regex, string text)
    {
        var match = regex.Match(text ?? string.Empty);
        return match.Success ? Regex.Replace(match.Groups["value"].Value.Trim(), @"\s+", string.Empty) : string.Empty;
    }

    private static string MatchBeamAngle(string text)
    {
        foreach (var rawLine in (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            var match = BeamAngleRegex().Match(line);
            if (!match.Success) continue;
            return Regex.Replace(match.Groups["value"].Value.Trim(), @"\s+", " ");
        }

        return string.Empty;
    }

    private static string MatchLine(Regex regex, string text)
    {
        var match = regex.Match(text ?? string.Empty);
        return match.Success ? Regex.Replace(match.Value.Trim(), @"\s+", " ") : string.Empty;
    }

    private static double? MatchNumber(Regex regex, string text)
    {
        var match = regex.Match(text ?? string.Empty);
        return match.Success ? LocalizedNumber.ParseNullable(match.Groups["value"].Value) : null;
    }

    private static double? MatchTechnicalFlux(string text)
    {
        foreach (var rawLine in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!FluxLabelRegex().IsMatch(line) || FluxMarketingRegex().IsMatch(line) || LumenValueRegex().Matches(line).Count != 1) continue;
            var match = FluxRegex().Match(line);
            if (match.Success) return LocalizedNumber.ParseNullable(match.Groups["value"].Value);
        }

        return null;
    }

    private static string Format(double value) => Math.Abs(value - Math.Round(value)) < 0.000001 ? Math.Round(value).ToString() : value.ToString("0.##");

    private sealed record CandidateRank(CatalogueEntry Candidate, int Matches, int Missing, int Extra);
    private sealed record DescriptorTechnicalSection(CatalogueProductDescriptor Descriptor, FamilyTechnicalSection? Section);
    private sealed record FamilyTechnicalSection(string Article, int PageNumber, IReadOnlyList<FamilyTechnicalColumn> Columns);
    private sealed record FamilyTechnicalColumn(string Header, IReadOnlyDictionary<string, string> Facts);
    private sealed record FamilyDimensionAlternative(double LengthMillimetres, double WidthMillimetres, double HeightMillimetres);

    [GeneratedRegex(@"(?<!\d)\d{12}(?!\d)", RegexOptions.CultureInvariant)] private static partial Regex SkuRegex();
    [GeneratedRegex(@"(?<value>\d[\d.,]*)\s*(?:°\s*)?K\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex KelvinCandidateRegex();
    [GeneratedRegex(@"^L(?<length>\d{3,5})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LengthTokenRegex();
    [GeneratedRegex(@"^(?<lengths>\d[\d.,]*(?:\s*mm)?(?:\s*/\s*\d[\d.,]*(?:\s*mm)?)*)(?:\s+\d[\d.,]*\s*mm(?:\s*/\s*\d[\d.,]*\s*mm)*)?\s*[*x×]\s*(?<widths>\d[\d.,]*\s*mm(?:\s*/\s*\d[\d.,]*\s*mm)*)\s*[*x×]\s*(?<heights>\d[\d.,]*\s*mm(?:\s*/\s*\d[\d.,]*\s*mm)*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex FamilyDimensionMatrixRegex();
    [GeneratedRegex(@"(?<value>\d[\d.,]*)", RegexOptions.CultureInvariant)] private static partial Regex DimensionNumberRegex();
    [GeneratedRegex(@"(?<value>\d[\d.,]*)\s*mm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex MillimetreValueRegex();
    [GeneratedRegex(@"(?<value>\d[\d.,]*)\s*lm\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex FluxValueRegex();
    [GeneratedRegex(@"(?<value>\d[\d.,]*)\s*W\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex WattValueRegex();
    [GeneratedRegex(@"\b(?<article>[A-Z]{1,6}\d{2,5}[A-Z]?)\s+(?<description>LED\d+[A-Z]?(?:[\s_/-]+[A-Z0-9]+)+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ProductDescriptorRegex();
    [GeneratedRegex(@"(?:Quang\s+thông|Luminous\s+flux|Light\s+output|Lumen\s+output|Total\s+flux)(?:\s*\([^)]*\))?[^\r\n\d]*(?<value>\d[\d.,]*)\s*lm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex FluxRegex();
    [GeneratedRegex(@"(?:Quang\s+thông|Luminous\s+flux|Light\s+output|Lumen\s+output|Total\s+flux)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex FluxLabelRegex();
    [GeneratedRegex(@"(?:\bfrom\b|\bto\b|\brange\b|\bavailable\b|\boptions?\b|\btừ\b|\bđến\b|dung\s+sai)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex FluxMarketingRegex();
    [GeneratedRegex(@"(?<![A-Za-z])\d[\d.,]*\s*lm(?!\s*/\s*W)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LumenValueRegex();
    [GeneratedRegex(@"(?:Mức\s+tiêu\s+thụ\s+điện|Công\s+suất(?:\s+tiêu\s+thụ)?|Power\s+consumption|Rated\s+power|System\s+power|Wattage|Input\s+power)(?:\s*\([^)]*\))?[^\r\n\d]*(?<value>\d[\d.,]*)\s*W", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex PowerRegex();
    [GeneratedRegex(@"(?:Ingress\s+protection(?:\s+code)?|Mức\s+bảo\s+vệ|Mã\s+bảo\s+vệ\s+chống\s+xâm\s+nhập)[^\r\n]*?(?<value>IP\s*\d{2}[A-Z]?(?:\s*/\s*(?:IP\s*)?\d{2}[A-Z]?)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LabeledIpRegex();
    [GeneratedRegex(@"\b(?<value>IP\s*\d{2}[A-Z]?(?:\s*/\s*(?:IP\s*)?\d{2}[A-Z]?)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex GenericIpRegex();
    [GeneratedRegex(@"(?:Mechanical\s+impact(?:\s+(?:protection\s+)?code)?|Tác\s+động\s+cơ\s+học|Mã\s+bảo\s+vệ\s+khỏi\s+tác\s+động\s+cơ\s+học)[^\r\n]*?(?<value>IK\s*\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LabeledIkRegex();
    [GeneratedRegex(@"\b(?<value>IK\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex GenericIkRegex();
    [GeneratedRegex(@"(?:Chỉ\s+số\s+hoàn\s+màu|Colou?r\s+rendering\s+index|\bCRI\b|\bRa\b)[^\r\n\d>≥]*(?<value>(?:>|≥)?\s*\d{2,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CriRegex();
    [GeneratedRegex(@"(?:(?:Input|Operating|Supply|Mains)\s+voltage|Điện\s+áp(?:\s+(?:đầu\s+vào|hoạt\s+động|vận\s+hành|nguồn))?)[^\r\n]*?\d[^\r\n]*?V(?:\s*(?:AC|DC))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex VoltageRegex();
    [GeneratedRegex(@"(?:Beam\s+angle(?:\s+of\s+light\s+source)?|Optical\s+beam\s+angle|Góc\s+(?:chiếu|chùm\s+tia))\s*(?:\([^\r\n)]*\))?\s*(?:[:=]|\-|–|—)?\s*(?<value>(?:\d[\d.,]*\s*(?:°|deg(?:ree)?s?|độ))(?:\s*(?:to|đến|[-–—])\s*\d[\d.,]*\s*(?:°|deg(?:ree)?s?|độ))?)\s*(?:$|[;,.]\s*$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex BeamAngleRegex();
    [GeneratedRegex(@"(?:Nhiệt\s+độ\s+môi\s+trường\s+hiệu\s+quả|Phạm\s+vi\s+nhiệt\s+độ\s+môi\s+trường\s+xung\s+quanh|Nhiệt\s+độ\s+môi\s+trường(?:\s+cho\s+phép)?|Nhiệt\s+độ\s+xung\s+quanh|(?:Nhiệt\s+độ\s+)?hiệu\s+quả|Operating\s+temperature|Ambient\s+temperature(?:\s+range)?)[^\r\n]*?°\s*C(?:\s*(?:to|đến|–|—|-)\s*[+-]?\d[\d.,]*\s*°\s*C)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex TemperatureRegex();
    [GeneratedRegex(@"(?:Overall\s+diameter|Đường\s+kính)[^\r\n\d]*(?<value>\d[\d.,]*)\s*mm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex DiameterRegex();
    [GeneratedRegex(@"(?:Overall\s+height|Chiều\s+cao)[^\r\n\d]*(?<value>\d[\d.,]*)\s*mm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex HeightRegex();
    [GeneratedRegex(@"(?:Overall\s+length|Chiều\s+dài)[^\r\n\d]*(?<value>\d[\d.,]*)\s*mm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LengthRegex();
    [GeneratedRegex(@"(?:Overall\s+width|Chiều\s+rộng)[^\r\n\d]*(?<value>\d[\d.,]*)\s*mm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex WidthRegex();
    [GeneratedRegex(@"(?:Height\s*[x×]\s*Width\s*[x×]\s*Depth|Cao\s*[x×]\s*Rộng\s*[x×]\s*Sâu)[^\r\n\d]*(?<height>\d[\d.,]*)\s*[x×]\s*(?<width>\d[\d.,]*)\s*[x×]\s*(?<depth>\d[\d.,]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex HwdRegex();
    [GeneratedRegex(@"(?:Dimensions?|Kích\s+thước)[^\r\n\d]*(?<length>\d[\d.,]*)\s*[x×]\s*(?<width>\d[\d.,]*)\s*[x×]\s*(?<height>\d[\d.,]*)\s*mm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LabeledDimensionsRegex();
}

public sealed record CatalogueComponentMatch(string ArticleNumber, string Designation, CatalogueEntry? Entry);

public sealed record CatalogueDocument
{
    public string SourcePdf { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public (string Article, IReadOnlyList<string> Tokens) FileIdentity { get; init; }
    public IReadOnlyList<CatalogueProductDescriptor> ProductDescriptors { get; init; } = [];
    public bool IsFamilyDocument { get; init; }
}

public sealed record CatalogueProductDescriptor(string Sku, string ArticleNumber, IReadOnlyList<string> Tokens, string Description, int PageNumber);

public sealed record CatalogueEntry
{
    public string ArticleNumber { get; init; } = string.Empty;
    public string Sku { get; init; } = string.Empty;
    public string VariantDescription { get; init; } = string.Empty;
    public double? PowerWatts { get; init; }
    public double? LuminousFluxLumens { get; init; }
    public string IpCode { get; init; } = string.Empty;
    public string IkCode { get; init; } = string.Empty;
    public string Cct { get; init; } = string.Empty;
    public string CctProvenance { get; init; } = string.Empty;
    public string Cri { get; init; } = string.Empty;
    public string Voltage { get; init; } = string.Empty;
    public string BeamAngle { get; init; } = string.Empty;
    public string AmbientTemperature { get; init; } = string.Empty;
    public string Dimensions { get; init; } = string.Empty;
    public byte[]? ImagePng { get; init; }
    public double? ImageAspect { get; init; }
    public string SourcePdf { get; init; } = string.Empty;
    public string DocumentHash { get; init; } = string.Empty;
    public IReadOnlyList<string> Tokens { get; init; } = [];
    public bool IsFamilyDocument { get; init; }
    public bool HasVariantEvidence { get; init; }
}
