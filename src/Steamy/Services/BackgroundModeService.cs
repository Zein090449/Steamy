using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Steamy.Models;

namespace Steamy.Services;

public interface IBackgroundModeService : IDisposable
{
    bool IsActive { get; }
    void Attach(Window window, bool startHidden);
    void ShowWindow();
    void HideToTray();
    void Quit();
}

/// <summary>
/// Opt-in notification-area mode. Closing the window keeps the process (and its Discord
/// connection, and therefore message archiving) alive; the tray menu reopens or quits.
/// </summary>
public sealed class BackgroundModeService : IBackgroundModeService, IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Steamy";
    private readonly AppSettings _settings;
    private readonly ILoggingService _log;
    private readonly IDiscordSocialService? _discord;
    private Window? _window;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private bool _active;
    private bool _hidden;
    private bool _quitting;
    private bool _notified;

    public BackgroundModeService(ISettingsService settings, ILoggingService log, IDiscordSocialService discord)
    {
        _settings = settings.Load();
        _log = log;
        _discord = discord;
        _settings.PropertyChanged += SettingsChanged;
    }

    public bool IsActive => _active;

    public void Attach(Window window, bool startHidden)
    {
        _window = window;
        window.Closing += WindowClosing;
        Application.Current.SessionEnding += SessionEnding;
        if (_settings.KeepRunningInBackground) Activate();
        SyncStartupRegistration();
        // The first render runs the startup navigation; hide afterwards so nothing is skipped.
        if (startHidden && _active) window.ContentRendered += HideAfterStartupRender;
    }

    private void HideAfterStartupRender(object? sender, EventArgs args)
    {
        if (_window is not null) _window.ContentRendered -= HideAfterStartupRender;
        if (_active) HideToTray(notify: false);
    }

    private void Activate()
    {
        if (_active || _window is null) return;
        _active = true;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!CreateTray())
        {
            // Without an icon the window could never be reopened after closing.
            _active = false;
            Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
            return;
        }
        if (_discord is IDiscordChatService chat) chat.MessageChanged += ChatMessageChanged;
    }

    private void Deactivate()
    {
        if (!_active) return;
        _active = false;
        if (_hidden) ShowWindow();
        DisposeTray();
        if (_discord is IDiscordChatService chat) chat.MessageChanged -= ChatMessageChanged;
        if (!_quitting) Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    private bool CreateTray()
    {
        if (_tray is not null) return true;
        try
        {
            _icon = LoadIcon();
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open Steamy", null, (_, _) => ShowWindow());
            menu.Items.Add("Quit", null, (_, _) => Quit());
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = _icon,
                Text = "Steamy",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (_, _) => ShowWindow();
            return true;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException or ArgumentException or IOException)
        {
            _log.Add(LogLevel.Warning, "Application", $"The notification-area icon couldn't start: {error.GetType().Name}.");
            DisposeTray();
            return false;
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/Brand/steamy.ico"))?.Stream;
            if (stream is not null) return new System.Drawing.Icon(stream, 32, 32);
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.Runtime.InteropServices.ExternalException) { }
        return Environment.ProcessPath is { } path && System.Drawing.Icon.ExtractAssociatedIcon(path) is { } icon
            ? icon : System.Drawing.SystemIcons.Application;
    }

    public void HideToTray() => HideToTray(notify: true);

    private void HideToTray(bool notify)
    {
        if (_window is null) return;
        if (!_hidden)
        {
            _hidden = true;
            _window.ShowInTaskbar = false;
            _window.Hide();
        }
        if (!notify || _notified || _tray is null) return;
        _notified = true;
        _tray.ShowBalloonTip(5000, "Steamy is still running",
            "Discord messages keep saving in the background. Open Steamy from this icon, or choose Quit.", System.Windows.Forms.ToolTipIcon.None);
    }

    public void ShowWindow()
    {
        if (_window is null) return;
        if (_hidden)
        {
            _hidden = false;
            var area = SystemParameters.WorkArea;
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Left = area.Left + Math.Max(0, (area.Width - _window.Width) / 2);
            _window.Top = area.Top + Math.Max(0, (area.Height - _window.Height) / 2);
            _window.ShowInTaskbar = true;
            _window.ShowActivated = true;
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    public void Quit()
    {
        _quitting = true;
        Application.Current.Shutdown();
    }

    private void WindowClosing(object? sender, CancelEventArgs args)
    {
        if (!_active || _quitting) return;
        args.Cancel = true;
        HideToTray(notify: true);
    }

    private void SessionEnding(object sender, SessionEndingCancelEventArgs args) => _quitting = true;

    private void ChatMessageChanged(object? sender, DiscordMessageChange change)
    {
        if (!_hidden || change.Message is not { LobbyId: 0, Id: > 0 } message || message.IsOwn || message.IsSystem) return;
        void Show() => _tray?.ShowBalloonTip(5000, message.AuthorLabel, message.PreviewLabel, System.Windows.Forms.ToolTipIcon.None);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Show();
        else dispatcher.BeginInvoke(Show);
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_window is null) return;
        if (args.PropertyName == nameof(AppSettings.KeepRunningInBackground))
        {
            if (_settings.KeepRunningInBackground) Activate();
            else
            {
                if (_settings.StartWithWindows) _settings.StartWithWindows = false;
                Deactivate();
                SyncStartupRegistration();
            }
        }
        else if (args.PropertyName == nameof(AppSettings.StartWithWindows)) SyncStartupRegistration();
    }

    private static bool CanManageStartup => App.IsLaunched
        && string.Equals(Path.GetFileName(Environment.ProcessPath), "Steamy.exe", StringComparison.OrdinalIgnoreCase);

    private void SyncStartupRegistration()
    {
        if (!CanManageStartup) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            if (_settings.StartWithWindows && _settings.KeepRunningInBackground)
                key.SetValue(RunValue, "\"" + Environment.ProcessPath + "\" --background", RegistryValueKind.String);
            else if (key.GetValue(RunValue) is string existing && existing.Contains("--background", StringComparison.Ordinal))
                key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Add(LogLevel.Warning, "Application", $"Start with Windows couldn't be updated: {error.GetType().Name}.");
        }
    }

    private void DisposeTray()
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _tray = null;
        }
        _icon?.Dispose();
        _icon = null;
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= SettingsChanged;
        if (Application.Current is { } application) application.SessionEnding -= SessionEnding;
        if (_window is not null) _window.Closing -= WindowClosing;
        if (_discord is IDiscordChatService chat) chat.MessageChanged -= ChatMessageChanged;
        DisposeTray();
    }
}
