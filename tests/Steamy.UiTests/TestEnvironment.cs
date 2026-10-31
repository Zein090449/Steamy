using System.IO;
using System.Runtime.CompilerServices;
using Steamy.Services;

namespace Steamy.UiTests;

internal static class TestEnvironment
{
    internal static string Root { get; } = Path.Combine(Path.GetTempPath(), "steamy-ui-tests-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(Root);
        // Keep every test write (conversation store, settings, logs) away from the real %APPDATA% profile.
        Environment.SetEnvironmentVariable("STEAMY_DATA_DIR", Root);
    }
}

internal static class TestStores
{
    public static DiscordConversationStore Isolated() =>
        new(Path.Combine(Path.GetTempPath(), "steamy-ui-tests-conversations-" + Guid.NewGuid().ToString("N") + ".json"));
}
