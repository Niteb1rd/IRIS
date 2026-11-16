using System.Globalization;

namespace Iris.Core.Config;

public static class DateText
{
    public const int UniverseYearOffset = 930;

    public static DateTimeOffset Shown(DateTimeOffset local, DateFormat format) =>
        format == DateFormat.StarCitizen ? local.AddYears(UniverseYearOffset) : local;

    public static string Stamp(DateTimeOffset local, DateFormat format) =>
        Shown(local, format).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
