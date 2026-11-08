using System.Globalization;

namespace LanQueue;

/// <summary>A daily time window such as "22:00-07:00" (may wrap midnight), evaluated in the orchestrator's local time.</summary>
public sealed class HoursWindow
{
    private readonly TimeOnly _start;
    private readonly TimeOnly _end;

    private HoursWindow(TimeOnly start, TimeOnly end)
    {
        _start = start;
        _end = end;
    }

    public static bool TryParse(string text, out HoursWindow window)
    {
        window = null!;
        var parts = text.Split('-');
        if (parts.Length != 2
            || !TimeOnly.TryParseExact(parts[0].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s)
            || !TimeOnly.TryParseExact(parts[1].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e))
        {
            return false;
        }

        window = new HoursWindow(s, e);
        return true;
    }

    public bool Contains(TimeOnly t) =>
        _start <= _end ? t >= _start && t <= _end : t >= _start || t <= _end;

    public static bool IsAllowed(string? window, TimeOnly now) =>
        window is null || (TryParse(window, out var w) && w.Contains(now));
}
