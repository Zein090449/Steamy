namespace Steamy.Controls;

/// <summary>Twemoji asset URLs (jdecked fork, pinned). WPF renders emoji text monochrome, so emoji display as images.</summary>
public static class Twemoji
{
    private const string Base = "https://cdn.jsdelivr.net/gh/jdecked/twemoji@15.1.0/assets/72x72/";

    public static string? Url(string emoji)
    {
        var file = File(emoji);
        return file.Length == 0 ? null : Base + file + ".png";
    }

    // Twemoji naming: hex codepoints joined with '-', VS16 dropped unless the sequence contains a ZWJ.
    public static string File(string emoji)
    {
        if (emoji.Length == 0) return "";
        if (!emoji.Contains('\u200D')) emoji = emoji.Replace("\ufe0f", "");
        return string.Join("-", emoji.EnumerateRunes().Select(r => r.Value.ToString("x")));
    }
}
