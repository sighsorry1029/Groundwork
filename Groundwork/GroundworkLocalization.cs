using System;
using System.Globalization;

namespace Groundwork;

internal static class GroundworkLocalization
{
    internal static string Text(string key, string fallback)
    {
        string token = "$" + key;
        string? localized = Localization.instance?.Localize(token);
        return string.IsNullOrWhiteSpace(localized) || string.Equals(localized, token, StringComparison.Ordinal)
            ? fallback
            : localized!;
    }

    internal static string Format(string key, string fallback, params object[] args)
    {
        return string.Format(CultureInfo.InvariantCulture, Text(key, fallback), args);
    }

    internal static string FormatDuration(float seconds)
    {
        int totalSeconds = (int)Math.Ceiling(Math.Max(0f, seconds));
        if (totalSeconds <= 0)
        {
            return Text("groundwork_time_ready", "ready");
        }

        if (totalSeconds < 60)
        {
            return Format("groundwork_time_seconds", "{0}s", totalSeconds);
        }

        int minutes = totalSeconds / 60;
        int remainingSeconds = totalSeconds % 60;
        if (minutes < 60)
        {
            return remainingSeconds > 0
                ? Format("groundwork_time_minutes_seconds", "{0}m {1}s", minutes, remainingSeconds)
                : Format("groundwork_time_minutes", "{0}m", minutes);
        }

        int hours = minutes / 60;
        minutes %= 60;
        return minutes > 0
            ? Format("groundwork_time_hours_minutes", "{0}h {1}m", hours, minutes)
            : Format("groundwork_time_hours", "{0}h", hours);
    }
}
