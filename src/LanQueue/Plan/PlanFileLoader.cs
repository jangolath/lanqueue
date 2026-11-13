using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Jobs;
using LanQueue.Output;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace LanQueue.Plan;

/// <summary>
/// Reads a repository's plan file (lanq.yml, version 1) into <see cref="PlanFile"/>. Pure apart from <see cref="Load"/> and
/// <see cref="Locate"/>: nothing is probed, sent or run. The file is untrusted input (it comes with the repository), so it can only say what
/// a lanq run command line can: each job expands to exactly that command line (<see cref="PlanJob.RunArgs"/>) and goes through
/// <see cref="ParsedArgs.Parse"/>; the allowlist, allowed dirs, secret scans and the Windows rule then apply as for any run. There are no keys
/// for ssh, hosts, keys, paths, wake, priority or policy: every key not listed here is refused with its line and a did-you-mean. env: is a
/// job's own variables (--env flags), never a machine's: machines.yml policy.allowed_env decides which names a job may set.
/// </summary>
/// <remarks>
/// Read with YamlDotNet's RepresentationModel (no reflection, as <see cref="ConfigLoader"/>), through <see cref="StandardTagParser"/> (only
/// the standard tags), with no anchors or aliases (<see cref="RefuseAnchors"/>), and <see cref="YamlShape.Check"/> (no empty values or entries, no deep nesting). At most
/// <see cref="MaxBytes"/> and one document; a mapping with <c>version: 1</c> (required), optional <c>defaults:</c> and <c>jobs:</c> (1 to
/// <see cref="MaxJobs"/>). A job's own value REPLACES the default (lists are not merged; size and min_size are one setting), except env:,
/// which is merged by name (the job's value wins).
/// </remarks>
public static partial class PlanFileLoader
{
    /// <summary>The plan file's name at the repository top.</summary>
    public const string FileName = "lanq.yml";

    /// <summary>The only version this lanq reads.</summary>
    public const int Version = 1;

    /// <summary>The largest plan file read (256 KB).</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>The most jobs one plan file may hold.</summary>
    public const int MaxJobs = 32;

    /// <summary>The longest job name.</summary>
    public const int MaxNameLength = 32;

    /// <summary>The top-level keys.</summary>
    internal static readonly string[] TopKeys = ["version", "defaults", "jobs"];

    /// <summary>The keys of defaults:, each with the same meaning as on a job.</summary>
    internal static readonly string[] DefaultKeys = ["dir", "needs", "mem", "size", "min_size", "on", "timeout", "env", "exclusive"];

    /// <summary>The keys of a job.</summary>
    internal static readonly string[] JobKeys =
        ["command", "dir", "needs", "mem", "size", "min_size", "on", "timeout", "shards", "target_minutes", "shard_retries", "fail_fast", "after", "fetch", "env", "exclusive"];

    /// <summary>Keys people may expect from machines.yml; the unknown-key message then says where they belong (env: is a plan key: a job's own variables).</summary>
    private static readonly string[] MachineKeys =
    [
        "ssh", "host", "hosts", "key", "keys", "key_env", "path", "path_prepend", "wake", "priority", "policy", "work_root", "machines", "allowed_commands",
        "allowed_directories", "password", "token", "secret", "secrets", "max_jobs", "transfer", "allowed_hours",
    ];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex JobNameRegex();

    [GeneratedRegex(@"^[0-9]+\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex WholeNumberRegex();

    /// <summary>
    /// The plan file lanq run --plan reads: <paramref name="planFile"/> (relative to <paramref name="cwd"/>) when given, else lanq.yml at the
    /// repository top (the nearest folder up from <paramref name="cwd"/> holding .git, as --fetch finds it; else <paramref name="cwd"/>).
    /// </summary>
    /// <exception cref="PlanFileException">The file is not there (the message says how to write one).</exception>
    public static string Locate(string? planFile, string cwd)
    {
        if (planFile is not null)
        {
            var given = Path.GetFullPath(planFile, Path.GetFullPath(cwd));
            return File.Exists(given)
                ? given
                : throw new PlanFileException($"Plan file not found: {planFile} (looked for {given}). Check the path, or leave out --plan-file to use {FileName} at the repository top.");
        }

        var top = RepositoryTop(cwd) ?? Path.GetFullPath(cwd);
        var path = Path.Combine(top, FileName);
        return File.Exists(path)
            ? path
            : throw new PlanFileException($"No {FileName} in {top.Replace('\\', '/')} (the repository top). Write one (see docs/PLANS.md) or pass --plan-file PATH.");
    }

    /// <summary>The nearest folder up from <paramref name="dir"/> (itself included) that holds .git (a folder or a worktree's file), or null.</summary>
    internal static string? RepositoryTop(string dir)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(dir)); d is not null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
            {
                return d.FullName;
            }
        }

        return null;
    }

    /// <summary>Reads and checks the plan file at <paramref name="path"/> (at most <see cref="MaxBytes"/>, UTF-8).</summary>
    /// <exception cref="PlanFileException">The file is too big, unreadable or not a valid plan.</exception>
    public static PlanFile Load(string path)
    {
        var label = Path.GetFileName(path);
        string text;
        try
        {
            var size = new FileInfo(path).Length;
            if (size > MaxBytes)
            {
                throw new PlanFileException(TooBig(label, size));
            }

            text = File.ReadAllText(path, new UTF8Encoding(false, true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new PlanFileException($"{label}: could not be read: {ex.Message}");
        }

        return Parse(text, path);
    }

    private static string TooBig(string label, long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{label} is {Math.Ceiling(bytes / 1024.0):0} KB; a plan file may be at most {MaxBytes / 1024} KB.");

    /// <summary>Checks the text of a plan file and expands every job (see the class summary).</summary>
    /// <param name="path">The file's path, for <see cref="PlanFile.Path"/>; its name starts every message.</param>
    /// <exception cref="PlanFileException">The first problem, with the file's name, the job and the line where it can.</exception>
    public static PlanFile Parse(string yaml, string path = FileName)
    {
        var label = Path.GetFileName(path) is { Length: > 0 } fileName ? fileName : FileName;
        var bytes = Encoding.UTF8.GetByteCount(yaml);
        if (bytes > MaxBytes)
        {
            throw new PlanFileException(TooBig(label, bytes));
        }

        var root = LoadRoot(yaml, label);
        CheckVersion(root, label);
        YamlShape.Check(root, label + ": ", message => new PlanFileException(message), new HashSet<string>(StringComparer.Ordinal));

        YamlMappingNode? defaults = null;
        YamlMappingNode? jobs = null;
        YamlNode? jobsKey = null;
        foreach (var (k, v) in Keys(root, TopKeys, label, "", "at the top"))
        {
            switch (k.Value)
            {
                case "defaults":
                    defaults = v as YamlMappingNode ?? throw new PlanFileException($"{label}: defaults: must be a mapping of keys such as needs: and timeout: (line {v.Start.Line}).");
                    break;
                case "jobs":
                    jobsKey = k;
                    jobs = v as YamlMappingNode ?? throw new PlanFileException($"{label}: jobs: must be a mapping of job names to jobs (line {v.Start.Line}).");
                    break;
            }
        }

        if (jobs is null || jobs.Children.Count == 0)
        {
            throw new PlanFileException($"{label}: jobs: is required and must hold at least one job{(jobsKey is null ? "" : $" (line {jobsKey.Start.Line})")}.");
        }

        if (jobs.Children.Count > MaxJobs)
        {
            throw new PlanFileException($"{label}: jobs: holds {jobs.Children.Count} jobs; a plan file may have at most {MaxJobs} (line {jobsKey!.Start.Line}).");
        }

        var defaultValues = defaults is null ? [] : Keys(defaults, DefaultKeys, label, "defaults: ", "in defaults:").ToDictionary(x => x.Key.Value!, x => x.Value, StringComparer.Ordinal);

        // Names first: after: may name a job further down the file.
        var named = new List<(string Name, int Line, YamlMappingNode Body)>();
        foreach (var (k, v) in jobs.Children)
        {
            var name = (k as YamlScalarNode)?.Value ?? throw new PlanFileException($"{label}: a job name must be a plain word (line {k.Start.Line}).");
            if (name == "<<")
            {
                throw new PlanFileException($"{label}: merge keys (<<) are not supported (line {k.Start.Line}); write each job's keys out, or put shared values under defaults:.");
            }

            if (!JobNameRegex().IsMatch(name))
            {
                throw new PlanFileException(
                    $"{label}: job name '{name}' (line {k.Start.Line}) must start with a letter or digit and hold only letters, digits, _ and - (at most {MaxNameLength} characters).");
            }

            if (named.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } twin)
            {
                throw new PlanFileException($"{label}: job '{name}' (line {k.Start.Line}) has the same name as job '{twin.Name}' (line {twin.Line}); job names must differ ignoring case.");
            }

            named.Add((name, (int)k.Start.Line, v as YamlMappingNode ?? throw new PlanFileException(
                $"{label}: job '{name}' (line {k.Start.Line}) must be a mapping of keys, at least command: (for example command: dotnet build).")));
        }

        var expanded = new List<PlanJob>();
        foreach (var (index, (name, line, body)) in named.Index())
        {
            expanded.Add(Expand(label, name, line, index, body, defaultValues, named.Select(n => n.Name).ToList()));
        }

        if (PlanGraph.FindCycle([.. expanded.Select(j => (j.Name, j.After))]) is { } cycle)
        {
            var first = expanded.First(j => j.Name == cycle[0]);
            throw new PlanFileException($"{label}: job '{first.Name}' (line {first.Line}): after: makes a cycle: {string.Join(" -> ", cycle)}; no job of a cycle could ever start.");
        }

        return new PlanFile(path, Version, expanded, Warnings(expanded));
    }

    /// <summary>
    /// lanq.yml takes no anchors and no aliases (&amp;name, *name), checked on the parser's events before any node tree is built or walked:
    /// an alias is a shared node, and a few lines of aliases to aliases (the "billion laughs") make a tree that every later walk would visit
    /// exponentially many times. defaults: is how a plan shares values. A syntax error is left to the load that follows, which reports it.
    /// </summary>
    private static void RefuseAnchors(string yaml, string label)
    {
        var parser = new Parser(new StringReader(yaml));
        try
        {
            while (parser.MoveNext())
            {
                if (parser.Current is AnchorAlias or NodeEvent { Anchor.IsEmpty: false })
                {
                    throw new PlanFileException(
                        $"{label}: line {parser.Current.Start.Line}: anchors and aliases (&name, *name) are not allowed; use defaults: to share values.");
                }
            }
        }
        catch (YamlException)
        {
            // Not valid YAML: the real load reports it, with its line.
        }
    }

    /// <summary>The one document's root, which must be a mapping; YAML errors (and tags lanq does not take) are reported with the file's name.</summary>
    private static YamlMappingNode LoadRoot(string yaml, string label)
    {
        RefuseAnchors(yaml, label);
        var stream = new YamlStream();
        try
        {
            stream.Load(StandardTagParser.For(yaml));
        }
        catch (ConfigException ex)
        {
            // A tag StandardTagParser refuses: the same words, with this file's name in front.
            throw new PlanFileException(label + ": " + ex.Message.Replace("Invalid config: ", "", StringComparison.Ordinal));
        }
        catch (YamlException ex)
        {
            var why = (ex.InnerException ?? ex).Message;
            throw new PlanFileException($"{label}: not valid YAML (line {ex.Start.Line}): {why}"
                + (why.Contains("Duplicate key", StringComparison.OrdinalIgnoreCase) ? " (a key may appear only once in a mapping; job names must differ)" : ""));
        }
        catch (ArgumentException ex)
        {
            throw new PlanFileException($"{label}: not valid YAML: {ex.Message} (a key may appear only once in a mapping)");
        }

        if (stream.Documents.Count > 1)
        {
            throw new PlanFileException($"{label}: holds {stream.Documents.Count} YAML documents (--- lines); a plan file is one document.");
        }

        if (stream.Documents.Count == 0 || YamlShape.IsNull(stream.Documents[0].RootNode))
        {
            throw new PlanFileException($"{label}: is empty; it needs version: {Version} and jobs: (see docs/PLANS.md).");
        }

        return stream.Documents[0].RootNode as YamlMappingNode
            ?? throw new PlanFileException($"{label}: must be a mapping with version: and jobs: keys (line {stream.Documents[0].RootNode.Start.Line}).");
    }

    /// <summary>version: is required and must be 1; a later version is told apart (update lanq) before any other key is judged.</summary>
    private static void CheckVersion(YamlMappingNode root, string label)
    {
        var entry = root.Children.FirstOrDefault(c => c.Key is YamlScalarNode { Value: "version" });
        if (entry.Key is null)
        {
            throw new PlanFileException($"{label}: version: is required; this lanq reads version: {Version} (put it on the first line).");
        }

        if (entry.Value is YamlScalarNode { Value: { } text } scalar && !YamlShape.IsNull(scalar) && WholeNumberRegex().IsMatch(text))
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v == Version)
            {
                return;
            }

            throw new PlanFileException($"{label}: this lanq reads {FileName} version {Version}; the file says {text}: update lanq (line {entry.Value.Start.Line}).");
        }

        throw new PlanFileException($"{label}: version must be the number {Version} (line {entry.Value.Start.Line}).");
    }

    /// <summary>The entries of a mapping whose keys must all be in <paramref name="allowed"/>: plain words, no merge key, each known (else a did-you-mean).</summary>
    private static List<KeyValuePair<YamlScalarNode, YamlNode>> Keys(YamlMappingNode map, string[] allowed, string label, string where, string place)
    {
        var entries = new List<KeyValuePair<YamlScalarNode, YamlNode>>();
        foreach (var (k, v) in map.Children)
        {
            if (k is not YamlScalarNode { Value: { } key } scalar)
            {
                throw new PlanFileException($"{label}: {where}a key must be a plain word (line {k.Start.Line}).");
            }

            if (key == "<<")
            {
                throw new PlanFileException($"{label}: {where}merge keys (<<) are not supported (line {k.Start.Line}); write the keys out, or put shared values under defaults:.");
            }

            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                throw new PlanFileException($"{label}: {where}unknown key '{key}' {place} (line {k.Start.Line}){Hint(key, allowed)}");
            }

            entries.Add(new(scalar, v));
        }

        return entries;
    }

    /// <summary>"; did you mean 'command'?" (edit distance at most 2, ignoring case), where machine settings belong, and the keys allowed.</summary>
    private static string Hint(string key, string[] allowed)
    {
        var lower = key.ToLowerInvariant();
        var close = allowed.Select(a => (Key: a, Distance: Distance(lower, a))).Where(x => x.Distance <= 2).OrderBy(x => x.Distance).ThenBy(x => Array.IndexOf(allowed, x.Key)).FirstOrDefault();
        var hint = MachineKeys.Contains(lower, StringComparer.Ordinal)
            ? "; machines, ssh, keys, paths and other machine settings belong in your machines.yml, never in a repository's plan file."
            : close.Key is not null ? $"; did you mean '{close.Key}'?" : ".";

        return hint + " Keys here: " + string.Join(", ", allowed) + ".";
    }

    /// <summary>Levenshtein distance (small strings only: keys).</summary>
    internal static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    /// <summary>One job: its keys checked, defaults applied, expanded to its lanq run arguments and parsed by the CLI's own parser.</summary>
    private static PlanJob Expand(
        string label, string name, int line, int index, YamlMappingNode body, Dictionary<string, YamlNode> defaults, List<string> names)
    {
        var where = $"job '{name}' (line {line}): ";
        var own = Keys(body, JobKeys, label, where, "in a job").ToDictionary(x => x.Key.Value!, x => x.Value, StringComparer.Ordinal);
        PlanFileException Error(string message) => new($"{label}: {where}{message}");

        // A job's own value replaces the default; size and min_size are one setting, so either one on the job drops both defaults. env: is
        // the exception (see Env below): the job's variables are merged with the defaults' by name, so it is read from both, not from here.
        var values = new Dictionary<string, YamlNode>(defaults, StringComparer.Ordinal);
        if (own.ContainsKey("size") || own.ContainsKey("min_size"))
        {
            values.Remove("size");
            values.Remove("min_size");
        }

        foreach (var (k, v) in own)
        {
            values[k] = v;
        }

        if (!values.TryGetValue("command", out var commandNode))
        {
            throw Error("command: is required (for example command: dotnet build, or a list such as command: [dotnet, test, tests/Unit]).");
        }

        var command = Command(commandNode, Error);
        var after = values.TryGetValue("after", out var afterNode) ? Words(afterNode, "after", Error) : [];
        foreach (var dep in after)
        {
            var target = names.FirstOrDefault(n => string.Equals(n, dep, StringComparison.OrdinalIgnoreCase))
                ?? throw Error($"after: names '{dep}', which is not a job in {label} (jobs: {string.Join(", ", names)}).");
            if (string.Equals(target, name, StringComparison.Ordinal))
            {
                throw Error("after: names the job itself; a job cannot wait for itself.");
            }
        }

        List<string> args = ["run"];
        void Option(string key, string option, Func<YamlNode, string> read)
        {
            if (values.TryGetValue(key, out var node))
            {
                args.AddRange([option, read(node)]);
            }
        }

        string Scalar(YamlNode node, string key) => node is YamlScalarNode { Value: { } v }
            ? v
            : throw Error($"{key}: must be a single value (line {node.Start.Line}).");
        string List(YamlNode node, string key) => string.Join(',', Words(node, key, Error));

        Option("needs", "--needs", n => List(n, "needs"));
        if (values.TryGetValue("dir", out var dirNode) && Scalar(dirNode, "dir") is var dir && dir != ".")
        {
            args.AddRange(["--dir", dir]);
        }

        Option("mem", "--mem", n => Scalar(n, "mem"));
        Option("size", "--size", n => List(n, "size"));
        Option("min_size", "--min-size", n => Scalar(n, "min_size"));
        Option("on", "--on", n => List(n, "on"));
        Option("shards", "--shards", n => Scalar(n, "shards"));
        Option("target_minutes", "--target-minutes", n => Scalar(n, "target_minutes"));
        Option("shard_retries", "--shard-retries", n => Scalar(n, "shard_retries"));
        if (values.TryGetValue("fail_fast", out var failFast) && Flag(failFast, "fail_fast", Error))
        {
            args.Add("--fail-fast");
        }

        if (values.TryGetValue("exclusive", out var exclusive) && Flag(exclusive, "exclusive", Error))
        {
            args.Add("--exclusive");
        }

        // --timeout's own parser throws a FormatException for a word; say what the CLI says for a number out of range instead.
        Option("timeout", "--timeout", n => Scalar(n, "timeout") is var t && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? t
            : throw Error("--timeout must be a whole number of minutes from 1 to 10080 (one week)."));
        if (values.TryGetValue("fetch", out var fetchNode))
        {
            foreach (var pattern in Patterns(fetchNode, Error))
            {
                args.AddRange(["--fetch", pattern]);
            }
        }

        // env: merged by name, the job's value winning (an exception to "a job's value replaces the default": a job that sets one variable
        // keeps the others defaults: sets). Each becomes one --env NAME=VALUE, judged by --env's own rules.
        foreach (var (envName, envValue) in Env(defaults.GetValueOrDefault("env"), own.GetValueOrDefault("env"), Error))
        {
            args.AddRange(["--env", envName + "=" + envValue]);
        }

        args.Add("--");
        args.AddRange(command);

        ParsedArgs parsed;
        try
        {
            parsed = ParsedArgs.Parse([.. args]);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            throw Error(ex.Message);
        }

        return new PlanJob(name, line, index, [.. after.Select(d => names.First(n => string.Equals(n, d, StringComparison.OrdinalIgnoreCase)))], args, parsed);
    }

    /// <summary>
    /// command: a list is the exact argv (no shell); a string is split on spaces, and only when every token is plain (as lanq writes bare on
    /// a command line) apart from the literal {shard} and {shards}.
    /// </summary>
    private static List<string> Command(YamlNode node, Func<string, PlanFileException> error)
    {
        switch (node)
        {
            case YamlSequenceNode list:
                var argv = list.Children.Select(item => item is YamlScalarNode { Value: { } v }
                    ? v
                    : throw error($"command: each entry must be a single value (line {item.Start.Line}).")).ToList();
                return argv.Count > 0 ? argv : throw error("command: is empty; give the program and its arguments.");
            case YamlScalarNode { Value: { } text }:
                var tokens = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
                if (tokens.Count == 0)
                {
                    throw error("command: is empty; give the program and its arguments.");
                }

                if (tokens.FirstOrDefault(t => t.Replace(Jobs.ShardInfo.CountToken, "", StringComparison.Ordinal).Replace(Jobs.ShardInfo.IndexToken, "", StringComparison.Ordinal) is var rest
                    && rest.Length > 0 && !CommandLine.IsBare(rest)) is { } odd)
                {
                    throw error($"write command as a list (quotes, $ and other shell characters are not interpreted): '{odd}' is not a plain word; for example command: [{string.Join(", ", tokens.Take(3))}, ...] (line {node.Start.Line}).");
                }

                return tokens;
            default:
                throw error($"command: must be a list (command: [dotnet, test]) or a plain string (line {node.Start.Line}).");
        }
    }

    /// <summary>needs, size, on, after: a list of words or one string of comma-separated words.</summary>
    private static List<string> Words(YamlNode node, string key, Func<string, PlanFileException> error)
    {
        var raw = node switch
        {
            YamlScalarNode { Value: { } v } => [v],
            YamlSequenceNode list => list.Children.Select(item => item is YamlScalarNode { Value: { } s }
                ? s
                : throw error($"{key}: each entry must be a single value (line {item.Start.Line}).")).ToList(),
            _ => throw error($"{key}: must be a list or a comma-separated string (line {node.Start.Line})."),
        };
        var words = raw.SelectMany(w => w.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        return words.Count > 0 ? words : throw error($"{key}: names nothing (line {node.Start.Line}).");
    }

    /// <summary>fetch: one pattern or a list of patterns (each checked by the CLI's own --fetch rules).</summary>
    private static List<string> Patterns(YamlNode node, Func<string, PlanFileException> error) => node switch
    {
        YamlScalarNode { Value: { } v } => [v],
        YamlSequenceNode list => list.Children.Select(item => item is YamlScalarNode { Value: { } s }
            ? s
            : throw error($"fetch: each entry must be a single pattern (line {item.Start.Line}).")).ToList(),
        _ => throw error($"fetch: must be a pattern or a list of patterns (line {node.Start.Line})."),
    };

    /// <summary>
    /// env: of defaults: and of the job, merged by name ignoring case (the job's value wins and its spelling of the name is kept), defaults'
    /// names first in file order, then the job's new ones. Each value is a single value, taken as written (3 is "3", true is "true").
    /// </summary>
    private static List<(string Name, string Value)> Env(YamlNode? defaults, YamlNode? own, Func<string, PlanFileException> error)
    {
        var merged = new List<(string Name, string Value)>();
        foreach (var node in new[] { defaults, own })
        {
            if (node is null)
            {
                continue;
            }

            if (node is not YamlMappingNode map)
            {
                throw error($"env: must be a mapping of NAME: value, for example env: {{ CONFIGURATION: Release }} (line {node.Start.Line}).");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in map.Children)
            {
                // A name that is not one is never quoted back: it may be a value written in the wrong place.
                if (k is not YamlScalarNode { Value: { } name } || !Policy.JobEnv.IsValidName(name))
                {
                    throw error($"env: a variable name must be letters, digits and _ and not start with a digit (line {k.Start.Line}).");
                }

                if (v is not YamlScalarNode { Value: { } value })
                {
                    throw error($"env: {name} must be a single value (line {v.Start.Line}).");
                }

                if (!seen.Add(name))
                {
                    throw error($"env: {name} is listed twice (names are compared ignoring case, as on Windows) (line {k.Start.Line}).");
                }

                var at = merged.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                {
                    merged[at] = (name, value);
                }
                else
                {
                    merged.Add((name, value));
                }
            }
        }

        return merged;
    }

    /// <summary>fail_fast: true or false.</summary>
    private static bool Flag(YamlNode node, string key, Func<string, PlanFileException> error) => node is YamlScalarNode { Value: { } v } && bool.TryParse(v, out var b)
        ? b
        : throw error($"{key}: must be true or false (line {node.Start.Line}).");

    /// <summary>
    /// What a dry run warns about (stderr on a real run): two jobs with the same command in the same folder, and a sharded job whose command
    /// has neither {shard} nor {shards} (each shard would run the same command unless a script reads LANQ_SHARD).
    /// </summary>
    private static List<string> Warnings(List<PlanJob> jobs)
    {
        var warnings = new List<string>();
        var seen = new Dictionary<string, PlanJob>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            var key = JobHistory.Key(job.Args.Argv) + "\n" + JobHistory.Dir(job.Args.Directory);
            if (seen.TryGetValue(key, out var first))
            {
                warnings.Add($"jobs '{first.Name}' and '{job.Name}' run the same command in the same folder ({JobHistory.Key(job.Args.Argv)} in {JobHistory.Dir(job.Args.Directory)})");
            }
            else
            {
                seen[key] = job;
            }

            if (job.Args.Shards is not null
                && !job.Args.Argv.Any(a => a.Contains(Jobs.ShardInfo.IndexToken, StringComparison.Ordinal) || a.Contains(Jobs.ShardInfo.CountToken, StringComparison.Ordinal)))
            {
                warnings.Add($"job '{job.Name}' has shards: but its command has no {{shard}} or {{shards}}: each shard runs the same command unless a script reads LANQ_SHARD");
            }
        }

        return warnings;
    }
}
