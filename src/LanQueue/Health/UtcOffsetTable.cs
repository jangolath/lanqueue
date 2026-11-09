using System.Globalization;

namespace LanQueue;

/// <summary>
/// The worker's UTC offset (minutes) at its own now + 0 h, + 1 h ... + 36 h, as its probe reports it (<c>now_epoch=</c> and
/// <c>tz_offsets=</c>), so an allowed-hours window end in the worker's local time can be turned into a real instant across a DST change.
/// The table is sampled hourly, so an offset change is placed to within the hour it was sampled in; beyond 36 hours the last offset holds.
/// </summary>
public sealed record UtcOffsetTable(long NowEpoch, IReadOnlyList<int> OffsetMinutes)
{
    /// <summary>Hourly samples the probe prints: now and the next 36 hours.</summary>
    public const int Samples = 37;

    /// <summary>No real zone is more than 18 hours from UTC.</summary>
    private const int MaxOffsetMinutes = 18 * 60;

    /// <summary>Parses the probe's two values; null when either is missing or garbled (the caller then uses wall-clock time).</summary>
    public static UtcOffsetTable? TryParse(string? nowEpoch, string? offsets)
    {
        if (!long.TryParse(nowEpoch, NumberStyles.None, CultureInfo.InvariantCulture, out var now) || string.IsNullOrEmpty(offsets))
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

        return new UtcOffsetTable(now, list);
    }

    /// <summary>The offset in force at <paramref name="epoch"/>: the sample of the hour it falls in, clamped to the table.</summary>
    private int OffsetAt(long epoch) => OffsetMinutes[(int)Math.Clamp((epoch - NowEpoch) / 3600, 0, Samples - 1)];

    private long SampleAt(int k) => NowEpoch + (k * 3600L);

    /// <summary>
    /// The instant (Unix seconds) of a worker-local wall-clock time. A time that happens twice (the hour repeated when clocks go back)
    /// is the EARLIER instant, so a window never ends later than its owner expects. A time that does not exist (skipped when clocks go
    /// forward) is the first instant after the skip that the table knows.
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

        // In a gap: the change lies between the candidates. The first sample after it is a valid instant, and so is the latest candidate.
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
