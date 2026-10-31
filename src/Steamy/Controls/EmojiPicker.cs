using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Steamy.Controls;

/// <summary>Searchable emoji grid with a recents row. Emits real unicode, never images or shortcodes.</summary>
public sealed class EmojiPicker : StackPanel
{
    public static readonly DependencyProperty RecentsProperty = DependencyProperty.Register(nameof(Recents), typeof(IReadOnlyList<string>),
        typeof(EmojiPicker), new PropertyMetadata(null, (d, _) => ((EmojiPicker)d).Rebuild()));

    public IReadOnlyList<string>? Recents { get => (IReadOnlyList<string>?)GetValue(RecentsProperty); set => SetValue(RecentsProperty, value); }
    public event EventHandler<string>? Picked;

    private const string EmojiFont = "Segoe UI Emoji, Segoe UI";
    private readonly TextBox _search;
    private readonly ScrollViewer _listHost;
    private readonly WrapPanel _list = new();

    private static readonly (string Emoji, string Name)[] Catalog =
    [
        ("😀", "grinning smile happy"), ("😃", "smiley happy"), ("😄", "laughing joy"), ("😁", "grin beam"), ("😅", "sweat laugh nervous"),
        ("🤣", "rofl rolling laugh"), ("😂", "joy tears laugh"), ("🙂", "slight smile"), ("😉", "wink"), ("😊", "blush happy"),
        ("😍", "heart eyes love"), ("😘", "kiss"), ("😜", "zany tongue"), ("🤪", "crazy zany"), ("😎", "cool sunglasses"),
        ("🤩", "star struck"), ("🥳", "party celebrate"), ("😏", "smirk"), ("😌", "relieved"), ("😴", "sleep zzz"),
        ("🤤", "drool"), ("😪", "sleepy"), ("🥱", "yawn"), ("🙄", "eye roll"), ("🤔", "thinking hmm"),
        ("🤨", "raised eyebrow"), ("😐", "neutral"), ("😑", "expressionless"), ("😶", "quiet mute"),
        ("😔", "sad pensive"), ("🙁", "frown sad"), ("😢", "cry tear"), ("😭", "sob cry loud"), ("😤", "triumph steam"),
        ("😠", "angry mad"), ("😡", "rage angry"), ("🤬", "swearing cursing"), ("🤯", "mind blown explode"), ("😳", "flushed shy"),
        ("🥵", "hot heat"), ("🥶", "cold freeze"), ("😱", "scream fear"), ("😨", "fearful scared"), ("🤗", "hug"),
        ("🤭", "oops hand"), ("🤫", "shush quiet"), ("🥺", "pleading puppy"), ("😒", "unamused meh"), ("😬", "grimace awkward"),
        ("🙏", "pray thanks please"), ("👏", "clap applause"), ("👍", "thumbs up like ok yes"), ("👎", "thumbs down dislike no"),
        ("👌", "ok perfect"), ("✌️", "peace victory"), ("🤙", "call shaka"), ("💪", "muscle strong flex"), ("🫡", "salute respect"),
        ("👋", "wave hi hello bye"), ("🤝", "handshake deal"), ("✊", "fist bump"), ("🖐️", "hand five"), ("🫶", "heart hands love"),
        ("❤️", "red heart love"), ("🧡", "orange heart"), ("💛", "yellow heart"), ("💚", "green heart"), ("💙", "blue heart"),
        ("💜", "purple heart"), ("🖤", "black heart"), ("🤍", "white heart"), ("💔", "broken heart sad"), ("💖", "sparkle heart"),
        ("💯", "hundred perfect score"), ("🔥", "fire lit hot flame"), ("⭐", "star favorite"), ("🌟", "glowing star"), ("✨", "sparkles shine"),
        ("🎉", "party popper celebrate yay"), ("🎊", "confetti celebrate"), ("🎈", "balloon"), ("🎁", "gift present"), ("🏆", "trophy win"),
        ("🥇", "gold medal first"), ("🎮", "game controller gaming"), ("🕹️", "joystick arcade"), ("🎯", "target bullseye aim"),
        ("⚽", "soccer football"), ("🏀", "basketball"), ("🏈", "football american"), ("🏓", "ping pong table tennis"), ("🎲", "dice roll random"),
        ("💾", "save disk"), ("🖥️", "computer desktop pc"), ("⌨️", "keyboard"), ("🖱️", "mouse click"), ("📱", "phone mobile"),
        ("🔌", "plug power"), ("🔋", "battery energy"), ("⚡", "zap lightning fast power"), ("🛠️", "tools fix build"), ("⚙️", "gear settings"),
        ("🚀", "rocket launch ship fast"), ("✈️", "plane travel"), ("🚗", "car drive"), ("🏁", "checkered flag race finish"), ("🚨", "police alert emergency"),
        ("✅", "check done yes complete"), ("☑️", "checkbox done"), ("❌", "cross no fail wrong"), ("⚠️", "warning careful"), ("❓", "question what huh"),
        ("❗", "exclamation important"), ("💬", "speech chat message"), ("💭", "thought think"), ("👁️", "eye watch look"), ("👀", "eyes look watching"),
        ("🍿", "popcorn movie drama"), ("☕", "coffee tea drink"), ("🍕", "pizza food"), ("🍔", "burger food"), ("🍜", "noodles ramen food"),
        ("🍩", "donut sweet"), ("🍪", "cookie snack"), ("🎂", "cake birthday"), ("🍺", "beer drink cheers"), ("🥤", "soda drink"),
        ("🌙", "moon night"), ("☀️", "sun day bright"), ("🌈", "rainbow"), ("☁️", "cloud weather"), ("🌧️", "rain weather"),
        ("🐷", "pig oink"), ("🐶", "dog woof puppy"), ("🐱", "cat meow kitten"), ("🦊", "fox"), ("🐸", "frog"), ("🐔", "chicken"),
        ("🐲", "dragon"), ("🦄", "unicorn magic"), ("👑", "crown king royal"), ("💎", "gem diamond"), ("💰", "money bag rich"),
        ("🎮", "gaming"), ("🛡️", "shield defense"), ("⚔️", "swords battle fight"), ("🧙", "wizard mage magic"), ("🥷", "ninja stealth"),
        ("👻", "ghost boo spooky"), ("💀", "skull dead death"), ("🎃", "pumpkin halloween"), ("🤖", "robot bot"), ("👽", "alien ufo"),
        ("😂", "lol again"), ("🆗", "ok button"), ("🆕", "new"), ("🔝", "top"), ("🔴", "red circle live dot"), ("🟢", "green circle online"),
        ("🕐", "clock time"), ("📌", "pin important"), ("📎", "paperclip attach"), ("🔒", "lock private"), ("🗝️", "key unlock")
    ];

    public EmojiPicker()
    {
        Width = 328; MaxHeight = 340;
        _search = new TextBox { FontSize = 13 };
        _search.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
        var hint = new TextBlock { Text = "Search emoji", IsHitTestVisible = false, Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var searchHost = new Grid { Margin = new(10, 10, 10, 6) };
        searchHost.Children.Add(_search);
        searchHost.Children.Add(hint);
        _search.TextChanged += (_, _) => { hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Rebuild(); };
        _listHost = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(4, 0, 4, 8) };
        Children.Add(searchHost);
        Children.Add(_listHost);
        Rebuild();
        _search.PreviewKeyDown += (_, args) => { if (args.Key is Key.Escape) { KeyDownOverride?.Invoke(this, args); } };
    }
    internal event EventHandler<KeyEventArgs>? KeyDownOverride;

    private void Rebuild()
    {
        _list.Children.Clear();
        var query = _search.Text.Trim();
        var recents = Recents ?? [];
        if (query.Length == 0 && recents.Count > 0)
        {
            AddSection("Recently used");
            foreach (var emoji in recents.Take(12)) AddEmoji(emoji);
        }
        if (query.Length == 0) AddSection("All emoji");
        var matches = 0;
        foreach (var (emoji, name) in Catalog)
        {
            if (query.Length > 0 && !name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (++matches > 96) break;
            AddEmoji(emoji);
        }
        if (query.Length > 0 && matches == 0)
        {
            var empty = new TextBlock { Text = "No emoji match that.", FontSize = 12, Margin = new(12, 14, 12, 12), Opacity = 0.75 };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            _list.Children.Add(empty);
        }
    }
    private void AddSection(string title)
    {
        var label = new TextBlock { Text = title, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new(12, 8, 4, 4) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        _list.Children.Add(label);
    }
    private void AddEmoji(string emoji)
    {
        object content;
        var url = Twemoji.Url(emoji);
        if (url is null)
        {
            content = new TextBlock { Text = emoji, FontSize = 21, FontFamily = new FontFamily(EmojiFont), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        else
        {
            var image = new System.Windows.Controls.Image { Width = 26, Height = 26, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            AsyncArtwork.SetPrivate(image, false);
            AsyncArtwork.SetWidth(image, 52);
            AsyncArtwork.SetHeight(image, 52);
            AsyncArtwork.SetUrl(image, url);
            content = image;
        }
        var button = new System.Windows.Controls.Button
        {
            Content = content,
            Width = 40, Height = 40, Margin = new(2), Padding = new(0), BorderThickness = new(0), Cursor = Cursors.Hand,
            Background = Brushes.Transparent, ToolTip = Describe(emoji)
        };
        button.Click += (_, args) => { args.Handled = true; Picked?.Invoke(this, emoji); };
        button.MouseEnter += (_, _) => button.Opacity = 0.85;
        button.MouseLeave += (_, _) => button.Opacity = 1;
        _list.Children.Add(button);
    }
    private static string Describe(string emoji) { var match = Array.Find(Catalog, entry => entry.Emoji == emoji); return match.Name.Length > 0 ? match.Name : "Emoji"; }
}
