using System.IO;
using System.Windows.Media.Imaging;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed class EmulatedPlayButtonTests
{
    [Fact]
    public async Task DownloadedArtifactInOutputFolderSwapsDownloadForPlay()
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-emulated-play-" + Guid.NewGuid().ToString("N"));
        var dlps = Path.Combine(root, "dlps");
        var outDir = Path.Combine(root, "Z");
        Directory.CreateDirectory(Path.Combine(dlps, "data"));
        Directory.CreateDirectory(outDir);
        var artifact = Path.Combine(outDir, "ANIMAL WELL [PPSA22520][v01.00][GAME].7z");
        try
        {
            await File.WriteAllTextAsync(artifact, "archive");
            await File.WriteAllTextAsync(Path.Combine(dlps, "data", "downloaded-ps5.xml"),
                "<Downloaded><Game><Title>Animal Well</Title><FileName>ANIMAL WELL [PPSA22520][v01.00][GAME].7z</FileName><PPSA>PPSA22520</PPSA></Game></Downloaded>");
            var model = CreateModel();
            model.DlpsPath = dlps;
            model.DlpsDownloadDir = outDir;
            model.SelectedDlpsGame = new DlpsGameResult("animal-well", "Animal Well", null, "PlayStation 5", "");
            try
            {
                Assert.True(model.HasDlpsLocalGame);
                Assert.True(model.ShowDlpsPlayButton);
                Assert.False(model.ShowDlpsDownloadButton);
                Assert.Equal(artifact, model.DlpsLocalGamePath);
            }
            finally { await model.OnNavigatedFromAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PpsaMatchFindsArtifactWhenRecordedFileNameChangedOnDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-emulated-ppsa-" + Guid.NewGuid().ToString("N"));
        var dlps = Path.Combine(root, "dlps");
        var outDir = Path.Combine(root, "Z");
        Directory.CreateDirectory(Path.Combine(dlps, "data"));
        Directory.CreateDirectory(outDir);
        var artifact = Path.Combine(outDir, "ANIMAL WELL [PPSA22520][v01.000.011][GAME].exfat");
        try
        {
            await File.WriteAllTextAsync(artifact, "image");
            await File.WriteAllTextAsync(Path.Combine(dlps, "data", "downloaded-ps5.xml"),
                "<Downloaded><Game><Title>Animal Well</Title><FileName>ANIMAL WELL [PPSA22520][v01.00][GAME].7z</FileName><PPSA>PPSA22520</PPSA></Game></Downloaded>");
            var model = CreateModel();
            model.DlpsPath = dlps;
            model.DlpsDownloadDir = outDir;
            model.SelectedDlpsGame = new DlpsGameResult("animal-well", "Animal Well", null, "PlayStation 5", "");
            try
            {
                Assert.True(model.HasDlpsLocalGame);
                Assert.Equal(artifact, model.DlpsLocalGamePath);
            }
            finally { await model.OnNavigatedFromAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TitleScanFindsExtractedFolderWithoutADatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-emulated-scan-" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(root, "Z");
        var folder = Path.Combine(outDir, "Animal Well [PPSA22520][v01.00]");
        Directory.CreateDirectory(folder);
        try
        {
            var model = CreateModel();
            model.DlpsPath = Path.Combine(root, "missing-dlps");
            model.DlpsDownloadDir = outDir;
            model.SelectedDlpsGame = new DlpsGameResult("animal-well", "Animal Well", null, "PlayStation 5", "");
            try
            {
                Assert.True(model.HasDlpsLocalGame);
                Assert.Equal(folder, model.DlpsLocalGamePath);
            }
            finally { await model.OnNavigatedFromAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingArtifactKeepsTheDownloadButton()
    {
        var root = Path.Combine(Path.GetTempPath(), "Steamy-emulated-missing-" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(root, "Z");
        Directory.CreateDirectory(outDir);
        try
        {
            var model = CreateModel();
            model.DlpsPath = Path.Combine(root, "missing-dlps");
            model.DlpsDownloadDir = outDir;
            model.SelectedDlpsGame = new DlpsGameResult("animal-well", "Animal Well", null, "PlayStation 5", "");
            try
            {
                Assert.False(model.HasDlpsLocalGame);
                Assert.True(model.ShowDlpsDownloadButton);
                Assert.False(model.ShowDlpsPlayButton);
                Assert.Null(model.DlpsLocalGamePath);
            }
            finally { await model.OnNavigatedFromAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }

    private static LibraryViewModel CreateModel() => new(new AppDataStore(), new NavigationService(),
        (ILoggingService)OfflineServiceProxy.Create(typeof(ILoggingService)),
        (ISteamCatalogService)OfflineServiceProxy.Create(typeof(ISteamCatalogService)),
        (IRyuuCatalogService)OfflineServiceProxy.Create(typeof(IRyuuCatalogService)),
        new Artwork(),
        (ILibrarySyncService)OfflineServiceProxy.Create(typeof(ILibrarySyncService)),
        (IHubcapCatalogService)OfflineServiceProxy.Create(typeof(IHubcapCatalogService)),
        (IFreeManifestCatalogService)OfflineServiceProxy.Create(typeof(IFreeManifestCatalogService)));

    private sealed class Artwork : IArtworkService
    {
        public Task<BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default) => Task.FromResult<BitmapImage?>(null);
        public Task LoadAsync(Game game, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadHeadersAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadManyAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}