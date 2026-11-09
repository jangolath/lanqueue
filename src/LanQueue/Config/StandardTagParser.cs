using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace LanQueue.Config;

/// <summary>
/// Reads YAML with the explicit standard tags (<c>!!str</c>, <c>!!int</c>, <c>!!float</c>, <c>!!bool</c>, <c>!!null</c>, <c>!!seq</c>,
/// <c>!!map</c>) removed, so a tagged value loads as the same value untagged, as it did in 0.4, with two guarantees:
/// <list type="bullet">
/// <item><c>!!str</c> keeps a scalar a string: it is passed on quoted, so <c>!!str ~</c>, <c>!!str null</c> or an empty <c>!!str</c> can
/// never become YAML null (for allowed_hours that would silently mean "any time").</item>
/// <item>A tag must fit its node: <c>!!map</c> only on a mapping, <c>!!seq</c> only on a sequence, a scalar tag only on a scalar holding
/// that kind of value.</item>
/// </list>
/// Any other tag (a custom <c>!name</c>, the non-specific <c>!</c>, <c>!!binary</c>, <c>!!timestamp</c>, a verbatim <c>!&lt;...&gt;</c>),
/// and a tag that contradicts its node, is refused with the tag and line (ConfigException sanitises the quoted value).
/// </summary>
internal sealed partial class StandardTagParser(IParser inner) : IParser
{
    private const string Str = "tag:yaml.org,2002:str";
    private const string Int = "tag:yaml.org,2002:int";
    private const string Float = "tag:yaml.org,2002:float";
    private const string Bool = "tag:yaml.org,2002:bool";
    private const string Null = "tag:yaml.org,2002:null";
    private const string Seq = "tag:yaml.org,2002:seq";
    private const string Map = "tag:yaml.org,2002:map";

    // YAML integers (decimal with optional underscores, 0x hex, 0o octal), the special floats, and the boolean words.
    [GeneratedRegex(@"^[-+]?([0-9][0-9_]*|0x[0-9A-Fa-f_]+|0o[0-7_]+)\z", RegexOptions.None, 1000)]
    private static partial Regex IntRegex();

    [GeneratedRegex(@"^([-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))\z", RegexOptions.None, 1000)]
    private static partial Regex SpecialFloatRegex();

    [GeneratedRegex(@"^(true|false|yes|no|on|off|y|n)\z", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex BoolRegex();

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
            Scalar s when Fits(s.Tag, s.Start, s) => new Scalar(
                s.Anchor, TagName.Empty, Value(s), s.Tag.Value == Str && s.Style == ScalarStyle.Plain ? ScalarStyle.DoubleQuoted : s.Style,
                true, true, s.Start, s.End, s.IsKey),
            SequenceStart q when Fits(q.Tag, q.Start, q) => new SequenceStart(q.Anchor, TagName.Empty, true, q.Style, q.Start, q.End),
            MappingStart m when Fits(m.Tag, m.Start, m) => new MappingStart(m.Anchor, TagName.Empty, true, m.Style, m.Start, m.End),
            var other => other,
        };
        return true;
    }

    /// <summary>
    /// 0.4 read a <c>!!float</c> value as a double and then converted it to the property's type, so <c>max_jobs: !!float 2.0</c> was 2.
    /// A whole-number float is therefore passed on as that whole number (the same value for a double key); anything else is unchanged.
    /// </summary>
    private static string Value(Scalar s) =>
        s.Tag.Value == Float
        && double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
        && double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue
            ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : s.Value;

    private static bool IsNumber(string value) =>
        IntRegex().IsMatch(value) || SpecialFloatRegex().IsMatch(value) || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>True when the node has a standard tag that fits it (and is removed); false for no tag; throws for any other tag.</summary>
    private static bool Fits(TagName tag, Mark at, ParsingEvent node)
    {
        if (tag.IsEmpty)
        {
            return false;
        }

        if (tag.Value is not (Str or Int or Float or Bool or Null or Seq or Map))
        {
            throw new ConfigException($"Invalid config: the YAML tag '{tag.Value}' is not supported (line {at.Line}); remove it.");
        }

        var fits = (tag.Value, node) switch
        {
            (Map, MappingStart) or (Seq, SequenceStart) or (Str, Scalar) => true,
            (Int, Scalar s) => IntRegex().IsMatch(s.Value),
            (Float, Scalar s) => IsNumber(s.Value),
            (Bool, Scalar s) => BoolRegex().IsMatch(s.Value),
            (Null, Scalar s) => s.Style == ScalarStyle.Plain && s.Value is "" or "~" or "null" or "Null" or "NULL", // quoted is a string, never null
            _ => false,
        };
        return fits ? true : throw new ConfigException($"Invalid config: the YAML tag '{tag.Value}' does not fit this value (line {at.Line}); remove it.");
    }
}
