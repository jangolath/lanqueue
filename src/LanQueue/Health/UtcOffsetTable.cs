using System.Globalization;

namespace LanQueue;

/// <summary>
/// The worker's UTC offset (minutes) on every UTC quarter hour from the one at or before its own now for the next 36 hours, as its probe
/// reports it (<c>now_epoch=</c>, <c>tz_start=</c> and <c>tz_offsets=</c>), so an allowed-hours window end in the worker's local time can
/// be turned into a real instant across a DST change. Clock changes happen on a UTC quarter hour everywhere, so sample k holds for
/// exactly [start + k * 15 min, start + (k + 1) * 15 min); beyond 36 hours the last offset holds.
/// </summary>
/// <param name="NowEpoch">The worker's now, Unix seconds.</param>
/// <param name="StartEpoch">The first sample: the UTC quarter hour at or before <paramref name="NowEpoch"/>.</param>
public sealed record UtcOffsetTable(long NowEpoch, long StartEpoch, IReadOnlyList<int> OffsetMinutes)
{
    /// <summary>Seconds between samples: a quarter hour.</summary>
    public const int Step = 900;

    /// <summary>Samples the probe prints: 36 hours of quarter hours, plus the first.</summary>
    public const int Samples = (36 * 4) + 1;

    /// <summary>No real zone is more than 18 hours from UTC.</summary>
    private const int MaxOffsetMinutes = 18 * 60;

    /// <summary>Parses the probe's three values; null when any is missing or garbled (the caller then uses wall-clock time).</summary>
    public static UtcOffsetTable? TryParse(string? nowEpoch, string? startEpoch, string? offsets)
    {
        if (!long.TryParse(nowEpoch, NumberStyles.None, CultureInfo.InvariantCulture, out var now)
            || !long.TryParse(startEpoch, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || start % Step != 0 || now < start || now - start >= Step || string.IsNullOrEmpty(offsets))
        {
            return null;
        }

        var parts = offsets.Split(',');
        if (parts.Length != Samples)
        {
            return null;
        }

        var list = new int[Samples];
        for (var i = 0; i < Samples; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out list[i]) || Math.Abs(list[i]) > MaxOffsetMinutes)
            {
                return null;
            }
        }

        return new UtcOffsetTable(now, start, list);
    }

    /// <summary>The offset in force at <paramref name="epoch"/>: the sample of the quarter hour it falls in, clamped to the table.</summary>
    private int OffsetAt(long epoch) => OffsetMinutes[(int)Math.Clamp(Math.Floor((epoch - StartEpoch) / (double)Step), 0, Samples - 1)];

    private long SampleAt(int k) => StartEpoch + (k * (long)Step);

    /// <summary>
    /// The instant (Unix seconds) of a worker-local wall-clock time. A time that happens twice (the hour repeated when clocks go back)
    /// is the EARLIER instant, so a window never ends later than its owner expects. A time that does not exist (skipped when clocks go
    /// forward) is the instant of the jump, the first one after the skip.
    /// </summary>
    public long ToEpoch(DateTime local)
    {
        var wall = (long)(DateTime.SpecifyKind(local, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;
        var candidates = OffsetMinutes.Distinct().Select(o => (Offset: o, Epoch: wall - (o * 60L))).ToList();
        var valid = candidates.Where(c => OffsetAt(c.Epoch) == c.Offset).Select(c => c.Epoch).ToList();
        if (valid.Count > 0)
        {
            return valid.Min();
        }

        // In a gap: the change lies between the candidates, at a sample boundary.
        var (from, to) = (candidates.Min(c => c.Epoch), candidates.Max(c => c.Epoch));
        for (var k = 1; k < Samples; k++)
        {
            if (OffsetMinutes[k] != OffsetMinutes[k - 1] && SampleAt(k) > from && SampleAt(k) <= to)
            {
                return SampleAt(k);
            }
        }

        return to;
    }
}
