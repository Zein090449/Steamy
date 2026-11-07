using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;
using Microsoft.Win32;

namespace Steamy.Pages;

public partial class EmulatorsPage : Page
{
    public EmulatorsPage()
    {
        InitializeComponent();
        var viewModel = App.Services.GetRequiredService<EmulatorsViewModel>();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.RefreshInstalledState();
        SizeChanged += (_, args) => viewModel.Columns = args.NewSize.Width < 1060 ? 1 : 2;
    }

    private void AddGameFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EmulatorCardViewModel card }) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Add a {card.Name} game",
            Filter = card.GameFileFilter,
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) card.AddGames(dialog.FileNames);
    }

    private void DownloadGameFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EmulatorCardViewModel card }) return;
        // Open dlps_downloader download page - integrates full pipeline (1fichier API, FDM, post-processing)
        OpenDlpsDownloadPage(card.Name);
    }

    private void OpenDlpsDownloadPage(string gameTitle)
    {
        try
        {
            // Use the dlps_downloader CLI to open the game's download page
            // This invokes: dlps open "GameTitle"
            // Which queries dlpsgame.com and opens the download page in the default browser
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = $"/c dlps open \"{gameTitle}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // Fallback: open the GitHub repo if dlps CLI fails
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/jayjun911/dlps_downloader") { UseShellExecute = true });
            }
            catch
            {
                // Ignore errors
            }
        }
    }

    private void AddGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EmulatorCardViewModel card }) return;
        var dialog = new OpenFolderDialog
        {
            Title = $"Add a {card.Name} game folder",
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) card.AddGames([dialog.FolderName]);
    }
}