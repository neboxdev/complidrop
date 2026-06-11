namespace CompliDrop.Api.Services;

/// <summary>
/// Single home for the "unknown timezone id → null (callers fall back to UTC)" policy,
/// consolidated from three private copies (ReminderBackgroundService, AuthEndpoints,
/// ExportService — #262 review, rule of three). A future change to the policy (logging
/// unknown ids, caching lookups) now lands once.
/// </summary>
internal static class TimeZones
{
    /// <summary>Returns the named IANA / Windows zone, or null for an unknown id.</summary>
    internal static TimeZoneInfo? TryFind(string tz)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(tz); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
}
