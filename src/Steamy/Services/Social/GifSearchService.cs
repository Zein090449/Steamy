using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Steamy.Models;

namespace Steamy.Services;

public sealed record GifItem(string Provider, string Id, string Title, string PreviewUrl, string ShareUrl);
public sealed record GifSearchPage(IReadOnlyList<GifItem> Items, bool HasMore);
public sealed class GifProviderException(string message) : Exception(message);
public interface IGifSearchService
{
    string Provider { get; }
    bool IsConfigured { get; }
    Task<GifSearchPage> SearchAsync(string query, int page, CancellationToken token);
    Task<GifSearchPage> FavoritesAsync(IReadOnlyList<string> ids, CancellationToken token);
}

/// <summary>Direct provider requests. Store provider IDs only, never search responses or media URLs.</summary>
public sealed partial class GifSearchService : IGifSearchService
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private DateTimeOffset _retryAfter;
    private string _rateLimitedKey = "";
    public GifSearchService(ISettingsService settings) : this(settings.Load(), SharedClient) { }
    internal GifSearchService(AppSettings settings, HttpClient http) { _settings = settings; _http = http; }
    public string Provider => _settings.GifProvider switch { "KLIPY" => "KLIPY", "GIPHY" => "GIPHY", _ => "TENOR" };
    // Tenor's public website catalog needs no key; the API providers require a client key.
    public bool IsConfigured => Provider == "TENOR" || Key().Length > 0;
    [GeneratedRegex("^[A-Za-z0-9_-]{1,160}$")]
    private static partial Regex Identifier();
    internal static bool ValidId(string id) => Identifier().IsMatch(id);
    private string Key() => _settings.GifApiKey.Trim();
    public Task<GifSearchPage> SearchAsync(string query, int page, CancellationToken token)
    {
        if (query.Length > 50) throw new GifProviderException("Search can be up to 50 characters.");
        page = Math.Clamp(page, 1, 20);
        if (Provider == "TENOR") return TenorSearchAsync(query, token);
        var trending = query.Length == 0;
        var parameters = Provider == "GIPHY" ? $"limit=24&offset={(page - 1) * 24}&rating=pg-13" : $"per_page=24&page={page}";
        if (!trending) parameters += "&q=" + Uri.EscapeDataString(query);
        return RequestAsync(trending ? "trending" : "search", parameters, token);
    }
    public Task<GifSearchPage> FavoritesAsync(IReadOnlyList<string> ids, CancellationToken token)
    {
        if (ids.Count == 0) return Task.FromResult(new GifSearchPage([], false));
        if (ids.Count > 50 || ids.Any(id => !ValidId(id))) throw new GifProviderException("Invalid GIF favorite.");
        if (Provider == "TENOR") return TenorFavoritesAsync(ids, token);
        return RequestAsync(Provider == "GIPHY" ? "" : "items", (Provider == "GIPHY" ? "ids=" : "slugs=") + Uri.EscapeDataString(string.Join(',', ids)), token);
    }
    private async Task<GifSearchPage> RequestAsync(string endpoint, string parameters, CancellationToken token)
    {
        var provider = Provider; var key = Key();
        if (key.Length == 0) throw new GifProviderException($"{provider} search needs an app key. You can paste a GIF link without one.");
        if (!ValidId(key)) throw new GifProviderException($"Check the {provider} app key.");
        if (_rateLimitedKey == key && DateTimeOffset.UtcNow < _retryAfter) throw new GifProviderException($"{provider}'s request limit was reached. Try again later.");
        var url = provider == "GIPHY" ? $"https://api.giphy.com/v1/gifs{(endpoint.Length > 0 ? "/" + endpoint : "")}?api_key={Uri.EscapeDataString(key)}&{parameters}"
            : $"https://api.klipy.com/api/v1/{Uri.EscapeDataString(key)}/gifs/{endpoint}?{parameters}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new() { NoStore = true };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _rateLimitedKey = key;
                _retryAfter = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60, 1, 3600));
                throw new GifProviderException($"{provider}'s request limit was reached. Try again later.");
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new GifProviderException($"{provider} couldn't accept this app key. Check the provider settings.");
            if (!response.IsSuccessStatusCode) throw new GifProviderException($"{provider} is unavailable. Try again shortly.");
            const int limit = 2 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new GifProviderException("The GIF response was too large.");
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var memory = new MemoryStream(); var buffer = new byte[16384]; int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            { if (memory.Length + read > limit) throw new GifProviderException("The GIF response was too large."); memory.Write(buffer, 0, read); }
            return Parse(memory.ToArray(), provider);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new GifProviderException("GIF search timed out. Try again."); }
        catch (HttpRequestException) { throw new GifProviderException("GIF search couldn't connect. Check your connection."); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new GifProviderException("The GIF provider returned an unsupported response."); }
    }
    // Tenor's public search pages carry the catalog as JSON in the store-cache script; no API key applies.
    [GeneratedRegex("<script id=\"store-cache\"[^>]*>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex StoreCache();
    private async Task<GifSearchPage> TenorSearchAsync(string query, CancellationToken token)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var path = words.Length == 0 ? "trending" : string.Join('+', words.Select(Uri.EscapeDataString));
        var html = await TenorPageAsync($"https://tenor.com/search/{path}-gifs", token).ConfigureAwait(false);
        return new(ParseTenor(html, null, 24), false);
    }
    private async Task<GifSearchPage> TenorFavoritesAsync(IReadOnlyList<string> ids, CancellationToken token)
    {
        var results = new GifItem?[ids.Count];
        using var slots = new SemaphoreSlim(4);
        await Task.WhenAll(ids.Select(async (id, index) =>
        {
            await slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var html = await TenorPageAsync($"https://tenor.com/view/{Uri.EscapeDataString(id)}", token).ConfigureAwait(false);
                results[index] = ParseTenor(html, id, 1).FirstOrDefault();
            }
            catch (Exception error) when (error is GifProviderException or HttpRequestException) { }
            finally { slots.Release(); }
        })).ConfigureAwait(false);
        return new(results.Where(item => item is not null).Select(item => item!).ToArray(), false);
    }
    private async Task<string> TenorPageAsync(string url, CancellationToken token)
    {
        if (_rateLimitedKey == "tenor" && DateTimeOffset.UtcNow < _retryAfter) throw new GifProviderException("Tenor's request limit was reached. Try again later.");
        try
        {
            using var response = await ImageRedirects.FollowAsync(_http, new Uri(url), token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _rateLimitedKey = "tenor";
                _retryAfter = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60, 1, 3600));
                throw new GifProviderException("Tenor's request limit was reached. Try again later.");
            }
            if (!response.IsSuccessStatusCode) throw new GifProviderException("Tenor is unavailable. Try again shortly.");
            const int limit = 3 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new GifProviderException("The Tenor page was too large.");
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var memory = new MemoryStream(); var buffer = new byte[16384]; int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            { if (memory.Length + read > limit) throw new GifProviderException("The Tenor page was too large."); memory.Write(buffer, 0, read); }
            return Encoding.UTF8.GetString(memory.ToArray());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new GifProviderException("GIF search timed out. Try again."); }
        catch (HttpRequestException) { throw new GifProviderException("GIF search couldn't connect. Check your connection."); }
    }
    internal static IReadOnlyList<GifItem> ParseTenor(string html, string? onlyId, int max)
    {
        var script = StoreCache().Match(html);
        if (!script.Success) return [];
        try
        {
            using var document = JsonDocument.Parse(script.Groups[1].Value, new JsonDocumentOptions { MaxDepth = 64 });
            var items = new List<GifItem>();
            if (document.RootElement.TryGetProperty("gifs", out var gifs) && gifs.TryGetProperty("byId", out var byId))
                CollectTenor(byId, onlyId, max, items);
            if (items.Count == 0 && document.RootElement.TryGetProperty("universal", out var universal) && universal.TryGetProperty("search", out var search))
                CollectTenor(search, onlyId, max, items);
            return items;
        }
        catch (JsonException) { return []; }
    }
    private static void CollectTenor(JsonElement container, string? onlyId, int max, List<GifItem> items)
    {
        foreach (var entry in container.EnumerateObject())
        {
            if (onlyId is not null && !entry.Name.Equals(onlyId, StringComparison.Ordinal)) continue;
            if (!entry.Value.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in results.EnumerateArray())
            {
                if (items.Count >= max) return;
                if (TenorItem(item) is { } gif && (onlyId is null || gif.Id == onlyId) && !items.Any(existing => existing.Id == gif.Id)) items.Add(gif);
            }
        }
    }
    private static GifItem? TenorItem(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var idValue) || idValue.GetString() is not { } id || !ValidId(id)) return null;
        var title = item.TryGetProperty("content_description", out var description) && description.ValueKind == JsonValueKind.String && description.GetString() is { Length: > 0 } text
            ? text
            : item.TryGetProperty("title", out var label) && label.ValueKind == JsonValueKind.String && label.GetString() is { Length: > 0 } fallback ? fallback : "GIF";
        if (title.Length > 200) title = title[..200];
        if (!item.TryGetProperty("itemurl", out var itemUrl) || !Uri.TryCreate(itemUrl.GetString(), UriKind.Absolute, out var share) || !ChatMediaPolicy.IsPageUri(share)) return null;
        if (!item.TryGetProperty("media_formats", out var formats)) return null;
        foreach (var size in new[] { "tinygif", "mediumgif", "gif" })
        {
            if (!formats.TryGetProperty(size, out var format) || !format.TryGetProperty("url", out var url)) continue;
            if (Uri.TryCreate(url.GetString(), UriKind.Absolute, out var preview) && ChatMediaPolicy.IsImageUri(preview))
                return new GifItem("TENOR", id, title, preview.AbsoluteUri, share.AbsoluteUri);
        }
        return null;
    }
    internal static GifSearchPage Parse(byte[] bytes, string provider)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
        var root = document.RootElement;
        if (provider != "GIPHY" && root.TryGetProperty("result", out var success) && success.ValueKind == JsonValueKind.False) throw new GifProviderException("KLIPY couldn't load GIFs. Check the app key and try again.");
        var container = root.GetProperty("data");
        var entries = provider == "GIPHY" ? container : container.GetProperty("data");
        var items = new List<GifItem>();
        foreach (var item in entries.EnumerateArray())
        {
            if (items.Count >= 50) throw new GifProviderException("The GIF provider returned too many results.");
            var id = item.GetProperty(provider == "GIPHY" ? "id" : "slug").GetString() ?? "";
            var title = item.TryGetProperty("title", out var label) ? label.GetString() ?? "GIF" : "GIF";
            if (title.Length > 200) title = title[..200];
            var images = item.GetProperty(provider == "GIPHY" ? "images" : "file");
            string preview, share;
            if (provider == "GIPHY")
            {
                preview = images.GetProperty("fixed_width_small").GetProperty("url").GetString()!;
                share = (images.TryGetProperty("downsized", out var small) ? small : images.GetProperty("original")).GetProperty("url").GetString()!;
            }
            else
            {
                var thumb = images.TryGetProperty("xs", out var xs) ? xs : images.GetProperty("sm");
                preview = thumb.GetProperty("gif").GetProperty("url").GetString()!;
                share = (images.TryGetProperty("md", out var md) ? md : images.GetProperty("hd")).GetProperty("gif").GetProperty("url").GetString()!;
            }
            if (!ValidId(id) || !ValidMedia(preview, provider) || !ValidMedia(share, provider)) throw new GifProviderException("The GIF provider returned an unsupported media link.");
            items.Add(new(provider, id, title, preview, share));
        }
        var hasMore = provider == "GIPHY" ? root.TryGetProperty("pagination", out var pages) && pages.GetProperty("offset").GetInt32() + pages.GetProperty("count").GetInt32() < pages.GetProperty("total_count").GetInt32()
            : container.TryGetProperty("has_next", out var next) && next.ValueKind == JsonValueKind.True;
        return new(items, hasMore);
    }
    private static bool ValidMedia(string? value, string provider) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && ChatMediaPolicy.IsImageUri(uri)
        && (provider == "GIPHY" ? uri.Host.EndsWith(".giphy.com", StringComparison.Ordinal) : uri.Host is "static.klipy.com" or "static1.klipy.com" or "static2.klipy.com");
}
