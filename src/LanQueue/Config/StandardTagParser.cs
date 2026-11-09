using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace LanQueue.Config;

/// <summary>
/// Reads YAML with the explicit standard tags (<c>!!str</c>, <c>!!int</c>, <c>!!float</c>, <c>!!bool</c>, <c>!!null</c>, <c>!!seq</c>,
/// <c>!!map</c> and the non-specific <c>!</c>) removed, so a tagged value loads exactly as the same value untagged, as it did in 0.4.
/// Any other tag (a custom <c>!name</c>, <c>!!binary</c>, <c>!!timestamp</c>, a verbatim <c>!&lt;...&gt;</c>) is refused by name:
/// lanq's config has no use for it.
/// </summary>
internal sealed class StandardTagParser(IParser inner) : IParser
{
    private static readonly HashSet<string> Standard = new(StringComparer.Ordinal)
    {
        "tag:yaml.org,2002:str", "tag:yaml.org,2002:int", "tag:yaml.org,2002:float", "tag:yaml.org,2002:bool",
        "tag:yaml.org,2002:null", "tag:yaml.org,2002:seq", "tag:yaml.org,2002:map",
    };

    public ParsingEvent? Current { get; private set; }

    public static IParser For(string yaml) => new StandardTagParser(new Parser(new StringReader(yaml)));

    public bool MoveNext()
    {
        if (!inner.MoveNext())
        {
            Current = null;
            return false;
        }

        Current = inner.Current switch
        {
            Scalar s when Strip(s.Tag, s.Start) => new Scalar(s.Anchor, TagName.Empty, Value(s), s.Style, true, true, s.Start, s.End, s.IsKey),
            SequenceStart q when Strip(q.Tag, q.Start) => new SequenceStart(q.Anchor, TagName.Empty, true, q.Style, q.Start, q.End),
            MappingStart m when Strip(m.Tag, m.Start) => new MappingStart(m.Anchor, TagName.Empty, true, m.Style, m.Start, m.End),
            var other => other,
        };
        return true;
    }

    /// <summary>
    /// 0.4 read a <c>!!float</c> value as a double and then converted it to the property's type, so <c>max_jobs: !!float 2.0</c> was 2.
    /// A whole-number float is therefore passed on as that whole number (the same value for a double key); anything else is unchanged.
    /// </summary>
    private static string Value(Scalar s) =>
        s.Tag.Value == "tag:yaml.org,2002:float"
        && double.TryParse(s.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
        && double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue
            ? ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : s.Value;

    /// <summary>True when the node has a standard tag to remove; throws for any other tag.</summary>
    private static bool Strip(TagName tag, Mark at)
    {
        if (tag.IsEmpty)
        {
            return false;
        }

        if (tag.IsNonSpecific || Standard.Contains(tag.Value))
        {
            return true;
        }

        throw new ConfigException($"Invalid config: the YAML tag '{tag.Value}' is not supported (line {at.Line}); remove it.");
    }
}
