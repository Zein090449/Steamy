using Steamy.Services;

namespace Steamy.Tests;

public sealed class EmulatorReleaseParserTests
{
    [Fact]
    public void GitHubParserPicksTheWindowsArchiveAndKeepsItsDigest()
    {
        const string json = """
            {
              "tag_name": "KytyPS5-2026-11-04-91c5820",
              "assets": [
                { "name": "KytyPS5-2026-11-04-91c5820-Linux-x86_64.tar.gz", "browser_download_url": "https://github.com/KytyPS5/KytyPS5/releases/download/KytyPS5-2026-11-04-91c5820/KytyPS5-linux.tar.gz", "size": 42143780 },
                { "name": "KytyPS5-2026-11-04-91c5820-macOS-x86_64.zip", "browser_download_url": "https://github.com/KytyPS5/KytyPS5/releases/download/KytyPS5-2026-11-04-91c5820/KytyPS5-mac.zip", "size": 55062124 },
                { "name": "KytyPS5-2026-11-04-91c5820-Windows-x64.zip", "browser_download_url": "https://github.com/KytyPS5/KytyPS5/releases/download/KytyPS5-2026-11-04-91c5820/KytyPS5-Windows-x64.zip", "digest": "sha256:d8eb543d625d9b7a193a54e7fd1ccd266f10941cdcdb720ab88e7df96c12c5ff", "size": 26841347 }
              ]
            }
            """;

        var release = EmulatorReleaseParser.ParseGitHub(json, EmulatorCatalog.KytyPS5);

        Assert.NotNull(release);
        Assert.Equal("KytyPS5-2026-11-04-91c5820", release.Version);
        Assert.Equal("KytyPS5-2026-11-04-91c5820-Windows-x64.zip", release.Archive.Name);
        Assert.Equal(26841347, release.Archive.SizeBytes);
        Assert.Equal("d8eb543d625d9b7a193a54e7fd1ccd266f10941cdcdb720ab88e7df96c12c5ff", release.Archive.Sha256, ignoreCase: true);
    }

    [Fact]
    public void GitHubParserIgnoresTheRuzuInstaller()
    {
        const string json = """
            {
              "tag_name": "v0.1.1-rc8",
              "assets": [
                { "name": "Ruzu-Windows-v0.1.1-rc8-x64-msvc-installer.exe", "browser_download_url": "https://github.com/vricosti/ruzu-emu/releases/download/v0.1.1-rc8/Ruzu-Windows-x64-msvc-installer.exe" },
                { "name": "Ruzu-Linux-v0.1.1-rc8-x86_64.AppImage", "browser_download_url": "https://github.com/vricosti/ruzu-emu/releases/download/v0.1.1-rc8/Ruzu-Linux.AppImage" },
                { "name": "Ruzu-Windows-v0.1.1-rc8-x64-msvc.zip", "browser_download_url": "https://github.com/vricosti/ruzu-emu/releases/download/v0.1.1-rc8/Ruzu-Windows-v0.1.1-rc8-x64-msvc.zip" }
              ]
            }
            """;

        var release = EmulatorReleaseParser.ParseGitHub(json, EmulatorCatalog.Ruzu);

        Assert.NotNull(release);
        Assert.Equal("Ruzu-Windows-v0.1.1-rc8-x64-msvc.zip", release.Archive.Name);
    }

    [Fact]
    public void GitHubParserRejectsDownloadsOutsideTheProject()
    {
        const string json = """
            {
              "tag_name": "v0.21.0",
              "assets": [
                { "name": "shadps4-win64-sdl-0.21.0.zip", "browser_download_url": "https://example.com/shadps4-emu/shadps4/releases/download/v0.21.0/shadps4.zip" }
              ]
            }
            """;

        Assert.Null(EmulatorReleaseParser.ParseGitHub(json, EmulatorCatalog.ShadPS4));
    }

    [Fact]
    public void GitLabParserPicksTheNewestReleaseWithItsMatchingChecksum()
    {
        const string checksumUrl = "https://gitlab.com/api/v4/projects/rygt-team%2FRyGTS/packages/generic/gt-sport-prerelease/gt-sport-2026-10-29/GTSport-prerelease.zip.sha256";
        const string archiveUrl = "https://gitlab.com/api/v4/projects/rygt-team%2FRyGTS/packages/generic/gt-sport-prerelease/gt-sport-2026-10-29/GTSport-prerelease.zip";
        const string json = $$"""
            [
              {
                "tag_name": "gt-sport-2026-10-25",
                "released_at": "2026-10-25T23:30:29.771Z",
                "assets": { "links": [
                  { "name": "GTSport-prerelease.zip", "url": "https://gitlab.com/api/v4/projects/rygt-team%2FRyGTS/packages/generic/gt-sport-prerelease/gt-sport-2026-10-25/GTSport-prerelease.zip" }
                ] }
              },
              {
                "tag_name": "gt-sport-2026-10-29",
                "released_at": "2026-10-30T02:12:51.001Z",
                "assets": { "links": [
                  { "name": "GTSport-prerelease.zip.sha256", "direct_asset_url": "{{checksumUrl}}" },
                  { "name": "GTSport-prerelease.zip", "direct_asset_url": "{{archiveUrl}}" }
                ] }
              }
            ]
            """;

        var release = EmulatorReleaseParser.ParseGitLab(json, EmulatorCatalog.RyGts);

        Assert.NotNull(release);
        Assert.Equal("gt-sport-2026-10-29", release.Version);
        Assert.Equal("GTSport-prerelease.zip", release.Archive.Name);
        Assert.Equal(archiveUrl, release.Archive.DownloadUrl);
        Assert.Equal(checksumUrl, release.ChecksumUrl);
    }

    [Fact]
    public void GitLabParserRejectsLinksFromOtherProjects()
    {
        const string json = """
            [
              {
                "tag_name": "gt-sport-2026-10-29",
                "released_at": "2026-10-30T02:12:51.001Z",
                "assets": { "links": [
                  { "name": "GTSport-prerelease.zip", "url": "https://gitlab.com/someone-else/RyGTS/-/releases/download/GTSport-prerelease.zip" }
                ] }
              }
            ]
            """;

        Assert.Null(EmulatorReleaseParser.ParseGitLab(json, EmulatorCatalog.RyGts));
    }

    [Fact]
    public void MalformedReleaseJsonIsIgnored()
    {
        Assert.Null(EmulatorReleaseParser.ParseGitHub("{ not json", EmulatorCatalog.KytyPS5));
        Assert.Null(EmulatorReleaseParser.ParseGitLab("[", EmulatorCatalog.RyGts));
    }

    [Fact]
    public void GitHubListParserPicksTheNewestPrereleaseWithoutThePgoVariant()
    {
        const string json = """
            [
              {
                "tag_name": "build-70",
                "published_at": "2026-11-04T14:30:12Z",
                "prerelease": true,
                "assets": [
                  { "name": "lichii-windows-x86_64.zip", "browser_download_url": "https://github.com/zortos293/Lichii/releases/download/build-70/lichii-windows-x86_64.zip" }
                ]
              },
              {
                "tag_name": "build-75",
                "published_at": "2026-11-05T02:37:01Z",
                "prerelease": true,
                "assets": [
                  { "name": "lichii-windows-x86_64-pgo.zip", "browser_download_url": "https://github.com/zortos293/Lichii/releases/download/build-75/lichii-windows-x86_64-pgo.zip" },
                  { "name": "lichii-windows-x86_64.zip", "browser_download_url": "https://github.com/zortos293/Lichii/releases/download/build-75/lichii-windows-x86_64.zip", "digest": "sha256:d12f73fd711fbea70cf94f5cdbaaff4acdd88bb8dcbb6c45a631a487428342b0", "size": 115840760 },
                  { "name": "lichii-linux-x86_64.tar.gz", "browser_download_url": "https://github.com/zortos293/Lichii/releases/download/build-75/lichii-linux-x86_64.tar.gz" }
                ]
              }
            ]
            """;

        var release = EmulatorReleaseParser.ParseGitHubReleases(json, EmulatorCatalog.Lichii);

        Assert.NotNull(release);
        Assert.Equal("build-75", release.Version);
        Assert.Equal("lichii-windows-x86_64.zip", release.Archive.Name);
        Assert.Equal(115840760, release.Archive.SizeBytes);
    }

    [Fact]
    public void GitHubListParserSkipsDraftReleases()
    {
        const string json = """
            [
              {
                "tag_name": "build-80",
                "published_at": "2026-11-06T02:37:01Z",
                "draft": true,
                "assets": [
                  { "name": "lichii-windows-x86_64.zip", "browser_download_url": "https://github.com/zortos293/Lichii/releases/download/build-80/lichii-windows-x86_64.zip" }
                ]
              },
              {
                "tag_name": "build-75",
                "published_at": "2026-11-05T02:37:01Z",
                "assets": [
                  { "name": "lichii-windows-x86_64.zip", "browser_download_url": "https://github.com/zortos293/Lichii/releases/download/build-75/lichii-windows-x86_64.zip" }
                ]
              }
            ]
            """;

        var release = EmulatorReleaseParser.ParseGitHubReleases(json, EmulatorCatalog.Lichii);

        Assert.NotNull(release);
        Assert.Equal("build-75", release.Version);
    }

    [Fact]
    public void CatalogCoversAllEmulatorsWithLaunchExecutables()
    {
        Assert.Equal(8, EmulatorCatalog.All.Count);
        Assert.Equal(8, EmulatorCatalog.All.Select(definition => definition.Id).Distinct().Count());
        Assert.All(EmulatorCatalog.All, definition => Assert.False(string.IsNullOrWhiteSpace(definition.ExecutableName)));
        Assert.Contains("kyty_emulator.exe", EmulatorCatalog.KytyPS5.ExecutableNames);
        Assert.Equal("lichii.exe", EmulatorCatalog.Lichii.ExecutableName);
        Assert.True(EmulatorCatalog.Lichii.IncludePrereleases);
        Assert.False(EmulatorCatalog.RyGts.SupportsGameLibrary);
        Assert.Equal("--game", EmulatorCatalog.KytyPS5.GameArgumentFlag);
        Assert.Equal("--game", EmulatorCatalog.Ruzu.GameArgumentFlag);
    }
}
