using System.Globalization;
using System.Text.RegularExpressions;

namespace LanQueue;

/// <summary>
/// One allowed_hours entry: <c>[Days ]HH:mm-HH:mm</c>, for example "22:00-07:00", "Mon-Fri 18:00-23:00" or "Sat,Sun 08:00-22:00".
/// Days are Mon..Sun (any case), as ranges (Mon-Fri; a range may wrap, Fri-Mon) and comma lists; no days means every day. A window that
/// crosses midnight belongs to its START day, so "Fri 22:00-07:00" covers Friday night into Saturday morning. The start minute is in the
/// window, the end minute is not. Times are the worker's own local clock when it reports one (see Health.HealthProbe).
/// </summary>
public sealed partial class HoursWindow
{
    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"]; // index = (int)DayOfWeek

    private readonly int _days; // bit (int)DayOfWeek set for each day a window may start on
    private readonly TimeOnly _start;
    private readonly TimeOnly _end;

    private HoursWindow(int days, TimeOnly start, TimeOnly end)
    {
        _days = days;
        _start = start;
        _end = end;
    }

    [GeneratedRegex(@"^(?:(?<days>[A-Za-z][A-Za-z, -]*?) +)?(?<start>[0-9]{2}:[0-9]{2}) *- *(?<end>[0-9]{2}:[0-9]{2})\z", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex EntryRegex();

    public static bool TryParse(string text, out HoursWindow window)
    {
        window = null!;
        var match = EntryRegex().Match(text.Trim(' '));
        if (!match.Success
            || !TimeOnly.TryParseExact(match.Groups["start"].Value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s)
            || !TimeOnly.TryParseExact(match.Groups["end"].Value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e))
        {
            return false;
        }

        var days = match.Groups["days"].Success ? ParseDays(match.Groups["days"].Value) : 0x7F;
        if (days == 0)
        {
            return false;
        }

        window = new HoursWindow(days, s, e) { HasDays = match.Groups["days"].Success };
        return true;
    }

    /// <summary>"Mon-Fri", "Sat,Sun" or "Mon,Wed-Fri" (spaces around ',' and '-' allowed) as a day bit mask; 0 when invalid.</summary>
    private static int ParseDays(string text)
    {
        var mask = 0;
        foreach (var part in text.Replace(" ", "", StringComparison.Ordinal).Split(','))
        {
            var range = part.Split('-');
            var from = DayIndex(range[0]);
            var to = range.Length == 2 ? DayIndex(range[1]) : from;
            if (range.Length > 2 || from < 0 || to < 0)
            {
                return 0;
            }

            for (var d = from; ; d = (d + 1) % 7)
            {
                mask |= 1 << d;
                if (d == to)
                {
                    break;
                }
            }
        }

        return mask;
    }

    private static int DayIndex(string name) => Array.FindIndex(DayNames, d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The times alone, normalised as HH:mm-HH:mm (no days, no spaces): what lanq passes to lanq-makeready as -Hours.</summary>
    public string TimeText => _start.ToString("HH:mm", CultureInfo.InvariantCulture) + "-" + _end.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>True when the entry names days ("Mon-Fri 18:00-23:00"); false for every day ("18:00-23:00").</summary>
    public bool HasDays { get; private init; }

    /// <summary>True for a window like "22:00-22:00", which would match a single minute; config validation refuses it.</summary>
    public bool IsSingleInstant => _start == _end;

    /// <summary>The end of this window's occurrence that contains <paramref name="t"/> (one that started today or yesterday), or null.</summary>
    private DateTime? EndOfOccurrenceContaining(DateTime t)
    {
        for (var back = 0; back <= 1; back++)
        {
            var day = t.Date.AddDays(-back);
            var start = day + _start.ToTimeSpan();
            var end = (_end > _start ? day : day.AddDays(1)) + _end.ToTimeSpan();
            if ((_days & (1 << (int)day.DayOfWeek)) != 0 && t >= start && t < end)
            {
                return end;
            }
        }

        return null;
    }

    /// <summary>
    /// How long the machine may still work from <paramref name="now"/> (its local clock): to the end of the window it is in, carried on
    /// through windows that continue it within a minute (so "Sat 00:00-23:59" then "Sun 00:00-23:59" is one stretch). Null when outside
    /// every window. With the worker's <paramref name="offsets"/> table the window end becomes a real instant, so a night with a DST
    /// change counts real time; without one it is wall-clock time (an hour off on such a night).
    /// </summary>
    public static TimeSpan? Remaining(IReadOnlyList<HoursWindow> windows, DateTime now, UtcOffsetTable? offsets = null)
    {
        var end = windows.Max(w => w.EndOfOccurrenceContaining(now));
        for (var hops = 0; end is { } e && hops < 64; hops++)
        {
            var next = windows.Max(w => w.EndOfOccurrenceContaining(e) ?? w.EndOfOccurrenceContaining(e.AddMinutes(1)));
            if (next is null || next <= e)
            {
                break;
            }

            end = next;
        }

        return end is not { } last ? null
            : offsets is null ? last - now
            : TimeSpan.FromSeconds(offsets.ToEpoch(last) - offsets.NowEpoch);
    }

    /// <summary>Parses allowed_hours entries that config validation has accepted; null when there are none (any time is fine).</summary>
    public static List<HoursWindow>? ParseAll(IEnumerable<string>? entries) =>
        entries is null || !entries.Any()
            ? null
            : [.. entries.Select(e => TryParse(e, out var w) ? w : throw new FormatException($"Invalid allowed_hours entry '{e}'."))];
}
