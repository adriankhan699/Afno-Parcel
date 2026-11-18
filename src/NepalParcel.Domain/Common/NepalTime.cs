using System.Globalization;

namespace NepalParcel.Domain.Common;

/// <summary>
/// All timestamps are stored and exchanged in UTC. This helper converts them to Nepal Time
/// (Asia/Kathmandu, UTC+05:45, no daylight saving) for display only.
/// </summary>
public static class NepalTime
{
    public const string IanaId = "Asia/Kathmandu";

    private static readonly TimeZoneInfo Zone = Resolve();

    public static TimeSpan Offset { get; } = new(5, 45, 0);

    public static DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    /// <summary>Formats as "2026-11-17 14:30 NPT".</summary>
    public static string Format(DateTimeOffset utc) =>
        ToLocal(utc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " NPT";

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { IanaId, "Nepal Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // try next id
            }
            catch (InvalidTimeZoneException)
            {
                // try next id
            }
        }

        // Nepal has a fixed offset, so a custom zone is exactly equivalent when tz data is unavailable (e.g. minimal containers).
        return TimeZoneInfo.CreateCustomTimeZone(IanaId, new TimeSpan(5, 45, 0), "Nepal Time", "Nepal Time");
    }
}
