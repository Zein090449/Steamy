using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Steamy.Services;

internal sealed record ChatMedia(byte[] Bytes, BitmapSource Poster, bool Animated)
{
    public Uri? VideoUrl { get; init; }
    public bool IsVideo => VideoUrl is not null;
}

/// <summary>Bounded downloads and frozen poster decoding outside WPF's dispatcher. No disk cache.</summary>
internal static partial class ChatMediaLoader
{
    internal const int MaxBytes = 8 * 1024 * 1024;
    private static readonly BitmapSource VideoPoster = CreateVideoPoster();
    internal static Task<ChatMedia> LoadAsync(Uri uri, CancellationToken token) => LoadAsync(uri, token, 960);
    internal static async Task<ChatMedia> LoadAsync(Uri uri, CancellationToken token, int decodeWidth)
    {
        if (!ChatMediaPolicy.IsMediaReference(uri)) throw new IOException("Unsupported image link.");
        if (ChatMediaPolicy.IsVideoUri(uri)) return LoadVideo(uri, token);
        if (!ChatMediaPolicy.IsImageUri(uri)) return await LoadSharePageAsync(uri, token, decodeWidth).ConfigureAwait(false);
        var bytes = await ImagePipeline.Shared.LoadTransientContentAsync(uri, token).ConfigureAwait(false)
            ?? throw new IOException("The image is too large or unavailable.");
        return await Task.Run(() => Decode(bytes, decodeWidth), token).ConfigureAwait(false);
    }
    private static async Task<ChatMedia> LoadSharePageAsync(Uri page, CancellationToken token, int decodeWidth)
    {
        foreach (var candidate in await ShareCandidatesAsync(page, token).ConfigureAwait(false))
        {
            if (ChatMediaPolicy.IsVideoUri(candidate)) return LoadVideo(candidate, token);
            var bytes = await ImagePipeline.Shared.LoadTransientContentAsync(candidate, token).ConfigureAwait(false);
            if (bytes is null) continue;
            try { return await Task.Run(() => Decode(bytes, decodeWidth), token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException) { }
        }
        throw new IOException("This GIF link couldn't be opened.");
    }
    // Clips stream straight into MediaElement, so downloading them here would only repeat the transfer.
    private static ChatMedia LoadVideo(Uri video, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return new ChatMedia([], VideoPoster, false) { VideoUrl = video };
    }
    private static async Task<List<Uri>> ShareCandidatesAsync(Uri page, CancellationToken token)
    {
        var candidates = new List<Uri>();
        candidates.AddRange(ProviderCandidates(page));
        if (KlipySlug(page) is { } slug)
            candidates.AddRange(await KlipyCandidatesAsync(slug, token).ConfigureAwait(false));
        candidates.AddRange(await ScrapedMediaAsync(page, token).ConfigureAwait(false));
        return candidates.Distinct().ToList();
    }
    // Provider share links resolve without scraping the page: Klipy answers its public item API,
    // Giphy share pages use the same ID as the canonical animated asset, and Tenor short links
    // embed the media path.
    internal static IEnumerable<Uri> ProviderCandidates(Uri page)
    {
        if (GiphyId(page) is { } id && Uri.TryCreate($"https://media.giphy.com/media/{id}/giphy.gif", UriKind.Absolute, out var giphy))
            yield return giphy;
        if (TenorShortLink(page) is { } tenor) yield return tenor;
    }
    [GeneratedRegex("^/(?:gifs|stickers|watch|media)/(?:[A-Za-z0-9]+-)*([A-Za-z0-9]{6,40})/?$")]
    private static partial Regex GiphyPath();
    private static string? GiphyId(Uri page) => IsHost(page, "giphy.com") && GiphyPath().Match(page.AbsolutePath) is { Success: true } match
        ? match.Groups[1].Value : null;
    [GeneratedRegex("^/([A-Za-z0-9]{8,30})/([A-Za-z0-9-]{1,120})$")]
    private static partial Regex TenorMediaPath();
    private static Uri? TenorShortLink(Uri page) => IsHost(page, "tenor.com") && TenorMediaPath().Match(page.AbsolutePath) is { Success: true } match
        && Uri.TryCreate($"https://media.tenor.com/{match.Groups[1].Value}/{match.Groups[2].Value}.gif", UriKind.Absolute, out var media) ? media : null;
    [GeneratedRegex("^/gifs/([A-Za-z0-9_-]{1,160})/?$")]
    private static partial Regex KlipyPath();
    private static string? KlipySlug(Uri page) => IsHost(page, "klipy.com") && KlipyPath().Match(page.AbsolutePath) is { Success: true } match
        ? match.Groups[1].Value : null;
    private static bool IsHost(Uri uri, string root) =>
        uri.Host.Equals(root, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);
    private static async Task<List<Uri>> KlipyCandidatesAsync(string slug, CancellationToken token)
    {
        byte[]? json;
        try { json = await ImagePipeline.Shared.LoadTransientContentAsync(new Uri($"https://api.klipy.com/api/v1/gifs/{Uri.EscapeDataString(slug)}"), token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return []; }
        return json is null ? [] : KlipyCandidates(json);
    }
    internal static List<Uri> KlipyCandidates(byte[] json)
    {
        var candidates = new List<Uri>();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("data", out var data) || !data.TryGetProperty("file", out var file)) return candidates;
            foreach (var size in new[] { "md", "hd", "sm", "xs" })
            {
                if (!file.TryGetProperty(size, out var variant) || !variant.TryGetProperty("gif", out var gif)
                    || !gif.TryGetProperty("url", out var url)) continue;
                if (Uri.TryCreate(url.GetString(), UriKind.Absolute, out var media) && ChatMediaPolicy.IsImageUri(media)) candidates.Add(media);
            }
        }
        catch (JsonException) { }
        return candidates;
    }
    private static async Task<List<Uri>> ScrapedMediaAsync(Uri page, CancellationToken token)
    {
        var media = new List<Uri>();
        byte[]? bytes;
        try { bytes = await ImagePipeline.Shared.LoadTransientContentAsync(page, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return media; }
        if (bytes is null || bytes.Length > 512 * 1024) return media;
        var html = Encoding.UTF8.GetString(bytes);
        foreach (var match in PageMedia().Matches(html).Cast<Match>()
            .Concat(PageMediaReversed().Matches(html).Cast<Match>())
            .Concat(PageVideo().Matches(html).Cast<Match>())
            .Concat(PageVideoReversed().Matches(html).Cast<Match>()))
        {
            if (Uri.TryCreate(match.Groups[1].Value.Replace("&amp;", "&", StringComparison.Ordinal), UriKind.Absolute, out var uri)
                && (ChatMediaPolicy.IsImageUri(uri) || ChatMediaPolicy.IsVideoUri(uri)) && !media.Contains(uri)) media.Add(uri);
        }
        return media;
    }
    private static BitmapSource CreateVideoPoster()
    {
        var poster = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        poster.Freeze();
        return poster;
    }
    [GeneratedRegex("property=\"og:image\"\\s+content=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PageMedia();
    [GeneratedRegex("content=\"([^\"]+)\"\\s+property=\"og:image\"", RegexOptions.IgnoreCase)]
    private static partial Regex PageMediaReversed();
    [GeneratedRegex("property=\"(?:og:video|og:video:url|og:video:secure_url|twitter:player:stream)\"\\s+content=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PageVideo();
    [GeneratedRegex("content=\"([^\"]+)\"\\s+property=\"(?:og:video|og:video:url|og:video:secure_url|twitter:player:stream)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PageVideoReversed();
    internal static ChatMedia Decode(byte[] bytes) => Decode(bytes, 960);
    private static ChatMedia Decode(byte[] bytes, int decodeWidth)
    {
        if (bytes.Length > MaxBytes) throw new IOException("Images can be up to 8 MB.");
        var animated = bytes.Length >= 6 && bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8) || bytes.Length >= 6 && bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8);
        if (animated) ValidateGif(bytes);
        using var memory = new ReleasingImageStream(bytes);
        var metadata = BitmapDecoder.Create(memory, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        if ((long)metadata.PixelWidth * metadata.PixelHeight > 40_000_000 || metadata.PixelWidth > 32768 || metadata.PixelHeight > 32768) throw new IOException("Image dimensions are too large.");
        memory.Position = 0;
        var poster = new BitmapImage(); poster.BeginInit(); poster.CacheOption = BitmapCacheOption.OnLoad;
        var scale = Math.Min(1, Math.Clamp(decodeWidth, 1, 1920) / (double)Math.Max(metadata.PixelWidth, metadata.PixelHeight));
        poster.DecodePixelWidth = Math.Max(1, (int)(metadata.PixelWidth * scale)); poster.StreamSource = memory; poster.EndInit(); poster.Freeze();
        return new(bytes, poster, animated);
    }
    // Scan GIF blocks before giving compressed data to the animator; never expand every frame into memory.
    internal static int ValidateGif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14) throw new IOException("Incomplete GIF.");
        var width = Read16(bytes, 6); var height = Read16(bytes, 8);
        if (width is <= 0 or > 1536 || height is <= 0 or > 1536 || (long)width * height > 1_500_000) throw new IOException("This GIF exceeds the playback size limit.");
        var at = 13 + ((bytes[10] & 0x80) != 0 ? 3 * (1 << ((bytes[10] & 7) + 1)) : 0);
        var frames = 0; long pixels = 0;
        while (at < bytes.Length)
        {
            var marker = bytes[at++];
            if (marker == 0x3b) { if (frames == 0) throw new IOException("Empty GIF."); return frames; }
            if (marker == 0x21) { if (at >= bytes.Length) break; at++; }
            else if (marker == 0x2c)
            {
                if (at + 9 > bytes.Length) break;
                var frameWidth = Read16(bytes, at + 4); var frameHeight = Read16(bytes, at + 6);
                if (frameWidth <= 0 || frameHeight <= 0 || Read16(bytes, at) + frameWidth > width || Read16(bytes, at + 2) + frameHeight > height)
                    throw new IOException("Invalid GIF dimensions.");
                pixels += (long)frameWidth * frameHeight;
                if (++frames > 300 || pixels > 120_000_000) throw new IOException("This GIF exceeds the playback resource limit.");
                var packed = bytes[at + 8]; at += 9;
                if ((packed & 0x80) != 0) at += 3 * (1 << ((packed & 7) + 1));
                at++; // LZW minimum code size
            }
            else throw new IOException("Unsupported GIF data.");
            var terminated = false;
            while (at < bytes.Length)
            {
                var length = bytes[at++]; if (length == 0) { terminated = true; break; }
                at += length;
            }
            if (!terminated || at > bytes.Length) break;
        }
        throw new IOException("Incomplete GIF.");
    }
    private static int Read16(ReadOnlySpan<byte> bytes, int at) => bytes[at] | bytes[at + 1] << 8;
}
