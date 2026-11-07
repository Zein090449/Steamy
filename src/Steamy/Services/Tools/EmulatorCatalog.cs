namespace Steamy.Services;

/// <summary>An emulator Steamy can download, install, and launch. Steamy only supplies the emulator itself.</summary>
public sealed record EmulatorDefinition(
    string Id,
    string Name,
    string Platform,
    string Badge,
    string Description,
    string Requirements,
    string ProjectUrl,
    string ExecutableName,
    IReadOnlyList<string> AlternateExecutables,
    string? GitHubOwner,
    string? GitHubRepo,
    string? GitLabProject,
    Func<string, bool> AssetFilter,
    bool IncludePrereleases = false,
    bool SupportsGameLibrary = true,
    string GameFileFilter = "All files|*.*",
    string? GameExecutableName = null,
    string? GameArgumentFlag = null,
    // For projects that publish Windows builds outside GitHub releases: the newest matching
    // git tag is turned into a download URL with {version} substituted in the template.
    string? TagReleaseUrlTemplate = null,
    string? TagPattern = null,
    string? DownloadReferer = null)
{
    public IReadOnlyList<string> ExecutableNames { get; } = [ExecutableName, .. AlternateExecutables];
    public bool IsGitHub => GitHubOwner is not null && GitHubRepo is not null;
}

public static class EmulatorCatalog
{
    private static bool Zip(string name) => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    private static bool Has(string name, string value) => name.Contains(value, StringComparison.OrdinalIgnoreCase);

    public static readonly EmulatorDefinition KytyPS5 = new(
        "kytyps5",
        "KytyPS5",
        "PlayStation 5",
        "PS5",
        "An open-source PlayStation 5 translation layer for Windows. It runs your own PS5 game dumps; compatibility is still early and varies by game.",
        "You need a dump of a PS5 game you own and its matching system modules. No games, firmware, or system files are included.",
        "https://github.com/KytyPS5/KytyPS5",
        "kyty_launcher.exe",
        ["kyty_emulator.exe"],
        "KytyPS5", "KytyPS5", null,
        name => Zip(name) && Has(name, "windows") && Has(name, "x64"),
        GameFileFilter: "PS5 game (*.elf;*.zar;*.bin)|*.elf;*.zar;*.bin|All files|*.*",
        GameExecutableName: "kyty_emulator.exe",
        GameArgumentFlag: "--game");

    public static readonly EmulatorDefinition ShadPS4 = new(
        "shadps4",
        "shadPS4",
        "PlayStation 4",
        "PS4",
        "A free PlayStation 4 emulator for Windows. First launch asks for PS4 firmware modules; games run from your own decrypted dumps.",
        "You need a dump of a PS4 game you own and PS4 firmware files. No games, firmware, or keys are included.",
        "https://github.com/shadps4-emu/shadps4",
        "shadps4.exe",
        [],
        "shadps4-emu", "shadps4", null,
        name => Zip(name) && Has(name, "win64"),
        GameFileFilter: "PS4 game (*.elf;*.bin;*.pkg)|*.elf;*.bin;*.pkg|All files|*.*");

    public static readonly EmulatorDefinition RyGts = new(
        "rygts",
        "RyGTS",
        "PlayStation 4 · Gran Turismo Sport",
        "GT SPORT",
        "A Gran Turismo Sport port built on shadPS4. It launches GT Sport only and is not a general PlayStation 4 emulator.",
        "Install it on the same drive as your own decrypted GT Sport dump (CUSA03220, update 1.69). PS4 firmware modules and fonts are required.",
        "https://gitlab.com/rygt-team/RyGTS",
        "RyGT.exe",
        [],
        null, null, "rygt-team/RyGTS",
        name => Zip(name) && !name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase),
        SupportsGameLibrary: false);

    public static readonly EmulatorDefinition Ruzu = new(
        "ruzu",
        "Ruzu",
        "Nintendo Switch",
        "SWITCH",
        "An open-source Nintendo Switch emulator for Windows with a game library and Vulkan/OpenGL rendering.",
        "You need your own Switch game dumps, prod.keys, and firmware. No games, keys, or firmware are included.",
        "https://github.com/vricosti/ruzu-emu",
        "ruzu.exe",
        [],
        "vricosti", "ruzu-emu", null,
        name => Zip(name) && Has(name, "windows") && Has(name, "x64") && !Has(name, "installer"),
        GameFileFilter: "Switch game (*.nsp;*.xci;*.nca;*.nro;*.xcz)|*.nsp;*.xci;*.nca;*.nro;*.xcz|All files|*.*",
        GameArgumentFlag: "--game");

    public static readonly EmulatorDefinition Lichii = new(
        "lichii",
        "Lichii",
        "Nintendo Switch 2",
        "SWITCH 2",
        "A very experimental open-source Nintendo Switch 2 emulator built on the Eden, yuzu, and Sudachi lineage. Compatibility is early and varies by game.",
        "You need your own Switch 2 game dumps, prod.keys, and firmware. No games, keys, or firmware are included.",
        "https://github.com/zortos293/Lichii",
        "lichii.exe",
        [],
        "zortos293", "Lichii", null,
        name => Zip(name) && Has(name, "windows") && Has(name, "x86_64") && !Has(name, "pgo"),
        IncludePrereleases: true,
        GameFileFilter: "Switch 2 game (*.nsp;*.xci;*.nca;*.xcz)|*.nsp;*.xci;*.nca;*.xcz|All files|*.*");

    public static readonly EmulatorDefinition Dolphin = new(
        "dolphin",
        "Dolphin",
        "Nintendo Wii · GameCube",
        "WII",
        "The long-running GameCube and Wii emulator for Windows, with HD rendering, save states, and netplay. Runs your own game dumps.",
        "You need your own GameCube or Wii game dumps (ISO, RVZ, WBFS, or a dumped disc image). No games or system files are included.",
        "https://dolphin-emu.org/download/",
        "Dolphin.exe",
        [],
        "dolphin-emu", "dolphin", null,
        name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
        GameFileFilter: "Wii / GameCube game (*.iso;*.rvz;*.gcm;*.wbfs;*.gcz;*.ciso;*.wad;*.elf;*.dol)|*.iso;*.rvz;*.gcm;*.wbfs;*.gcz;*.ciso;*.wad;*.elf;*.dol|All files|*.*",
        GameExecutableName: "Dolphin.exe",
        GameArgumentFlag: "-e",
        TagReleaseUrlTemplate: "https://dl.dolphin-emu.org/releases/{version}/dolphin-{version}-x64.7z",
        TagPattern: "^[0-9]{4}[a-z]?$",
        DownloadReferer: "https://dolphin-emu.org/download/");

    public static readonly EmulatorDefinition Azahar = new(
        "azahar",
        "Azahar",
        "Nintendo 3DS",
        "3DS",
        "The maintained continuation of Citra, the Nintendo 3DS emulator. Plays your own 3DS dumps in both screens with upscaling.",
        "You need your own decrypted 3DS game dumps and the shared system files from a 3DS you own. No games, keys, or system files are included.",
        "https://github.com/azahar-emu/azahar",
        "azahar.exe",
        [],
        "azahar-emu", "azahar", null,
        name => Zip(name) && Has(name, "windows-msvc") && !Has(name, "installer") && !Has(name, "libretro"),
        IncludePrereleases: true,
        GameFileFilter: "3DS game (*.3ds;*.cci;*.cxi;*.cia;*.app;*.3dsx)|*.3ds;*.cci;*.cxi;*.cia;*.app;*.3dsx|All files|*.*");

    public static readonly EmulatorDefinition Cemu = new(
        "cemu",
        "Cemu",
        "Nintendo Wii U",
        "WII U",
        "A Wii U emulator for Windows with Vulkan and OpenGL rendering, graphic packs, and support for your own decrypted disc dumps.",
        "You need your own decrypted Wii U game dumps (WUD/WUX/RPX) and the shared keys from a Wii U you own. No games, keys, or system files are included.",
        "https://github.com/cemu-project/Cemu",
        "Cemu.exe",
        [],
        "cemu-project", "Cemu", null,
        name => Zip(name) && Has(name, "windows") && !Has(name, "macos") && !Has(name, "ubuntu") && !Has(name, "appimage"),
        GameFileFilter: "Wii U game (*.wud;*.wux;*.wua;*.rpx)|*.wud;*.wux;*.wua;*.rpx|All files|*.*");

    public static IReadOnlyList<EmulatorDefinition> All { get; } = [KytyPS5, ShadPS4, RyGts, Ruzu, Lichii, Dolphin, Azahar, Cemu];
}

public sealed record EmulatorAsset(string Name, string DownloadUrl, string? Sha256, long SizeBytes = 0);

public sealed record EmulatorRelease(string Version, EmulatorAsset Archive, string? ChecksumUrl = null);

/// <summary>A game the user added to Steamy's own library; Steamy only stores the path.</summary>
public sealed record EmulatorGame(string Name, string Path);
