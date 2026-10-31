using System.Text.RegularExpressions;

namespace Steamy.Services;

public static partial class ChatMediaPolicy
{
    [GeneratedRegex("https://[^\\s<>\"]+", RegexOptions.IgnoreCase)]
    private static partial Regex Links();
    public static bool IsImageUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && IsMediaHost(uri.Host)
        && System.IO.Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".apng";
    public static bool IsVideoUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && IsMediaHost(uri.Host)
        && System.IO.Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".mp4" or ".webm";
    public static bool IsStickerUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && (uri.Host is "media.discordapp.net" or "cdn.discordapp.com")
        && uri.AbsolutePath.Contains("/stickers/", StringComparison.OrdinalIgnoreCase);
    private static bool IsMediaHost(string host) => host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".discordapp.net", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".giphy.com", StringComparison.OrdinalIgnoreCase)
        || host is "static.klipy.com" or "static1.klipy.com" or "static2.klipy.com" || host.EndsWith(".klipy.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".tenor.com", StringComparison.OrdinalIgnoreCase)
        || host is "i.imgur.com" or "i.redd.it" or "preview.redd.it" or "external-preview.redd.it";
    // GIF share pages (tenor/imgur/klipy/giphy) do not point at the media file itself; the loader resolves them to the embedded image.
    public static bool IsPageUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && (uri.Host is "tenor.com" or "imgur.com" or "klipy.com" or "giphy.com"
            || uri.Host.EndsWith(".tenor.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".imgur.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".klipy.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".giphy.com", StringComparison.OrdinalIgnoreCase));
    public static bool IsMediaReference(Uri uri) => IsImageUri(uri) || IsVideoUri(uri) || IsPageUri(uri);
    public static string? ImageUrl(string content)
    {
        foreach (Match match in Links().Matches(content))
            if (Uri.TryCreate(match.Value.TrimEnd(')', ']', '.', ',', '!'), UriKind.Absolute, out var uri)
                && IsMediaReference(uri) && !IsStickerUri(uri)) return uri.AbsoluteUri;
        return null;
    }
    public static string? StickerUrl(string content)
    {
        foreach (Match match in Links().Matches(content))
            if (Uri.TryCreate(match.Value.TrimEnd(')', ']', '.', ',', '!'), UriKind.Absolute, out var uri) && IsStickerUri(uri)) return uri.AbsoluteUri;
        return null;
    }
    // Discord's sticker CDN links carry a human name (?name=im+in); use it when there is no markdown label.
    public static string? StickerQueryName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            if (split <= 0 || !pair[..split].Equals("name", StringComparison.OrdinalIgnoreCase)) continue;
            var value = pair[(split + 1)..].Replace('+', ' ');
            try { value = Uri.UnescapeDataString(value); } catch (UriFormatException) { }
            value = value.Trim();
            if (value.Length is > 0 and <= 80) return value;
        }
        return null;
    }
    public static string? VideoUrl(string content)
    {
        foreach (Match match in Links().Matches(content))
            if (Uri.TryCreate(match.Value.TrimEnd(')', ']', '.', ',', '!'), UriKind.Absolute, out var uri) && IsVideoUri(uri)) return uri.AbsoluteUri;
        return null;
    }
    public static string? MarkdownText(string content, string url)
    {
        foreach (Match match in Markdown().Matches(content))
            if (string.Equals(match.Groups[2].Value, url, StringComparison.OrdinalIgnoreCase)) return match.Groups[1].Value;
        return null;
    }
    public static bool IsOnlyMarkdownLinkTo(string content, string url)
    {
        var text = content.Trim();
        var match = Markdown().Match(text);
        return match.Success && match.Index == 0 && match.Length == text.Length
            && string.Equals(match.Groups[2].Value, url, StringComparison.OrdinalIgnoreCase);
    }
    [GeneratedRegex(@"\[([^\]\n]{1,160})\]\((https://[^)\s]{1,600})\)", RegexOptions.IgnoreCase)]
    private static partial Regex Markdown();
}
