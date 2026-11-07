using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Steamy.Services;

public sealed record EmulatorInstallState(bool IsInstalled, string? Version, string InstallDirectory, string? ExecutablePath);

public sealed record EmulatorCheckResult(bool Succeeded, string Message, string? Version = null, long SizeBytes = 0);

public sealed record EmulatorInstallResult(bool Succeeded, string Message, string? Version = null);

/// <summary>Fraction is 0..1 while bytes are known, or a negative value for indeterminate work.</summary>
public readonly record struct EmulatorInstallProgress(string Phase, double Fraction = -1);

public interface IEmulatorInstallService
{
    string Root { get; }
    EmulatorInstallState GetState(EmulatorDefinition definition);
    Task<EmulatorCheckResult> CheckAsync(EmulatorDefinition definition, CancellationToken cancellationToken = default);
    Task<EmulatorInstallResult> InstallAsync(EmulatorDefinition definition, IProgress<EmulatorInstallProgress>? progress = null,
        bool force = false, CancellationToken cancellationToken = default);
    void Launch(EmulatorDefinition definition);
    void OpenInstallFolder(EmulatorDefinition definition);
    void OpenProjectPage(EmulatorDefinition definition);
    IReadOnlyList<EmulatorGame> GetGames(EmulatorDefinition definition);
    IReadOnlyList<EmulatorGame> AddGames(EmulatorDefinition definition, IEnumerable<string> paths);
    bool RemoveGame(EmulatorDefinition definition, string path);
    void LaunchGame(EmulatorDefinition definition, EmulatorGame game);
}

public sealed class EmulatorInstallService : IEmulatorInstallService, IDisposable
{
    private const long MaximumDownloadBytes = 1024L * 1024 * 1024;
    private const string VersionFileName = "version.txt";
    private const string GitHubLatestApi = "https://api.github.com/repos/{0}/{1}/releases/latest";
    private const string GitHubReleasesApi = "https://api.github.com/repos/{0}/{1}/releases?per_page=30";
    private const string GitLabReleasesApi = "https://gitlab.com/api/v4/projects/{0}/releases";
    private const string GitHubTagsApi = "https://api.github.com/repos/{0}/{1}/tags?per_page=100";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly object _gamesGate = new();

    public EmulatorInstallService(HttpClient? httpClient = null, string? dataRoot = null)
    {
        _httpClient = httpClient ?? new HttpClient(StableDnsHandler.Create());
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromMinutes(30);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Steamy/0.1");
        Root = Path.GetFullPath(Path.Combine(dataRoot ?? Steamy.Storage.AppDataPaths.Root, "Tools", "Emulators"));
    }

    public string Root { get; }

    private string GamesPath => Path.Combine(Root, "games.json");

    public EmulatorInstallState GetState(EmulatorDefinition definition)
    {
        var directory = Path.Combine(Root, definition.Id);
        var executable = FindExecutable(directory, definition);
        var versionFile = Path.Combine(directory, VersionFileName);
        var version = executable is not null && File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : null;
        return new EmulatorInstallState(executable is not null, string.IsNullOrWhiteSpace(version) ? null : version, directory, executable);
    }

    public async Task<EmulatorCheckResult> CheckAsync(EmulatorDefinition definition, CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await ResolveLatestAsync(definition, cancellationToken).ConfigureAwait(false);
            var size = release.Archive.SizeBytes > 0 ? $" ({FormatSize(release.Archive.SizeBytes)})" : string.Empty;
            return new EmulatorCheckResult(true, $"Latest version: {release.Version}{size}.", release.Version, release.Archive.SizeBytes);
        }
        catch (OperationCanceledException)
        {
            return new EmulatorCheckResult(false, "The update check was cancelled.");
        }
        catch (Exception exception)
        {
            return new EmulatorCheckResult(false, exception.Message);
        }
    }

    public async Task<EmulatorInstallResult> InstallAsync(EmulatorDefinition definition, IProgress<EmulatorInstallProgress>? progress = null,
        bool force = false, CancellationToken cancellationToken = default)
    {
        string? archivePath = null;
        string? stage = null;
        try
        {
            progress?.Report(new EmulatorInstallProgress($"Checking the latest {definition.Name} release"));
            var release = await ResolveLatestAsync(definition, cancellationToken).ConfigureAwait(false);
            var state = GetState(definition);
            if (!force && state.IsInstalled && string.Equals(state.Version, release.Version, StringComparison.Ordinal))
                return new EmulatorInstallResult(true, $"{definition.Name} {release.Version} is already installed.", release.Version);

            Directory.CreateDirectory(Root);
            archivePath = Path.Combine(Path.GetTempPath(), $"Steamy-{definition.Id}-{Guid.NewGuid():N}.zip");
            progress?.Report(new EmulatorInstallProgress($"Downloading {release.Archive.Name}", 0));
            await DownloadAsync(release.Archive.DownloadUrl, archivePath, release.Archive.Name, progress, cancellationToken, definition.DownloadReferer).ConfigureAwait(false);

            var expected = release.Archive.Sha256;
            if (string.IsNullOrWhiteSpace(expected) && release.ChecksumUrl is not null)
                expected = await DownloadChecksumAsync(release.ChecksumUrl, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(expected) && !ComputeSha256(archivePath).Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase))
                return new EmulatorInstallResult(false, $"{definition.Name} did not pass its checksum check. Nothing was installed; try again.", release.Version);

            stage = Path.Combine(Root, $"{definition.Id}.preparing-{Guid.NewGuid():N}");
            progress?.Report(new EmulatorInstallProgress("Extracting the emulator", -1));
            var extractProgress = new Progress<ArchiveExtractProgress>(update =>
                progress?.Report(new EmulatorInstallProgress($"Extracting… {update.Done}/{update.Total} files", -1)));
            var extracted = await Task.Run(() => ArchiveExtractor.Extract(archivePath!, stage, progress: extractProgress), cancellationToken).ConfigureAwait(false);
            if (!extracted.Succeeded)
                return new EmulatorInstallResult(false, $"{definition.Name} could not be extracted: {extracted.Message}", release.Version);

            RejectLinks(stage);
            if (FindExecutable(stage, definition) is null)
                return new EmulatorInstallResult(false, $"The {definition.Name} download did not contain {definition.ExecutableName}.", release.Version);

            File.WriteAllText(Path.Combine(stage, VersionFileName), release.Version);
            Commit(stage, Path.Combine(Root, definition.Id));
            stage = null;
            return new EmulatorInstallResult(true, $"{definition.Name} {release.Version} installed.", release.Version);
        }
        catch (OperationCanceledException)
        {
            return new EmulatorInstallResult(false, "The download was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && IsSharingViolation(exception))
        {
            return new EmulatorInstallResult(false, $"Could not replace the {definition.Name} files while they are in use. Close the emulator and try again.");
        }
        catch (Exception exception)
        {
            return new EmulatorInstallResult(false, $"{definition.Name} could not be installed: {exception.Message}");
        }
        finally
        {
            if (archivePath is not null) TryDeleteFile(archivePath);
            if (stage is not null) TryDeleteDirectory(stage);
        }
    }

    public void Launch(EmulatorDefinition definition)
    {
        var state = GetState(definition);
        if (state.ExecutablePath is null)
            throw new InvalidOperationException($"{definition.Name} is not installed yet.");
        Process.Start(new ProcessStartInfo(state.ExecutablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(state.ExecutablePath)!,
            UseShellExecute = true
        });
    }

    public void OpenInstallFolder(EmulatorDefinition definition)
    {
        var directory = Path.Combine(Root, definition.Id);
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    public void OpenProjectPage(EmulatorDefinition definition)
        => Process.Start(new ProcessStartInfo(definition.ProjectUrl) { UseShellExecute = true });

    public IReadOnlyList<EmulatorGame> GetGames(EmulatorDefinition definition)
    {
        lock (_gamesGate)
            return LoadGames(definition);
    }

    public IReadOnlyList<EmulatorGame> AddGames(EmulatorDefinition definition, IEnumerable<string> paths)
    {
        if (!definition.SupportsGameLibrary) throw new InvalidOperationException($"{definition.Name} does not take separate games.");
        lock (_gamesGate)
        {
            var games = LoadGames(definition).ToList();
            foreach (var raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
                var isFile = File.Exists(path);
                if (!isFile && !Directory.Exists(path)) continue;
                if (games.Any(game => string.Equals(game.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

                var name = isFile ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
                games.Add(new EmulatorGame(string.IsNullOrWhiteSpace(name) ? path : name, path));
            }

            SaveGames(definition, games);
            return games;
        }
    }

    public bool RemoveGame(EmulatorDefinition definition, string path)
    {
        lock (_gamesGate)
        {
            var games = LoadGames(definition).ToList();
            var removed = games.RemoveAll(game => string.Equals(game.Path, path, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) SaveGames(definition, games);
            return removed;
        }
    }

    public void LaunchGame(EmulatorDefinition definition, EmulatorGame game)
    {
        if (!definition.SupportsGameLibrary) throw new InvalidOperationException($"{definition.Name} does not take separate games.");
        if (!File.Exists(game.Path) && !Directory.Exists(game.Path))
            throw new FileNotFoundException($"Steamy could not find {game.Name}. Reconnect the drive or add it again.", game.Path);

        var state = GetState(definition);
        if (!state.IsInstalled) throw new InvalidOperationException($"Install {definition.Name} before playing.");
        var executable = definition.GameExecutableName is { Length: > 0 } name
            ? FindExecutableByName(state.InstallDirectory, name)
            : state.ExecutablePath;
        if (executable is null) throw new InvalidOperationException($"{definition.Name} was not installed correctly. Reinstall it from this page.");

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false
        };
        if (definition.GameArgumentFlag is { Length: > 0 } flag) startInfo.ArgumentList.Add(flag);
        startInfo.ArgumentList.Add(game.Path);
        Process.Start(startInfo)?.Dispose();
    }

    internal static string? FindExecutable(string directory, EmulatorDefinition definition)
    {
        foreach (var name in definition.ExecutableNames)
        {
            var executable = FindExecutableByName(directory, name);
            if (executable is not null) return executable;
        }
        return null;
    }

    private static string? FindExecutableByName(string? directory, string name)
    {
        if (directory is null || !Directory.Exists(directory)) return null;
        var direct = Path.Combine(directory, name);
        if (File.Exists(direct)) return direct;
        try
        {
            return Directory.EnumerateFiles(directory, name, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private List<EmulatorGame> LoadGames(EmulatorDefinition definition)
    {
        try
        {
            if (!File.Exists(GamesPath)) return [];
            var stored = JsonSerializer.Deserialize<Dictionary<string, List<EmulatorGame>>>(File.ReadAllText(GamesPath));
            return stored is not null && stored.TryGetValue(definition.Id, out var games) ? games : [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged optional library file must never block installing or playing.
            return [];
        }
    }

    private void SaveGames(EmulatorDefinition definition, List<EmulatorGame> games)
    {
        Dictionary<string, List<EmulatorGame>> stored = [];
        try
        {
            if (File.Exists(GamesPath))
                stored = JsonSerializer.Deserialize<Dictionary<string, List<EmulatorGame>>>(File.ReadAllText(GamesPath)) ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }

        stored[definition.Id] = games;
        Directory.CreateDirectory(Root);
        var temporary = GamesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, GamesPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<EmulatorRelease> ResolveLatestAsync(EmulatorDefinition definition, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(definition.TagReleaseUrlTemplate))
            return await ResolveLatestFromTagsAsync(definition, cancellationToken).ConfigureAwait(false);

        var url = definition.IsGitHub
            ? string.Format(definition.IncludePrereleases ? GitHubReleasesApi : GitHubLatestApi, definition.GitHubOwner, definition.GitHubRepo)
            : string.Format(GitLabReleasesApi, Uri.EscapeDataString(definition.GitLabProject!));
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                ? $"{definition.Name}: the release service is rate-limiting requests. Try again in a little while."
                : $"{definition.Name}: the release service returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var release = definition.IsGitHub
            ? definition.IncludePrereleases
                ? EmulatorReleaseParser.ParseGitHubReleases(json, definition)
                : EmulatorReleaseParser.ParseGitHub(json, definition)
            : EmulatorReleaseParser.ParseGitLab(json, definition);
        return release ?? throw new InvalidDataException($"{definition.Name}: no matching Windows download was found in the latest release.");
    }

    // Projects such as Dolphin ship Windows builds from their own download host, named after the
    // git tag, so the newest matching tag is turned into the archive URL.
    private async Task<EmulatorRelease> ResolveLatestFromTagsAsync(EmulatorDefinition definition, CancellationToken cancellationToken)
    {
        var url = string.Format(GitHubTagsApi, definition.GitHubOwner, definition.GitHubRepo);
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                ? $"{definition.Name}: the release service is rate-limiting requests. Try again in a little while."
                : $"{definition.Name}: the release service returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{definition.Name}: the release service returned an unexpected response.");

        var pattern = definition.TagPattern ?? "^[0-9]+$";
        var tags = document.RootElement.EnumerateArray()
            .Select(tag => tag.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null)
            .Where(tag => !string.IsNullOrWhiteSpace(tag) && System.Text.RegularExpressions.Regex.IsMatch(tag!, pattern))
            .Select(tag => tag!)
            .ToList();

        var version = tags
            .OrderByDescending(TagSortKey)
            .FirstOrDefault()
            ?? throw new InvalidDataException($"{definition.Name}: no matching Windows download was found in the latest release.");

        var downloadUrl = definition.TagReleaseUrlTemplate!.Replace("{version}", version, StringComparison.Ordinal);
        var assetName = downloadUrl[(downloadUrl.LastIndexOf('/') + 1)..];
        return new EmulatorRelease(version, new EmulatorAsset(assetName, downloadUrl, null));
    }

    private static (int Number, char Suffix) TagSortKey(string tag)
    {
        var digits = new string(tag.TakeWhile(char.IsDigit).ToArray());
        var suffix = tag.SkipWhile(char.IsDigit).FirstOrDefault();
        return (int.TryParse(digits, out var number) ? number : 0, suffix);
    }

    private async Task DownloadAsync(string url, string target, string assetName, IProgress<EmulatorInstallProgress>? progress, CancellationToken cancellationToken, string? referer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(referer)) request.Headers.Referrer = new Uri(referer);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        if (total > MaximumDownloadBytes) throw new InvalidDataException("The emulator download is too large.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[81920];
        long read = 0;
        var lastPercent = -1;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (read + count > MaximumDownloadBytes) throw new InvalidDataException("The emulator download is too large.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            read += count;
            if (total <= 0) continue;
            var percent = (int)(read * 100 / total);
            if (percent == lastPercent) continue;
            lastPercent = percent;
            progress?.Report(new EmulatorInstallProgress($"Downloading {assetName}", percent / 100d));
        }
    }

    private async Task<string?> DownloadChecksumAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var value = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return value is { Length: 64 } && value.All(Uri.IsHexDigit) ? value : null;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Commit(string stage, string destination)
    {
        var backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(destination)) Directory.Move(destination, backup);
        try
        {
            Directory.Move(stage, destination);
        }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(backup)) Directory.Move(backup, destination);
            throw;
        }
        TryDeleteDirectory(backup);
    }

    private static void RejectLinks(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
            if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("The emulator archive contains a symbolic link.");
    }

    private static bool IsSharingViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is IOException && (current.HResult & 0xffff) is 32 or 33) return true;
        return false;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
        return bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0} MB" : $"{bytes / 1024d:0} KB";
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
