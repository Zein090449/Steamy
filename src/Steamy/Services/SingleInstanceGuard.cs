using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Steamy.Services;

/// <summary>
/// One Steamy per data folder. A second launch (including Discord's launch command) hands its
/// request to the running instance over a named pipe and exits, so a background window is never
/// duplicated and the Discord session stays with a single client.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string Prefix = "Steamy.Shell.";
    private readonly Mutex? _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Action<bool>? _activation;
    private bool _pendingShow;
    private bool _pendingJoin;

    private SingleInstanceGuard(Mutex? mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _ = ListenAsync();
    }

    public static SingleInstanceGuard? TryCreate(string dataRoot)
    {
        var key = Key(dataRoot);
        Mutex? mutex = null;
        bool created;
        try { mutex = new Mutex(true, @"Local\" + Prefix + key, out created); }
        catch (UnauthorizedAccessException) { return new SingleInstanceGuard(null, Prefix + key); }
        if (!created) { mutex.Dispose(); return null; }
        return new SingleInstanceGuard(mutex, Prefix + key);
    }

    private static string Key(string dataRoot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToLowerInvariant())))[..16];

    public static void Signal(string dataRoot, bool join)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Prefix + Key(dataRoot), PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.Write(join ? "JOIN" : "SHOW");
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException) { }
    }

    public void Bind(Action<bool> activation)
    {
        bool show, join;
        lock (_gate)
        {
            _activation = activation;
            show = _pendingShow; join = _pendingJoin;
            _pendingShow = _pendingJoin = false;
        }
        if (show) activation(join);
    }

    private void Dispatch(bool join)
    {
        Action<bool>? activation;
        lock (_gate)
        {
            activation = _activation;
            if (activation is null) { _pendingShow = true; _pendingJoin |= join; return; }
        }
        activation(join);
    }

    private async Task ListenAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_lifetime.Token);
                using var reader = new StreamReader(server);
                Dispatch((await reader.ReadToEndAsync(_lifetime.Token)).Trim() == "JOIN");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex?.Dispose();
        _lifetime.Dispose();
    }
}
