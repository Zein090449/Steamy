using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Steamy.Controls;

/// <summary>Renders chat text: twemoji images, oversized emoji-only messages, custom Discord emoji, markdown links.</summary>
public sealed partial class MessageContent : TextBlock
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string),
        typeof(MessageContent), new PropertyMetadata("", (d, _) => ((MessageContent)d).Rebuild()));

    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    private static readonly string EmojiFont = "Segoe UI Emoji, Segoe UI Variable Text, Segoe UI";
    private static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xa8, 0xfc));
    [GeneratedRegex(@"<(a)?:(\w{2,64}):(\d{5,32})>", RegexOptions.CultureInvariant)]
    private static partial Regex CustomEmoji();
    [GeneratedRegex(@"\[([^\]\n]{1,120})\]\((https://[^)\s]{1,600})\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();
    // One emoji cluster: keycaps, flag pairs, astral emoji, emoji-default BMP symbols, and text-default
    // BMP symbols only when followed by VS16. The suffix extends the match across skin tones and ZWJ chains.
    private const string EmojiCore = @"(?:(?:[#*0-9]\uFE0F?\u20E3)|(?:\uD83C[\uDDE6-\uDDFF]){2}|[\uD83C\uD83D\uD83E][\uDC00-\uDFFF]"
        + @"|[\u231A\u231B\u23E9-\u23EC\u23F0\u23F3\u25FD\u25FE\u2614\u2615\u2648-\u2653\u267F\u2693\u26A1\u26AA\u26AB\u26BD\u26BE\u26C4\u26C5\u26CE\u26D4\u26EA\u26F2\u26F3\u26F5\u26FA\u26FD\u2705\u270A\u270B\u2728\u274C\u274E\u2753-\u2755\u2757\u2795-\u2797\u27B0\u27BF\u2B1B\u2B1C\u2B50\u2B55]"
        + @"|[\u00A9\u00AE\u203C\u2049\u2122\u2139\u2194-\u21AA\u2328\u23CF\u23ED-\u23EF\u23F1\u23F2\u23F8-\u23FA\u24C2\u25AA\u25AB\u25B6\u25C0\u25FB\u25FC\u2600-\u27BF\u2934\u2935\u2B00-\u2B1A\u2B20-\u2B54\u3030\u303D\u3297\u3299]\uFE0F)";
    private const string EmojiCluster = EmojiCore + @"(?:\uFE0F|\uD83C[\uDFFB-\uDFFF]|\u200D" + EmojiCore + @")*\uFE0F?";
    [GeneratedRegex(EmojiCluster, RegexOptions.CultureInvariant)]
    private static partial Regex Emoji();
    [GeneratedRegex(@"^[\p{Zs}\t\r\n]*(?:<a?:\w{2,64}:\d{5,32}>" + EmojiCluster + @"){1,3}[\p{Zs}\t\r\n]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EmojiOnly();

    static MessageContent() => LinkBrush.Freeze();

    public MessageContent()
    {
        // Derived from TextBlock, so implicit TextBlock styles don't apply; pick up the theme text color explicitly.
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        FontFamily = new FontFamily(EmojiFont);
        TextWrapping = TextWrapping.Wrap;
        Rebuild();
    }

    private void Rebuild()
    {
        Inlines.Clear();
        var text = Message ?? "";
        if (text.Length == 0) return;
        if (text.Length > 3800) text = text[..3800] + "…";
        var emojiOnly = text.Length <= 64 && EmojiOnly().IsMatch(text) && CustomEmoji().Matches(text).Count is 1 or 2 or 3;
        FontSize = emojiOnly ? 38 : 14;
        var position = 0;
        var matches = CustomEmoji().Matches(text).Cast<Match>()
            .Concat(MarkdownLink().Matches(text).Cast<Match>())
            .OrderBy(match => match.Index).ToList();
        foreach (var match in matches)
        {
            if (match.Index < position) continue;
            if (match.Index > position) AddText(text[position..match.Index], emojiOnly);
            if (match.Value.StartsWith('<')) AddCustomEmoji(match, emojiOnly);
            else AddLink(match);
            position = match.Index + match.Length;
        }
        if (position < text.Length) AddText(text[position..], emojiOnly);
    }

    private void AddText(string text, bool emojiOnly)
    {
        if (text.Length == 0) return;
        var position = 0;
        foreach (var match in Emoji().Matches(text).Cast<Match>())
        {
            if (match.Index > position) Inlines.Add(new Run(text[position..match.Index]) { FontSize = emojiOnly ? 38 : 14 });
            AddEmojiImage(match.Value, emojiOnly);
            position = match.Index + match.Length;
        }
        if (position < text.Length) Inlines.Add(new Run(text[position..]) { FontSize = emojiOnly ? 38 : 14 });
    }

    private void AddEmojiImage(string emoji, bool emojiOnly)
    {
        var url = Twemoji.Url(emoji);
        if (url is null) { Inlines.Add(new Run(emoji) { FontSize = emojiOnly ? 38 : 14 }); return; }
        var size = emojiOnly ? 42 : 21;
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        AsyncArtwork.SetPrivate(image, false);
        AsyncArtwork.SetWidth(image, size * 2);
        AsyncArtwork.SetHeight(image, size * 2);
        AsyncArtwork.SetUrl(image, url);
        Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom });
    }

    private void AddCustomEmoji(Match match, bool emojiOnly)
    {
        var animated = match.Groups[1].Value.Length > 0;
        var id = match.Groups[3].Value;
        var url = $"https://cdn.discordapp.com/emojis/{id}.{(animated ? "gif" : "png")}";
        var size = emojiOnly ? 44 : 22;
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        AsyncArtwork.SetPrivate(image, true);
        AsyncArtwork.SetWidth(image, size * 2);
        AsyncArtwork.SetHeight(image, size * 2);
        AsyncArtwork.SetUrl(image, url);
        var container = new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom };
        container.ToolTip = match.Groups[2].Value;
        Inlines.Add(container);
        Inlines.Add(new Run(" ") { FontSize = emojiOnly ? 38 : 14 });
    }

    private void AddLink(Match match)
    {
        if (!Uri.TryCreate(match.Groups[2].Value, UriKind.Absolute, out var uri) || uri.Scheme != "https") { AddText(match.Value, false); return; }
        var label = match.Groups[1].Value;
        var link = new Hyperlink(new Run(label.Length > 0 ? label : uri.Host) { FontSize = 14 })
        { Foreground = LinkBrush, ToolTip = uri.AbsoluteUri, NavigateUri = uri };
        link.RequestNavigate += (_, args) =>
        {
            try { using var opened = Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { }
            args.Handled = true;
        };
        Inlines.Add(link);
    }
}
