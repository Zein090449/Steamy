using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed class EmulatorsViewModel : ObservableObject
{
    private readonly IEmulatorInstallService _service;
    private readonly ILoggingService _logging;
    private int _columns = 2;

    public EmulatorsViewModel(IEmulatorInstallService service, ILoggingService logging)
    {
        _service = service;
        _logging = logging;
        Emulators = new ObservableCollection<EmulatorCardViewModel>(
            EmulatorCatalog.All.Select(definition => new EmulatorCardViewModel(definition, service, logging)));
        foreach (var card in Emulators) card.PropertyChanged += CardChanged;
        CheckAllCommand = new AsyncRelayCommand(CheckAllAsync, () => Emulators.All(card => !card.IsBusy));
    }

    public ObservableCollection<EmulatorCardViewModel> Emulators { get; }
    public string RootPath => _service.Root;
    public IAsyncRelayCommand CheckAllCommand { get; }

    public int Columns
    {
        get => _columns;
        set => SetProperty(ref _columns, value);
    }

    public string Summary
    {
        get
        {
            var installed = Emulators.Count(card => card.IsInstalled);
            var updates = Emulators.Count(card => card.HasUpdate);
            var text = installed == 0 ? "No emulators installed yet." : $"{installed} of {Emulators.Count} emulators installed.";
            return updates == 0 ? text : $"{text.TrimEnd('.')} · {updates} update{(updates == 1 ? string.Empty : "s")} available.";
        }
    }

    public void RefreshInstalledState()
    {
        foreach (var card in Emulators) card.RefreshState();
        OnPropertyChanged(nameof(Summary));
        CheckAllCommand.NotifyCanExecuteChanged();
    }

    private void CardChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(EmulatorCardViewModel.IsInstalled) or nameof(EmulatorCardViewModel.HasUpdate))
            OnPropertyChanged(nameof(Summary));
        if (args.PropertyName == nameof(EmulatorCardViewModel.IsBusy))
            CheckAllCommand.NotifyCanExecuteChanged();
    }

    private async Task CheckAllAsync()
    {
        await Task.WhenAll(Emulators.Select(card => card.CheckAsync()));
        OnPropertyChanged(nameof(Summary));
    }
}

public sealed class EmulatorCardViewModel : ObservableObject
{
    private readonly IEmulatorInstallService _service;
    private readonly ILoggingService _logging;
    private bool _isInstalled;
    private bool _isBusy;
    private bool _isProgressIndeterminate;
    private double _progressValue;
    private string _status = "Not installed.";
    private string? _installedVersion;
    private string? _latestVersion;

    public EmulatorCardViewModel(EmulatorDefinition definition, IEmulatorInstallService service, ILoggingService logging)
    {
        Definition = definition;
        _service = service;
        _logging = logging;
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !IsBusy);
        LaunchCommand = new RelayCommand(Launch, () => IsInstalled && !IsBusy);
        OpenFolderCommand = new RelayCommand(() => OpenFolder());
        OpenProjectCommand = new RelayCommand(() => OpenProject());
        RefreshState();
        SyncGames(_service.GetGames(Definition));
    }

    public EmulatorDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Platform => Definition.Platform;
    public string Badge => Definition.Badge;
    public string Description => Definition.Description;
    public string Requirements => Definition.Requirements;
    public string ProjectUrl => Definition.ProjectUrl;
    public string InstallActionName => $"{InstallLabel} {Name}";
    public string LaunchActionName => $"Launch {Name}";
    public string OpenFolderActionName => $"Open {Name} folder";
    public string OpenProjectActionName => $"Open {Name} project page";
    public string AddGameActionName => $"Add a game to {Name}";
    public string DownloadGameActionName => $"Download {Name}";
    public bool SupportsGameLibrary => Definition.SupportsGameLibrary;
    public string GameFileFilter => Definition.GameFileFilter;
    public ObservableCollection<EmulatorGameViewModel> Games { get; } = [];
    public string GamesHeader => Games.Count == 0 ? "Games" : $"Games · {Games.Count}";
    public bool HasNoGames => Games.Count == 0;

    public bool IsInstalled
    {
        get => _isInstalled;
        private set
        {
            if (!SetProperty(ref _isInstalled, value)) return;
            OnPropertyChanged(nameof(InstallLabel));
            OnPropertyChanged(nameof(InstallActionName));
            OnPropertyChanged(nameof(HasUpdate));
            LaunchCommand.NotifyCanExecuteChanged();
        }
    }

    public string? InstalledVersion
    {
        get => _installedVersion;
        private set
        {
            if (!SetProperty(ref _installedVersion, value)) return;
            OnPropertyChanged(nameof(InstallLabel));
            OnPropertyChanged(nameof(InstallActionName));
            OnPropertyChanged(nameof(HasUpdate));
        }
    }

    public string? LatestVersion
    {
        get => _latestVersion;
        private set
        {
            if (!SetProperty(ref _latestVersion, value)) return;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(InstallLabel));
            OnPropertyChanged(nameof(InstallActionName));
        }
    }

    public bool HasUpdate => IsInstalled && LatestVersion is not null
        && !string.Equals(LatestVersion, InstalledVersion, StringComparison.Ordinal);

    public string InstallLabel => !IsInstalled ? "Install"
        : HasUpdate ? "Update"
        : "Reinstall";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            InstallCommand.NotifyCanExecuteChanged();
            LaunchCommand.NotifyCanExecuteChanged();
        }
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public IAsyncRelayCommand InstallCommand { get; }
    public IRelayCommand LaunchCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }
    public IRelayCommand OpenProjectCommand { get; }

    public void RefreshState(bool keepStatus = false)
    {
        var state = _service.GetState(Definition);
        InstalledVersion = state.Version;
        IsInstalled = state.IsInstalled;
        if (!keepStatus)
            Status = IsInstalled
                ? $"Installed{(state.Version is null ? string.Empty : " · " + state.Version)}. Ready to launch."
                : "Not installed.";
    }

    public async Task CheckAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        Status = "Checking the latest version…";
        try
        {
            var result = await _service.CheckAsync(Definition);
            if (!result.Succeeded)
            {
                Status = result.Message;
                return;
            }

            LatestVersion = result.Version;
            Status = IsInstalled
                ? HasUpdate
                    ? $"Update available: {LatestVersion} (installed {InstalledVersion})."
                    : $"{Name} is up to date ({InstalledVersion})."
                : result.Message;
        }
        catch (Exception exception)
        {
            Status = $"{Name}: {exception.Message}";
        }
        finally
        {
            IsProgressIndeterminate = false;
            IsBusy = false;
        }
    }

    private async Task InstallAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        Status = $"Preparing {Name}…";
        var force = IsInstalled && LatestVersion is not null
            && string.Equals(LatestVersion, InstalledVersion, StringComparison.Ordinal);
        var progress = new Progress<EmulatorInstallProgress>(update =>
        {
            if (!IsBusy) return;
            Status = update.Phase;
            if (update.Fraction < 0)
            {
                IsProgressIndeterminate = true;
                return;
            }
            IsProgressIndeterminate = false;
            ProgressValue = Math.Round(update.Fraction * 100);
        });
        try
        {
            var result = await _service.InstallAsync(Definition, progress, force);
            Status = result.Message;
            if (result.Succeeded)
            {
                LatestVersion = result.Version ?? LatestVersion;
                _logging.Add(LogLevel.Info, "Emulators", $"{Definition.Name}: {result.Message}");
            }
            else
            {
                _logging.Add(LogLevel.Warning, "Emulators", $"{Definition.Name}: {result.Message}");
            }
        }
        catch (Exception exception)
        {
            Status = $"{Name} could not be installed: {exception.Message}";
            _logging.Add(LogLevel.Error, "Emulators", $"{Definition.Name}: {exception}");
        }
        finally
        {
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            IsBusy = false;
            RefreshState(keepStatus: true);
        }
    }

    private void Launch()
    {
        try
        {
            _service.Launch(Definition);
            Status = $"Launching {Name}…";
            _logging.Add(LogLevel.Info, "Emulators", $"Launched {Definition.Name}");
        }
        catch (Exception exception)
        {
            Status = $"{Name} could not be launched: {exception.Message}";
        }
    }

    private void OpenFolder()
    {
        try { _service.OpenInstallFolder(Definition); }
        catch (Exception exception) { Status = $"Could not open the {Name} folder: {exception.Message}"; }
    }

    private void OpenProject()
    {
        try { _service.OpenProjectPage(Definition); }
        catch (Exception exception) { Status = $"Could not open the {Name} page: {exception.Message}"; }
    }

    public void AddGames(IEnumerable<string> paths)
    {
        if (!SupportsGameLibrary) return;
        try
        {
            var before = Games.Count;
            var updated = _service.AddGames(Definition, paths);
            SyncGames(updated);
            var added = Math.Max(0, updated.Count - before);
            Status = added > 0
                ? $"Added {added} game{(added == 1 ? string.Empty : "s")} to your library."
                : updated.Count == before ? "Those games are already in your library." : "No game files were added.";
        }
        catch (Exception exception)
        {
            Status = $"{Name} could not add that game: {exception.Message}";
        }
    }

    internal void PlayGame(EmulatorGameViewModel item)
    {
        try
        {
            _service.LaunchGame(Definition, item.Game);
            Status = $"Launching {item.Name}…";
            _logging.Add(LogLevel.Info, "Emulators", $"Launched {Definition.Name}: {item.Path}");
        }
        catch (Exception exception)
        {
            Status = $"{Name} could not launch {item.Name}: {exception.Message}";
        }
    }

    internal void RemoveGame(EmulatorGameViewModel item)
    {
        try
        {
            if (!_service.RemoveGame(Definition, item.Game.Path)) return;
            SyncGames(_service.GetGames(Definition));
            Status = $"Removed {item.Name} from your library.";
        }
        catch (Exception exception)
        {
            Status = $"{Name} could not remove that game: {exception.Message}";
        }
    }

    private void SyncGames(IReadOnlyList<EmulatorGame> games)
    {
        Games.Clear();
        foreach (var game in games) Games.Add(new EmulatorGameViewModel(this, game));
        OnPropertyChanged(nameof(GamesHeader));
        OnPropertyChanged(nameof(HasNoGames));
    }
}

public sealed class EmulatorGameViewModel
{
    private readonly EmulatorCardViewModel _owner;
    private readonly EmulatorGame _game;

    public EmulatorGameViewModel(EmulatorCardViewModel owner, EmulatorGame game)
    {
        _owner = owner;
        _game = game;
        PlayCommand = new RelayCommand(() => _owner.PlayGame(this));
        RemoveCommand = new RelayCommand(() => _owner.RemoveGame(this));
    }

    public EmulatorGame Game => _game;
    public string Name => _game.Name;
    public string Path => _game.Path;
    public string PlayActionName => $"Play {Name}";
    public string RemoveActionName => $"Remove {Name} from Steamy";
    public IRelayCommand PlayCommand { get; }
    public IRelayCommand RemoveCommand { get; }
}
