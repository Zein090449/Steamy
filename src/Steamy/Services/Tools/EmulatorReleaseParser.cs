using System.Text.Json;

namespace Steamy.Services;

/// <summary>Reads the latest installer asset from GitHub and GitLab release responses.</summary>
public static class EmulatorReleaseParser
{
    public static EmulatorRelease? ParseGitHub(string json, EmulatorDefinition definition)
    {
        if (!definition.IsGitHub || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ParseGitHubRelease(document.RootElement, definition)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Projects whose builds are all prereleases (Lichii) publish no "latest", so the list is read instead.</summary>
    public static EmulatorRelease? ParseGitHubReleases(string json, EmulatorDefinition definition)
    {
        if (!definition.IsGitHub || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            var releases = document.RootElement.EnumerateArray()
                .Where(release => release.ValueKind == JsonValueKind.Object)
                .Where(release => !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.True)
                .OrderByDescending(release => ReadString(release, "published_at") ?? ReadString(release, "created_at") ?? string.Empty, StringComparer.Ordinal);
            foreach (var release in releases)
            {
                var parsed = ParseGitHubRelease(release, definition);
                if (parsed is not null) return parsed;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static EmulatorRelease? ParseGitHubRelease(JsonElement root, EmulatorDefinition definition)
    {
        var version = ReadString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(version)
            || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in assets.EnumerateArray())
        {
            var name = ReadString(item, "name");
            var url = ReadString(item, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
            if (!IsAllowedGitHubUrl(url, definition) || !definition.AssetFilter(name)) continue;

            var digest = ReadString(item, "digest");
            var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest[7..].Trim() : null;
            var size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value) ? value : 0;
            return new EmulatorRelease(version, new EmulatorAsset(name, url, string.IsNullOrWhiteSpace(sha256) ? null : sha256, size));
        }

        return null;
    }

    public static EmulatorRelease? ParseGitLab(string json, EmulatorDefinition definition)
    {
        if (definition.GitLabProject is null || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            var releases = document.RootElement.EnumerateArray()
                .OrderByDescending(release => ReadString(release, "released_at") ?? string.Empty, StringComparer.Ordinal)
                .ToList();
            foreach (var release in releases)
            {
                var version = ReadString(release, "tag_name");
                if (string.IsNullOrWhiteSpace(version)) continue;
                if (!release.TryGetProperty("assets", out var assets)
                    || !assets.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
                    continue;

                EmulatorAsset? archive = null;
                string? checksumUrl = null;
                string? checksumName = null;
                foreach (var link in links.EnumerateArray())
                {
                    var name = ReadString(link, "name");
                    var url = ReadString(link, "direct_asset_url") ?? ReadString(link, "url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                    if (!IsAllowedGitLabUrl(url, definition.GitLabProject)) continue;

                    if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                    {
                        checksumUrl ??= url;
                        checksumName ??= name;
                        continue;
                    }

                    if (archive is null && definition.AssetFilter(name))
                        archive = new EmulatorAsset(name, url, null);
                }

                if (archive is null) continue;
                var checksum = checksumName is not null && checksumName.Equals(archive.Name + ".sha256", StringComparison.OrdinalIgnoreCase)
                    ? checksumUrl : null;
                return new EmulatorRelease(version, archive, checksum);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool IsAllowedGitHubUrl(string value, EmulatorDefinition definition)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        return uri.AbsolutePath.StartsWith($"/{definition.GitHubOwner}/{definition.GitHubRepo}/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedGitLabUrl(string value, string projectPath)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase)) return false;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        return path.StartsWith($"/{projectPath}/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith($"/api/v4/projects/{projectPath}/", StringComparison.OrdinalIgnoreCase);
    }
}
