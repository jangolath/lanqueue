using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace LanQueue.Config;

/// <summary>
/// The shape checks every YAML file lanq reads goes through (machines.yml in <see cref="ConfigLoader"/>, lanq.yml in Plan/PlanFileLoader.cs),
/// on the loaded node tree (YamlDotNet's RepresentationModel, no reflection): a key may not be left without a value (except the few that
/// mean "not set"), a list may not have an empty entry, an anchor may not hold its own alias (a cycle that would recurse for ever), and
/// nothing may be nested deeper than 32 levels. Each problem names the key and its line.
/// </summary>
internal static class YamlShape
{
    /// <summary>The deepest nesting accepted; lanq's files are never more than a few levels deep.</summary>
    public const int MaxDepth = 32;

    /// <summary>A plain (unquoted) empty value, ~ or null: YAML null. A quoted "" or "~" is a string.</summary>
    public static bool IsNull(YamlNode node) =>
        node is YamlScalarNode { Style: ScalarStyle.Plain, Value: "" or "~" or "null" or "Null" or "NULL" or null };

    /// <summary>A key's path as shown in messages: "machines.env" for "machines[].env".</summary>
    public static string Shown(string path) => path.Replace("[]", "", StringComparison.Ordinal);

    /// <summary>Walks the tree below <paramref name="root"/> and throws <paramref name="error"/>(message) at the first problem.</summary>
    /// <param name="prefix">The start of every message ("Invalid config: ", "lanq.yml: ").</param>
    /// <param name="nullable">Paths (as "machines[].key_env") whose value may be YAML null.</param>
    /// <param name="special">
    /// Called for every mapping value before the generic checks, with the parent's path, the value's path and the value; true means the
    /// caller has checked that value itself (it is then neither null-checked nor walked).
    /// </param>
    public static void Check(
        YamlNode root, string prefix, Func<string, Exception> error, IReadOnlySet<string> nullable, Func<string, string, YamlNode, bool>? special = null)
    {
        Walk(root, "", new HashSet<YamlNode>(ReferenceEqualityComparer.Instance));

        void Enter(YamlNode node, string path, HashSet<YamlNode> ancestors)
        {
            if (ancestors.Contains(node))
            {
                throw error($"{prefix}'{Shown(path)}' refers to itself through a YAML alias (line {node.Start.Line}).");
            }

            if (ancestors.Count >= MaxDepth)
            {
                throw error($"{prefix}'{Shown(path)}' is nested too deeply (line {node.Start.Line}).");
            }
        }

        void Walk(YamlNode node, string path, HashSet<YamlNode> ancestors)
        {
            if (node is not (YamlMappingNode or YamlSequenceNode))
            {
                return;
            }

            ancestors.Add(node);
            switch (node)
            {
                case YamlMappingNode map:
                    foreach (var (k, v) in map.Children)
                    {
                        var child = (path.Length == 0 ? "" : path + ".") + ((k as YamlScalarNode)?.Value ?? "");
                        Enter(v, child, ancestors);
                        if (special?.Invoke(path, child, v) == true)
                        {
                            continue;
                        }

                        if (IsNull(v) && !nullable.Contains(child))
                        {
                            throw error($"{prefix}'{Shown(child)}' has no value (line {v.Start.Line}). Give it a value or leave the key out.");
                        }

                        Walk(v, child, ancestors);
                    }

                    break;
                case YamlSequenceNode list:
                    foreach (var item in list.Children)
                    {
                        Enter(item, path, ancestors);
                        if (IsNull(item))
                        {
                            throw error($"{prefix}'{Shown(path)}' has an empty entry (line {item.Start.Line}).");
                        }

                        Walk(item, path + "[]", ancestors);
                    }

                    break;
            }

            ancestors.Remove(node);
        }
    }
}
