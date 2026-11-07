using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Steamy.Services;

/// <summary>A metadata-only catalog game. Providers never serve game files.</summary>
public sealed record CatalogGame(
    string Id,
    string Name,
    string? CoverUrl,
    int? Year,
    double? Rating,
    string? Platform,
    string PageUrl)
{
    public string Monogram => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();

    public string YearLabel => Year is > 0 ? Year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    public string MetaLabel
    {
        get
        {
            var platform = Platform ?? string.Empty;
            return string.IsNullOrEmpty(YearLabel) ? platform
                : string.IsNullOrEmpty(platform) ? YearLabel : $"{YearLabel} · {platform}";
        }
    }
}

public sealed record CatalogPage(IReadOnlyList<CatalogGame> Items, int? Total, bool HasMore);

public sealed class CatalogProviderException(string message) : Exception(message);

/// <summary>
/// The metadata sources offered in the emulated tab, primary first. A concrete type so the
/// offline UI harness cannot replace it with a service proxy.
/// </summary>
public sealed class EmulatedCatalogProviderRegistry(DlpsCatalogService dlps)
{
    public IReadOnlyList<IEmulatedCatalogProvider> Providers { get; } = [dlps];
}

/// <summary>
/// A metadata source for the emulated-games browser. Implementations supply titles, artwork and
/// store-page links only — never game files.
/// </summary>
public interface IEmulatedCatalogProvider
{
    string Name { get; }
    string WebsiteUrl { get; }
    string SetupUrl { get; }
    string SetupHint { get; }
    bool IsConfigured { get; }
    bool NeedsCredentials { get; }
    Task<IReadOnlyList<string>> GetPlatformsAsync(CancellationToken cancellationToken = default);
    Task<CatalogPage> SearchAsync(string? platform, string? search, int page, CancellationToken cancellationToken = default);
}

/// <summary>
/// Catalog provider powered by dlps_downloader (https://github.com/jayjun911/dlps_downloader).
/// Sources games from dlpsgame.com and affiliated platforms (nswgame.com, downloadgamexbox.com, downloadgamepsp.org).
/// </summary>
public sealed class DlpsCatalogService(HttpClient? http = null) : IEmulatedCatalogProvider
{
    public const string DefaultHost = "https://dlpsgame.com";
    public const string RepoUrl = "https://github.com/jayjun911/dlps_downloader";
    public const int PageSize = 24;

    public sealed record PlatformDefinition(string Name, string Key, string Host, string Slug);

    public static readonly IReadOnlyList<PlatformDefinition> SupportedPlatforms =
    [
        new("PlayStation 5", "ps5", "https://dlpsgame.com", "list-game-ps5"),
        new("PlayStation 4", "ps4", "https://dlpsgame.com", "list-all-game-ps4"),
        new("PlayStation 3", "ps3", "https://dlpsgame.com", "list-all-game-ps3"),
        new("PlayStation 2", "ps2", "https://dlpsgame.com", "list-all-game-ps2"),
        new("Nintendo Switch", "switch", "https://nswgame.com", "list-all-game-switch"),
        new("Nintendo Wii", "wii", "https://nswgame.com", "list-all-game-wii"),
        new("Nintendo Wii U", "wiiu", "https://nswgame.com", "list-all-game-wii-u"),
        new("Nintendo 3DS", "3ds", "https://nswgame.com", "list-all-game-3ds"),
        new("Xbox (JTAG/RGH)", "xbox-jtag", "https://downloadgamexbox.com", "list-all-game-xbox-jtag"),
        new("Xbox (ISO)", "xbox-iso", "https://downloadgamexbox.com", "list-all-game-xbox-iso"),
        new("PSP", "psp", "https://downloadgamepsp.org", "list-all-game-psp-ppsspp"),
        new("PS Vita", "psvita", "https://downloadgamepsp.org", "list-all-game-psvita")
    ];

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, IReadOnlyList<CatalogGame> Games)> _listCache = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "dlps_downloader";
    public string WebsiteUrl => RepoUrl;
    public string SetupUrl => RepoUrl;
    public string SetupHint => "Powered by dlps_downloader. Free and requires no account.";
    public bool IsConfigured => true;
    public bool NeedsCredentials => false;

    public Task<IReadOnlyList<string>> GetPlatformsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(SupportedPlatforms.Select(platform => platform.Name).ToArray());

    public async Task<CatalogPage> SearchAsync(string? platform, string? search, int page, CancellationToken cancellationToken = default)
    {
        var targetPlatform = ResolvePlatform(platform);
        var trimmedSearch = (search ?? "").Trim();

        if (trimmedSearch.Length > 0)
        {
            return await SearchPostsAsync(targetPlatform, trimmedSearch, page, cancellationToken).ConfigureAwait(false);
        }

        return await BrowsePlatformAsync(targetPlatform, page, cancellationToken).ConfigureAwait(false);
    }

    private static PlatformDefinition ResolvePlatform(string? platform)
    {
        if (!string.IsNullOrWhiteSpace(platform) && !string.Equals(platform, "All platforms", StringComparison.OrdinalIgnoreCase))
        {
            var found = SupportedPlatforms.FirstOrDefault(p =>
                string.Equals(p.Name, platform, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Key, platform, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;
            throw new CatalogProviderException($"{platform} is not supported by dlps_downloader.");
        }

        return SupportedPlatforms[0]; // Default to PlayStation 5
    }

    private async Task<CatalogPage> SearchPostsAsync(PlatformDefinition platform, string search, int page, CancellationToken cancellationToken)
    {
        var targetPage = Math.Max(1, page);
        var uri = $"{platform.Host}/wp-json/wp/v2/posts?search={Uri.EscapeDataString(search)}&per_page={PageSize}&page={targetPage}&_fields=id,slug,title,link,yoast_head_json,content";

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new CatalogPage([], 0, false);
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)451)
            throw new CatalogProviderException("Access was blocked. If you are using a VPN, Threat Protection, or ad-blocker, it may be filtering this domain.");

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new CatalogProviderException("dlpsgame is rate-limiting requests. Try again in a moment.");

        if (!response.IsSuccessStatusCode)
            throw new CatalogProviderException($"The DLPS catalog returned HTTP {(int)response.StatusCode}.");

        int? total = null;
        if (response.Headers.TryGetValues("X-WP-Total", out var totalVals) && int.TryParse(totalVals.FirstOrDefault(), out var parsedTotal))
        {
            total = parsedTotal;
        }

        JsonDocument document;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new CatalogProviderException("The DLPS catalog returned an unreadable response.");
        }

        using (document)
        {
            var games = new List<CatalogGame>();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idElem) ? idElem.ToString() : Guid.NewGuid().ToString();
                    var slug = Text(item, "slug") ?? id;
                    var titleRaw = item.TryGetProperty("title", out var titleElem) ? Text(titleElem, "rendered") : null;
                    var title = CleanTitle(titleRaw);
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    var link = Text(item, "link") ?? $"{platform.Host}/{slug}/";
                    var coverUrl = ExtractCoverUrl(item);
                    var year = ExtractYear(item);

                    games.Add(new CatalogGame(slug, title, coverUrl, year, null, platform.Name, link));
                }
            }

            var hasMore = total.HasValue ? targetPage * PageSize < total.Value : games.Count == PageSize;
            return new CatalogPage(games, total ?? games.Count, hasMore);
        }
    }

    private async Task<CatalogPage> BrowsePlatformAsync(PlatformDefinition platform, int page, CancellationToken cancellationToken)
    {
        var allGames = await GetOrLoadPlatformListAsync(platform, cancellationToken).ConfigureAwait(false);
        var targetPage = Math.Max(1, page);
        var skip = (targetPage - 1) * PageSize;
        var pageItems = allGames.Skip(skip).Take(PageSize).ToList();

        await TryEnrichCoversAsync(platform, pageItems, cancellationToken).ConfigureAwait(false);

        var hasMore = skip + PageSize < allGames.Count;
        return new CatalogPage(pageItems, allGames.Count, hasMore);
    }

    private async Task<IReadOnlyList<CatalogGame>> GetOrLoadPlatformListAsync(PlatformDefinition platform, CancellationToken cancellationToken)
    {
        if (_listCache.TryGetValue(platform.Key, out var entry) && DateTimeOffset.UtcNow - entry.CachedAt < TimeSpan.FromHours(1))
        {
            return entry.Games;
        }

        var uri = $"{platform.Host}/wp-json/wp/v2/pages?slug={platform.Slug}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)451)
            throw new CatalogProviderException("Access was blocked. If you are using a VPN, Threat Protection, or ad-blocker, it may be filtering this domain.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new CatalogProviderException("dlpsgame is rate-limiting requests. Try again in a moment.");
        if (!response.IsSuccessStatusCode)
            throw new CatalogProviderException($"The DLPS catalog returned HTTP {(int)response.StatusCode}.");

        JsonDocument document;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new CatalogProviderException("The DLPS catalog returned an unreadable response.");
        }

        using (document)
        {
            var html = string.Empty;
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("content", out var contentElem) && Text(contentElem, "rendered") is { Length: > 0 } rendered)
                    {
                        if (rendered.Contains("display-posts-listing") || string.IsNullOrEmpty(html))
                        {
                            html = rendered;
                            if (rendered.Contains("display-posts-listing")) break;
                        }
                    }
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("content", out var contentElem))
            {
                html = Text(contentElem, "rendered") ?? string.Empty;
            }

            var games = ParseListingHtml(html, platform);
            _listCache[platform.Key] = (DateTimeOffset.UtcNow, games);
            return games;
        }
    }

    private static readonly Regex ListingRegex = new(
        @"(?is)<li\b[^>]*class=[""'][^""']*listing-item[^""']*[""'][^>]*>.*?<a\b[^>]*href=[""'](?<url>https?://[^""']+)[""'][^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled);

    private static IReadOnlyList<CatalogGame> ParseListingHtml(string html, PlatformDefinition platform)
    {
        var results = new List<CatalogGame>();
        var matches = ListingRegex.Matches(html);
        foreach (Match match in matches)
        {
            var url = match.Groups["url"].Value.Trim();
            var rawTitle = match.Groups["title"].Value;
            var title = CleanTitle(rawTitle);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;

            var trimmedUrl = url.TrimEnd('/');
            var slug = trimmedUrl[(trimmedUrl.LastIndexOf('/') + 1)..];

            results.Add(new CatalogGame(slug, title, null, null, null, platform.Name, url));
        }

        return results;
    }

    private async Task TryEnrichCoversAsync(PlatformDefinition platform, List<CatalogGame> pageItems, CancellationToken cancellationToken)
    {
        var needsCovers = pageItems.Where(g => string.IsNullOrEmpty(g.CoverUrl)).Take(24).ToList();
        if (needsCovers.Count == 0) return;

        try
        {
            var query = string.Join("&", needsCovers.Select(g => "slug[]=" + Uri.EscapeDataString(g.Id)));
            var uri = $"{platform.Host}/wp-json/wp/v2/posts?{query}&_fields=slug,yoast_head_json,content";

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (document.RootElement.ValueKind != JsonValueKind.Array) return;

            var coverMap = new Dictionary<string, (string? CoverUrl, int? Year)>(StringComparer.OrdinalIgnoreCase);
            foreach (var post in document.RootElement.EnumerateArray())
            {
                var slug = Text(post, "slug");
                if (string.IsNullOrEmpty(slug)) continue;
                coverMap[slug] = (ExtractCoverUrl(post), ExtractYear(post));
            }

            for (var i = 0; i < pageItems.Count; i++)
            {
                var item = pageItems[i];
                if (coverMap.TryGetValue(item.Id, out var metadata))
                {
                    pageItems[i] = item with
                    {
                        CoverUrl = metadata.CoverUrl ?? item.CoverUrl,
                        Year = metadata.Year ?? item.Year
                    };
                }
            }
        }
        catch
        {
            // Non-fatal: titles remain viewable with monograms
        }
    }

    private static string? ExtractCoverUrl(JsonElement item)
    {
        if (item.TryGetProperty("yoast_head_json", out var yoast) &&
            yoast.TryGetProperty("og_image", out var ogImage) &&
            ogImage.ValueKind == JsonValueKind.Array)
        {
            foreach (var img in ogImage.EnumerateArray())
            {
                if (Text(img, "url") is { Length: > 0 } url) return url;
            }
        }

        if (item.TryGetProperty("content", out var content) && Text(content, "rendered") is { Length: > 0 } html)
        {
            var match = Regex.Match(html, @"(?is)<img\b[^>]*\bsrc=[""'](?<src>https?://[^""']+)[""']");
            if (match.Success) return match.Groups["src"].Value;
        }

        return null;
    }

    private static int? ExtractYear(JsonElement item)
    {
        if (item.TryGetProperty("content", out var content) && Text(content, "rendered") is { Length: > 0 } html)
        {
            var releaseMatch = Regex.Match(html, @"(?i)\bRELEASE\b[^\d]*(?<year>19\d\d|20\d\d)");
            if (releaseMatch.Success && int.TryParse(releaseMatch.Groups["year"].Value, out var year))
                return year;
        }

        if (Text(item, "date") is { Length: >= 4 } dateStr && int.TryParse(dateStr[..4], out var postYear))
        {
            return postYear;
        }

        return null;
    }

    private static string CleanTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return WebUtility.HtmlDecode(raw).Trim();
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Nintendo's European storefront search. Keyless and free; returns official titles, artwork,
/// release dates and store links. Metadata only.
/// </summary>
public sealed class NintendoCatalogService(HttpClient? http = null) : IEmulatedCatalogProvider
{
    public const string Endpoint = "https://search.nintendo-europe.com/en/select";
    public const string SiteBase = "https://www.nintendo.com";
    public const int PageSize = 24;

    private static readonly (string Name, string[] Clauses)[] Platforms =
    [
        ("Nintendo Switch 2", ["system_type:nintendoswitch2"]),
        // "nintendoswitch" prefix-matches "nintendoswitch2", so Switch 1 needs the exclusion.
        ("Nintendo Switch", ["system_type:nintendoswitch", "-system_type:nintendoswitch2"]),
        ("Nintendo 64", ["system_type:nintendo64"]),
        ("Game Boy Advance", ["system_type:gameboyadvance"]),
        ("Wii", ["system_type:wii"])
    ];

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    public string Name => "Nintendo";
    public string WebsiteUrl => "https://www.nintendo.com/en-gb/";
    public string SetupUrl => "https://www.nintendo.com/en-gb/Games/";
    public string SetupHint => "Nintendo's catalog is free and needs no account.";
    public bool IsConfigured => true;
    public bool NeedsCredentials => false;

    public Task<IReadOnlyList<string>> GetPlatformsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Platforms.Select(platform => platform.Name).ToArray());

    public async Task<CatalogPage> SearchAsync(string? platform, string? search, int page, CancellationToken cancellationToken = default)
    {
        var terms = SolrTerms(search);
        var clauses = new List<string> { "type:GAME" };
        if (!string.IsNullOrWhiteSpace(platform) && !string.Equals(platform, "All platforms", StringComparison.OrdinalIgnoreCase))
        {
            (string Name, string[] Clauses)? selected = null;
            foreach (var candidate in Platforms)
                if (string.Equals(candidate.Name, platform, StringComparison.OrdinalIgnoreCase)) { selected = candidate; break; }
            clauses.AddRange(selected?.Clauses ?? throw new CatalogProviderException($"{platform} is not in the Nintendo catalog."));
        }

        var parameters = new List<string> { "q=" + Uri.EscapeDataString(terms.Length == 0 ? "*" : terms) };
        parameters.AddRange(clauses.Select(clause => "fq=" + Uri.EscapeDataString(clause)));
        parameters.Add("rows=" + PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        parameters.Add("start=" + ((Math.Max(1, page) - 1) * PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Relevance ordering only applies with a query; browsing falls back to alphabetical.
        if (terms.Length == 0) parameters.Add("sort=" + Uri.EscapeDataString("sorting_title asc"));
        parameters.Add("wt=json");

        using var document = await GetAsync(Endpoint + "?" + string.Join("&", parameters), cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            throw new CatalogProviderException("The Nintendo catalog returned an unreadable response.");
        var games = new List<CatalogGame>();
        if (response.TryGetProperty("docs", out var docs) && docs.ValueKind == JsonValueKind.Array)
        {
            foreach (var doc in docs.EnumerateArray())
            {
                var name = Text(doc, "title");
                var url = Text(doc, "url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                games.Add(new CatalogGame(
                    Text(doc, "fs_id") ?? url,
                    name,
                    Text(doc, "image_url"),
                    ReleaseYear(doc),
                    null,
                    FirstString(doc, "system_names_txt"),
                    SiteBase + url));
            }
        }
        var total = response.TryGetProperty("numFound", out var totalElement) && totalElement.TryGetInt32(out var found) ? found : games.Count;
        return new CatalogPage(games, total, games.Count > 0 && Math.Max(1, page) * PageSize < total);
    }

    // Solr treats -, :, quotes and similar characters as operators, so keep letters, digits,
    // apostrophes and spaces only, then join words with AND, which probed best for multi-word titles.
    private static string SolrTerms(string? search)
    {
        var cleaned = new string((search ?? "").Where(character => char.IsLetterOrDigit(character) || character is '\'' or ' ').ToArray());
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" AND ", words);
    }

    private static int? ReleaseYear(JsonElement doc)
    {
        if (Text(doc, "pretty_date_s") is { } pretty && string.Equals(pretty, "TBD", StringComparison.OrdinalIgnoreCase)) return null;
        if (!doc.TryGetProperty("dates_released_dts", out var dates) || dates.ValueKind != JsonValueKind.Array) return null;
        foreach (var date in dates.EnumerateArray())
            if (date.ValueKind == JsonValueKind.String
                && DateTime.TryParse(date.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed.Year;
        return null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FirstString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value) return value;
        return null;
    }

    private async Task<JsonDocument> GetAsync(string uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new CatalogProviderException("Nintendo is rate-limiting requests. Try again in a moment.");
        if (!response.IsSuccessStatusCode)
            throw new CatalogProviderException($"The Nintendo catalog returned HTTP {(int)response.StatusCode}.");
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new CatalogProviderException("The Nintendo catalog returned an unreadable response.");
        }
    }
}

/// <summary>
/// Optional metadata source backed by IGDB. Free with a Twitch developer app; the client ID and
/// secret belong to the user and stay in the local settings file. Metadata only.
/// </summary>
public sealed class IgdbCatalogService(Func<string> clientId, Func<string> clientSecret, HttpClient? http = null) : IEmulatedCatalogProvider
{
    public const string GamesEndpoint = "https://api.igdb.com/v4/games";
    public const string TokenEndpoint = "https://id.twitch.tv/oauth2/token";
    public const int PageSize = 24;

    private static readonly (string Name, int Id)[] Platforms =
    [
        ("Nintendo Switch 2", 508), ("Nintendo Switch", 130), ("Nintendo 64", 4),
        ("Game Boy Advance", 24), ("Nintendo GameCube", 21), ("Wii", 5),
        ("Super Nintendo Entertainment System", 19), ("Nintendo DS", 20), ("Nintendo 3DS", 37)
    ];

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    public string Name => "IGDB";
    public string WebsiteUrl => "https://www.igdb.com/";
    public string SetupUrl => "https://dev.twitch.tv/console/apps";
    public string SetupHint => "IGDB is free with a Twitch developer app: create one, then paste the Client ID and Secret. They stay in this PC's Steamy settings.";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(clientId()) && !string.IsNullOrWhiteSpace(clientSecret());
    public bool NeedsCredentials => true;

    public Task<IReadOnlyList<string>> GetPlatformsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Platforms.Select(platform => platform.Name).ToArray());

    public async Task<CatalogPage> SearchAsync(string? platform, string? search, int page, CancellationToken cancellationToken = default)
    {
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var trimmed = (search ?? "").Trim();
        int? platformId = null;
        if (!string.IsNullOrWhiteSpace(platform) && !string.Equals(platform, "All platforms", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in Platforms)
                if (string.Equals(candidate.Name, platform, StringComparison.OrdinalIgnoreCase)) platformId = candidate.Id;
            if (platformId is null) throw new CatalogProviderException($"{platform} is not in the IGDB catalog.");
        }

        var body = new StringBuilder("fields name,slug,cover.image_id,first_release_date,rating,platforms.name;");
        if (trimmed.Length > 0) body.Append("search \"").Append(trimmed.Replace("\"", "")).Append("\";");
        if (platformId is { } id)
        {
            body.Append("where platforms = (").Append(id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(')');
            // Browsing favours games people actually played; search keeps every result.
            if (trimmed.Length == 0) body.Append(" & total_rating_count > 0");
            body.Append(';');
        }
        if (trimmed.Length == 0)
            body.Append("sort total_rating_count desc;");
        body.Append("limit ").Append(PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(";offset ").Append(((Math.Max(1, page) - 1) * PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(';');

        using var request = new HttpRequestMessage(HttpMethod.Post, GamesEndpoint);
        request.Headers.TryAddWithoutValidation("Client-ID", clientId().Trim());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new StringContent(body.ToString(), Encoding.UTF8, "text/plain");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CatalogProviderException("IGDB rejected the credentials. Paste a valid Client ID and Secret.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new CatalogProviderException("IGDB is rate-limiting requests. Try again in a moment.");
        if (!response.IsSuccessStatusCode)
            throw new CatalogProviderException($"The IGDB catalog returned HTTP {(int)response.StatusCode}.");

        JsonDocument document;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new CatalogProviderException("The IGDB catalog returned an unreadable response.");
        }
        using (document)
        {
            var games = new List<CatalogGame>();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var name = Text(item, "name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var slug = Text(item, "slug") ?? "";
                    var cover = item.TryGetProperty("cover", out var coverElement) ? Text(coverElement, "image_id") : null;
                    games.Add(new CatalogGame(
                        Text(item, "id") ?? slug,
                        name,
                        string.IsNullOrEmpty(cover) ? null : $"https://images.igdb.com/igdb/image/upload/t_cover_big/{cover}.jpg",
                        ReleaseYear(item),
                        item.TryGetProperty("rating", out var rating) && rating.TryGetDouble(out var value) && value > 0 ? value : null,
                        FirstString(item, "platforms"),
                        $"https://www.igdb.com/games/{slug}"));
                }
            }
            return new CatalogPage(games, null, games.Count == PageSize);
        }
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is { } cached && DateTimeOffset.UtcNow < _tokenExpiresAt) return cached;
        var id = clientId().Trim();
        var secret = clientSecret().Trim();
        if (id.Length == 0 || secret.Length == 0)
            throw new CatalogProviderException("Add a free IGDB Client ID and Secret to browse this catalog.");
        var uri = $"{TokenEndpoint}?client_id={Uri.EscapeDataString(id)}&client_secret={Uri.EscapeDataString(secret)}&grant_type=client_credentials";
        using var response = await _http.PostAsync(uri, content: null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            throw new CatalogProviderException("Twitch rejected the IGDB credentials. Paste a valid Client ID and Secret.");
        if (!response.IsSuccessStatusCode)
            throw new CatalogProviderException($"Twitch returned HTTP {(int)response.StatusCode} while signing in.");
        JsonDocument document;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new CatalogProviderException("Twitch returned an unreadable token response.");
        }
        using (document)
        {
            var accessToken = document.RootElement.TryGetProperty("access_token", out var tokenElement) ? tokenElement.GetString() : null;
            var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var seconds) ? seconds : 0;
            if (string.IsNullOrWhiteSpace(accessToken)) throw new CatalogProviderException("Twitch returned no access token.");
            _token = accessToken;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
            return accessToken!;
        }
    }

    private static int? ReleaseYear(JsonElement item)
    {
        if (!item.TryGetProperty("first_release_date", out var date) || date.ValueKind != JsonValueKind.Number) return null;
        if (!date.TryGetInt64(out var unix)) return null;
        return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.Year;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FirstString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in array.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.Object && Text(entry, "name") is { Length: > 0 } name) return name;
        return null;
    }
}
