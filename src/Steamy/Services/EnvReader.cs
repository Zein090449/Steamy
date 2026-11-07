using System.IO;
using System.Text.Json;

namespace Steamy.Services;

/// <summary>
/// Reads key-value pairs from a .env file in the application root.
/// Format: KEY=VALUE (lines starting with # are comments, empty lines skipped).
/// </summary>
public static class EnvReader
{
    private static readonly Lazy<Dictionary<string, string>> _cache =
        new Lazy<Dictionary<string, string>>(LoadEnv);

    private static Dictionary<string, string> LoadEnv()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var appDir = AppContext.BaseDirectory;
        // Try workspace root (2 levels up from bin/Debugnet6.0/)
        var possiblePaths = new[]
        {
            Path.Combine(appDir, "..", "..", "..", ".."), // workspace root
            appDir,
            Directory.GetCurrentDirectory()
        };

        string? envFilePath = null;
        foreach (var baseDir in possiblePaths)
        {
            var candidate = Path.Combine(Path.GetFullPath(baseDir), ".env");
            if (File.Exists(candidate))
            {
                envFilePath = candidate;
                break;
            }
            // Also check parent directories
            var parentDir = Directory.GetParent(baseDir);
            while (parentDir != null)
            {
                var parentPath = Path.GetFullPath(parentDir.FullName);
                candidate = Path.Combine(parentPath, ".env");
                if (File.Exists(candidate))
                {
                    envFilePath = candidate;
                    break;
                }
                parentDir = parentDir.Parent;
            }
            if (envFilePath != null) break;
        }

        if (envFilePath is null) return dict;

        try
        {
            foreach (var line in File.ReadAllLines(envFilePath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx < 0) continue;
                var key = trimmed[..eqIdx].Trim();
                var value = trimmed[(eqIdx + 1)..].Trim();
                // Remove surrounding quotes if present
                if ((value.StartsWith('"') && value.EndsWith('"'))
                    || (value.StartsWith("'") && value.EndsWith("'")))
                {
                    value = value[1..^1];
                }
                dict[key] = value;
            }
        }
        catch
        {
            // If .env can't be read, return empty dict (key simply won't be available)
        }

        return dict;
    }

    /// <summary>
    /// Gets an environment variable value from the .env file, or from actual OS environment.
    /// </summary>
    public static string? Get(string key)
    {
        // First try .env file cache
        if (_cache.Value.TryGetValue(key, out var val))
            return val;

        // Fall back to OS environment
        return Environment.GetEnvironmentVariable(key);
    }

    /// <summary>
    /// Gets the 1fichier API key from .env or OS environment.
    /// </summary>
    public static string? GetFichierApiKey() => Get("FICHIER_API_KEY");
}