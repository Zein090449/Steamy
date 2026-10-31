using System.Globalization;

namespace Steamy.Services;

/// <summary>One shared 12-hour clock used by chat, lists, tooltips and notifications.</summary>
public static class SocialTime
{
    public static string Clock(DateTimeOffset time) => time.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);
    public static string ClockWithSeconds(DateTimeOffset time) => time.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.InvariantCulture);
    public static string Day(DateTime day) => day.Date == DateTime.Today ? "Today"
        : day.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
        : day.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
    public static string DayLong(DateTime day) => day.Date == DateTime.Today ? "Today"
        : day.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
        : day.ToString("ddd, MMM d, yyyy", CultureInfo.InvariantCulture);
    public static string Relative(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        var elapsed = DateTimeOffset.Now - local;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed < TimeSpan.FromMinutes(1)) return "now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}m";
        if (local.Date == DateTime.Today) return $"{(int)elapsed.TotalHours}h";
        if (local.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        if (local.Year == DateTime.Today.Year) return local.ToString("MMM d", CultureInfo.InvariantCulture);
        return local.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }
    public static string MemberSince(ulong userId)
    {
        var created = DiscordSnowflake.Time(userId).ToLocalTime();
        return "Member since " + created.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
    }
}
public static class DiscordSnowflake
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.FromUnixTimeMilliseconds(1_420_070_400_000);
    public static DateTimeOffset Time(ulong id) => id == 0 ? DateTimeOffset.MinValue : Epoch + TimeSpan.FromMilliseconds((double)(id >> 22));
}
