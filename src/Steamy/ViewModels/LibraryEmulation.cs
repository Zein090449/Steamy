using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed partial class LibraryViewModel
{
    private const string EmulatedPlatformName = "Emulated";
    private const string AllDlpsPlatforms = "All platforms";

    private IEmulatorInstallService? _emulators;
    private ISettingsService? _settingsService;
    private string _platform = "Steam";
    private string _dlpsPlatform = "PlayStation 5";
    private string _dlpsSearch = string.Empty;
    private string _emulatedStatus = string.Empty;
    private string _emulatedLocalStatus = string.Empty;
    private string _dlpsPath = string.Empty;
    private string _nodePath = "node";
    private string _dlpsDownloadDir = @"C:\Z";
    private string _dlpsEnvPath = string.Empty;
    private string _dlpsApiKeyStatus = "API key not loaded.";
    private string _dlpsLog = string.Empty;
    private string _dlpsDetailsDescription = string.Empty;
    private string _dlpsDetailsReleased = string.Empty;
    private string _dlpsDetailsGenre = string.Empty;
    private string _dlpsDetailsStatus = string.Empty;
    private string _dlpsDetailsHero = string.Empty;
    private string _dlpsApiKeyInput = string.Empty;
    private bool _dlpsDetailsLoading;
    private bool _emulatedLoading;
    private bool _dlpsDownloading;
    private int _emulatedPage;
    private int _emulatedCount;
    private bool _emulatedHasMore;
    private CancellationTokenSource? _emulatedRequest;
    private CancellationTokenSource? _dlpsDownloadCts;
    private Process? _dlpsProcess;
    private EmulatedEmulatorOption? _selectedEmulatedEmulator;
    private DlpsGameResult? _selectedDlpsGame;
    private string? _dlpsLocalGamePath;
    private readonly HttpClient _dlpsHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly (string Name, string Host, string Slug, string Category)[] DlpsPlatforms =
    [
        ("PlayStation 5", "https://dlpsgame.com", "list-game-ps5", "ps5"),
        ("PlayStation 4", "https://dlpsgame.com", "list-all-game-ps4", "ps4"),
        ("PlayStation 3", "https://dlpsgame.com", "list-all-game-ps3", "ps3"),
        ("PlayStation 2", "https://dlpsgame.com", "list-all-game-ps2", "ps2"),
        ("Nintendo Switch", "https://nswgame.com", "list-all-game-switch", "switch"),
        ("Nintendo Wii", "https://nswgame.com", "list-all-game-wii", "wii"),
        ("Nintendo Wii U", "https://nswgame.com", "list-all-game-wii-u", "wii-u"),
        ("Nintendo 3DS", "https://nswgame.com", "list-all-game-3ds", "3ds"),
        ("Xbox (JTAG/RGH)", "https://downloadgamexbox.com", "list-all-game-xbox-jtag", "xbox-360"),
        ("Xbox (ISO)", "https://downloadgamexbox.com", "list-all-game-xbox-iso", "xbox"),
        ("PSP", "https://downloadgamepsp.org", "list-all-game-psp-ppsspp", "psp"),
        ("PS Vita", "https://downloadgamepsp.org", "list-all-game-psvita", "ps-vita")
    ];

    // dlps_downloader writes one downloaded-<key>.xml per platform under its data folder.
    private static readonly Dictionary<string, string> DlpsPlatformKeys = new()
    {
        ["PlayStation 5"] = "ps5",
        ["PlayStation 4"] = "ps4",
        ["PlayStation 3"] = "ps3",
        ["PlayStation 2"] = "ps2",
        ["Nintendo Switch"] = "switch",
        ["Nintendo Wii"] = "wii",
        ["Nintendo Wii U"] = "wiiu",
        ["Nintendo 3DS"] = "3ds",
        ["Xbox (JTAG/RGH)"] = "xbox-jtag",
        ["Xbox (ISO)"] = "xbox-iso",
        ["PSP"] = "psp",
        ["PS Vita"] = "psvita"
    };

    // Archives the emulators cannot run directly; the user must extract/convert first.
    // (.exfat is handled separately — Play mounts and extracts it automatically.)
    private static readonly HashSet<string> NonLaunchableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".rar", ".zip", ".tar", ".gz", ".failed"
    };

    public string[] Platforms { get; } = ["Steam", EmulatedPlatformName];
    public ObservableCollection<string> EmulatedPlatforms { get; } = new(DlpsPlatforms.Select(p => p.Name));
    public ObservableCollection<EmulatedEmulatorOption> EmulatedEmulators { get; } = [];
    public RangeObservableCollection<DlpsGameResult> EmulatedCatalog { get; } = new();
    public RangeObservableCollection<EmulatedGameTile> EmulatedLocalGames { get; } = new();

    public ICommand AddEmulatedGameCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand OpenEmulatorsCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand RefreshEmulatedCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand LoadMoreEmulatedCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand OpenCatalogGameCommand { get; private set; } = new RelayCommand<DlpsGameResult>(_ => { });
    public ICommand DownloadCatalogGameCommand { get; private set; } = new RelayCommand<DlpsGameResult>(_ => { });
    public ICommand DlpsBrowseCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand DlpsSearchCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand DlpsDownloadSelectedCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand DlpsCancelDownloadCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand DlpsPickPathCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand DlpsPickDownloadDirCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand DlpsPickEnvCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand DlpsOpenRepoCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand DlpsLoadEnvCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand DlpsOpenDownloadFolderCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand DlpsOpenPageCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand CloseDlpsDetailsCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand SaveDlpsApiKeyCommand { get; private set; } = new RelayCommand(() => { });
    public ICommand PlaySelectedDlpsGameCommand { get; private set; } = new RelayCommand(() => { });

    // Compatibility aliases used by existing XAML/bindings if any remain.
    public ICommand OpenDlpsDownloaderCommand => DlpsOpenRepoCommand;
    public ICommand OpenCatalogProviderCommand => DlpsOpenRepoCommand;
    public ICommand OpenCatalogSetupCommand => DlpsPickEnvCommand;
    public ICommand SaveCatalogCredentialsCommand => DlpsLoadEnvCommand;
    public ObservableCollection<string> CatalogProviders { get; } = ["dlps_downloader"];
    public string SelectedCatalogProvider { get => "dlps_downloader"; set { } }
    public string SelectedProviderTitle => "dlps_downloader";
    public string CatalogAttribution => "Powered by dlps_downloader";
    public string CatalogFooter => "Browse games from dlpsgame.com and download through dlps_downloader. Use only content you are authorized to download.";
    public string CatalogCredentialTitle => "1fichier API key";
    public string CatalogCredentialHint => _dlpsApiKeyStatus;
    public string CatalogClientIdInput { get => string.Empty; set { } }
    public string CatalogClientSecretInput { get => string.Empty; set { } }
    public bool NeedsCatalogCredentials => IsEmulated && !IsDlpsApiKeyConfigured;

    public bool IsEmulated => _platform == EmulatedPlatformName;
    public bool IsSteam => !IsEmulated;
    public bool IsDlpsApiKeyConfigured => _dlpsApiKeyStatus.Contains("configured", StringComparison.OrdinalIgnoreCase);
    public bool HasEmulatedCatalog => EmulatedCatalog.Count > 0;
    public bool CanLoadMoreEmulated => _emulatedHasMore && !_emulatedLoading && !_dlpsDownloading;
    public bool HasInstalledEmulators => EmulatedEmulators.Count > 0;
    public bool HasEmulatedLocalGames => EmulatedLocalGames.Count > 0;
    public bool CanDownloadSelected => SelectedDlpsGame is not null && !_dlpsDownloading && !_emulatedLoading;
    public string SearchPlaceholder => "Search dlps games";
    public string EmulatedLocalEmptyHint => HasEmulatedLocalGames
        ? string.Empty
        : HasInstalledEmulators
            ? "Add a dump you own to launch it here with its emulator."
            : "Install an emulator first, then add your own game files here.";

    public string SelectedPlatform
    {
        get => _platform;
        set
        {
            if (!SetProperty(ref _platform, value)) return;
            OnPropertyChanged(nameof(IsEmulated));
            OnPropertyChanged(nameof(IsSteam));
            OnPropertyChanged(nameof(SearchPlaceholder));
            OnPropertyChanged(nameof(LibraryTitle));
            OnPropertyChanged(nameof(CatalogCountLabel));
            OnPropertyChanged(nameof(EmptyStateMessage));
            OnPropertyChanged(nameof(NeedsCatalogCredentials));
            if (IsEmulated) _ = PrepareEmulatedAsync();
            else
            {
                SelectedDlpsGame = null;
                CancelEmulatedWork();
                RefreshPage();
            }
        }
    }

    public string SelectedEmulatedPlatform
    {
        get => _dlpsPlatform;
        set
        {
            if (!SetProperty(ref _dlpsPlatform, value ?? "PlayStation 5")) return;
            if (IsEmulated) _ = LoadDlpsCatalogAsync(reset: true);
        }
    }

    public string DlpsSearch
    {
        get => _dlpsSearch;
        set => SetProperty(ref _dlpsSearch, value ?? string.Empty);
    }

    public string DlpsPath
    {
        get => _dlpsPath;
        set
        {
            if (!SetProperty(ref _dlpsPath, value ?? string.Empty)) return;
            RefreshDlpsLocalGame();
        }
    }

    public string DlpsNodePath
    {
        get => _nodePath;
        set => SetProperty(ref _nodePath, string.IsNullOrWhiteSpace(value) ? "node" : value);
    }

    public string DlpsDownloadDir
    {
        get => _dlpsDownloadDir;
        set
        {
            if (!SetProperty(ref _dlpsDownloadDir, value ?? string.Empty)) return;
            RefreshDlpsLocalGame();
        }
    }

    public string DlpsEnvPath
    {
        get => _dlpsEnvPath;
        set => SetProperty(ref _dlpsEnvPath, value ?? string.Empty);
    }

    public string DlpsApiKeyStatus
    {
        get => _dlpsApiKeyStatus;
        private set
        {
            if (SetProperty(ref _dlpsApiKeyStatus, value))
            {
                OnPropertyChanged(nameof(CatalogCredentialHint));
                OnPropertyChanged(nameof(NeedsCatalogCredentials));
                OnPropertyChanged(nameof(IsDlpsApiKeyConfigured));
                OnPropertyChanged(nameof(NeedsDlpsApiKey));
            }
        }
    }

    public string DlpsApiKeyInput
    {
        get => _dlpsApiKeyInput;
        set => SetProperty(ref _dlpsApiKeyInput, value ?? string.Empty);
    }

    public bool NeedsDlpsApiKey => IsEmulated && !IsDlpsApiKeyConfigured;

    public string DlpsDetailsDescription
    {
        get => _dlpsDetailsDescription;
        private set
        {
            if (SetProperty(ref _dlpsDetailsDescription, value))
                OnPropertyChanged(nameof(HasDlpsDetailsDescription));
        }
    }

    public bool HasDlpsDetailsDescription => !string.IsNullOrWhiteSpace(_dlpsDetailsDescription);

    public string DlpsDetailsReleased
    {
        get => _dlpsDetailsReleased;
        private set => SetProperty(ref _dlpsDetailsReleased, value);
    }

    public string DlpsDetailsGenre
    {
        get => _dlpsDetailsGenre;
        private set => SetProperty(ref _dlpsDetailsGenre, value);
    }

    public string DlpsDetailsStatus
    {
        get => _dlpsDetailsStatus;
        private set => SetProperty(ref _dlpsDetailsStatus, value);
    }

    // Wide screenshot used as the hero banner, the way Steam shows a library hero.
    public string DlpsDetailsHero
    {
        get => _dlpsDetailsHero;
        private set
        {
            if (SetProperty(ref _dlpsDetailsHero, value))
                OnPropertyChanged(nameof(HasDlpsDetailsHero));
        }
    }

    public bool HasDlpsDetailsHero => !string.IsNullOrWhiteSpace(_dlpsDetailsHero);

    public bool DlpsDetailsLoading
    {
        get => _dlpsDetailsLoading;
        private set => SetProperty(ref _dlpsDetailsLoading, value);
    }

    public string EmulatedStatus
    {
        get => _emulatedStatus;
        private set => SetProperty(ref _emulatedStatus, value);
    }

    public string EmulatedLocalStatus
    {
        get => _emulatedLocalStatus;
        private set => SetProperty(ref _emulatedLocalStatus, value);
    }

    public string DlpsLog
    {
        get => _dlpsLog;
        private set => SetProperty(ref _dlpsLog, value);
    }

    public bool EmulatedLoading
    {
        get => _emulatedLoading;
        private set
        {
            if (SetProperty(ref _emulatedLoading, value))
            {
                OnPropertyChanged(nameof(CanLoadMoreEmulated));
                OnPropertyChanged(nameof(CanDownloadSelected));
            }
        }
    }

    public bool DlpsDownloading
    {
        get => _dlpsDownloading;
        private set
        {
            if (SetProperty(ref _dlpsDownloading, value))
            {
                OnPropertyChanged(nameof(CanLoadMoreEmulated));
                OnPropertyChanged(nameof(CanDownloadSelected));
                OnPropertyChanged(nameof(CanPlaySelectedDlpsGame));
                (DlpsDownloadSelectedCommand as IRelayCommand)?.NotifyCanExecuteChanged();
                (DlpsCancelDownloadCommand as IRelayCommand)?.NotifyCanExecuteChanged();
                (PlaySelectedDlpsGameCommand as IRelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public DlpsGameResult? SelectedDlpsGame
    {
        get => _selectedDlpsGame;
        set
        {
            if (!SetProperty(ref _selectedDlpsGame, value)) return;
            OnPropertyChanged(nameof(CanDownloadSelected));
            // The button is disabled until a game is picked, so it must re-query on selection.
            (DlpsDownloadSelectedCommand as IRelayCommand)?.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelectedDlpsGame));
            OnPropertyChanged(nameof(ShowEmulatedCatalog));
            OnPropertyChanged(nameof(ShowEmulatedDetails));
            OnPropertyChanged(nameof(ShowLibraryChrome));
            OnPropertyChanged(nameof(NeedsDlpsApiKey));
            DlpsDetailsDescription = string.Empty;
            DlpsDetailsReleased = string.Empty;
            DlpsDetailsGenre = value?.Platform ?? string.Empty;
            DlpsDetailsStatus = string.Empty;
            DlpsDetailsHero = string.Empty;
            RefreshDlpsLocalGame();
            if (value is not null) _ = LoadDlpsGameDetailsAsync(value);
        }
    }

    public bool HasSelectedDlpsGame => SelectedDlpsGame is not null;
    public bool ShowEmulatedCatalog => !HasSelectedDlpsGame;
    public bool ShowEmulatedDetails => HasSelectedDlpsGame;
    public bool ShowLibraryChrome => !HasSelectedDlpsGame;

    // Set when the selected game's downloaded artifact exists in the dlps output folder.
    public string? DlpsLocalGamePath
    {
        get => _dlpsLocalGamePath;
        private set
        {
            if (!SetProperty(ref _dlpsLocalGamePath, value)) return;
            OnPropertyChanged(nameof(HasDlpsLocalGame));
            OnPropertyChanged(nameof(ShowDlpsDownloadButton));
            OnPropertyChanged(nameof(ShowDlpsPlayButton));
            OnPropertyChanged(nameof(CanPlaySelectedDlpsGame));
            (PlaySelectedDlpsGameCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    public bool HasDlpsLocalGame => _dlpsLocalGamePath is not null;
    public bool ShowDlpsDownloadButton => !HasDlpsLocalGame;
    public bool ShowDlpsPlayButton => HasDlpsLocalGame;
    public bool CanPlaySelectedDlpsGame => HasDlpsLocalGame && !_dlpsDownloading;

    public EmulatedEmulatorOption? SelectedEmulatedEmulator
    {
        get => _selectedEmulatedEmulator;
        set
        {
            if (!SetProperty(ref _selectedEmulatedEmulator, value)) return;
            (AddEmulatedGameCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    // Keep signature compatible with existing constructor/DI.
    internal void InitializeEmulation(IReadOnlyList<IEmulatedCatalogProvider>? catalogProviders, IEmulatorInstallService? emulators, ISettingsService? settings)
    {
        _emulators = emulators;
        _settingsService = settings;
        AddEmulatedGameCommand = new RelayCommand(AddEmulatedGame, () => SelectedEmulatedEmulator is not null);
        OpenEmulatorsCommand = new RelayCommand(() => Navigation.Navigate<Pages.EmulatorsPage>());
        RefreshEmulatedCommand = new AsyncRelayCommand(PrepareEmulatedAsync);
        LoadMoreEmulatedCommand = new AsyncRelayCommand(() => LoadDlpsCatalogAsync(reset: false), () => CanLoadMoreEmulated);
        OpenCatalogGameCommand = new RelayCommand<DlpsGameResult>(OpenCatalogGame);
        DownloadCatalogGameCommand = new RelayCommand<DlpsGameResult>(g => _ = DownloadDlpsGameAsync(g));
        DlpsBrowseCommand = new AsyncRelayCommand(() => LoadDlpsCatalogAsync(reset: true));
        DlpsSearchCommand = new AsyncRelayCommand(() => LoadDlpsCatalogAsync(reset: true));
        DlpsDownloadSelectedCommand = new AsyncRelayCommand(() => DownloadDlpsGameAsync(SelectedDlpsGame), () => CanDownloadSelected);
        DlpsCancelDownloadCommand = new RelayCommand(CancelDlpsDownload, () => DlpsDownloading);
        DlpsPickPathCommand = new RelayCommand(PickDlpsPath);
        DlpsPickDownloadDirCommand = new RelayCommand(PickDlpsDownloadDir);
        DlpsPickEnvCommand = new RelayCommand(PickDlpsEnv);
        DlpsOpenRepoCommand = new RelayCommand(() => OpenExternal("https://github.com/jayjun911/dlps_downloader"));
        DlpsLoadEnvCommand = new RelayCommand(LoadDlpsEnv);
        DlpsOpenDownloadFolderCommand = new RelayCommand(OpenDlpsDownloadFolder);
        DlpsOpenPageCommand = new RelayCommand(() => { if (SelectedDlpsGame is not null) OpenExternal(SelectedDlpsGame.PageUrl); });
        CloseDlpsDetailsCommand = new RelayCommand(() => SelectedDlpsGame = null);
        SaveDlpsApiKeyCommand = new RelayCommand(SaveDlpsApiKey);
        PlaySelectedDlpsGameCommand = new AsyncRelayCommand(PlaySelectedDlpsGameAsync, () => CanPlaySelectedDlpsGame);

        AutoDetectDlpsConfig();
        LoadDlpsEnv();
    }

    // Compatibility alias for main partial.
    internal Task LoadEmulatedCatalogAsync(bool reset) => LoadDlpsCatalogAsync(reset);

    internal void CancelEmulatedWork()
    {
        _emulatedRequest?.Cancel();
        _emulatedRequest?.Dispose();
        _emulatedRequest = null;
        if (_emulatedLoading) EmulatedLoading = false;
    }

    private void CancelDlpsDownload()
    {
        try { _dlpsDownloadCts?.Cancel(); } catch { }
        try
        {
            if (_dlpsProcess is { HasExited: false })
                _dlpsProcess.Kill(entireProcessTree: true);
        }
        catch { }
        AppendDlpsLog("[Cancelled] Download cancelled by user.");
        DlpsDownloading = false;
    }

    private CancellationTokenSource ReplaceEmulatedRequest()
    {
        _emulatedRequest?.Cancel();
        _emulatedRequest?.Dispose();
        _emulatedRequest = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetime.Token);
        return _emulatedRequest;
    }

    private void AutoDetectDlpsConfig()
    {
        var candidates = new[]
        {
            @"C:\Users\abdel\Downloads\Steamy-0.8.1.3\dlps_downloader",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Steamy-0.8.1.3", "dlps_downloader"),
            Path.Combine(AppContext.BaseDirectory, "dlps_downloader")
        };
        if (string.IsNullOrWhiteSpace(DlpsPath))
        {
            var found = candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "src", "index.js")) || File.Exists(Path.Combine(p, "package.json")));
            if (found is not null) DlpsPath = found;
        }

        if (string.IsNullOrWhiteSpace(DlpsEnvPath) || !File.Exists(DlpsEnvPath))
        {
            var envCandidates = new[]
            {
                @"C:\Users\abdel\Downloads\Steamy-0.8.1.3.env",
                @"C:\Users\abdel\Downloads\Steamy-0.8.1.3\Steamy-0.8.1.3\.env",
                @"C:\Users\abdel\Downloads\Steamy-0.8.1.3\.env",
                Path.Combine(DlpsPath ?? string.Empty, ".env"),
                Path.Combine(AppContext.BaseDirectory, ".env")
            };
            var env = envCandidates.FirstOrDefault(File.Exists);
            if (env is not null) DlpsEnvPath = env;
        }

        if (string.IsNullOrWhiteSpace(DlpsDownloadDir))
        {
            var fromEnv = TryReadEnvValue(DlpsEnvPath, "DOWNLOAD_DIR");
            DlpsDownloadDir = !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv! : @"C:\Z";
        }
    }

    private void LoadDlpsEnv()
    {
        AutoDetectDlpsConfig();
        var key = FindDlpsApiKey();
        if (!string.IsNullOrWhiteSpace(key))
            DlpsApiKeyStatus = $"API key configured ({MaskKey(key)}).";
        else
            DlpsApiKeyStatus = "No 1fichier API key yet. Enter it here and Steamy saves it for dlps_downloader.";

        var dlDir = TryReadEnvValue(DlpsEnvPath, "DOWNLOAD_DIR") ?? TryReadEnvValue(Path.Combine(DlpsPath ?? string.Empty, ".env"), "DOWNLOAD_DIR");
        if (!string.IsNullOrWhiteSpace(dlDir) && Directory.Exists(Path.GetPathRoot(dlDir) ?? "C:\\"))
            DlpsDownloadDir = dlDir;
    }

    private string? FindDlpsApiKey()
    {
        var candidates = new[]
        {
            DlpsEnvPath,
            Path.Combine(DlpsPath ?? string.Empty, ".env"),
            @"C:\Users\abdel\Downloads\Steamy-0.8.1.3\Steamy-0.8.1.3\.env",
            @"C:\Users\abdel\Downloads\Steamy-0.8.1.3\.env",
            Path.Combine(AppContext.BaseDirectory, ".env")
        };
        foreach (var candidate in candidates)
        {
            var value = TryReadEnvValue(candidate, "FICHIER_API_KEY");
            if (!string.IsNullOrWhiteSpace(value)
                && !string.Equals(value, "your_1fichier_api_key_here", StringComparison.OrdinalIgnoreCase)
                && value.Length >= 12)
                return value;
        }
        return EnvReader.Get("FICHIER_API_KEY");
    }

    private void SaveDlpsApiKey()
    {
        var key = (DlpsApiKeyInput ?? string.Empty).Trim().Trim('"', '\'');
        if (key.Length < 8)
        {
            DlpsApiKeyStatus = "That key looks too short. Paste the full 1fichier API key.";
            return;
        }
        try
        {
            WriteDlpsEnv(key, DlpsDownloadDir);
            DlpsApiKeyInput = string.Empty;
            LoadDlpsEnv();
            DlpsApiKeyStatus = $"API key configured ({MaskKey(key)}).";
            DlpsDetailsStatus = "API key saved. You can download now.";
            EmulatedStatus = "1fichier API key saved for dlps_downloader.";
        }
        catch (Exception ex)
        {
            DlpsApiKeyStatus = $"Could not save the key: {ex.Message}";
        }
    }

    // dlps_downloader loads <repo>/.env with override: true, so the key must live there for the CLI to use it.
    private string? WriteDlpsEnv(string key, string? downloadDir)
    {
        var dir = DlpsPath?.Trim();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            throw new InvalidOperationException("Pick the dlps_downloader folder first (it must contain src/index.js).");
        var envPath = Path.Combine(dir, ".env");
        var lines = File.Exists(envPath) ? File.ReadAllLines(envPath).ToList() : new List<string>();
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("FICHIER_API_KEY=", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("apikey=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"FICHIER_API_KEY={key}";
                replaced = true;
            }
            else if (trimmed.StartsWith("DOWNLOAD_MANAGER=", StringComparison.OrdinalIgnoreCase))
            {
                // Steamy streams downloads itself; an external download manager would
                // take over the file and hide progress from the app.
                lines[i] = "DOWNLOAD_MANAGER=";
            }
        }
        if (!replaced) lines.Add($"FICHIER_API_KEY={key}");
        if (!string.IsNullOrWhiteSpace(downloadDir) && !lines.Any(l => l.TrimStart().StartsWith("DOWNLOAD_DIR=", StringComparison.OrdinalIgnoreCase)))
            lines.Add($"DOWNLOAD_DIR={downloadDir}");
        File.WriteAllLines(envPath, lines);
        DlpsEnvPath = envPath;
        return envPath;
    }

    private void EnsureDlpsEnvHasKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var envPath = Path.Combine(DlpsPath ?? string.Empty, ".env");
        var existing = TryReadEnvValue(envPath, "FICHIER_API_KEY");
        if (string.Equals(existing, key, StringComparison.Ordinal)) return;
        try { WriteDlpsEnv(key, DlpsDownloadDir); AppendDlpsLog($"[Env] Wrote FICHIER_API_KEY into {envPath}"); }
        catch (Exception ex) { AppendDlpsLog($"[Env] Could not update {envPath}: {ex.Message}"); }
    }

    private async Task LoadDlpsGameDetailsAsync(DlpsGameResult game)
    {
        DlpsDetailsLoading = true;
        DlpsDetailsGenre = game.Platform;
        try
        {
            var host = HostOf(game.PageUrl);
            if (string.IsNullOrWhiteSpace(host)) { DlpsDetailsStatus = string.Empty; return; }
            var uri = $"{host}/wp-json/wp/v2/posts?slug={Uri.EscapeDataString(game.Id)}&_fields=content,excerpt,date,title,link";
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _dlpsHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode) { DlpsDetailsStatus = string.Empty; return; }
            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return;
            var post = doc.RootElement[0];
            var contentHtml = post.TryGetProperty("content", out var content) ? JsonText(content, "rendered") : null;
            var description = CleanHtml(post.TryGetProperty("excerpt", out var ex) ? JsonText(ex, "rendered") : null);
            if (description.Length < 40)
                description = CleanHtml(contentHtml);
            DlpsDetailsHero = FindHeroImage(contentHtml, game.CoverUrl);
            if (description.Length > 1600) description = description[..1600].TrimEnd() + "…";
            DlpsDetailsDescription = description;
            if (post.TryGetProperty("date", out var dateProp) && dateProp.ValueKind == JsonValueKind.String
                && DateTime.TryParse(dateProp.GetString(), out var date))
                DlpsDetailsReleased = date.ToString("MMM d, yyyy");
            DlpsDetailsStatus = string.Empty;
        }
        catch (Exception ex)
        {
            DlpsDetailsStatus = $"Details could not be loaded: {ex.Message}";
        }
        finally
        {
            DlpsDetailsLoading = false;
        }
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}" : string.Empty;

    private static string MaskKey(string key)
    {
        key = key.Trim();
        if (key.Length <= 6) return "***";
        return key[..3] + "..." + key[^3..];
    }

    private static string? TryReadEnvValue(string? path, string key)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line[..eq].Trim();
                if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)
                    && !(string.Equals(key, "FICHIER_API_KEY", StringComparison.OrdinalIgnoreCase) && string.Equals(k, "apikey", StringComparison.OrdinalIgnoreCase)))
                    continue;
                var v = line[(eq + 1)..].Trim();
                if (v.Length >= 2 && ((v.StartsWith('"') && v.EndsWith('"')) || (v.StartsWith('\'') && v.EndsWith('\''))))
                    v = v[1..^1];
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        catch { }
        return null;
    }

    private async Task PrepareEmulatedAsync()
    {
        if (!IsEmulated) return;
        RefreshEmulatedLocalGames();
        AutoDetectDlpsConfig();
        LoadDlpsEnv();
        // Keep the CLI's own .env in sync: it loads that file with override, so a stale or
        // placeholder key there would silently break every download.
        var key = FindDlpsApiKey();
        if (!string.IsNullOrWhiteSpace(key)) EnsureDlpsEnvHasKey(key);
        EmulatedStatus = File.Exists(Path.Combine(DlpsPath ?? string.Empty, "src", "index.js"))
            ? $"dlps_downloader ready at {DlpsPath}. Search or browse games, then download."
            : "Set the dlps_downloader folder (it should contain src/index.js), then browse or search.";
        if (EmulatedCatalog.Count == 0 && !EmulatedLoading)
            await LoadDlpsCatalogAsync(reset: true);
    }

    private async Task LoadDlpsCatalogAsync(bool reset)
    {
        if (!IsEmulated) return;
        var request = ReplaceEmulatedRequest();
        var token = request.Token;
        var append = !reset;
        var page = append ? _emulatedPage + 1 : 1;
        EmulatedLoading = true;
        EmulatedStatus = append ? "Loading more…" : (string.IsNullOrWhiteSpace(DlpsSearch) ? $"Browsing {SelectedEmulatedPlatform}…" : "Searching dlps…");
        try
        {
            var (items, total, hasMore) = await FetchDlpsGamesAsync(SelectedEmulatedPlatform, DlpsSearch?.Trim(), page, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_emulatedRequest, request)) return;
            if (!append) EmulatedCatalog.Clear();
            foreach (var item in items) EmulatedCatalog.Add(item);
            _emulatedPage = page;
            _emulatedCount = total ?? EmulatedCatalog.Count;
            _emulatedHasMore = hasMore;
            OnPropertyChanged(nameof(HasEmulatedCatalog));
            OnPropertyChanged(nameof(CatalogCountLabel));
            OnPropertyChanged(nameof(CanLoadMoreEmulated));
            EmulatedStatus = items.Count == 0
                ? "No games found."
                : $"Showing {EmulatedCatalog.Count:N0}{(total.HasValue ? $" of {total:N0}" : "")} games. Select a game and click Download.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            EmulatedStatus = $"Could not load games: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_emulatedRequest, request)) EmulatedLoading = false;
        }
    }

    private async Task<(List<DlpsGameResult> Items, int? Total, bool HasMore)> FetchDlpsGamesAsync(string platformName, string? search, int page, CancellationToken token)
    {
        var platform = DlpsPlatforms.FirstOrDefault(p => string.Equals(p.Name, platformName, StringComparison.OrdinalIgnoreCase));
        if (platform == default) platform = DlpsPlatforms[0];

        if (!string.IsNullOrWhiteSpace(search))
            return await SearchDlpsPostsAsync(platform, search, page, token);

        return await BrowseDlpsPlatformAsync(platform, page, token);
    }

    private async Task<(List<DlpsGameResult> Items, int? Total, bool HasMore)> SearchDlpsPostsAsync((string Name, string Host, string Slug, string Category) platform, string search, int page, CancellationToken token)
    {
        var uri = $"{platform.Host}/wp-json/wp/v2/posts?search={Uri.EscapeDataString(search)}&per_page=24&page={Math.Max(1, page)}&_fields=id,slug,title,link,yoast_head_json";
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await _dlpsHttp.SendAsync(req, token);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return ([], 0, false);
        resp.EnsureSuccessStatusCode();
        int? total = resp.Headers.TryGetValues("X-WP-Total", out var vals) && int.TryParse(vals.FirstOrDefault(), out var t) ? t : null;
        await using var stream = await resp.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var list = new List<DlpsGameResult>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var slug = JsonText(item, "slug") ?? Guid.NewGuid().ToString("n");
                var title = CleanHtml(JsonText(item.TryGetProperty("title", out var te) ? te : default, "rendered") ?? slug);
                var link = JsonText(item, "link") ?? $"{platform.Host}/{slug}/";
                var cover = ExtractOgImage(item);
                list.Add(new DlpsGameResult(slug, title, cover, platform.Name, link));
            }
        }
        var hasMore = total.HasValue ? page * 24 < total.Value : list.Count == 24;
        return (list, total ?? list.Count, hasMore);
    }

    private readonly Dictionary<string, int?> _dlpsCategoryIds = new(StringComparer.OrdinalIgnoreCase);

    private async Task<(List<DlpsGameResult> Items, int? Total, bool HasMore)> BrowseDlpsPlatformAsync((string Name, string Host, string Slug, string Category) platform, int page, CancellationToken token)
    {
        // Preferred: the REST category feed, which carries cover art and real pagination.
        var categoryId = await ResolveDlpsCategoryIdAsync(platform.Host, platform.Category, token);
        if (categoryId.HasValue)
            return await BrowseDlpsCategoryAsync(platform, categoryId.Value, page, token);

        // Fallback: parse the platform listing page (no covers).
        var uri = $"{platform.Host}/wp-json/wp/v2/pages?slug={platform.Slug}";
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await _dlpsHttp.SendAsync(req, token);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var html = string.Empty;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var rendered = item.TryGetProperty("content", out var c) ? JsonText(c, "rendered") : null;
                if (!string.IsNullOrEmpty(rendered) && (rendered.Contains("display-posts-listing") || html.Length == 0))
                { html = rendered; if (rendered.Contains("display-posts-listing")) break; }
            }
        }
        var all = ParseDlpsListing(html, platform.Name);
        var skip = (Math.Max(1, page) - 1) * 24;
        var items = all.Skip(skip).Take(24).ToList();
        return (items, all.Count, skip + 24 < all.Count);
    }

    private async Task<int?> ResolveDlpsCategoryIdAsync(string host, string categorySlug, CancellationToken token)
    {
        var cacheKey = $"{host}|{categorySlug}";
        if (_dlpsCategoryIds.TryGetValue(cacheKey, out var cached)) return cached;
        int? id = null;
        try
        {
            var uri = $"{host}/wp-json/wp/v2/categories?slug={Uri.EscapeDataString(categorySlug)}&_fields=id,slug,count";
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _dlpsHttp.SendAsync(req, token);
            if (resp.IsSuccessStatusCode)
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var first = doc.RootElement[0];
                    if (first.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var parsed) && parsed > 0) id = parsed;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { id = null; }
        _dlpsCategoryIds[cacheKey] = id;
        return id;
    }

    private async Task<(List<DlpsGameResult> Items, int? Total, bool HasMore)> BrowseDlpsCategoryAsync((string Name, string Host, string Slug, string Category) platform, int categoryId, int page, CancellationToken token)
    {
        var uri = $"{platform.Host}/wp-json/wp/v2/posts?categories={categoryId}&per_page=24&page={Math.Max(1, page)}&_fields=id,slug,title,link,yoast_head_json";
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await _dlpsHttp.SendAsync(req, token);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return ([], 0, false);
        resp.EnsureSuccessStatusCode();
        int? total = resp.Headers.TryGetValues("X-WP-Total", out var vals) && int.TryParse(vals.FirstOrDefault(), out var t) ? t : null;
        await using var stream = await resp.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var list = new List<DlpsGameResult>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var slug = JsonText(item, "slug") ?? Guid.NewGuid().ToString("n");
                var title = CleanHtml(JsonText(item.TryGetProperty("title", out var te) ? te : default, "rendered") ?? slug);
                var link = JsonText(item, "link") ?? $"{platform.Host}/{slug}/";
                list.Add(new DlpsGameResult(slug, title, ExtractOgImage(item), platform.Name, link));
            }
        }
        var hasMore = total.HasValue ? page * 24 < total.Value : list.Count == 24;
        return (list, total ?? list.Count, hasMore);
    }

    private static readonly Regex ListingRegex = new(
        @"(?is)<li\b[^>]*class=[""'][^""']*listing-item[^""']*[""'][^>]*>.*?<a\b[^>]*href=[""'](?<url>https?://[^""']+)[""'][^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled);

    private static List<DlpsGameResult> ParseDlpsListing(string html, string platform)
    {
        var results = new List<DlpsGameResult>();
        foreach (Match m in ListingRegex.Matches(html))
        {
            var url = m.Groups["url"].Value.Trim();
            var title = CleanHtml(m.Groups["title"].Value);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            var slug = url.TrimEnd('/');
            slug = slug[(slug.LastIndexOf('/') + 1)..];
            results.Add(new DlpsGameResult(slug, title, null, platform, url));
        }
        return results;
    }

    private static string? ExtractOgImage(JsonElement item)
    {
        if (item.TryGetProperty("yoast_head_json", out var yoast)
            && yoast.TryGetProperty("og_image", out var og)
            && og.ValueKind == JsonValueKind.Array)
        {
            foreach (var img in og.EnumerateArray())
                if (JsonText(img, "url") is { Length: > 0 } u) return u;
        }
        return null;
    }

    // Steam-style wide hero art: prefer a large screenshot from the release page, never the
    // square cover (that one is shown as the box art next to the title).
    private static string FindHeroImage(string? contentHtml, string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(contentHtml)) return string.Empty;
        var candidates = new List<string>();
        foreach (Match match in Regex.Matches(contentHtml, "<img[^>]+src=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase))
        {
            var url = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (url.Length == 0 || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(coverUrl) && string.Equals(url, coverUrl.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            candidates.Add(url);
        }
        var wide = candidates.FirstOrDefault(url =>
            Regex.IsMatch(url, @"/(?:s|w)(?:1280|1600|1920)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(url, @"-\d{4}x\d{3,4}\.", RegexOptions.IgnoreCase)
            || url.Contains("blogger.googleusercontent.com", StringComparison.OrdinalIgnoreCase));
        return wide ?? candidates.FirstOrDefault() ?? string.Empty;
    }

    private static string CleanHtml(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = Regex.Replace(raw, "<.*?>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(s).Trim();
    }

    private static string? JsonText(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task DownloadDlpsGameAsync(DlpsGameResult? game)
    {
        if (game is null) return;
        if (DlpsDownloading) { EmulatedStatus = "A download is already running."; return; }

        var dlpsDir = DlpsPath?.Trim();
        var indexJs = Path.Combine(dlpsDir ?? string.Empty, "src", "index.js");
        if (string.IsNullOrWhiteSpace(dlpsDir) || !File.Exists(indexJs))
        {
            EmulatedStatus = "dlps_downloader folder is not configured (missing src/index.js). Pick the correct folder first.";
            AppendDlpsLog($"[Error] {EmulatedStatus}");
            return;
        }

        var outDir = string.IsNullOrWhiteSpace(DlpsDownloadDir) ? @"C:\Z" : DlpsDownloadDir.Trim();
        try { Directory.CreateDirectory(outDir); } catch (Exception ex) { EmulatedStatus = $"Cannot create download folder: {ex.Message}"; return; }

        LoadDlpsEnv();
        var apiKey = FindDlpsApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            EmulatedStatus = "Add your 1fichier API key first — paste it in the key box on this page.";
            AppendDlpsLog("[Error] No 1fichier API key found. Enter it in the app to create the .env for dlps_downloader.");
            return;
        }
        EnsureDlpsEnvHasKey(apiKey);

        _dlpsDownloadCts?.Dispose();
        _dlpsDownloadCts = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetime.Token);
        var token = _dlpsDownloadCts.Token;
        DlpsDownloading = true;
        EmulatedStatus = $"Downloading '{game.Name}' via dlps_downloader…";
        var job = TrackDlpsDownload(game, outDir);
        AppendDlpsLog($"\n=== Download: {game.Name} ===");
        AppendDlpsLog($"Platform: {game.Platform}");
        AppendDlpsLog($"Page: {game.PageUrl}");
        AppendDlpsLog($"Output: {outDir}");
        AppendDlpsLog($"dlps path: {dlpsDir}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(DlpsNodePath) ? "node" : DlpsNodePath.Trim(),
                Arguments = $"src/index.js download \"{game.Name.Replace("\"", "\\\"")}\" -o \"{outDir.Replace("\"", "\\\"")}\"",
                WorkingDirectory = dlpsDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // Pass API key/config to the CLI process (do not log the value).
            if (!string.IsNullOrWhiteSpace(apiKey)) psi.Environment["FICHIER_API_KEY"] = apiKey.Trim();
            if (!string.IsNullOrWhiteSpace(outDir)) psi.Environment["DOWNLOAD_DIR"] = outDir;
            var envFile = !string.IsNullOrWhiteSpace(DlpsEnvPath) && File.Exists(DlpsEnvPath) ? DlpsEnvPath : null;
            if (envFile is not null) psi.Environment["DOTENV_CONFIG_PATH"] = envFile;

            _dlpsProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!_dlpsProcess.Start())
            {
                EmulatedStatus = "Failed to start Node/dlps process.";
                AppendDlpsLog("[Error] Process did not start. Ensure Node.js is installed and the dlps folder is correct.");
                return;
            }

            var stdout = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await _dlpsProcess.StandardOutput.ReadLineAsync();
                    if (line is null) break;
                    AppendDlpsLog(line);
                    UpdateDlpsDownloadJob(job, line);
                }
            }, token);
            var stderr = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await _dlpsProcess.StandardError.ReadLineAsync();
                    if (line is null) break;
                    AppendDlpsLog(line);
                }
            }, token);

            await _dlpsProcess.WaitForExitAsync(token);
            await Task.WhenAll(stdout.ContinueWith(_ => { }), stderr.ContinueWith(_ => { }));

            var code = _dlpsProcess.ExitCode;
            if (token.IsCancellationRequested)
            {
                EmulatedStatus = $"Download cancelled: {game.Name}";
                FinishDlpsDownloadJob(job, DownloadJobState.Cancelled, "Cancelled");
            }
            else if (code == 0)
            {
                EmulatedStatus = $"Download finished: {game.Name}";
                AppendDlpsLog($"[Done] Exit code 0. Check output folder: {outDir}");
                FinishDlpsDownloadJob(job, DownloadJobState.Completed, "Downloaded");
                RefreshDlpsLocalGame();
            }
            else
            {
                EmulatedStatus = $"dlps exited with code {code} for '{game.Name}'. See log.";
                AppendDlpsLog($"[Error] Exit code {code}.");
                FinishDlpsDownloadJob(job, DownloadJobState.Failed, $"Downloader exited with code {code}");
            }
        }
        catch (OperationCanceledException)
        {
            EmulatedStatus = $"Download cancelled: {game.Name}";
            FinishDlpsDownloadJob(job, DownloadJobState.Cancelled, "Cancelled");
        }
        catch (Exception ex)
        {
            EmulatedStatus = $"Download failed: {ex.Message}";
            AppendDlpsLog($"[Error] {ex}");
            FinishDlpsDownloadJob(job, DownloadJobState.Failed, ex.Message);
        }
        finally
        {
            try { _dlpsProcess?.Dispose(); } catch { }
            _dlpsProcess = null;
            DlpsDownloading = false;
        }
    }

    // Emulated downloads also appear on the Downloads page, with the same live progress.
    private DownloadJob TrackDlpsDownload(DlpsGameResult game, string outDir)
    {
        var job = new DownloadJob
        {
            GameName = game.Name,
            AppId = 0,
            CoverImageUrl = game.CoverUrl ?? string.Empty,
            TargetFolder = outDir,
            Started = DateTime.Now,
            DownloadMode = "dlps (Emulated)",
            Status = "Preparing download…",
            CurrentFile = game.PageUrl,
            State = DownloadJobState.Preparing,
        };
        OnUiThread(() => { if (!Store.Downloads.Contains(job)) Store.Downloads.Insert(0, job); });
        return job;
    }

    private static void UpdateDlpsDownloadJob(DownloadJob job, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var match = Regex.Match(line, @"(\d+(?:\.\d+)?)%\s*\(([\d.]+)\s*MB\s*/\s*([\d.]+)\s*MB\)");
        if (match.Success)
        {
            var percent = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var done = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var total = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            OnUiThread(() =>
            {
                job.State = DownloadJobState.Downloading;
                job.Progress = Math.Clamp(percent, 0, 100);
                job.Downloaded = FormatMegabytes(done);
                job.TotalSize = FormatMegabytes(total);
                job.Status = $"{percent:0}% · {FormatMegabytes(done)} of {FormatMegabytes(total)}";
            });
            return;
        }

        if (line.Contains("Verifying download link", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Scraping", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Matched Section", StringComparison.OrdinalIgnoreCase))
        {
            var text = line.Trim().TrimStart('-', 'ℹ', ' ').Trim();
            OnUiThread(() => { job.State = DownloadJobState.Preparing; job.Status = text; });
        }
    }

    private static void FinishDlpsDownloadJob(DownloadJob job, DownloadJobState state, string status)
    {
        OnUiThread(() =>
        {
            job.State = state;
            job.Status = status;
            job.Finished = DateTime.Now;
            if (state == DownloadJobState.Completed)
            {
                job.Progress = 100;
                if (!string.IsNullOrWhiteSpace(job.TotalSize)) job.Downloaded = job.TotalSize;
            }
        });
    }

    private static string FormatMegabytes(double megabytes) => megabytes >= 1024
        ? $"{megabytes / 1024:0.0} GB"
        : $"{megabytes:0.0} MB";

    private static void OnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(action);
        else action();
    }

    private void AppendDlpsLog(string line)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => AppendDlpsLog(line)));
            return;
        }
        // The CLI's spinner writes ANSI escape codes; keep the log readable.
        line = Regex.Replace(line, "\u001b\\[[0-9;?]*[A-Za-z]", string.Empty).TrimEnd();
        if (line.Length == 0) return;
        var text = DlpsLog;
        if (text.Length > 120000) text = text[^80000..];
        text += (text.Length == 0 ? "" : Environment.NewLine) + line;
        DlpsLog = text;
    }

    internal void RefreshEmulatedLocalGames()
    {
        EmulatedLocalGames.Clear();
        EmulatedEmulators.Clear();
        if (_emulators is not null)
        {
            try
            {
                foreach (var definition in EmulatorCatalog.All.Where(definition => definition.SupportsGameLibrary))
                {
                    if (_emulators.GetState(definition).IsInstalled) EmulatedEmulators.Add(new EmulatedEmulatorOption(definition));
                    foreach (var game in _emulators.GetGames(definition))
                        EmulatedLocalGames.Add(new EmulatedGameTile(definition, game, PlayEmulatedGame));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Logging.Add(LogLevel.Warning, "Library", $"Your emulated games could not be read: {error.Message}");
            }
        }
        if (_selectedEmulatedEmulator is null || !EmulatedEmulators.Contains(_selectedEmulatedEmulator))
            SelectedEmulatedEmulator = EmulatedEmulators.FirstOrDefault();
        OnPropertyChanged(nameof(HasInstalledEmulators));
        OnPropertyChanged(nameof(HasEmulatedLocalGames));
        OnPropertyChanged(nameof(EmulatedLocalEmptyHint));
    }

    private void AddEmulatedGame()
    {
        var option = SelectedEmulatedEmulator;
        if (option is null || _emulators is null) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Add {option.Definition.Name} games",
            Filter = option.Definition.GameFileFilter,
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var before = _emulators.GetGames(option.Definition).Count;
            var games = _emulators.AddGames(option.Definition, dialog.FileNames);
            var added = Math.Max(0, games.Count - before);
            EmulatedLocalStatus = added > 0
                ? $"Added {added} game{(added == 1 ? string.Empty : "s")} to your library."
                : "Those games are already in your library.";
            RefreshEmulatedLocalGames();
        }
        catch (Exception error) { EmulatedLocalStatus = error.Message; }
    }

    private void PlayEmulatedGame(EmulatedGameTile tile)
    {
        if (_emulators is null) return;
        try { _emulators.LaunchGame(tile.Definition, tile.Game); EmulatedLocalStatus = $"Launching {tile.Name}…"; }
        catch (Exception error) { EmulatedLocalStatus = error.Message; }
    }

    private void OpenCatalogGame(DlpsGameResult? game)
    {
        if (game is not null) SelectedDlpsGame = game;
    }

    private void RefreshDlpsLocalGame()
    {
        DlpsLocalGamePath = FindLocalDlpsGame(SelectedDlpsGame);
    }

    // Locates the selected game's downloaded artifact in the dlps output folder. Prefers the
    // exact FileName recorded by dlps, then a PPSA/title match so renamed or recompressed
    // artifacts (Title [PPSA][vXX].7z / .exfat / extracted folders) are still found.
    private string? FindLocalDlpsGame(DlpsGameResult? game)
    {
        if (game is null) return null;
        var outDir = string.IsNullOrWhiteSpace(DlpsDownloadDir) ? @"C:\Z" : DlpsDownloadDir.Trim();
        if (!Directory.Exists(outDir)) return null;
        var title = NormalizeGameTitle(game.Name);
        if (title.Length == 0) return null;

        var dlpsDir = DlpsPath?.Trim();
        if (!string.IsNullOrWhiteSpace(dlpsDir) && DlpsPlatformKeys.TryGetValue(game.Platform, out var key))
        {
            var dbPath = Path.Combine(dlpsDir!, "data", $"downloaded-{key}.xml");
            if (File.Exists(dbPath))
            {
                try
                {
                    foreach (var entry in XDocument.Load(dbPath).Descendants("Game"))
                    {
                        if (NormalizeGameTitle((string?)entry.Element("Title")) != title) continue;
                        var fileName = ((string?)entry.Element("FileName"))?.Trim();
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            var exact = Path.Combine(outDir, fileName!);
                            if (File.Exists(exact) || Directory.Exists(exact)) return exact;
                        }
                        var ppsa = ((string?)entry.Element("PPSA"))?.Trim();
                        if (!string.IsNullOrWhiteSpace(ppsa))
                        {
                            var byPpsa = ScanOutputDir(outDir, name => name.Contains(ppsa!, StringComparison.OrdinalIgnoreCase));
                            if (byPpsa is not null) return byPpsa;
                        }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    // Fall through to the folder scan if the dlps database is unreadable.
                }
            }
        }

        return ScanOutputDir(outDir, name => NormalizeGameTitle(name).StartsWith(title, StringComparison.Ordinal));
    }

    private static string? ScanOutputDir(string outDir, Func<string, bool> matches)
    {
        try
        {
            var candidates = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(outDir))
            {
                var name = Path.GetFileName(entry);
                if (matches(name)) candidates.Add(entry);
            }
            // Prefer an extracted folder over an archive so Play launches something runnable.
            return candidates.FirstOrDefault(Directory.Exists) ?? candidates.FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string NormalizeGameTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Regex.Replace(value, @"[^\p{L}\p{N}]+", " ").Trim().ToLowerInvariant();
    }

    private static EmulatorDefinition? EmulatorForPlatform(string platform) => platform switch
    {
        "PlayStation 5" => EmulatorCatalog.KytyPS5,
        "PlayStation 4" => EmulatorCatalog.ShadPS4,
        "Nintendo Switch" => EmulatorCatalog.Ruzu,
        "Nintendo Wii" => EmulatorCatalog.Dolphin,
        "Nintendo Wii U" => EmulatorCatalog.Cemu,
        "Nintendo 3DS" => EmulatorCatalog.Azahar,
        _ => null
    };

    private async Task PlaySelectedDlpsGameAsync()
    {
        var game = SelectedDlpsGame;
        var path = DlpsLocalGamePath;
        if (game is null || path is null || _emulators is null) return;

        var definition = EmulatorForPlatform(game.Platform);
        if (definition is null)
        {
            EmulatedStatus = $"Steamy doesn't manage an emulator for {game.Platform}. Your download is in {Path.GetDirectoryName(path)}.";
            return;
        }
        if (!_emulators.GetState(definition).IsInstalled)
        {
            EmulatedStatus = $"{definition.Name} isn't installed yet. Install it from Emulators, then Play will launch {game.Name}.";
            return;
        }

        var launchPath = path;
        var extension = Path.GetExtension(path);
        if (!Directory.Exists(path) && string.Equals(extension, ".exfat", StringComparison.OrdinalIgnoreCase))
        {
            var extracted = await ExtractExfatAsync(path, _pageLifetime.Token);
            if (extracted is null) return;
            launchPath = extracted;
            RefreshDlpsLocalGame();
        }
        else if (!Directory.Exists(path) && NonLaunchableExtensions.Contains(extension))
        {
            EmulatedStatus = $"{definition.Name} can't run \"{Path.GetFileName(path)}\" directly. Extract it in {Path.GetDirectoryName(path)}, then Play will launch the extracted game.";
            return;
        }

        try
        {
            var games = _emulators.AddGames(definition, [launchPath]);
            var added = games.FirstOrDefault(g => string.Equals(g.Path, launchPath, StringComparison.OrdinalIgnoreCase));
            if (added is null) { EmulatedStatus = "Couldn't add the game to the emulator library."; return; }
            _emulators.LaunchGame(definition, added);
            EmulatedStatus = $"Launching {game.Name} with {definition.Name}…";
            RefreshEmulatedLocalGames();
        }
        catch (Exception error) { EmulatedStatus = error.Message; }
    }

    // Mounts an exFAT image read-only with OSFMount, copies the game files out to a sibling
    // folder, then dismounts. Returns the extracted folder, or null (with a status message).
    private async Task<string?> ExtractExfatAsync(string exfatPath, CancellationToken token)
    {
        var dest = Path.Combine(Path.GetDirectoryName(exfatPath)!, Path.GetFileNameWithoutExtension(exfatPath));
        try
        {
            if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            {
                AppendDlpsLog($"[Extract] Using already-extracted folder: {dest}");
                return dest;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        var osf = FindOsfMountPath();
        if (osf is null)
        {
            EmulatedStatus = "OSFMount isn't installed, so the exFAT image can't be extracted. Install OSFMount, then Play again.";
            AppendDlpsLog($"[Extract] {EmulatedStatus}");
            return null;
        }
        var letter = FindFreeDriveLetter();
        if (letter is null) { EmulatedStatus = "No free drive letter available to mount the exFAT image."; return null; }

        try { Directory.CreateDirectory(dest); }
        catch (Exception error) { EmulatedStatus = $"Can't create the extraction folder: {error.Message}"; return null; }

        EmulatedStatus = $"Extracting {Path.GetFileName(exfatPath)}…";
        AppendDlpsLog($"[Extract] Mounting {Path.GetFileName(exfatPath)} at {letter} (read-only)…");
        try
        {
            var mount = await RunToolCaptureAsync(osf, $"-a -t file -f \"{exfatPath}\" -m {letter} -o ro", token);
            if (mount.ExitCode != 0)
            {
                EmulatedStatus = $"Couldn't mount the exFAT image — try running Steamy as administrator. {mount.Output}";
                AppendDlpsLog($"[Extract] {EmulatedStatus}");
                return null;
            }
            await Task.Delay(2000, token);

            AppendDlpsLog($"[Extract] Copying game files to {dest}…");
            // robocopy exit codes 0-7 mean success; 8+ indicate a copy failure.
            var copy = await RunToolCaptureAsync("robocopy.exe", $"\"{letter}\\\" \"{dest}\" /E /COPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP", token);
            if (copy.ExitCode >= 8)
            {
                EmulatedStatus = $"Copying the game files failed (robocopy exit {copy.ExitCode}). {copy.Output}";
                AppendDlpsLog($"[Extract] {EmulatedStatus}");
                return null;
            }
            AppendDlpsLog($"[Extract] Extracted to {dest}");
            return dest;
        }
        catch (OperationCanceledException)
        {
            EmulatedStatus = "Extraction cancelled.";
            return null;
        }
        catch (Exception error)
        {
            EmulatedStatus = $"Extraction failed: {error.Message}";
            AppendDlpsLog($"[Extract] {error}");
            return null;
        }
        finally
        {
            try { await RunToolCaptureAsync(osf, $"-d -m {letter}", CancellationToken.None); }
            catch { try { await RunToolCaptureAsync(osf, $"-D -m {letter}", CancellationToken.None); } catch { } }
        }
    }

    private static string? FindOsfMountPath()
    {
        var env = Environment.GetEnvironmentVariable("OSFMOUNT_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (env.EndsWith("osfmount.exe", StringComparison.OrdinalIgnoreCase))
            {
                var com = env[..^4] + ".com";
                if (File.Exists(com)) return com;
            }
            if (File.Exists(env)) return env;
        }
        var defaultCom = @"C:\Program Files\OSFMount\OSFMount.com";
        if (File.Exists(defaultCom)) return defaultCom;
        var defaultExe = @"C:\Program Files\OSFMount\OSFMount.exe";
        return File.Exists(defaultExe) ? defaultExe : null;
    }

    private static string? FindFreeDriveLetter()
    {
        for (var c = 'V'; c >= 'D'; c--)
        {
            var letter = c + ":";
            if (!Directory.Exists(letter + "\\")) return letter;
        }
        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunToolCaptureAsync(string fileName, string arguments, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        return (process.ExitCode, (stdout.Result + stderr.Result).Trim());
    }

    private void PickDlpsPath()
    {
        var dialog = new OpenFolderDialog { Title = "Select dlps_downloader folder (contains src/index.js)" };
        if (dialog.ShowDialog() == true) { DlpsPath = dialog.FolderName; LoadDlpsEnv(); EmulatedStatus = $"dlps folder set: {DlpsPath}"; }
    }

    private void PickDlpsDownloadDir()
    {
        var dialog = new OpenFolderDialog { Title = "Select download output folder" };
        if (dialog.ShowDialog() == true) { DlpsDownloadDir = dialog.FolderName; }
    }

    private void PickDlpsEnv()
    {
        var dialog = new OpenFileDialog { Title = "Select .env file with FICHIER_API_KEY", Filter = "Env files (*.env)|*.env|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog() == true) { DlpsEnvPath = dialog.FileName; LoadDlpsEnv(); }
    }

    private void OpenDlpsDownloadFolder()
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(DlpsDownloadDir) ? @"C:\Z" : DlpsDownloadDir;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { EmulatedStatus = ex.Message; }
    }

    private void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) { EmulatedStatus = $"Couldn't open the link: {error.Message}"; }
    }
}

public sealed class DlpsGameResult(string id, string name, string? coverUrl, string platform, string pageUrl)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string? CoverUrl { get; } = coverUrl;
    public string Platform { get; } = platform;
    public string PageUrl { get; } = pageUrl;
    public string Monogram => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();
    public string MetaLabel => Platform;
    public string GenreLabel => Platform;
    public string BadgeLabel => Platform switch
    {
        "PlayStation 5" => "PS5",
        "PlayStation 4" => "PS4",
        "PlayStation 3" => "PS3",
        "PlayStation 2" => "PS2",
        "Nintendo Switch" => "SWITCH",
        "Nintendo Wii U" => "WII U",
        "Nintendo Wii" => "WII",
        "Nintendo 3DS" => "3DS",
        "Xbox (JTAG/RGH)" => "XBOX",
        "Xbox (ISO)" => "XBOX",
        "PSP" => "PSP",
        "PS Vita" => "VITA",
        _ => Platform.ToUpperInvariant()
    };

    private ImageSource? _cover;
    private bool _coverResolved;

    public ImageSource? Cover
    {
        get
        {
            if (_coverResolved) return _cover;
            _coverResolved = true;
            _cover = LoadCover(CoverUrl);
            return _cover;
        }
    }

    public bool ShowCoverFallback => Cover is null;

    private static ImageSource? LoadCover(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = 360;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            return image;
        }
        catch { return null; }
    }
}

public sealed class EmulatedEmulatorOption(EmulatorDefinition definition)
{
    public EmulatorDefinition Definition { get; } = definition;
    public string Name => Definition.Name;
    public string Badge => Definition.Badge;
    public override string ToString() => Name;
}

public sealed class EmulatedGameTile
{
    public EmulatedGameTile(EmulatorDefinition definition, EmulatorGame game, Action<EmulatedGameTile> play)
    {
        Definition = definition;
        Game = game;
        PlayCommand = new RelayCommand(() => play(this));
    }

    public EmulatorDefinition Definition { get; }
    public EmulatorGame Game { get; }
    public string Name => Game.Name;
    public string Path => Game.Path;
    public string Badge => Definition.Badge;
    public string EmulatorName => Definition.Name;
    public string Monogram => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();
    public IRelayCommand PlayCommand { get; }
}
