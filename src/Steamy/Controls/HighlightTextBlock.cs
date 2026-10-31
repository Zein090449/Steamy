using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Steamy.Controls;

/// <summary>TextBlock that paints every occurrence of Term in the accent color (search highlighting).</summary>
public sealed class HighlightTextBlock : TextBlock
{
    // Bound through SourceText instead of Text on purpose: mutating Inlines re-projects
    // TextBlock.Text, so a Text binding gets cleared/echoed by every rebuild and the
    // name would render empty after the first deferred rebuild.
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(nameof(SourceText), typeof(string), typeof(HighlightTextBlock), new PropertyMetadata("", ScheduleRebuild));
    public static readonly DependencyProperty TermProperty = DependencyProperty.Register(nameof(Term), typeof(string), typeof(HighlightTextBlock), new PropertyMetadata("", ScheduleRebuild));
    private bool _rebuildQueued;

    public HighlightTextBlock()
    {
        TextWrapping = TextWrapping.NoWrap;
    }

    public string SourceText { get => (string)GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public string Term { get => (string)GetValue(TermProperty); set => SetValue(TermProperty, value); }

    private static void ScheduleRebuild(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is HighlightTextBlock block) block.QueueRebuild();
    }

    // Rebuilding inline in the property-change callback re-enters the text container while WPF is
    // still processing the update; queue the work onto a clean dispatcher frame instead.
    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, RebuildNow);
    }

    private void RebuildNow()
    {
        _rebuildQueued = false;
        var text = SourceText ?? "";
        var term = (Term ?? "").Trim();
        Inlines.Clear();
        if (term.Length == 0)
        {
            if (text.Length > 0) Inlines.Add(text);
            return;
        }
        var cursor = 0;
        while (true)
        {
            var hit = text.IndexOf(term, cursor, StringComparison.CurrentCultureIgnoreCase);
            if (hit < 0)
            {
                if (cursor < text.Length) Inlines.Add(text[cursor..]);
                break;
            }
            if (hit > cursor) Inlines.Add(text[cursor..hit]);
            Inlines.Add(new Run(text.Substring(hit, term.Length)) { Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xa8, 0xfc)), FontWeight = FontWeights.Bold });
            cursor = hit + term.Length;
        }
    }
}
