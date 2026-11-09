using System.Globalization;
using System.Reflection;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Fetch;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Payload;
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

    /// <summary>lanq run --shards N|auto, --shard-retries R, --fail-fast (see Cli.Shards.cs); null without --shards.</summary>
    public ShardOptions? Shards { get; init; }

    /// <summary>The options each command accepts (--config is accepted everywhere); anything else is an error, never silently ignored.</summary>
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["health"] = ["--json", "--wake", "--needs", "--mem"],
        ["run"] = ["--needs", "--dir", "--wait", "--timeout", "--mem", "--json", "--no-wake", "--fetch", "--fetch-dir", "--fetch-max", "--fetch-max-files", "--shards", "--shard-retries", "--fail-fast", "--log-tail", "--dry-run"],
        ["stats"] = [],
        ["unlock"] = ["--all", "--yes"],
        ["doctor"] = ["--remote", "--force"],
        ["makeready"] = ["--ssh", "--os", "--json", "--force", "--all"],
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
        var dryRun = false;
        var force = false;
        string? sshTarget = null;
        MachineOs? os = null;
        string? target = null;
        var fetch = new List<string>();
        string? fetchDir = null;
        long? fetchMax = null;
        int? fetchMaxFiles = null;
        int? logTail = null;
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

        if (logTail is not null && !json)
        {
            throw new ArgumentException("--log-tail needs --json (it adds logTail to the JSON object); without --json lanq prints the log's path before the status line.");
        }

        if (fetch.Count == 0 && (fetchDir is not null || fetchMax is not null || fetchMaxFiles is not null))
        {
            throw new ArgumentException("--fetch-dir, --fetch-max and --fetch-max-files need at least one --fetch pattern.");
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
            DryRun = dryRun,
            Force = force,
            SshTarget = sshTarget,
            Os = os,
            Fetch = FetchPatterns.Validate(fetch),
            FetchDir = fetchDir,
            FetchMaxBytes = fetchMax,
            FetchMaxFiles = fetchMaxFiles,
            Shards = shards.Build(),
            LogTail = logTail,
        };
    }

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
          lanq run ... --shards N|auto [--shard-retries R] [--fail-fast] -- npx jest --shard={shard}/{shards}
          lanq run ... --json [--log-tail 60] -- dotnet test
          lanq run ... --dry-run [--json] -- dotnet test
          lanq stats
          lanq unlock <machine> [--all --yes] [--config FILE]
          lanq doctor [--remote [--force]] [--config FILE]
          lanq makeready <machine> [--json] [--force] [--config FILE]
          lanq makeready --ssh user@host --os windows|linux [--json]
          lanq makeready --all [--json] [--force] [--config FILE]
          lanq init [--config FILE] [--json]
          lanq wake <machine> [--wait] [--json] [--config FILE]
          lanq --version

        Config lookup: --config, then $LANQ_CONFIG, then the per-user config folder (never the current directory).
        State (job log, logs) lives in $LANQ_HOME or a per-user folder, never inside your repository.
        --wait is in seconds (how long to wait for a free machine); --timeout is in minutes (how long the job may run, default 240).
        --mem is in GB: only machines reporting at least that much available memory take the job.
        --fetch (repeatable) brings files back from the job's working folder after the job, whether it passed or failed, into
        DIR/<job id>/ (default .lanq/artifacts in the repository): patterns relative to --dir with *, ? and **; regular files only,
        at most 500 files and 200 MB unless --fetch-max-files / --fetch-max (up to 2G). Fetch problems are reported, never change the exit code.
        unlock removes slot files past their recorded deadline (left by a crashed lanq); --all --yes removes every slot file on that machine.
        doctor checks this machine's setup without any network (ssh, git, config, known_hosts, keys, state folder); --remote also checks
        each worker over ssh (work_root, git or tar, and each capability's tool in the job environment); a paused machine or one outside its
        allowed hours is left alone (HOLD) unless --force. Exit 0 when no line is WARN or FAIL, 1 otherwise.
        health probes every machine and prints one line each (DOWN and HOLD machines are reported, not an error). Exit 0 every machine was
        probed, 2 bad arguments or config, 70 unexpected error (--json), 130 Ctrl+C. With --needs and/or --mem each machine is also judged for
        such a job by the rules lanq run uses ("-> eligible (2 slots)" or "-> not eligible: why"; --json rows add eligible, why, reason, permanent
        and jobSlots); exit 0 when at least one machine could take it now, 75 when none could.
        makeready runs the read-only lanq-makeready check on a worker (sent on ssh stdin, nothing is written to its disk) and prints the verdicts,
        the script's VERDICT line and numbered next steps (0.1 reports: the WARN/FAIL checks with advice), what the PC is suited for, the
        capability diff and a suggested machines.yml entry. Exit 0 ready, 1 not ready, 2 bad arguments, 70 ssh failed or the report was
        unusable, 75 the machine is paused or outside its allowed hours (--force scans anyway), 130 Ctrl+C. lanq never changes a worker.
        makeready --all checks every machine in the config (four at a time, same rules) and prints one table (machine, OS, verdict OK / WARN n /
        FAIL n / unreachable / held / error, top problem; FAIL only when the machine is NOT READY); exit 70 any machine unreachable or its
        report unusable, else 1 any NOT READY, else 75 any held, else 0 (warnings alone exit 0); 2 bad arguments or config; 130 Ctrl+C.
        init writes a commented starter machines.yml (default ./machines.yml, or --config FILE) and never overwrites a file or writes through
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
        object with status WouldRun (or the status run would end with), machine, ranked, machines, placement, wouldWake and retryable.
        --json: health prints one JSON array; run streams the job's output to stderr and prints one JSON object on stdout at the end;
        makeready prints the validated report (--all: one object with status, exitCode and machines); init prints one object (status,
        exitCode, path, message); wake prints one object (status, exitCode, machine, sent, awake, durationSeconds, message).
        --shards runs N copies of the command at once (1 to 64; auto = the free slots that can take it now), {shard} and {shards} substituted
        in its arguments and LANQ_SHARD / LANQ_SHARDS set; see docs/SHARDING.md. Exit 0 only when every shard passed; 130 Ctrl+C (every
        started shard is cleaned up).
        Exit codes (run): job's own code; 2 rejected by policy or bad arguments; 70 infrastructure error; 75 no eligible machine (at once, without waiting or waking, when no machine can ever take the job as configured; --json then says retryable: false); 124 job timed out or its machine's allowed hours ended; 130 Ctrl+C (the job is cleaned up where it had started).
        """;

    /// <summary>
    /// lanq --version: this build's version (with its commit) and the embedded lanq-makeready scripts, read back from the binary, so a
    /// packaging mistake (a missing resource in a trimmed build) shows up here and not on a worker.
    /// </summary>
    private static string VersionText()
    {
        var version = typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return $"lanq {version}\n"
            + $"lanq-makeready {MakereadyVersion} (embedded: lanq-makeready.ps1 {MakereadyScript(MachineOs.Windows).Length} bytes, "
            + $"lanq-makeready.sh {MakereadyScript(MachineOs.Linux).Length} bytes)";
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
            await stderr.WriteLineAsync(message).ConfigureAwait(false);
            var own = Array.IndexOf(args, "--") is var dash and >= 0 ? args[..dash] : args;
            if (own.Length > 0 && own[0] is "run" or "health" or "makeready" or "wake" or "init" && own.Contains("--json"))
            {
                // --fetch promises fetched and fetchSkipped on every run path, a refused command line included (only lanq's own words count).
                await (own[0] == "run" && own.Contains("--dry-run")
                    ? WritePlanJsonError("Rejected", 2, message, stdout, logTail: own.Contains("--log-tail"))
                    : own[0] == "run" && own.Contains("--shards")
                    ? WriteShardsJsonError("Rejected", 2, message, stdout)
                    : WriteJsonError(own[0], "Rejected", 2, message, stdout, fetch: own.Contains("--fetch"), logTail: own.Contains("--log-tail"))).ConfigureAwait(false);
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
        var log = new JobLog(home);
        if (p.Command == "stats")
        {
            return Stats(log, stdout);
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
            return await Doctor(p, LoadConfig, DoctorHost.Real(home), doctorSsh, new HealthProbe(doctorSsh, TimeProvider.System), stdout, ct).ConfigureAwait(false);
        }

        if (p.Command == "makeready")
        {
            var client = new ProcessSshClient(Environment.GetEnvironmentVariable);
            return await Makeready(p, LoadConfig, client, new HealthProbe(client, TimeProvider.System), stdout, stderr, ct).ConfigureAwait(false);
        }

        FleetConfig config;
        try
        {
            config = LoadConfig();
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            var message = JobRunner.SafeLine(ex.Message);
            await stderr.WriteLineAsync(message).ConfigureAwait(false);
            if (p.Json)
            {
                await (p.DryRun
                    ? WritePlanJsonError("Rejected", 2, message, stdout, p.LogTail is not null)
                    : p.Shards is not null
                    ? WriteShardsJsonError("Rejected", 2, message, stdout)
                    : WriteJsonError(p.Command, "Rejected", 2, message, stdout, fetch: p.Fetch.Count > 0, logTail: p.LogTail is not null)).ConfigureAwait(false);
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
            // --needs / --mem: judge each machine for such a job exactly as lanq run does (Windows-safe arguments assumed).
            var needs = p.Needs.Count > 0 || p.MemGb is not null ? new JobNeeds(p.Needs, p.MemGb, WindowsUnsafe: false) : null;
            return await Health(config, probe, stdout, stderr, ct, p.Json, p.WakeSleepers, needs).ConfigureAwait(false);
        }

        return await Run(p, config, ssh, new GitPayloadBuilder(), probe, log, home, Directory.GetCurrentDirectory(), stdout, stderr, ct).ConfigureAwait(false);
    }

    /// <summary>lanq run. With --json the job's output streams to stderr and stdout carries only the final JSON object.</summary>
    internal static async Task<int> Run(
        ParsedArgs p, FleetConfig config, ISshClient ssh, IPayloadBuilder payloads, HealthProbe probe, JobLog log, string home, string repoDir,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
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
            return await RunShards(p, shards, runner, repoDir, fetch, stdout, stderr, ct).ConfigureAwait(false);
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        JobResult result;
        try
        {
            result = await runner.RunAsync(
                new JobRequest(p.Needs, p.Argv, p.Directory, repoDir, TimeSpan.FromSeconds(p.WaitSeconds), TimeSpan.FromMinutes(p.TimeoutMinutes), p.MemGb, fetch) { NoWake = p.NoWake },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (p.Json && ct.IsCancellationRequested)
        {
            // Ctrl+C: an agent reading --json still gets its one object.
            await stderr.WriteLineAsync("[lanq] Cancelled").ConfigureAwait(false);
            await WriteJsonError("run", "Cancelled", 130, "cancelled (Ctrl+C); the job was cleaned up where it had started", stdout, started.Elapsed, fetch is not null, p.LogTail is not null).ConfigureAwait(false);
            return 130;
        }
        catch (Exception ex) when (p.Json && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            var message = JobRunner.SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}");
            await stderr.WriteLineAsync("[lanq] InfraError: " + message).ConfigureAwait(false);
            await WriteJsonError("run", "InfraError", 70, message, stdout, started.Elapsed, fetch is not null, p.LogTail is not null).ConfigureAwait(false);
            return 70;
        }

        // Where the whole log is, for people (--json carries it as logPath); always before the status line, which stays last.
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
                    },
                    result.LogPath,
                    p.LogTail),
                fetch is not null,
                result.Fetch);
            await stdout.WriteLineAsync(JsonSerializer.Serialize(json, LanqJson.Default.RunJson)).ConfigureAwait(false);
        }

        return result.CliExitCode;
    }

    /// <summary>
    /// The one JSON object --json promises on stdout when no normal result exists: for run the usual RunJson shape (no machine or job id),
    /// for health an object with status, exitCode and message instead of the array.
    /// </summary>
    private static Task WriteJsonError(string command, string status, int exitCode, string message, TextWriter stdout, TimeSpan? elapsed = null, bool fetch = false, bool logTail = false) =>
        stdout.WriteLineAsync(command switch
        {
            "run" => JsonSerializer.Serialize(
                WithFetch(new RunJson(status, exitCode, null, null, Math.Round((elapsed ?? TimeSpan.Zero).TotalSeconds, 1), message) { LogTail = logTail ? [] : null }, fetch, null),
                LanqJson.Default.RunJson),
            "init" => JsonSerializer.Serialize(new InitJson(status, exitCode, null, message), LanqJson.Default.InitJson),
            _ => JsonSerializer.Serialize(new ErrorJson(status, exitCode, message), LanqJson.Default.ErrorJson),
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

        var top = Path.GetFullPath(repoDir);
        for (var d = new DirectoryInfo(top); d is not null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
            {
                top = d.FullName;
                break;
            }
        }

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
    internal static string ResolveConfig(string? explicitPath, Func<string, string?> env, bool windows, Func<string, bool> exists)
    {
        var path = explicitPath ?? env("LANQ_CONFIG");
        if (!string.IsNullOrEmpty(path))
        {
            return exists(path) ? path : throw new ConfigException($"Config file not found: {path}. Check the path, or write a starter file with: lanq init --config {path}");
        }

        var userFile = UserConfigPath(env, windows);
        return exists(userFile)
            ? userFile
            : throw new ConfigException(
                $"No config found. Pass --config FILE, set LANQ_CONFIG, or create {userFile} (a starter file: lanq init --config {userFile}). "
                + "A machines file in the current directory is never picked up implicitly.");
    }

    /// <summary>The per-user config file lanq reads when neither --config nor $LANQ_CONFIG is given.</summary>
    internal static string UserConfigPath(Func<string, string?> env, bool windows)
    {
        string userDir;
        if (windows)
        {
            var appData = env("APPDATA");
            userDir = Path.Combine(string.IsNullOrEmpty(appData) ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : appData, "lanq");
        }
        else
        {
            var xdg = env("XDG_CONFIG_HOME");
            userDir = Path.Combine(!string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(HomeDir(env), ".config"), "lanq");
        }

        return Path.Combine(userDir, "machines.yml");
    }

    private static string HomeDir(Func<string, string?> env) =>
        env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// lanq health. With <paramref name="needs"/> (--needs / --mem) each machine is also judged for such a job by <see cref="Scheduler.Assess"/>,
    /// the same rules lanq run uses: JSON rows gain eligible, why, reason, permanent and jobSlots, text lines gain "  -> ...", and the exit code is
    /// 0 when at least one machine could take the job now, 75 when none could. Without it the output and exit code are exactly as before.
    /// </summary>
    internal static async Task<int> Health(
        FleetConfig config, HealthProbe probe, TextWriter stdout, TextWriter stderr, CancellationToken ct, bool json = false, bool wake = false, JobNeeds? needs = null)
    {
        foreach (var w in await KeyCheck.WarningsAsync(config, Environment.GetEnvironmentVariable, KeyCheck.SshKeygenAcceptsEmptyPassphrase).ConfigureAwait(false))
        {
            await stderr.WriteLineAsync(w).ConfigureAwait(false);
        }

        // Probe all machines concurrently, then print in config order.
        MachineHealth[] results;
        try
        {
            results = await Task.WhenAll(config.Machines.Select(m => probe.ProbeAsync(m, config.Policy, ct, wake ? WakeMode.SendAndWait : WakeMode.None))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (json && ct.IsCancellationRequested)
        {
            await WriteJsonError("health", "Cancelled", 130, "cancelled (Ctrl+C) before every machine answered", stdout).ConfigureAwait(false);
            return 130;
        }
        catch (Exception ex) when (json && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            await WriteJsonError("health", "InfraError", 70, JobRunner.SafeLine($"unexpected error: {ex.GetType().Name}: {ex.Message}"), stdout).ConfigureAwait(false);
            return 70;
        }

        var assessed = needs is null ? null : Scheduler.Assess(config.Machines.Zip(results), needs);
        var exitCode = assessed is null || assessed.Any(a => a.Eligible) ? 0 : 75;
        if (json)
        {
            var rows = config.Machines.Zip(results).Select(x => HealthRow(x.First, x.Second)).ToList();
            await stdout.WriteLineAsync(assessed is null
                ? JsonSerializer.Serialize(rows, LanqJson.Default.ListHealthJson)
                : JsonSerializer.Serialize([.. rows.Zip(assessed, HealthNeedsRow)], LanqJson.Default.ListHealthNeedsJson)).ConfigureAwait(false);
            return exitCode;
        }

        foreach (var (i, (m, h)) in config.Machines.Zip(results).Index())
        {
            var state = !h.Reachable ? "DOWN" : h.Hold is not null ? "HOLD" : "OK";
            var detail = h.Reachable
                ? string.Create(CultureInfo.InvariantCulture, $"load {h.LoadRatio:0.00}  {Size(h)}  disk free {h.DiskFreeGb:0.#} GB  {Memory(h)}  slots free {FreeSlots(m, h)}/{m.MaxJobs}")
                    + (m.AllowedHours is { Count: > 0 } && !h.WorkerClock ? "  time: local" : "")
                    + "  suited: " + MachineSize.SuitedFor(m, h.MemTotalGb, h.Cores)
                : h.Error ?? "";
            var line = $"{m.Name,-14} {state,-5} {detail} {(h.Hold is not null && h.Reachable ? "(" + h.Hold + ")" : "")}".TrimEnd();
            await stdout.WriteLineAsync(assessed is null ? line : line + "  -> " + Verdict(assessed[i])).ConfigureAwait(false);
        }

        return exitCode;

        static string Verdict(Assessment a) => a.Eligible
            ? string.Create(CultureInfo.InvariantCulture, $"eligible ({a.JobSlots} slot{(a.JobSlots == 1 ? "" : "s")})")
            : "not eligible: " + JobRunner.SafeLine(a.Why ?? a.Code ?? "?");
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
            FreeSlots = h.Reachable ? FreeSlots(m, h) : 0,
            Size = MachineSize.Classify(h.MemTotalGb, h.Cores).ToString().ToLowerInvariant(),
            SuitedFor = MachineSize.SuitedFor(m, h.MemTotalGb, h.Cores),
        };
    }

    /// <summary>Slots a job could take now: max_jobs minus the slots in use (never below 0).</summary>
    private static int FreeSlots(Machine m, MachineHealth h) => Math.Max(0, m.MaxJobs - h.SlotsUsed);

    private static string Size(MachineHealth h) =>
        (h.Cores is { } c ? string.Create(CultureInfo.InvariantCulture, $"cores {c}") : "cores ?")
        + (h.MemTotalGb is { } t ? string.Create(CultureInfo.InvariantCulture, $"  ram {t:0.#} GB") : "  ram ?");

    private static string Memory(MachineHealth h) =>
        (h.MemFreeGb is { } mem ? string.Create(CultureInfo.InvariantCulture, $"mem free {mem:0.0} GB") : "mem free ?")
        + (h.SwapFreeGb is { } swap ? string.Create(CultureInfo.InvariantCulture, $" (swap {swap:0.0} GB)") : "");

    private static int Stats(JobLog log, TextWriter stdout)
    {
        var all = log.ReadAll();
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{all.Count} jobs, {all.Sum(r => r.WallSeconds) / 60:0.#} min total wall-clock"));
        foreach (var g in all.GroupBy(r => r.Machine ?? "-").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {g.Key}: {g.Count()} jobs, {g.Sum(r => r.WallSeconds) / 60:0.#} min"));
        }

        return 0;
    }
}
