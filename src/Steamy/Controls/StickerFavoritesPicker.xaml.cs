using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Steamy.ViewModels;

namespace Steamy.Controls;

public partial class StickerFavoritesPicker : UserControl
{
    public StickerFavoritesPicker()
    {
        InitializeComponent(); DataContext = App.Services.GetRequiredService<StickerFavoritesViewModel>();
    }
    public void FocusInput() => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => LinkBox.Focus()));
    private void Link_KeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || DataContext is not StickerFavoritesViewModel model) return;
        if (model.AddCommand.CanExecute(null)) model.AddCommand.Execute(null);
        args.Handled = true;
    }
}
