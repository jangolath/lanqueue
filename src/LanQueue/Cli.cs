using System.Globalization;
using System.Reflection;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Fetch;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Payload;
using LanQueue.Results;
using LanQueue.Scheduling;
using LanQueue.Ssh;

namespace LanQueue;

public sealed record ParsedArgs(string Command, string? ConfigPath, IReadOnlyList<string> Needs, string Directory, int WaitSeconds, IReadOnlyList<string> Argv)
{
    public const int DefaultTimeoutMinutes = 240;

    /// <summary>The most lines --log-tail may ask for.</summary>
    public const int MaxLogTail = 200;

    /// <summary>How long the job itself may run (lanq run --timeout, in minutes).</summary>
    public int TimeoutMinutes { get; init; } = DefaultTimeoutMinutes;

    /// <summary>lanq run --mem: minimum available memory in GB, or null.</summary>
    public double? MemGb { get; init; }

    /// <summary>--json: machine-readable output on stdout.</summary>
    public bool Json { get; init; }

    /// <summary>The one positional argument some commands take (a machine name).</summary>
    public string? Target { get; init; }

    /// <summary>lanq unlock --all: every slot file, not only the expired ones.</summary>
    public bool All { get; init; }

    /// <summary>--yes: confirms a destructive option such as unlock --all.</summary>
    public bool Yes { get; init; }

    /// <summary>--force: makeready scans a machine even when it is paused or outside its allowed hours.</summary>
    public bool Force { get; init; }

    /// <summary>makeready --ssh user@host: a PC that is not in the config yet.</summary>
    public string? SshTarget { get; init; }

    /// <summary>makeready --os windows|linux, with --ssh.</summary>
    public MachineOs? Os { get; init; }

    /// <summary>makeready --key-env VAR, with --ssh: the NAME of an environment variable holding the private key file's path (as key_env).</summary>
    public string? KeyEnv { get; init; }

    /// <summary>makeready --ssh ... --setup: print the worker's one-time setup and connect to nothing (see Cli.MakereadySetup.cs).</summary>
    public bool Setup { get; init; }

    /// <summary>makeready --ssh ... --add: add the worker to the local machines.yml when it is ready (see Cli.MakereadyAdd.cs).</summary>
    public bool Add { get; init; }

    /// <summary>makeready --add --name NAME: the new machine's name.</summary>
    public string? Name { get; init; }

    /// <summary>makeready --tune (0.12.0): after the check, also run the read-only lanq-makeready-tune scan and report it (lanqTune).</summary>
    public bool Tune { get; init; }

    /// <summary>lanq doctor --remote: also check each worker over ssh.</summary>
    public bool Remote { get; init; }

    /// <summary>lanq run --fetch PATTERN (repeatable, validated): files to bring back from the job's working folder.</summary>
    public IReadOnlyList<string> Fetch { get; init; } = [];

    /// <summary>lanq run --fetch-dir: where fetched files go (out/jobid/...); null means .lanq/artifacts in the repository.</summary>
    public string? FetchDir { get; init; }

    /// <summary>lanq run --fetch-max: total bytes cap, or null for the default.</summary>
    public long? FetchMaxBytes { get; init; }

    /// <summary>lanq run --fetch-max-files: file count cap, or null for the default.</summary>
    public int? FetchMaxFiles { get; init; }

    /// <summary>lanq wake --wait: after sending the packet, wait until the machine answers ssh (up to its wake.wait_seconds).</summary>
    public bool WaitForSsh { get; init; }

    /// <summary>lanq health --wake: wake machines that look asleep (inside their hours) and wait for them.</summary>
    public bool WakeSleepers { get; init; }

    /// <summary>lanq run --no-wake: never wake a machine for this job.</summary>
    public bool NoWake { get; init; }

    /// <summary>lanq run --log-tail N (1 to 200, --json only): the last N lines of the job's log in the JSON object; null without it.</summary>
    public int? LogTail { get; init; }

    /// <summary>lanq run --dry-run: say what the run would do now (see Cli.DryRun.cs), and send, claim, log and wake nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>lanq run --shards N|auto, --shard-retries R, --fail-fast, --target-minutes M (see Cli.Shards.cs); null without --shards.</summary>
    public ShardOptions? Shards { get; init; }

    /// <summary>--target-minutes M (1 to 1440): lanq run --shards auto picks N for it; lanq stats adds suggestedShards. Null without it.</summary>
    public int? TargetMinutes { get; init; }

    /// <summary>
    /// --on m[,m] (run and health): only these machines (names as typed; matched to the config ignoring case once it is loaded, an unknown
    /// name refused before any network). Null without --on.
    /// </summary>
    public IReadOnlyList<string>? On { get; init; }

    /// <summary>--size C[,C] or --min-size C (run and health): the machine size classes a job may run on. Null without either.</summary>
    public SizeRule? Size { get; init; }

    /// <summary>
    /// lanq run --results FORMAT:PATTERN (lanq.yml results:, 0.11.0): the test results files to count after the job. Its pattern is also in
    /// <see cref="Fetch"/> (added unless a --fetch names it). Null without it.
    /// </summary>
    public ResultsSpec? Results { get; init; }

    /// <summary>lanq run --plan (or --plan-file PATH, which implies it): run the jobs of the repository's lanq.yml.</summary>
    public bool Plan { get; init; }

    /// <summary>lanq run --plan-file PATH: the plan file to read (relative to the current folder) instead of lanq.yml at the repository top.</summary>
    public string? PlanFile { get; init; }

    /// <summary>lanq run --plan --only a,b: only these jobs of the plan file (as typed; checked against the file). Null without --only.</summary>
    public IReadOnlyList<string>? Only { get; init; }

    /// <summary>lanq run --fail-fast: with --shards the first shard that does not complete cancels the rest (also in <see cref="Shards"/>); with --plan the first job.</summary>
    public bool FailFast { get; init; }

    /// <summary>
    /// lanq run --plan --plan-timeout M (lanq.yml plan_timeout: M; the command line wins): the whole plan's limit in minutes, 1 to 10080, from
    /// after the snapshot to the last baseline (0.10.0). Null without it.
    /// </summary>
    public int? PlanTimeoutMinutes { get; init; }

    /// <summary>
    /// lanq run --env NAME=VALUE (repeatable): the job's own variables, in the order given (<see cref="Policy.JobEnv.Parse"/>: split at the first
    /// '=', a valid name not on the hard deny table, a non-empty value of at most 4,096 characters without control characters, no name twice,
    /// at most 16,384 characters in all). policy.allowed_env and the secret scan are checked when the job is (JobRunner's precheck); a machine
    /// whose env: sets the same name is not used for it. Shards inherit them.
    /// </summary>
    public IReadOnlyList<Policy.JobEnvVar> Env { get; init; } = [];

    /// <summary>lanq run --exclusive (lanq.yml exclusive: true): the job takes every slot of its machine (no other lanq job runs there meanwhile).</summary>
    public bool Exclusive { get; init; }

    /// <summary>
    /// lanq run --baseline (lanq.yml baseline: true), or --baseline-ref REF, which implies it: when the job (a shard) ends Failed, the same
    /// command runs again at the base commit and the verdict (new, preexisting, inconclusive, skipped) is reported; status and exit stay the job's.
    /// </summary>
    public bool Baseline { get; init; }

    /// <summary>lanq run --baseline-ref REF (lanq.yml baseline_ref:): the branch, tag or commit to compare with; null means the default branch.</summary>
    public string? BaselineRef { get; init; }

    /// <summary>
    /// The run options that come from lanq.yml with --plan, and so are refused with it. Not among them (0.10.0): --on, --size and --min-size,
    /// which narrow every job (see <see cref="Plan.PlanNarrowing"/>), and --fetch-dir, --fetch-max and --fetch-max-files, the developer's own
    /// choice for every fetching job (lanq.yml may only lower the limits and never names the folder; see <see cref="Plan.PlanFetch"/>).
    /// </summary>
    internal static readonly string[] PlanFileOptions =
    [
        "--needs", "--dir", "--mem", "--timeout", "--shards", "--shard-retries", "--target-minutes", "--fetch", "--env", "--exclusive", "--baseline",
        "--baseline-ref", "--results",
    ];

    /// <summary>What lanq run --plan takes (the refusal of a run-only option lists them).</summary>
    internal const string PlanTakes =
        "lanq run --plan takes --plan-file, --only, --on, --size, --min-size, --plan-timeout, --fetch-dir, --fetch-max, --fetch-max-files, --wait, --no-wake, --json, --log-tail, --dry-run, --fail-fast and --config";

    /// <summary>
    /// The command line exactly as given (without the program name). A suggested next step that re-runs the command edits a copy of it
    /// (see <see cref="Output.SuggestedCommand.Rerun"/>), so the user's own options and job arguments survive unchanged.
    /// </summary>
    public IReadOnlyList<string> Raw { get; init; } = [];

    /// <summary>The options each command accepts (--config is accepted everywhere); anything else is an error, never silently ignored.</summary>
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["health"] = ["--json", "--wake", "--needs", "--mem", "--on", "--size", "--min-size"],
        ["run"] =
        [
            "--needs", "--dir", "--wait", "--timeout", "--mem", "--json", "--no-wake", "--fetch", "--fetch-dir", "--fetch-max", "--fetch-max-files", "--shards",
            "--shard-retries", "--fail-fast", "--target-minutes", "--log-tail", "--dry-run", "--on", "--size", "--min-size", "--plan", "--plan-file", "--only",
            "--env", "--exclusive", "--baseline", "--baseline-ref", "--plan-timeout", "--results",
        ],
        ["stats"] = ["--json", "--target-minutes"],
        ["unlock"] = ["--all", "--yes"],
        ["doctor"] = ["--remote", "--force", "--json"],
        ["makeready"] = ["--ssh", "--os", "--json", "--force", "--all", "--key-env", "--setup", "--add", "--name", "--yes", "--tune"],
        ["init"] = ["--json"],
        ["wake"] = ["--wait", "--json"],
        ["--version"] = [],
        ["version"] = [],
    };

    /// <summary>Commands that take one positional machine name.</summary>
    private static readonly HashSet<string> TakesTarget = new(StringComparer.Ordinal) { "unlock", "makeready", "wake" };

    /// <summary>Everything after a bare "--" is the job command and is never interpreted as lanq options.</summary>
    public static ParsedArgs Parse(string[] args)
    {
        var split = Array.IndexOf(args, "--");
        var own = split >= 0 ? args[..split] : args;
        var argv = split >= 0 ? args[(split + 1)..] : [];
        string? config = null;
        var needs = new List<string>();
        var dir = ".";
        var wait = 0;
        var timeout = DefaultTimeoutMinutes;
        double? mem = null;
        var json = false;
        var all = false;
        var yes = false;
        var remote = false;
        var waitForSsh = false;
        var wakeSleepers = false;
        var noWake = false;
        var exclusive = false;
        var baseline = false;
        string? baselineRef = null;
        var dryRun = false;
        var force = false;
        string? sshTarget = null;
        MachineOs? os = null;
        string? keyEnv = null;
        var setup = false;
        var add = false;
        var tune = false;
        string? name = null;
        string? target = null;
        var fetch = new List<string>();
        ResultsSpec? results = null;
        string? fetchDir = null;
        long? fetchMax = null;
        int? fetchMaxFiles = null;
        int? logTail = null;
        int? targetMinutes = null;
        int? planTimeout = null;
        List<string>? on = null;
        SizeRule? size = null;
        var plan = false;
        string? planFile = null;
        List<string>? only = null;
        var env = new List<string>();
        var seen = new List<string>();
        var shards = new Cli.ShardArgs();
        var command = own.Length > 0 ? own[0] : "help";
        for (var i = 1; i < own.Length; i++)
        {
            if (own[i].StartsWith("--", StringComparison.Ordinal))
            {
                seen.Add(own[i]);
            }

            switch (own[i])
            {
                case "--config" when i + 1 < own.Length:
                    config = own[++i];
                    break;
                case "--needs" when i + 1 < own.Length:
                    needs.AddRange(own[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--dir" when i + 1 < own.Length:
                    dir = own[++i];
                    break;
                case "--wait" when command == "wake":
                    waitForSsh = true;
                    break;
                case "--wait" when i + 1 < own.Length:
                    wait = int.Parse(own[++i], CultureInfo.InvariantCulture);
                    break;
                case "--timeout" when i + 1 < own.Length:
                    timeout = int.Parse(own[++i], CultureInfo.InvariantCulture);
                    if (timeout is < 1 or > 10080)
                    {
                        throw new ArgumentException("--timeout must be a whole number of minutes from 1 to 10080 (one week).");
                    }

                    break;
                case "--mem" when i + 1 < own.Length:
                    if (!double.TryParse(own[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) || !double.IsFinite(gb) || gb is <= 0 or > 4096)
                    {
                        throw new ArgumentException("--mem must be the available memory a machine needs, in GB, above 0 and at most 4096 (for example --mem 4 or --mem 2.5).");
                    }

                    mem = gb;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--fetch" when i + 1 < own.Length:
                    fetch.Add(own[++i]);
                    break;
                case "--results" when i + 1 < own.Length:
                    results = results is null ? ResultsSpec.Parse(own[++i]) : throw new ArgumentException("--results can be given once.");
                    break;
                case "--env" when i + 1 < own.Length:
                    env.Add(own[++i]);
                    break;
                case "--fetch-dir" when i + 1 < own.Length:
                    fetchDir = own[++i];
                    if (fetchDir.Length == 0 || fetchDir.Any(char.IsControl))
                    {
                        throw new ArgumentException("--fetch-dir must be a folder path.");
                    }

                    break;
                case "--fetch-max" when i + 1 < own.Length:
                    fetchMax = ParseSize(own[++i]);
                    break;
                case "--fetch-max-files" when i + 1 < own.Length:
                    if (!int.TryParse(own[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var files) || files is < 1 or > FetchLimits.CeilingFiles)
                    {
                        throw new ArgumentException($"--fetch-max-files must be a whole number from 1 to {FetchLimits.CeilingFiles}.");
                    }

                    fetchMaxFiles = files;
                    break;
                case "--log-tail" when i + 1 < own.Length:
                    if (!int.TryParse(own[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var tail) || tail is < 1 or > MaxLogTail)
                    {
                        throw new ArgumentException($"--log-tail must be a whole number of lines from 1 to {MaxLogTail}.");
                    }

                    logTail = tail;
                    break;
                case "--shards" or "--shard-retries" when i + 1 < own.Length:
                    shards.Set(own[i], own[++i]);
                    break;
                case "--fail-fast":
                    shards.FailFast = true;
                    break;
                case "--target-minutes" when i + 1 < own.Length:
                    targetMinutes = Cli.ParseTargetMinutes(own[++i]);
                    if (command == "run")
                    {
                        shards.Target(targetMinutes.Value);
                    }

                    break;
                case "--on" when i + 1 < own.Length:
                    on ??= [];
                    on.AddRange(Names(own[++i], "--on must name at least one machine from the config (for example --on big-01 or --on big-01,build-01)."));
                    break;
                case "--size" when i + 1 < own.Length:
                    size = SizeRule.AnyOfText(own[++i]);
                    break;
                case "--min-size" when i + 1 < own.Length:
                    size = SizeRule.AtLeastText(own[++i]);
                    break;
                case "--plan-timeout" when i + 1 < own.Length:
                    planTimeout = ParsePlanTimeout(own[++i]);
                    break;
                case "--plan":
                    plan = true;
                    break;
                case "--plan-file" when i + 1 < own.Length:
                    planFile = own[++i];
                    if (planFile.Length == 0 || planFile.Any(char.IsControl))
                    {
                        throw new ArgumentException("--plan-file must be a file path.");
                    }

                    plan = true;
                    break;
                case "--only" when i + 1 < own.Length:
                    only ??= [];
                    only.AddRange(Names(own[++i], "--only must name at least one job of lanq.yml (for example --only build,unit)."));
                    break;
                case "--all":
                    all = true;
                    break;
                case "--yes":
                    yes = true;
                    break;
                case "--wake":
                    wakeSleepers = true;
                    break;
                case "--no-wake":
                    noWake = true;
                    break;
                case "--exclusive":
                    exclusive = true;
                    break;

                // The base is optional, but this parser takes an option's value from the next argument, so "--baseline REF" could not tell a
                // ref from the job's next option or a typo. Two forms instead: --baseline alone uses the default branch, and --baseline-ref REF
                // names the base (and implies --baseline).
                case "--baseline":
                    baseline = true;
                    break;
                case "--baseline-ref" when i + 1 < own.Length:
                    baselineRef = own[++i];
                    if (Payload.BaseRef.CheckRefText(baselineRef) is { } badRef)
                    {
                        throw new ArgumentException(badRef);
                    }

                    baseline = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--remote":
                    remote = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--ssh" when i + 1 < own.Length:
                    sshTarget = own[++i];
                    break;
                case "--key-env" when i + 1 < own.Length:
                    keyEnv = own[++i];
                    if (!ConfigLoader.IsValidEnvName(keyEnv))
                    {
                        throw new ArgumentException("--key-env must be the NAME of an environment variable holding your key file's path (letters, digits and _), for example --key-env LANQ_KEY.");
                    }

                    break;
                case "--setup":
                    setup = true;
                    break;
                case "--add":
                    add = true;
                    break;
                case "--tune":
                    tune = true;
                    break;
                case "--name" when i + 1 < own.Length:
                    name = own[++i];
                    break;
                case "--os" when i + 1 < own.Length:
                    os = own[++i] switch
                    {
                        "windows" => MachineOs.Windows,
                        "linux" => MachineOs.Linux,
                        _ => throw new ArgumentException("--os must be windows or linux."),
                    };
                    break;
                default:
                    if (TakesTarget.Contains(command) && target is null && !own[i].StartsWith('-'))
                    {
                        target = own[i];
                        break;
                    }

                    throw new ArgumentException($"Unknown or incomplete option '{own[i]}'.");
            }
        }

        if (Allowed.TryGetValue(command, out var allowed) && seen.FirstOrDefault(o => o != "--config" && !allowed.Contains(o)) is { } bad)
        {
            throw new ArgumentException($"lanq {command} does not take {bad}.");
        }

        if (command == "health" && seen.Contains("--needs") && needs.Count == 0)
        {
            throw new ArgumentException("--needs must name at least one capability (for example --needs dotnet,linux).");
        }

        if (seen.Contains("--size") && seen.Contains("--min-size"))
        {
            throw new ArgumentException("--size and --min-size do not go together; --size light,medium or --min-size medium.");
        }

        if (command == "run")
        {
            CheckPlanOptions(seen, plan, split >= 0);
        }

        // After the option checks, so lanq health --env says "does not take --env" rather than judging the value.
        var jobEnv = Policy.JobEnv.ParseAll(env);

        if (logTail is not null && !json)
        {
            throw new ArgumentException("--log-tail needs --json (it adds logTail to the JSON object); without --json lanq prints the log's path before the status line.");
        }

        // With --plan these apply to the plan's fetching jobs (fetch: in lanq.yml); the plan refuses them when none of its jobs fetches.
        if (!plan && fetch.Count == 0 && results is null && (fetchDir is not null || fetchMax is not null || fetchMaxFiles is not null))
        {
            throw new ArgumentException("--fetch-dir, --fetch-max and --fetch-max-files need at least one --fetch pattern or --results.");
        }

        if (command == "makeready")
        {
            CheckMakereadyOptions(seen, json, setup, add);
        }

        if (split >= 0 && command != "run")
        {
            throw new ArgumentException($"lanq {command} does not take a command after --; only lanq run does.");
        }

        return new ParsedArgs(command, config, needs, dir, wait, argv)
        {
            TimeoutMinutes = timeout,
            MemGb = mem,
            Json = json,
            Target = target,
            All = all,
            Yes = yes,
            Remote = remote,
            WaitForSsh = waitForSsh,
            WakeSleepers = wakeSleepers,
            NoWake = noWake,
            Exclusive = exclusive,
            Baseline = baseline,
            BaselineRef = baselineRef,
            DryRun = dryRun,
            Force = force,
            SshTarget = sshTarget,
            Os = os,
            KeyEnv = keyEnv,
            Setup = setup,
            Add = add,
            Tune = tune,
            Name = name,
            Fetch = FetchPatterns.Validate(fetch, results?.Path),
            Results = results,
            FetchDir = fetchDir,
            FetchMaxBytes = fetchMax,
            FetchMaxFiles = fetchMaxFiles,
            Shards = plan ? null : shards.Build(),
            TargetMinutes = targetMinutes,
            On = on,
            Size = size,
            Plan = plan,
            PlanFile = planFile,
            Only = only,
            FailFast = shards.FailFast,
            PlanTimeoutMinutes = planTimeout,
            LogTail = logTail,
            Env = jobEnv,
            Raw = [.. args],
        };
    }

    /// <summary>
    /// The makeready options that only make sense together: --key-env only with --ssh; --setup only prints text (no --json, --add, --all,
    /// --force or --tune); --name and --yes only with --add; --tune not with --add. Whether --ssh, --os and --name are all there is checked by the command itself, so that
    /// lanq makeready --add --json still answers with its own object.
    /// </summary>
    private static void CheckMakereadyOptions(List<string> seen, bool json, bool setup, bool add)
    {
        if (seen.Contains("--key-env") && !seen.Contains("--ssh"))
        {
            throw new ArgumentException("--key-env goes with --ssh user@host (a configured machine uses its key_env from machines.yml).");
        }

        if (setup && (json || add || seen.Contains("--all") || seen.Contains("--force") || seen.Contains("--tune")))
        {
            throw new ArgumentException("--setup only prints the one-time setup for people; it takes no --json, --add, --all, --force or --tune.");
        }

        if (add && seen.Contains("--tune"))
        {
            throw new ArgumentException("--tune does not go with --add: add the PC first, then run lanq makeready NAME --tune.");
        }

        if (!add && (seen.Contains("--name") || seen.Contains("--yes")))
        {
            throw new ArgumentException("--name and --yes go with --add (lanq makeready --ssh user@host --os windows|linux --add --name NAME).");
        }
    }

    /// <summary>A comma list of names (--on, --only): trimmed, empty entries dropped; at least one, without control characters.</summary>
    private static string[] Names(string text, string error)
    {
        var names = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length > 0 && !names.Any(n => n.Any(char.IsControl)) ? names : throw new ArgumentException(error);
    }

    /// <summary>
    /// lanq run --plan takes its jobs from lanq.yml: no command after --, and none of the options a job sets there; --only and --plan-file
    /// only with --plan.
    /// </summary>
    private static void CheckPlanOptions(List<string> seen, bool plan, bool hasCommand)
    {
        if (!plan)
        {
            if (seen.Contains("--only"))
            {
                throw new ArgumentException("--only goes with --plan (it picks jobs of lanq.yml): lanq run --plan --only build,unit");
            }

            if (seen.Contains("--plan-timeout"))
            {
                throw new ArgumentException("--plan-timeout goes with --plan (it limits the whole plan); a single run's limit is --timeout: lanq run --plan --plan-timeout 60");
            }

            return;
        }

        if (hasCommand)
        {
            throw new ArgumentException("with --plan the jobs and their commands come from lanq.yml; leave out the command after --.");
        }

        if (seen.FirstOrDefault(o => PlanFileOptions.Contains(o)) is { } option)
        {
            throw new ArgumentException($"with --plan each job's {option} comes from lanq.yml; leave {option} out ({PlanTakes}).");
        }
    }

    /// <summary>--plan-timeout (and lanq.yml plan_timeout:): whole minutes from 1 to 10080 (one week), as --timeout.</summary>
    internal static int ParsePlanTimeout(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is >= 1 and <= 10080
            ? minutes
            : throw new ArgumentException("--plan-timeout must be a whole number of minutes from 1 to 10080 (one week).");

    /// <summary>--fetch-max: a size in bytes with an optional K, M or G suffix (powers of 1024, an optional trailing B), from 1 KB to 2 GB.</summary>
    internal static long ParseSize(string text)
    {
        var t = text.Trim().ToUpperInvariant();
        if (t.EndsWith('B'))
        {
            t = t[..^1];
        }

        long unit = 1;
        if (t.Length > 0 && t[^1] is 'K' or 'M' or 'G')
        {
            unit = t[^1] switch { 'K' => 1024L, 'M' => 1024L * 1024, _ => 1024L * 1024 * 1024 };
            t = t[..^1];
        }

        return t.Length is > 0 and <= 12 && long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n <= FetchLimits.CeilingBytes / unit && n * unit >= 1024
            ? n * unit
            : throw new ArgumentException("--fetch-max must be a size from 1K to 2G (for example 500M or 1G).");
    }
}

public static partial class Cli
{
    private const string Usage = """
        lanq - headless job runner for your own LAN machines

          lanq health [--json] [--wake] [--config FILE]
          lanq health --needs dotnet,linux [--mem 4] [--json] [--wake] [--config FILE]
          lanq run --needs dotnet,linux [--dir src] [--wait 120] [--timeout 240] [--mem 4] [--no-wake] [--json] [--config FILE] -- dotnet test
          lanq run ... [--fetch 'TestResults/**/*.trx' ...] [--fetch-dir DIR] [--fetch-max 200M] [--fetch-max-files 500] -- dotnet test
          lanq run ... --results trx:TestResults/*.trx | junit:PATTERN -- dotnet test --logger trx
          lanq run ... --shards N|auto [--target-minutes M] [--shard-retries R] [--fail-fast] -- npx jest --shard={shard}/{shards}
          lanq run ... --json [--log-tail 60] -- dotnet test
          lanq run ... --dry-run [--json] -- dotnet test
          lanq run ... [--on big-01,build-01] [--size light,medium | --min-size heavy] -- dotnet test
          lanq run ... [--env NAME=VALUE ...] -- dotnet test
          lanq run ... --exclusive [--shards N|auto] -- dotnet test
          lanq run ... --baseline | --baseline-ref REF [--shards N|auto] -- dotnet test
          lanq health [--on big-01] [--size light,medium | --min-size heavy] [--needs dotnet] [--json]
          lanq run --plan [--plan-file PATH] [--only a,b] [--fail-fast] [--wait 120] [--no-wake] [--json [--log-tail 60]] [--dry-run]
          lanq run --plan ... [--on big-01,build-01] [--size light,medium | --min-size heavy] [--plan-timeout M]
          lanq run --plan ... [--fetch-dir DIR] [--fetch-max 1G] [--fetch-max-files 2000]
          lanq stats [--json] [--target-minutes M]
          lanq unlock <machine> [--all --yes] [--config FILE]
          lanq doctor [--remote [--force]] [--json] [--config FILE]
          lanq makeready <machine> [--json] [--force] [--tune] [--config FILE]
          lanq makeready --ssh user@host --os windows|linux [--key-env VAR] [--json] [--tune]
          lanq makeready --ssh user@host --os windows|linux --setup [--key-env VAR]
          lanq makeready --ssh user@host --os windows|linux --add --name NAME [--key-env VAR] [--yes] [--config FILE] [--json]
          lanq makeready --all [--json] [--force] [--tune] [--config FILE]
          lanq init [--config FILE] [--json]
          lanq wake <machine> [--wait] [--json] [--config FILE]
          lanq --version

        Config lookup: --config, then $LANQ_CONFIG, then the per-user config folder (never the current directory); lanq init writes there too.
        State (job log, logs) lives in $LANQ_HOME or a per-user folder, never inside your repository.
        --wait is in seconds (how long to wait for a free machine); --timeout is in minutes (how long the job may run, default 240).
        --mem is in GB: only machines reporting at least that much available memory take the job.
        --min-size light|medium|heavy (that class or larger) or --size C[,C] (one of these classes) routes by machine size: a machine's size:
        in machines.yml when it has one, else its probed class (under 6 GB light; 16 GB or more with 4 cores heavy; else medium). A machine of
        another size, or one that does not report its memory, can never take the job (wrong_size, size_unknown). --on m[,m] uses only the
        named machines (an unknown name is refused before any network); every other rule still applies (capabilities, size, --mem, holds,
        allowed hours, slots, wake). On health, --size / --min-size judge each machine like --needs and --on shows only those machines.
        --env NAME=VALUE (repeatable; lanq.yml env:) sets a variable for the job only (shards too): the name must be in machines.yml
        policy.allowed_env (exact, or a prefix ending in *) and not on lanq's deny list (loader, runtime and shell startup variables such as
        NODE_OPTIONS, DOTNET_STARTUP_HOOKS, GIT_*, GOFLAGS, HOME, TEMP, LANQ_*), the value non-empty, at most 4,096 characters (16,384 in all),
        free of control characters and secrets; refused with exit 2 otherwise. A machine whose env: sets the same name, or a Windows machine
        whose command the values make too long, is not used for the job (env_conflict, windows_command_too_long). Settings only: values are
        visible to other users of the worker (its process list) and are not secret-safe; never pass secrets. The JSON env field and jobs.jsonl
        list names only; suggested commands in nextSteps repeat your command line, values included.
        --exclusive (lanq.yml exclusive: true) takes every slot of the machine, so no other lanq job runs there while it does. It waits, like a
        busy machine, until every slot is free (busy, retryable; no reservation, so a machine that is never idle never takes it: use --on with
        an idle machine). With --shards each shard takes a whole machine: auto counts the idle machines, and a fixed N above the machines
        that can take the job is refused (exit 2). --mem and every other rule still apply.
        --baseline (lanq.yml baseline: true) answers "is this failure new?": when the job (a shard, a plan job) ends Failed, the same request
        runs again at the base commit, a clean tree: the merge-base of HEAD and the default branch (origin/HEAD, else origin/main, origin/master,
        main, master), or with --baseline-ref REF (lanq.yml baseline_ref:) the merge-base of HEAD and REF. The verdict is new (the base
        completed), preexisting (it failed there too), inconclusive (anything else) or skipped (the snapshot is the base): one "[lanq]
        baseline:" line, a baseline object in --json and the fix_code reason. It never changes the status or exit code, never runs after
        TimedOut, WindowClosed, InfraError, NoCapacity, Rejected or Cancelled, and a REF that is not a commit is refused (exit 2) before anything is sent.
        --fetch (repeatable) brings files back from the job's working folder after the job, whether it passed or failed, into
        DIR/<job id>/ (default .lanq/artifacts in the repository): patterns relative to --dir with *, ? and **; regular files only,
        at most 500 files and 200 MB unless --fetch-max-files / --fetch-max (up to 2G). Fetch problems are reported, never change the exit code.
        --results trx:PATTERN or junit:PATTERN (lanq.yml results: { format:, path: }) fetches the results files too (the pattern joins --fetch)
        and counts their tests: "[lanq] results:" with up to 10 failed names, and --json results (counts, at most 50 failedTests, warnings).
        When the job completed but no test was executed, lanq warns "no tests ran" and adds an ask_developer step; information only, the status
        and exit code never change. Test names are data the worker wrote: never run them.
        unlock removes slot files past their recorded deadline (left by a crashed lanq); --all --yes removes every slot file on that machine.
        doctor checks this machine's setup without any network (ssh, git, config, known_hosts, keys, state folder, job logs); --remote also checks
        each worker over ssh (work_root, git or tar, and each capability's tool in the job environment); a paused machine or one outside its
        allowed hours is left alone (HOLD) unless --force. Exit 0 when no line is WARN or FAIL, 1 otherwise. --json: the lines go to stderr and
        stdout gets one object (status Ok, Warnings, Failing, Rejected, InfraError or Cancelled, exitCode, message, remote, counts, checks: scope,
        machine, name, level, detail, fix, capability; nextSteps); 2 bad arguments, 70 unexpected error, 130 Ctrl+C, each with that object.
        stats summarises this computer's job log (nothing leaves it): the totals and one line per machine, then per machine the median and p90
        of completed jobs, the last run and its speed factor (from 5 jobs), the newest 20 commands (runs, jobs: each shard is one job, passed,
        failed, median, p90, and timing: the work and per-shard overhead --target-minutes would use) and the size of logs/. --target-minutes M
        adds the shards M needs on a typical machine (no probe, before any free-slot cap). Unreadable lines are skipped with a warning. --json
        prints one object (status Ok, Rejected, InfraError or Cancelled, exitCode, message, files, jobs, wallMinutes, skippedLines, oldestUtc,
        newestUtc, logs, machines, commands, planJobs, nextSteps; times in UTC; at most 500 commands). Exit 0 (no job log is 0 jobs), 2 bad
        arguments, 70 the job log could not be read, 130 Ctrl+C. Plan jobs (a "Plan jobs" table; planJobs, at most 500, then planJobsMore) are
        grouped by plan file (its full path) and job name, one run per plan run (completed when all its shards' last attempts completed, failed
        when any failed, else other; wall time from the first start to the last end); clones in different folders are separate rows.
        health probes every machine and prints one line each (DOWN and HOLD machines are reported, not an error). Exit 0 every machine was
        probed, 2 bad arguments or config, 70 unexpected error (--json), 130 Ctrl+C. With --needs and/or --mem each machine is also judged for
        such a job by the rules lanq run uses ("-> eligible (2 slots)" or "-> not eligible: why"; --json rows add eligible, why, reason, permanent
        and jobSlots); exit 0 when at least one machine could take it now, 75 when none could.
        makeready runs the read-only lanq-makeready check on a worker (sent on ssh stdin, nothing is written to its disk) and prints the verdicts,
        the script's VERDICT line and numbered next steps (0.1 reports: the WARN/FAIL checks with advice), what the PC is suited for, the
        capability diff and a suggested machines.yml entry. Exit 0 ready, 1 not ready, 2 bad arguments, 70 ssh failed or the report was
        unusable, 75 the machine is paused or outside its allowed hours (--force scans anyway), 130 Ctrl+C. lanq never changes a worker.
        makeready --ssh user@host scans a PC that is not in the config yet, with the keys in ssh-agent or, with --key-env VAR, the key file
        whose path is in $VAR (the NAME of the variable, as key_env). --setup prints that PC's full one-time setup and connects to nothing: the
        build user (from user@host), your public key in its authorized_keys (Windows: icacls by SID; Linux: chmod 700/600), the work folder,
        firewall scoping, password logins off, the host key fingerprint to compare and the first connection; only .pub files are read (the
        first key ssh-agent lists; with --key-env the .pub beside that key; else ~/.ssh/lanq_ed25519.pub or ~/.ssh/id_ed25519.pub). Text only,
        exit 0. --add --name NAME scans the PC and, only when it is READY, adds it to your machines.yml (found as for every command) as text
        after its last machine, every other line kept byte for byte; it shows the lines and asks first (without a terminal, and with --json,
        --yes instead), never writes through a link, then probes the new machine once (read-only, never a wake). Exit 0 added, 1 NOT READY or
        declined (nothing written), 2 bad arguments, config, a duplicate name or host, or a file lanq does not edit by itself (paste the entry
        by hand), 70 ssh failed, the report was unusable or the file could not be written; --json prints one object (status Added, NotAdded,
        Rejected, InfraError or Cancelled, exitCode, path, machine, entry, afterLine, health, report, message, nextSteps).
        makeready --all checks every machine in the config (four at a time, same rules) and prints one table (machine, OS, verdict OK / WARN n /
        FAIL n / unreachable / held / error, top problem; FAIL only when the machine is NOT READY); exit 70 any machine unreachable or its
        report unusable, else 1 any NOT READY, else 75 any held, else 0 (warnings alone exit 0); 2 bad arguments or config; 130 Ctrl+C.
        A configured machine's allowed_hours window (the same HH:mm-HH:mm in every entry) goes to the check as -Hours.
        makeready --tune also runs the read-only lanq-makeready-tune scan after a report (the same way, on ssh stdin; up to 150 s): what slows
        the PC down as a build worker (power plan, PC-maker updaters, Defender exclusions and scan times, inotify, journald, ...), OK / INFO /
        WARN / SKIP, never FAIL. The text adds a tune section (WARN and INFO items with advice and fix ids; --all a TUNE column); --json adds
        lanqTune to the report (status Ok, TimedOut, Unreachable or Unusable; report; nextSteps). It never changes the exit code, and is not
        taken with --setup or --add. Fixes are for the PC's owner at that PC (lanq-makeready-fix -Tune); lanq never sends or runs the fix script.
        init writes a commented starter machines.yml (default the per-user config file, or --config FILE) and never overwrites a file or writes through
        a link; exit 0 written, 2 the path exists (a file, folder or link) or bad arguments, 70 it could not be written.
        wake sends a Wake-on-LAN magic packet to a machine that has a wake: setting, even outside its allowed hours (you asked for it); --wait then waits
        up to its wake.wait_seconds for ssh. lanq run wakes one such machine by itself (once) only when no awake machine can take the job, it is
        inside its allowed hours with min_window_minutes + wake.wait_seconds left and --wait is above 0 (--no-wake turns that off); lanq health --wake does the same for every sleeper and waits. Exit 0 sent (and, with --wait, answering), 1 no answer within the wait, 2 bad arguments or no wake: setting, 70 could not send.
        Every job's output is also kept in a log in the state folder: run prints "[lanq] log: PATH" just before its status line (--shards: for
        each shard that did not complete); run --json always carries logPath (null when no job started), and --log-tail N (1 to 200, --json
        only) adds logTail, the log's last N lines (each shard its own with --shards).
        run --dry-run says what lanq run would do now and does none of it: the same command-line checks, snapshot and secret scan, one probe
        of every machine and the same choice (machine, --shards N or auto and placement, every excluded machine and why, the sleeper it would
        wake); it claims no slot, sends, logs, fetches and wakes nothing (the snapshot's git objects are written as for a run). Every run option
        is accepted. Exit 0 would start now, 75 no machine could take it now, 2 refused, 70 the snapshot failed, 130 Ctrl+C; --json prints one
        object with status WouldRun (or the status run would end with), machine, ranked, machines, placement, shardCountWhy, shardTiming,
        wouldWake and retryable.
        --json: health prints one JSON array; run streams the job's output to stderr and prints one JSON object on stdout at the end;
        makeready prints the validated report (--all: one object with status, exitCode and machines); init prints one object (status,
        exitCode, path, message); wake prints one object (status, exitCode, machine, sent, awake, durationSeconds, message).
        Next steps: every --json object (each health row, each makeready --all row; not the single makeready report, which has them only
        inside lanqTune with --tune) carries nextSteps, at least one {action, reason, argv, command, safeToRunUnattended}; action is proceed, retry, wait, fix_code, fix_command, ask_developer,
        run_doctor, run_health or none (more may be added: treat an unknown one as ask_developer). argv (the exact arguments) is there when a
        command is suggested; command is the same line for bash and PowerShell, left out when an argument cannot be quoted safely for both.
        A suggestion is only lanq run, health, doctor or makeready (never unlock, init, wake, --wake, --force or --yes), keeps --config, and
        never does more than the command it follows (--no-wake when that could not wake a machine; --wait 120 at most once). For people the
        same steps are "Next: ..." lines on stderr ("[lanq] Next: ..." in run), never for none, always before the final status line.
        --shards runs N copies of the command at once (1 to 64; auto = the free slots that can take it now), {shard} and {shards} substituted
        in its arguments and LANQ_SHARD / LANQ_SHARDS set; see docs/SHARDING.md. Exit 0 only when every shard passed; 130 Ctrl+C (every
        started shard is cleaned up). --target-minutes M (1 to 1440, with --shards auto only) picks N from earlier completed runs of the same
        command and --dir (the newest 10 of the last 90 days) so each shard takes about M minutes on the slowest machine it would go to,
        never more than the free slots; with no such run it is plain auto. The --shards auto line says why (--json adds shardTiming).
        run --plan runs the jobs of lanq.yml at the repository top (or --plan-file PATH; see docs/PLANS.md): each job is the lanq run line it
        expands to (the same checks; the dry run prints it), every job's command line is checked before anything is sent (one refusal refuses
        the plan, exit 2; a job no machine can ever take ends it with 75), the repository is snapshotted once and jobs run side by side unless
        after: orders them (a job whose after: job did not complete is Skipped). --only a,b runs only those jobs; --fail-fast cancels the rest
        after the first job that does not complete; --wait counts only while no job of the plan holds a slot. Lines are prefixed [job machine];
        a summary table, then the status line. Exit 0 only when every job completed, else the first failed job's own code, then 2, 124, 70, 75,
        130. --json prints one object (status, exitCode, plan, durationSeconds, message, order, jobs, warnings, nextSteps).
        run --plan --on / --size / --min-size narrow every job, never widen one: a job's own on: is intersected with --on (--on alone when it
        has none), its size classes with the command line's; the dry run prints each job's narrowed line. An unknown --on name is refused (2)
        before any network; a job left with no machine ends the plan before the snapshot (75, retryable: false; the suggested rerun's --only
        lists the jobs that can still run, never one after a narrowed-out job, replaces any --only given and is not safe unattended).
        run --plan --plan-timeout M (lanq.yml plan_timeout: M; the command line wins; 1 to 10080 minutes, plan only) limits the whole plan, from
        after the snapshot to the last baseline: when it is reached the running jobs are stopped and cleaned up and end TimedOut, jobs waiting
        for a slot end TimedOut, jobs not started are Skipped, unfinished baselines are inconclusive, and the plan is TimedOut (124) unless a
        Failed or Rejected job decides it; --json adds planTimeoutMinutes and planTimedOut. A job's own --timeout still applies.
        run --plan --fetch-dir DIR, --fetch-max SIZE and --fetch-max-files N apply to every job with fetch: (refused when no selected job
        fetches) and replace lanq.yml's values, up to 2G and 5000 files. lanq.yml fetch_max and fetch_max_files may only lower the limits (at
        most 200M and 500 files; larger is refused, exit 2) and there is no fetch_dir key: a repository never picks where files land on your
        disk. No plan-wide total: at worst the fetching jobs times the per-job limit (32 jobs at the defaults: 6.4 GB).
        Exit codes (run): job's own code; 2 rejected by policy or bad arguments; 70 infrastructure error; 75 no eligible machine (at once, without waiting or waking, when no machine can ever take the job as configured; --json then says retryable: false); 124 job timed out or its machine's allowed hours ended; 130 Ctrl+C (the job is cleaned up where it had started).
        """;

    /// <summary>
    /// lanq --version: this build's version (with its commit) and the embedded lanq-makeready scripts (scanner and tune), read back from the binary, so a
    /// packaging mistake (a missing resource in a trimmed build) shows up here and not on a worker.
    /// </summary>
    private static string VersionText()
    {
        var version = typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return $"lanq {version}\n"
            + $"lanq-makeready {MakereadyVersion} (embedded: lanq-makeready.ps1 {MakereadyScript(MachineOs.Windows).Length} bytes, "
            + $"lanq-makeready.sh {MakereadyScript(MachineOs.Linux).Length} bytes)\n"
            + $"lanq-makeready-tune {MakereadyVersion} (embedded: lanq-makeready-tune.ps1 {TuneScript(MachineOs.Windows).Length} bytes, "
            + $"lanq-makeready-tune.sh {TuneScript(MachineOs.Linux).Length} bytes)";
    }

    public static Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct) => RunAsync(args, stdout, stderr, ct, null);

    /// <param name="loadConfig">Stands in for reading the config file (tests); null reads it as usual.</param>
    internal static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct, Func<FleetConfig>? loadConfig)
    {
        ParsedArgs p;
        try
        {
            p = ParsedArgs.Parse(args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            // The message can quote what was typed: sanitised. With --json (run or health) stdout still gets its one object.
            var message = JobRunner.SafeLine(ex.Message);
            var steps = NextSteps.ArgumentsError(message);
            await stderr.WriteLineAsync(message).ConfigureAwait(false);
            await NextSteps.WriteAsync(stderr, steps).ConfigureAwait(false);
            var own = Array.IndexOf(args, "--") is var dash and >= 0 ? args[..dash] : args;
            if (own.Length > 0 && own[0] is "run" or "health" or "makeready" or "wake" or "init" or "doctor" or "stats" && own.Contains("--json"))
            {
                // --fetch promises fetched and fetchSkipped on every run path, a refused command line included (only lanq's own words count).
                await (own[0] == "run" && (own.Contains("--plan") || own.Contains("--plan-file"))
                    ? WritePlanFileJsonError(own.Contains("--dry-run"), "Rejected", 2, message, stdout, steps, logTail: own.Contains("--log-tail"))
                    : own[0] == "run" && own.Contains("--dry-run")
                    ? WritePlanJsonError("Rejected", 2, message, stdout, logTail: own.Contains("--log-tail"), steps)
                    : own[0] == "run" && own.Contains("--shards")
                    ? WriteShardsJsonError("Rejected", 2, message, stdout, steps)
                    : WriteJsonError(own[0], "Rejected", 2, message, stdout, steps, fetch: own.Contains("--fetch") || own.Contains("--results"), logTail: own.Contains("--log-tail") ? 1 : null)).ConfigureAwait(false);
            }

            return 2;
        }

        if (p.Command is "--version" or "version")
        {
            await stdout.WriteLineAsync(VersionText()).ConfigureAwait(false);
            return 0;
        }

        if (p.Command == "init")
        {
            return await Init(p, Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), stdout, stderr).ConfigureAwait(false);
        }

        var home = ResolveHome(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());
        var log = new JobLog(home, line => stderr.WriteLine(line));
        if (p.Command == "stats")
        {
            return await Stats(p, log, StatsKeepDays(p, loadConfig), stdout, stderr, ct).ConfigureAwait(false);
        }

        if (p.Command is not ("health" or "run" or "unlock" or "doctor" or "makeready" or "wake"))
        {
            // Help that was asked for goes to stdout; usage after a mistake goes to stderr, so stdout stays clean for scripts.
            var asked = p.Command is "help" or "--help" or "-h";
            await (asked ? stdout : stderr).WriteLineAsync(Usage).ConfigureAwait(false);
            return asked ? 0 : 2;
        }

        FleetConfig LoadConfig() => loadConfig?.Invoke() ?? ConfigLoader.Load(ResolveConfig(p.ConfigPath, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), File.Exists));
        if (p.Command == "doctor")
        {
            var doctorSsh = new ProcessSshClient(Environment.GetEnvironmentVariable);
            return await Doctor(p, LoadConfig, DoctorHost.Real(home), doctorSsh, new HealthProbe(doctorSsh, TimeProvider.System), stdout, stderr, ct).ConfigureAwait(false);
        }

        if (p.Command == "makeready")
        {
            // No waker: makeready (and the one probe of --add) never wakes a machine. --setup connects to nothing at all.
            var client = new ProcessSshClient(Environment.GetEnvironmentVariable);
            return await Makeready(p, LoadConfig, client, new HealthProbe(client, TimeProvider.System), stdout, stderr, ct, MakereadyHost.Real(stderr)).ConfigureAwait(false);
        }

        FleetConfig config;
        try
        {
            config = LoadConfig();
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            var message = JobRunner.SafeLine(ex.Message);
            var steps = NextSteps.ConfigError(p.ConfigPath, message, p.Json);
            await stderr.WriteLineAsync(message).ConfigureAwait(false);
            await NextSteps.WriteAsync(stderr, steps).ConfigureAwait(false);
            if (p.Json)
            {
                await (p.Plan
                    ? WritePlanFileJsonError(p.DryRun, "Rejected", 2, message, stdout, steps, logTail: p.LogTail is not null)
                    : p.DryRun
                    ? WritePlanJsonError("Rejected", 2, message, stdout, p.LogTail is not null, steps)
                    : p.Shards is not null
                    ? WriteShardsJsonError("Rejected", 2, message, stdout, steps)
                    : WriteJsonError(p.Command, "Rejected", 2, message, stdout, steps, fetch: p.Fetch.Count > 0, logTail: p.LogTail)).ConfigureAwait(false);
            }

            return 2;
        }

        var ssh = new ProcessSshClient(Environment.GetEnvironmentVariable);
        if (p.Command == "unlock")
        {
            return await Unlock(p, config, ssh, stdout, stderr, ct).ConfigureAwait(false);
        }

        var waker = new WakeService(new UdpMagicPacketSender(), TimeProvider.System);
        if (p.Command == "wake")
        {
            return await Wake(p, config, waker, new HealthProbe(ssh, TimeProvider.System), stdout, stderr, ct).ConfigureAwait(false);
        }

        var probe = new HealthProbe(ssh, TimeProvider.System, waker);
        if (p.Command == "health")
        {
            // --needs / --mem / --size / --min-size: judge each machine for such a job exactly as lanq run does (Windows-safe arguments
            // assumed). --on only narrows the rows.
            var needs = p.Needs.Count > 0 || p.MemGb is not null || p.Size is not null ? new JobNeeds(p.Needs, p.MemGb, WindowsUnsafe: false) { Size = p.Size } : null;
            return await Health(config, probe, stdout, stderr, ct, p.Json, p.WakeSleepers, needs, p.ConfigPath, p.On).ConfigureAwait(false);
        }

        return await Run(p, config, ssh, new GitPayloadBuilder(), probe, log, home, Directory.GetCurrentDirectory(), stdout, stderr, ct).ConfigureAwait(false);
    }

    /// <summary>lanq run. With --json the job's output streams to stderr and stdout carries only the final JSON object.</summary>
    internal static async Task<int> Run(
        ParsedArgs p, FleetConfig config, ISshClient ssh, IPayloadBuilder payloads, HealthProbe probe, JobLog log, string home, string repoDir,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (p.Plan)
        {
            // lanq.yml (see Cli.PlanFile.cs); its dry run too.
            return await RunPlanFile(p, config, ssh, payloads, probe, log, home, repoDir, stdout, stderr, ct).ConfigureAwait(false);
        }

        if (p.DryRun)
        {
            // Before anything below: FetchRoot creates the .lanq folder, and the runner here would use bases.json.
            return await DryRun(p, config, ssh, payloads, log, home, repoDir, stdout, stderr, ct).ConfigureAwait(false);
        }

        var jobOutput = p.Json ? stderr : stdout;
        FetchRequest? fetch = null;
        if (p.Fetch.Count > 0)
        {
            var total = p.FetchMaxBytes ?? FetchLimits.DefaultMaxTotalBytes;
            var (root, anchor) = FetchRoot(p.FetchDir, repoDir);
            fetch = new FetchRequest(p.Fetch, root, new FetchLimits(p.FetchMaxFiles ?? FetchLimits.DefaultMaxFiles, total, total), anchor);
        }

        var runner = new JobRunner(config, ssh, payloads, probe, log, Path.Combine(home, "tmp"), line => jobOutput.WriteLine(line), bases: new BaseStore(Path.Combine(home, "bases.json")));
        if (p.Shards is { } shards)
        {
            return await RunShards(p, shards, runner, repoDir, fetch, stdout, stderr, ct, () => PruneLogs(log, config, stderr)).ConfigureAwait(false);
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        JobResult result;
        try
        {
            result = await runner.RunAsync(
                RequestOf(p, repoDir, fetch),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (p.Json && ct.IsCancellationRequested)
        {
            // Ctrl+C: an agent reading --json still gets its one object, with the log of a job that had already started.
            await stderr.WriteLineAsync("[lanq] Cancelled").ConfigureAwait(false);
            await WriteJsonError(
                "run", "Cancelled", 130, "cancelled (Ctrl+C); the job was cleaned up where it had started", stdout, NextSteps.Nothing("cancelled (Ctrl+C)"), started.Elapsed,
                fetch is not null, p.LogTail, runner.StartedLogPath).ConfigureAwait(false);
            return 130;
        }
        catch (Exception ex) when (p.Json && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            var message = JobRunner.SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}");
            var steps = NextSteps.ForRun(p, new NextSteps.RunOutcome("InfraError", 70, message, null, InfraKind.Unexpected, false, null, null, null, runner.StartedLogPath));
            await NextSteps.WriteAsync(stderr, steps, "[lanq] ").ConfigureAwait(false);
            await stderr.WriteLineAsync("[lanq] InfraError: " + message).ConfigureAwait(false);
            await WriteJsonError("run", "InfraError", 70, message, stdout, steps, started.Elapsed, fetch is not null, p.LogTail, runner.StartedLogPath).ConfigureAwait(false);
            return 70;
        }

        PruneLogs(log, config, stderr);

        // What to do next, then where the whole log is (--json carries it as logPath): always before the status line, which stays last.
        var next = NextSteps.ForRun(p, NextSteps.Outcome(result));
        if (NoTestsRan(result.Status.ToString(), result.Results) is { } noTests)
        {
            next = WithNoTestsSteps(next, [(null, noTests)]);
            await stderr.WriteLineAsync(NoTestsLine(null, noTests)).ConfigureAwait(false);
        }

        await WriteBaselineLines(stderr, result.Baseline is { } b ? [("", b)] : []).ConfigureAwait(false);
        await NextSteps.WriteAsync(stderr, next, "[lanq] ").ConfigureAwait(false);
        if (!p.Json && result.LogPath is { } logPath)
        {
            await stderr.WriteLineAsync("[lanq] log: " + JobRunner.SafeLine(logPath)).ConfigureAwait(false);
        }

        await stderr.WriteLineAsync(StatusLine(result)).ConfigureAwait(false);
        if (p.Json)
        {
            var json = WithFetch(
                WithLog(
                    new RunJson(result.Status.ToString(), result.CliExitCode, result.Machine, result.JobId, Math.Round(started.Elapsed.TotalSeconds, 1), JobRunner.SafeLine(result.Message))
                    {
                        Retryable = result.Status == JobStatus.NoCapacity ? result.Retryable ?? true : null,
                        Excluded = result.Status == JobStatus.NoCapacity ? ExcludedJsons(result.Excluded) : null,
                        NextSteps = next,
                        Env = EnvNames(p.Env),
                        Exclusive = Flag(p.Exclusive),
                        Baseline = BaselineJsonOf(result.Baseline),
                        ScanSkipped = ScanSkippedJson(runner.ScanSkipped),
                    },
                    result.LogPath,
                    p.LogTail),
                fetch is not null,
                result.Fetch) with
            {
                Results = ResultsJsonOf(result.Results),
            };
            await stdout.WriteLineAsync(JsonSerializer.Serialize(json, LanqJson.Default.RunJson)).ConfigureAwait(false);
        }

        return result.CliExitCode;
    }

    /// <summary>The names of a job's --env for the JSON (never the values), or null when it sets none (the key is then left out).</summary>
    internal static IReadOnlyList<string>? EnvNames(IReadOnlyList<Policy.JobEnvVar> env) => env.Count > 0 ? [.. env.Select(e => e.Name)] : null;

    /// <summary>"[lanq] baseline: {label}{message}" per rerun, before the Next lines and the status line (which stays last).</summary>
    internal static async Task WriteBaselineLines(TextWriter stderr, IEnumerable<(string Label, BaselineResult Baseline)> baselines)
    {
        foreach (var (label, b) in baselines)
        {
            await stderr.WriteLineAsync(JobRunner.SafeLine($"[lanq] baseline: {label}{b.Message}")).ConfigureAwait(false);
        }
    }

    /// <summary>The baseline object of a run, shard or plan job (sanitised), or null (the key left out) when no baseline was asked for or run.</summary>
    internal static BaselineJson? BaselineJsonOf(BaselineResult? b) => b is null ? null : new BaselineJson(
        JobRunner.SafeLine(b.Ref),
        b.Sha,
        b.Status,
        b.ExitCode,
        b.Machine is { } m ? JobRunner.SafeLine(m) : null,
        b.JobId,
        b.LogPath is { } l ? JobRunner.SafeLine(l) : null,
        b.DurationSeconds,
        b.Verdict.Length == 0 ? null : b.Verdict,
        JobRunner.SafeLine(b.Message))
    {
        SameMachine = b.SameMachine,
    };

    /// <summary>scanSkipped (0.9.2): the files the secret scan did not read in full, paths sanitised; null (the key left out) when there are none.</summary>
    internal static List<ScanSkipJson>? ScanSkippedJson(IReadOnlyList<Policy.ScanSkip> skipped) =>
        skipped.Count == 0 ? null : [.. skipped.Select(s => new ScanSkipJson(JobRunner.SafeLine(s.Path), s.Bytes, s.Reason))];

    /// <summary>An optional JSON flag such as exclusive: true when set, null (the key left out) when not.</summary>
    internal static bool? Flag(bool set) => set ? true : null;

    /// <summary>The job lanq run (or its dry run) asks for, from its command line: one job, or the template of every shard.</summary>
    internal static JobRequest RequestOf(ParsedArgs p, string repoDir, FetchRequest? fetch) =>
        new(p.Needs, p.Argv, p.Directory, repoDir, TimeSpan.FromSeconds(p.WaitSeconds), TimeSpan.FromMinutes(p.TimeoutMinutes), p.MemGb, fetch)
        {
            NoWake = p.NoWake,
            On = p.On,
            Size = p.Size,
            Env = p.Env,
            Exclusive = p.Exclusive,
            Baseline = p.Baseline,
            BaselineRef = p.BaselineRef,
            Results = p.Results,
        };

    /// <summary>
    /// After a run's result is decided and before its Next and status lines (the status line stays the last stderr line): deletes old job logs
    /// (<see cref="LogPrune"/>, policy.keep_logs_days). Never throws and never changes the result; a problem is one warning line.
    /// </summary>
    internal static void PruneLogs(JobLog log, FleetConfig config, TextWriter stderr) =>
        LogPrune.AfterRun(log.LogsFolder, config.Policy.KeepLogsDays, line => stderr.WriteLine(line));

    /// <summary>
    /// The one JSON object --json promises on stdout when no normal result exists: for run the usual RunJson shape (no machine or job id),
    /// for health an object with status, exitCode and message instead of the array.
    /// </summary>
    /// <param name="logPath">run only: the log of a job that had started (Ctrl+C or an unexpected error after it held a slot), or null.</param>
    private static Task WriteJsonError(
        string command, string status, int exitCode, string message, TextWriter stdout, IReadOnlyList<NextStepJson> steps, TimeSpan? elapsed = null, bool fetch = false,
        int? logTail = null, string? logPath = null) =>
        stdout.WriteLineAsync(command switch
        {
            "run" => JsonSerializer.Serialize(
                WithFetch(WithLog(new RunJson(status, exitCode, null, null, Math.Round((elapsed ?? TimeSpan.Zero).TotalSeconds, 1), message) { NextSteps = steps }, logPath, logTail), fetch, null),
                LanqJson.Default.RunJson),
            "init" => JsonSerializer.Serialize(new InitJson(status, exitCode, null, message) { NextSteps = steps }, LanqJson.Default.InitJson),
            "doctor" => JsonSerializer.Serialize(
                new DoctorJson(status, exitCode, message, null, new DoctorCountsJson(0, 0, 0, 0, 0), []) { NextSteps = steps }, LanqJson.Default.DoctorJson),
            "stats" => JsonSerializer.Serialize(StatsError(status, exitCode, message, null, steps), LanqJson.Default.StatsJson),
            _ => JsonSerializer.Serialize(new ErrorJson(status, exitCode, message) { NextSteps = steps }, LanqJson.Default.ErrorJson),
        });

    /// <summary>The excluded list of a NoCapacity object (empty when the result named no machine), sanitised.</summary>
    internal static List<ExcludedJson> ExcludedJsons(IReadOnlyList<ExcludedMachine>? excluded) =>
        [.. (excluded ?? []).Select(x => new ExcludedJson(JobRunner.SafeLine(x.Machine), x.Reason, JobRunner.SafeLine(x.Why), x.Permanent))];

    /// <summary>
    /// logPath (always; null when no job started) and, with --log-tail N, logTail: the log's last N lines (<see cref="JobLog.Tail"/>), empty when
    /// no job started.
    /// </summary>
    internal static RunJson WithLog(RunJson json, string? logPath, int? tail) => json with
    {
        LogPath = logPath is null ? null : JobRunner.SafeLine(logPath),
        LogTail = tail is { } n ? logPath is null ? [] : JobLog.Tail(logPath, n) : null,
    };

    /// <summary>
    /// With --fetch the run object always carries fetched and fetchSkipped (empty when nothing was fetched or the job never ran), plus fetchDir
    /// and fetchError when a fetch happened. Without --fetch they are left out, so the object is exactly as before.
    /// </summary>
    internal static RunJson WithFetch(RunJson json, bool requested, FetchReport? report) => !requested
        ? json
        : json with
        {
            Fetched = report?.Fetched.Select(f => new FetchedJson(f.Path, f.Bytes, f.Sha256)).ToList() ?? [],
            FetchSkipped = report?.Skipped.Select(s => new FetchSkipJson(JobRunner.SafeLine(s.Path), s.Reason)).ToList() ?? [],
            FetchSkippedMore = report is { MoreSkipped: > 0 } ? report.MoreSkipped : null,
            FetchDir = report?.Directory is { } d ? JobRunner.SafeLine(d) : null,
            FetchError = report?.Error is { } e ? JobRunner.SafeLine(e) : null,
        };

    /// <summary>
    /// The folder fetched files go to: --fetch-dir (relative to the current folder), else .lanq/artifacts in the repository's top folder (the
    /// nearest folder up from <paramref name="repoDir"/> holding .git), else in <paramref name="repoDir"/>. The snapshot already leaves out
    /// every .lanq folder, so fetched files never travel back to a worker; the default .lanq folder also gets a .gitignore of "*" so they
    /// stay out of git status and commits.
    /// </summary>
    /// <returns>
    /// The output root, and the anchor: the folder lanq trusts as given (the repository top, or --fetch-dir itself). Every folder below the
    /// anchor down to out/jobid must be a real folder, never a symlink or junction.
    /// </returns>
    internal static (string Root, string Anchor) FetchRoot(string? fetchDir, string repoDir)
    {
        if (fetchDir is not null)
        {
            var dir = Path.GetFullPath(fetchDir);
            return (dir, dir);
        }

        var top = Plan.PlanFileLoader.RepositoryTop(repoDir) ?? Path.GetFullPath(repoDir);
        var lanq = Path.Combine(top, ".lanq");
        try
        {
            // A committed .lanq symlink or junction is never written through (the receiver refuses every file below it too).
            if (FetchReceiver.IsLink(lanq))
            {
                return (Path.Combine(lanq, "artifacts"), top);
            }

            Directory.CreateDirectory(lanq);
            using var ignore = new FileStream(Path.Combine(lanq, ".gitignore"), FileMode.CreateNew, FileAccess.Write);
            ignore.Write("*\n"u8);
        }
        catch (IOException)
        {
            // already there (or not writable: the fetch reports the write error itself)
        }
        catch (UnauthorizedAccessException)
        {
            // likewise
        }

        return (Path.Combine(lanq, "artifacts"), top);
    }

    /// <summary>The final line on stderr; TimedOut and WindowClosed (both exit 124) are told apart here.</summary>
    internal static string StatusLine(JobResult result) => $"[lanq] {result.Status}: {result.Message}";

    /// <summary>State folder: $LANQ_HOME, else a per-user folder. Deliberately never inside the current repository.</summary>
    internal static string ResolveHome(Func<string, string?> env, bool windows)
    {
        var explicitHome = env("LANQ_HOME");
        if (!string.IsNullOrEmpty(explicitHome))
        {
            return explicitHome;
        }

        if (windows)
        {
            var local = env("LOCALAPPDATA");
            return Path.Combine(string.IsNullOrEmpty(local) ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : local, "lanq");
        }

        var state = env("XDG_STATE_HOME");
        return !string.IsNullOrEmpty(state)
            ? Path.Combine(state, "lanq")
            : Path.Combine(HomeDir(env), ".local", "state", "lanq");
    }

    /// <summary>Config lookup: --config, then $LANQ_CONFIG, then the per-user config folder. Never the current directory.</summary>
    internal static string ResolveConfig(
        string? explicitPath, Func<string, string?> env, bool windows, Func<string, bool> exists, Func<Environment.SpecialFolder, string>? folder = null)
    {
        var path = explicitPath ?? env("LANQ_CONFIG");
        if (!string.IsNullOrEmpty(path))
        {
            return exists(path) ? path : throw new ConfigException($"Config file not found: {path}. Check the path, or write a starter file with: lanq init --config {path}");
        }

        var userFile = UserConfigPath(env, windows, folder);
        return exists(userFile)
            ? userFile
            : throw new ConfigException(
                $"No config found. Pass --config FILE, set LANQ_CONFIG, or create {userFile} (a starter file: lanq init --config {userFile}). "
                + "A machines file in the current directory is never picked up implicitly.");
    }

    /// <summary>
    /// The per-user config file lanq reads when neither --config nor $LANQ_CONFIG is given: %APPDATA%\lanq\machines.yml on Windows, else
    /// $XDG_CONFIG_HOME/lanq/machines.yml or ~/.config/lanq/machines.yml. Only a full path counts: a relative XDG_CONFIG_HOME is ignored (as the
    /// XDG spec says), and a relative or empty HOME / APPDATA falls back to the system's answer; when that is empty too there is no per-user
    /// folder, and this throws (exit 2) instead of resolving against the current folder.
    /// </summary>
    /// <param name="folder">Environment.GetFolderPath (tests pass their own).</param>
    /// <exception cref="ConfigException">The per-user folder cannot be determined.</exception>
    internal static string UserConfigPath(Func<string, string?> env, bool windows, Func<Environment.SpecialFolder, string>? folder = null) =>
        TryUserConfigPath(env, windows, folder) ?? throw new ConfigException(NoUserConfigFolder(windows));

    /// <summary>Why there is no per-user config file to read or write (<see cref="UserConfigPath"/>).</summary>
    internal static string NoUserConfigFolder(bool windows) =>
        $"lanq cannot tell where your per-user config folder is ({(windows ? "APPDATA is" : "HOME and XDG_CONFIG_HOME are")} not set to a full path). "
        + "Pass --config FILE, or set LANQ_CONFIG to the config file's full path.";

    /// <summary><see cref="UserConfigPath"/>, or null when the per-user folder cannot be determined.</summary>
    internal static string? TryUserConfigPath(Func<string, string?> env, bool windows, Func<Environment.SpecialFolder, string>? folder = null)
    {
        folder ??= Environment.GetFolderPath;
        string? userDir;
        if (windows)
        {
            userDir = FullPath(env("APPDATA")) ?? FullPath(folder(Environment.SpecialFolder.ApplicationData));
        }
        else
        {
            var home = FullPath(env("HOME")) ?? FullPath(folder(Environment.SpecialFolder.UserProfile));
            userDir = FullPath(env("XDG_CONFIG_HOME")) ?? (home is null ? null : Path.Combine(home, ".config"));
        }

        return userDir is null ? null : Path.Combine(userDir, "lanq", "machines.yml");

        // A full path in either system's form (tests apply the other system's rules too): /x, C:/x or C:\x, \\server\share.
        static string? FullPath(string? p) =>
            !string.IsNullOrEmpty(p) && (Path.IsPathRooted(p) || p[0] == '/' || (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '/' or '\\')) ? p : null;
    }

    private static string HomeDir(Func<string, string?> env) =>
        env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// lanq health. With <paramref name="needs"/> (--needs / --mem) each machine is also judged for such a job by <see cref="Scheduler.Assess"/>,
    /// the same rules lanq run uses: JSON rows gain eligible, why, reason, permanent and jobSlots, text lines gain "  -> ...", and the exit code is
    /// 0 when at least one machine could take the job now, 75 when none could. Without it the output and exit code are exactly as before.
    /// </summary>
    /// <param name="on">--on: only these machines are probed and printed (names matched ignoring case; an unknown one is refused, exit 2).</param>
    internal static async Task<int> Health(
        FleetConfig config, HealthProbe probe, TextWriter stdout, TextWriter stderr, CancellationToken ct, bool json = false, bool wake = false, JobNeeds? needs = null,
        string? configPath = null, IReadOnlyList<string>? on = null)
    {
        async Task<int> Error(string status, int code, string message, List<NextStepJson>? steps = null)
        {
            if (steps is not null)
            {
                await stderr.WriteLineAsync(message).ConfigureAwait(false); // a refusal before any probe says why, with or without --json
            }

            steps ??= NextSteps.ForHealthError(status, message, needs?.Capabilities ?? [], needs?.MemGb, configPath, json, on, needs?.Size);
            await NextSteps.WriteAsync(stderr, steps).ConfigureAwait(false);
            if (json)
            {
                await WriteJsonError("health", status, code, message, stdout, steps).ConfigureAwait(false);
            }

            return code;
        }

        if (Pinning.Resolve(config, on).Error is { } unknown)
        {
            return await Error("Rejected", 2, unknown, NextSteps.ArgumentsError(unknown)).ConfigureAwait(false);
        }

        var machines = Pinning.Candidates(config, on);

        foreach (var w in await KeyCheck.WarningsAsync(config, Environment.GetEnvironmentVariable, KeyCheck.SshKeygenAcceptsEmptyPassphrase).ConfigureAwait(false))
        {
            await stderr.WriteLineAsync(w).ConfigureAwait(false);
        }

        // Probe all machines concurrently, then print in config order.
        MachineHealth[] results;
        try
        {
            results = await Task.WhenAll(machines.Select(m => probe.ProbeAsync(m, config.Policy, ct, wake ? WakeMode.SendAndWait : WakeMode.None))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (json && ct.IsCancellationRequested)
        {
            return await Error("Cancelled", 130, "cancelled (Ctrl+C) before every machine answered").ConfigureAwait(false);
        }
        catch (Exception ex) when (json && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            return await Error("InfraError", 70, JobRunner.SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}")).ConfigureAwait(false);
        }

        var assessed = needs is null ? null : Scheduler.Assess(machines.Zip(results), needs);
        var exitCode = assessed is null || assessed.Any(a => a.Eligible) ? 0 : 75;

        // What to do about each machine: in every JSON row, and for people one "Next:" line per step on stderr (stdout is unchanged).
        var next = machines.Zip(results).Select((x, i) => NextSteps.ForHealthRow(x.First, x.Second, assessed?[i], configPath, json)).ToList();
        await NextSteps.WriteAsync(stderr, next.SelectMany(s => s)).ConfigureAwait(false);
        if (json)
        {
            var rows = machines.Zip(results).Select((x, i) => HealthRow(x.First, x.Second) with { NextSteps = next[i] }).ToList();
            await stdout.WriteLineAsync(assessed is null
                ? JsonSerializer.Serialize(rows, LanqJson.Default.ListHealthJson)
                : JsonSerializer.Serialize([.. rows.Zip(assessed, HealthNeedsRow)], LanqJson.Default.ListHealthNeedsJson)).ConfigureAwait(false);
            return exitCode;
        }

        foreach (var (i, (m, h)) in machines.Zip(results).Index())
        {
            var line = HealthLine(m, h);
            await stdout.WriteLineAsync(assessed is null ? line : line + "  -> " + Verdict(assessed[i])).ConfigureAwait(false);
        }

        return exitCode;

        static string Verdict(Assessment a) => a.Eligible
            ? string.Create(CultureInfo.InvariantCulture, $"eligible ({a.JobSlots} slot{(a.JobSlots == 1 ? "" : "s")})")
            : "not eligible: " + JobRunner.SafeLine(a.Why ?? a.Code ?? "?");
    }

    /// <summary>One machine's lanq health line (also printed by lanq makeready --add for the machine it added).</summary>
    internal static string HealthLine(Machine m, MachineHealth h)
    {
        var state = !h.Reachable ? "DOWN" : h.Hold is not null ? "HOLD" : "OK";
        var detail = h.Reachable
            ? string.Create(CultureInfo.InvariantCulture, $"{Load(h)}  {Size(h)}  disk free {h.DiskFreeGb:0.#} GB  {Memory(h)}  slots free {FreeSlots(m, h)}/{m.MaxJobs}{StaleText(h)}")
                + (m.AllowedHours is { Count: > 0 } && !h.WorkerClock ? "  time: local" : "")
                + "  suited: " + MachineSize.SuitedFor(m, h.MemTotalGb, h.Cores) + DeclaredSizeNote(m, h)
            : h.Error ?? "";
        return $"{m.Name,-14} {state,-5} {detail} {(h.Hold is not null && h.Reachable ? "(" + h.Hold + ")" : "")}".TrimEnd();
    }

    /// <summary>A health row with how the machine was judged for the --needs / --mem job (<paramref name="a"/> from <see cref="Scheduler.Assess"/>).</summary>
    internal static HealthNeedsJson HealthNeedsRow(HealthJson row, Assessment a) => new(row)
    {
        Eligible = a.Eligible,
        Why = a.Why is { } why ? JobRunner.SafeLine(why) : null,
        Reason = a.Code,
        Permanent = a.Permanent,
        JobSlots = a.JobSlots,
    };

    internal static HealthJson HealthRow(Machine m, MachineHealth h)
    {
        var state = !h.Reachable ? "down" : h.Hold is not null ? "hold" : "ok";
        var reason = !h.Reachable ? h.Error ?? h.Hold : h.Hold;
        return new HealthJson(
            m.Name, state, reason is null ? [] : [JobRunner.SafeLine(reason)], h.LoadRatio, h.DiskFreeGb, h.MemFreeGb, h.SlotsUsed, m.MaxJobs, h.LocalTime)
        {
            Cores = h.Cores,
            MemTotalGb = h.MemTotalGb,
            LoadKnown = !h.Reachable || h.LoadKnown,
            FreeSlots = h.Reachable ? FreeSlots(m, h) : 0,
            Size = MachineSize.Word(MachineSize.Effective(m, h.MemTotalGb, h.Cores)),
            SizeSource = MachineSize.Declared(m) is null ? "probe" : "config",
            SuitedFor = MachineSize.SuitedFor(m, h.MemTotalGb, h.Cores),
            SlotsStale = h.Reachable && h.SlotsStale > 0 ? h.SlotsStale : null,
        };
    }

    /// <summary>" (1 stale: reclaimed by the next claim)" after a health row's slots when the probe saw stale slots; empty otherwise.</summary>
    private static string StaleText(MachineHealth h) =>
        h.SlotsStale > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({h.SlotsStale} stale: reclaimed by the next claim)") : "";

    /// <summary>With a size: in machines.yml, " (size: heavy in machines.yml; probe: medium)" after the suited text; empty without one (the line stays as before).</summary>
    private static string DeclaredSizeNote(Machine m, MachineHealth h) =>
        MachineSize.Declared(m) is { } declared
            ? $" (size: {MachineSize.Word(declared)} in machines.yml; probe: {MachineSize.Word(MachineSize.Classify(h.MemTotalGb, h.Cores))})"
            : "";

    /// <summary>Slots a job could take now: max_jobs minus the slots in use, stale ones not counted (never below 0).</summary>
    private static int FreeSlots(Machine m, MachineHealth h) => Math.Max(0, m.MaxJobs - h.SlotsLive);

    private static string Load(MachineHealth h) => h.LoadKnown ? string.Create(CultureInfo.InvariantCulture, $"load {h.LoadRatio:0.00}") : "load ?";

    private static string Size(MachineHealth h) =>
        (h.Cores is { } c ? string.Create(CultureInfo.InvariantCulture, $"cores {c}") : "cores ?")
        + (h.MemTotalGb is { } t ? string.Create(CultureInfo.InvariantCulture, $"  ram {t:0.#} GB") : "  ram ?");

    private static string Memory(MachineHealth h) =>
        (h.MemFreeGb is { } mem ? string.Create(CultureInfo.InvariantCulture, $"mem free {mem:0.0} GB") : "mem free ?")
        + (h.SwapFreeGb is { } swap ? string.Create(CultureInfo.InvariantCulture, $" (swap {swap:0.0} GB)") : "");
}
