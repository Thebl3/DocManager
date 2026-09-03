using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocManager.Core;

namespace DocManager.Signify;

public sealed partial class SignifyClient
{
    private const string SearchUrl = "https://api.microservices.signify.com/api/product/v1/smc/{0}/search";
    private const string DirectPdfUrl = "https://www.assets.signify.com/is/content/Signify/{0}_EU.{1}.PROF.FP";
    private const string ConfiguratorIesUrl = "https://api.microservices.signify.com/api/configurator/v2/getPhotometricAssets/ies?id={0}&locale=en_AA";
    private const string DirectIesUrl = "https://www.docs.signify.com/assets/IES/{0}.ies";
    private const string AemDownloadAssetsUrl = "https://www.signify.com/content/signify-multibrand/{0}/{1}/product/product-detail-page/jcr:content.download-assets.{2}_EU.json";
    private static readonly string[] AemMarketLangSegments = ["ae/en", "sa/en"];
    private readonly HttpClient _http;

    public SignifyClient(HttpClient httpClient) => _http = httpClient;

    public async Task<ProductMatch?> FindProductAsync(ProductRecord record, DownloadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(options);

        // Direct Signify assets are keyed by the complete numeric Material, not by
        // digits embedded in an alphanumeric product code.
        var strictNumericMaterial = StrictNumericMaterial(record.Material);
        string directPdf = string.Empty;
        string directLocale = string.Empty;
        if (strictNumericMaterial.Length > 0)
        {
            foreach (var locale in new[] { options.Locale, "en_AA" }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var url = string.Format(DirectPdfUrl, strictNumericMaterial, locale);
                if (!await PdfExistsAsync(url, options, cancellationToken)) continue;
                directPdf = url;
                directLocale = locale;
                break;
            }
        }

        var exactMatches = new List<(SignifySearchCandidate Candidate, string Query)>();
        foreach (var query in UniqueQueries(record))
        {
            var candidates = await SearchAsync(query, options, cancellationToken);
            foreach (var candidate in candidates)
            {
                var scored = candidate with { Score = SignifyScoring.Score(record, candidate) };
                if (IsSameExactProduct(record, scored, strictNumericMaterial)) exactMatches.Add((scored, query));
            }
        }

        var exactApiQueries = SelectExactApiMatches(exactMatches, strictNumericMaterial);
        var selectedExactQueries = exactApiQueries.Select(item => item.Query)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var exactApiCandidates = exactApiQueries.Select(item => item.Candidate).ToArray();
        var exactApi = MergeExactCandidates(exactApiCandidates);

        if (directPdf.Length > 0)
        {
            // A direct PDF is proven only for strictNumericMaterial. Only a primary
            // API SKU equal to it may supply IES; a product-code alias is not proof
            // that a different primary SKU owns the photometric data.
            SignifySearchCandidate? primaryExactApi = null;
            foreach (var candidate in exactApiCandidates.Where(candidate =>
                         candidate.Sku.Trim().Equals(strictNumericMaterial, StringComparison.Ordinal)))
            {
                primaryExactApi = primaryExactApi is null
                    ? candidate
                    : MergeExactCandidate(primaryExactApi, candidate);
            }
            var directIesUrl = await DiscoverIesAsync(
                strictNumericMaterial,
                record.MaterialDescription,
                primaryExactApi?.IesUrl ?? string.Empty,
                options,
                cancellationToken);
            // Only exact aliases of the requested numeric material can back up a
            // direct PDF. This keeps an unrelated description match from becoming
            // a cross-variant download fallback.
            var fallbackLeaflets = exactApiQueries
                .Where(item => CandidateHasSku(item.Candidate, strictNumericMaterial))
                .OrderByDescending(item => item.Candidate.Score)
                .ThenBy(item => item.Query, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Candidate.LeafletUrl)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new ProductMatch
            {
                QueryUsed = exactApi is null
                    ? $"direct:{strictNumericMaterial}:{directLocale}"
                    : $"direct:{strictNumericMaterial}:{directLocale};api:{string.Join("|", selectedExactQueries)}",
                MatchedSku = strictNumericMaterial,
                MatchedName = exactApi is null ? record.MaterialDescription : CandidateName(exactApi),
                ProductUrl = exactApi?.ProductUrl ?? string.Empty,
                LeafletUrl = directPdf,
                LeafletUrlCandidates = fallbackLeaflets,
                IesUrl = directIesUrl,
                Score = 2000,
                IsExact = true
            };
        }

        if (exactApi is not null)
        {
            var exactIesUrl = await DiscoverIesAsync(
                strictNumericMaterial.Length > 0 ? strictNumericMaterial : exactApi.Sku,
                record.MaterialDescription,
                exactApi.IesUrl,
                options,
                cancellationToken);
            return new ProductMatch
            {
                QueryUsed = string.Join("|", selectedExactQueries),
                MatchedSku = exactApi.Sku,
                MatchedName = CandidateName(exactApi),
                ProductUrl = exactApi.ProductUrl,
                LeafletUrl = exactApi.LeafletUrl,
                IesUrl = exactIesUrl,
                Score = exactApi.Score,
                IsExact = true
            };
        }

        return null;
    }

    public async Task<IReadOnlyList<SignifySearchCandidate>> SearchAsync(string query, DownloadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var results = new List<SignifySearchCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var page = 1;
        var maximumPages = Math.Clamp(options.MaxSearchPages, 1, 100);
        for (var requestNumber = 0; requestNumber < maximumPages; requestNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var separator = requestNumber == 0 ? string.Empty : $"&page={page}";
            var url = $"{string.Format(SearchUrl, Uri.EscapeDataString(options.Locale))}?query={Uri.EscapeDataString(query)}{separator}";
            using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), options, cancellationToken);
            response.EnsureSuccessStatusCode();
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                foreach (var candidate in SignifyScoring.ParseResults(document))
                {
                    var key = $"{candidate.Sku}{candidate.Name}{candidate.LeafletUrl}";
                    if (seen.Add(key)) results.Add(candidate);
                }

                var next = SignifyScoring.NextPage(document, page);
                if (next is null) break;
                page = next.Value;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Phản hồi tìm kiếm Signify không phải JSON hợp lệ.", exception);
            }
        }
        return results;
    }

    public async Task<string> DownloadAsync(string url, string destinationPath, DownloadKind kind, DownloadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullDestination = Path.GetFullPath(destinationPath);
        if (!options.Force && File.Exists(fullDestination))
        {
            if (DownloadValidation.IsValidFile(fullDestination, null, kind)) return "exists";
            throw new InvalidDataException($"Tệp {kind} hiện có không hợp lệ: {fullDestination}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporary = AtomicFile.CreateSiblingTemporaryPath(fullDestination);
        try
        {
            using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), options, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }
            if (!DownloadValidation.IsValidFile(temporary, response.Content.Headers.ContentType?.MediaType, kind))
                throw new InvalidDataException($"Nội dung tải về không phải {kind} hợp lệ.");
            AtomicFile.Replace(temporary, fullDestination);
            return "downloaded";
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string PdfTarget(ProductRecord record, ProductMatch match, string outputRoot)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(match);
        var family = WindowsFileName.Sanitize(record.ProductFamily);
        var name = ProductAssetName.FromVerifiedMatch(match.MatchedName, record.MaterialDescription);
        return Path.Combine(Path.GetFullPath(outputRoot), family, $"{name}.pdf");
    }

    public string IesTarget(ProductRecord record, ProductMatch match, string outputRoot)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(match);
        var family = WindowsFileName.Sanitize(record.ProductFamily);
        var name = ProductAssetName.FromVerifiedMatch(match.MatchedName, record.MaterialDescription);
        return Path.Combine(Path.GetFullPath(outputRoot), family, "IES", $"{name}.ies");
    }

    private async Task<string?> TryResolveIesFromDownloadCenterAsync(string ctn, DownloadOptions options, CancellationToken cancellationToken)
    {
        foreach (var segment in AemMarketLangSegments)
        {
            try
            {
                var parts = segment.Split('/');
                if (parts.Length != 2) continue;
                var market = parts[0];
                var lang = parts[1];
                var url = string.Format(AemDownloadAssetsUrl, market, lang, Uri.EscapeDataString(ctn));
                using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), options, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var iesUrl = ExtractIesUrlFromAemJson(document);
                if (!string.IsNullOrWhiteSpace(iesUrl)) return iesUrl;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { }
        }
        return null;
    }

    private static string? ExtractIesUrlFromAemJson(JsonDocument document)
    {
        try
        {
            return ExtractIesUrlRecursive(document.RootElement, 0);
        }
        catch { }
        return null;
    }

    private static string? ExtractIesUrlRecursive(JsonElement element, int depth)
    {
        const int MaxDepth = 32;
        if (depth > MaxDepth) return null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            // Check if this object has type == "ies"
            if (element.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
            {
                var typeValue = typeElement.GetString() ?? string.Empty;
                if (typeValue.Equals("ies", StringComparison.OrdinalIgnoreCase))
                {
                    // Prefer downloadUrl, fallback to url
                    if (element.TryGetProperty("downloadUrl", out var downloadUrl) && downloadUrl.ValueKind == JsonValueKind.String)
                    {
                        var url = ResolveIesUrl(downloadUrl.GetString());
                        if (url is not null) return url;
                    }
                    if (element.TryGetProperty("url", out var urlProperty) && urlProperty.ValueKind == JsonValueKind.String)
                    {
                        var url = ResolveIesUrl(urlProperty.GetString());
                        if (url is not null) return url;
                    }
                }
            }

            // Recurse into all properties
            foreach (var property in element.EnumerateObject())
            {
                var result = ExtractIesUrlRecursive(property.Value, depth + 1);
                if (result is not null) return result;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            // Recurse into all array elements
            foreach (var item in element.EnumerateArray())
            {
                var result = ExtractIesUrlRecursive(item, depth + 1);
                if (result is not null) return result;
            }
        }

        return null;
    }

    private static string? ResolveIesUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Check if it's already an absolute URI
        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
        {
            // Only accept http and https schemes
            if (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps)
                return absoluteUri.ToString();
            return null;
        }

        // Try to resolve as relative URL against Signify base
        try
        {
            var baseUri = new Uri("https://www.signify.com");
            var resolvedUri = new Uri(baseUri, value);
            if (resolvedUri.Scheme == Uri.UriSchemeHttp || resolvedUri.Scheme == Uri.UriSchemeHttps)
                return resolvedUri.ToString();
        }
        catch
        {
        }

        return null;
    }

    private async Task<bool> PdfExistsAsync(string url, DownloadOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Head, url), options, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK) return false;

            // A HEAD success is insufficient evidence for an asset URL. Validate a
            // bounded GET response before selecting it, while leaving the actual
            // download to CatalogueDownloadService so alternatives stay independent.
            using var contentResponse = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), options, cancellationToken);
            if (!contentResponse.IsSuccessStatusCode) return false;
            var bytes = await ReadValidationPrefixAsync(contentResponse.Content, cancellationToken);
            return DownloadValidation.IsValid(bytes, contentResponse.Content.Headers.ContentType?.MediaType, DownloadKind.Pdf);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // A timeout from HttpClient is not a caller cancellation. Treat the direct
        // source as unavailable and continue with normal API discovery.
        catch (TaskCanceledException) { return false; }
        catch (HttpRequestException) { return false; }
    }

    private async Task<string> DiscoverIesAsync(
        string sku,
        string description,
        string supplied,
        DownloadOptions options,
        CancellationToken cancellationToken)
    {
        var strictNumericSku = StrictNumericMaterial(sku);
        var triedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Try supplied IES URL first (if non-blank)
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            if (await TryIesUrlAsync(supplied, options, cancellationToken))
                return supplied;
            triedUrls.Add(supplied);
        }

        // 2. Try AEM download-assets lookup (only if strictly numeric SKU)
        if (strictNumericSku.Length > 0)
        {
            var aemIesUrl = await TryResolveIesFromDownloadCenterAsync(strictNumericSku, options, cancellationToken);
            if (!string.IsNullOrWhiteSpace(aemIesUrl) && !triedUrls.Contains(aemIesUrl))
            {
                if (await TryIesUrlAsync(aemIesUrl, options, cancellationToken))
                    return aemIesUrl;
                triedUrls.Add(aemIesUrl);
            }
        }

        // 3. Try ConfiguratorIesUrl (only if strictly numeric SKU)
        if (strictNumericSku.Length > 0)
        {
            var configuratorUrl = string.Format(ConfiguratorIesUrl, Uri.EscapeDataString(strictNumericSku));
            if (!triedUrls.Contains(configuratorUrl))
            {
                if (await TryIesUrlAsync(configuratorUrl, options, cancellationToken))
                    return configuratorUrl;
                triedUrls.Add(configuratorUrl);
            }
        }

        // 4. Try slug-based fallback (whenever description is non-blank)
        if (!string.IsNullOrWhiteSpace(description))
        {
            var slugUrl = string.Format(DirectIesUrl, Slug(description));
            if (!triedUrls.Contains(slugUrl))
            {
                if (await TryIesUrlAsync(slugUrl, options, cancellationToken))
                    return slugUrl;
            }
        }

        return string.Empty;
    }

    private async Task<bool> TryIesUrlAsync(string url, DownloadOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), options, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return DownloadValidation.IsValid(bytes, response.Content.Headers.ContentType?.MediaType, DownloadKind.Ies);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private static async Task<byte[]> ReadValidationPrefixAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[4096];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return total == buffer.Length ? buffer : buffer[..total];
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, DownloadOptions options, CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, options.Retries);
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var request = requestFactory();
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!ShouldRetry(response.StatusCode) || attempt == attempts) return response;
                response.Dispose();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                last = exception;
                if (attempt == attempts) throw;
            }
            await Task.Delay(options.RetryBaseDelay * (attempt * 2), cancellationToken);
        }
        throw last ?? new HttpRequestException("Yêu cầu Signify thất bại sau khi thử lại.");
    }

    private static bool ShouldRetry(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static bool IsSameExactProduct(ProductRecord record, SignifySearchCandidate candidate, string strictNumericMaterial) =>
        strictNumericMaterial.Length > 0
            ? candidate.Sku.Trim().Equals(strictNumericMaterial, StringComparison.Ordinal) ||
              candidate.ProductCodes.Any(code => code.Trim().Equals(strictNumericMaterial, StringComparison.Ordinal))
            : SignifyScoring.IsExact(record, candidate);

    private static IReadOnlyList<(SignifySearchCandidate Candidate, string Query)> SelectExactApiMatches(
        IEnumerable<(SignifySearchCandidate Candidate, string Query)> matches,
        string strictNumericMaterial)
    {
        var items = matches.ToArray();
        if (items.Length == 0) return [];

        if (strictNumericMaterial.Length > 0)
        {
            // An alias proves relevance per candidate, not shared ownership. A numeric
            // material has no safe API fallback when results name multiple primaries.
            var identities = items.Select(item => CandidateIdentity(item.Candidate))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return identities.Length == 1 ? items : [];
        }

        // Retain the established first exact identity for nonnumeric searches, but only
        // merge observations that identify that same product.
        var selectedIdentity = CandidateIdentity(items[0].Candidate);
        return items.Where(item => CandidateIdentity(item.Candidate).Equals(selectedIdentity, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static SignifySearchCandidate? MergeExactCandidates(IEnumerable<SignifySearchCandidate> candidates)
    {
        SignifySearchCandidate? merged = null;
        foreach (var candidate in candidates)
            merged = merged is null ? candidate : MergeExactCandidate(merged, candidate);
        return merged;
    }

    private static bool CandidateHasSku(SignifySearchCandidate candidate, string sku) =>
        candidate.Sku.Trim().Equals(sku, StringComparison.Ordinal) ||
        candidate.ProductCodes.Any(code => code.Trim().Equals(sku, StringComparison.Ordinal));

    private static string CandidateIdentity(SignifySearchCandidate candidate)
    {
        var sku = candidate.Sku.Trim();
        if (sku.Length > 0) return $"sku:{sku}";
        var code = candidate.ProductCodes.Select(value => value.Trim()).FirstOrDefault(value => value.Length > 0);
        if (!string.IsNullOrEmpty(code)) return $"sku:{code}";
        return $"name:{CandidateName(candidate)}";
    }

    private static SignifySearchCandidate MergeExactCandidate(SignifySearchCandidate current, SignifySearchCandidate next)
    {
        var preferred = next.Score > current.Score ? next : current;
        return preferred with
        {
            LeafletUrl = FirstNonEmpty(current.LeafletUrl, next.LeafletUrl),
            IesUrl = FirstNonEmpty(current.IesUrl, next.IesUrl),
            ProductUrl = FirstNonEmpty(current.ProductUrl, next.ProductUrl),
            Name = FirstNonEmpty(preferred.Name, current.Name, next.Name),
            DisplayedDescription = FirstNonEmpty(preferred.DisplayedDescription, current.DisplayedDescription, next.DisplayedDescription),
            ProductCodes = current.ProductCodes.Concat(next.ProductCodes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Score = Math.Max(current.Score, next.Score)
        };
    }

    private static string CandidateName(SignifySearchCandidate candidate) =>
        FirstNonEmpty(candidate.Name, candidate.DisplayedDescription);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string StrictNumericMaterial(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length > 0 && trimmed.All(char.IsDigit) ? trimmed : string.Empty;
    }

    private static IEnumerable<string> UniqueQueries(ProductRecord record) => new[] { record.Material, record.MaterialDescription, record.MaterialDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase);
    private static string Slug(string value) => SlugRegex().Replace(value.ToUpperInvariant(), "-").Trim('-');
    [GeneratedRegex("[^A-Z0-9]+", RegexOptions.CultureInvariant)] private static partial Regex SlugRegex();
}
