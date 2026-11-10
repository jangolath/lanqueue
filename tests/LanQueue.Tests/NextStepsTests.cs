using System.Text;
using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Jobs;
using LanQueue.Output;
using LanQueue.Scheduling;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>nextSteps in every --json object and the "Next:" lines for people (Output/NextSteps.cs, Output/CommandLine.cs).</summary>
public sealed class NextStepsTests : IDisposable
{
    private const int TestTimeout = 60_000;

    /// <summary>The documented action values, in the documented order.</summary>
    private static readonly string[] Documented = ["proceed", "retry", "wait", "fix_code", "fix_command", "ask_developer", "run_doctor", "run_health", "none"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-next-" + Guid.NewGuid().ToString("N"));

    public NextStepsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private JobLog Log => new(Path.Combine(_dir, "state"));

    private static ParsedArgs Args(params string[] args) => ParsedArgs.Parse(args);

    private static readonly ParsedArgs PlainRun = Args("run", "--json", "--needs", "node", "--", "npx", "jest");

    private static NextSteps.RunOutcome Outcome(string status, int exit = 0, RejectKind? reject = null, InfraKind? infra = null, bool mayStillRun = false, bool? retryable = null,
        IReadOnlyList<ExcludedMachine>? excluded = null) =>
        new(status, exit, "message", reject, infra, mayStillRun, "big-01", retryable, excluded, Path.Combine("state", "logs", "j1.log"));

    private static Machine Machine(string name = "big-01", string? hours = null)
    {
        var yaml = $"""
            policy:
              allowed_commands: ["npx jest"]
            machines:
              - name: {name}
                os: linux
                ssh: user@10.20.30.12
                work_root: /srv/lanq
                capabilities: [node, linux]
                max_jobs: 2
            {(hours is null ? "" : $"    allowed_hours: \"{hours}\"")}
            """;
        return ConfigLoader.Parse(yaml).Machines[0];
    }

    private static MachineHealth Up(HoldKind kind = HoldKind.None, string? hold = null) =>
        new("big-01", true, 0.1, 50, 0, hold, null) { HoldKind = kind, MemFreeGb = 3, MemTotalGb = 8 };

    private static MachineHealth Down(string error, bool asleep = false) =>
        new("big-01", false, 0, 0, 0, "unreachable", error) { HoldKind = HoldKind.Unreachable, LooksAsleep = asleep };

    private static ShardResult Shard(int i, string status, int exit = 0, InfraKind? infra = null, bool mayStillRun = false) =>
        new(i, status, exit, "big-01", "j" + i, TimeSpan.Zero, 1, "m") { InfraKind = infra, MayStillRun = mayStillRun, LogPath = $"/state/logs/j{i}.log" };

    /// <summary>Every mapping, for every status of every command (each with the run command lines that matter).</summary>
    private static IEnumerable<(string Case, List<NextStepJson> Steps)> EveryMapping()
    {
        ParsedArgs[] runs =
        [
            PlainRun,
            Args("run", "--json", "--wait", "60", "--needs", "node", "--", "npx", "jest"),
            Args("run", "--json", "--wait", "300", "--no-wake", "--config", "fleet.yml", "--mem", "4", "--timeout", "10080", "--", "npx", "jest"),
        ];
        var unreachable = new ExcludedMachine("big-01", "unreachable", "ssh: connect to host big-01 port 22: Connection refused", false);
        var lacks = new ExcludedMachine("big-01", "missing_capability", "lacks gpu (capabilities: node)", true);
        foreach (var p in runs)
        {
            var outcomes = new List<NextSteps.RunOutcome>
            {
                Outcome("Completed"),
                Outcome("Failed", 3),
                Outcome("NoCapacity", 75, retryable: true, excluded: [unreachable]),
                Outcome("NoCapacity", 75, retryable: true, excluded: []),
                Outcome("NoCapacity", 75, retryable: false, excluded: [lacks]),
                Outcome("TimedOut", 124),
                Outcome("WindowClosed", 124),
                Outcome("Cancelled", 130),
                Outcome("InfraError", 70, infra: InfraKind.Connection),
                Outcome("InfraError", 70, infra: InfraKind.Connection, mayStillRun: true),
                Outcome("Rejected", 2),
            };
            outcomes.AddRange(Enum.GetValues<RejectKind>().Select(k => Outcome("Rejected", 2, reject: k)));
            outcomes.AddRange(Enum.GetValues<InfraKind>().Select(k => Outcome("InfraError", 70, infra: k)));
            foreach (var o in outcomes)
            {
                yield return ($"run {string.Join(' ', p.Raw)}: {o.Status} {o.RejectKind}{o.InfraKind}", NextSteps.ForRun(p, o));
            }

            var shardRun = Args([.. p.Raw.Take(1), "--shards", "3", .. p.Raw.Skip(1)]);
            ShardRunResult[] shardResults =
            [
                new("Completed", 0, 3, [Shard(1, "Completed"), Shard(2, "Completed"), Shard(3, "Completed")], "ok"),
                new("Failed", 1, 3, [Shard(1, "Completed"), Shard(2, "Failed", 1), Shard(3, "Failed", 2)], "failed"),
                new("InfraError", 70, 3, [Shard(1, "Completed"), Shard(2, "InfraError", 70, InfraKind.Connection)], "lost") { InfraKind = InfraKind.Connection },
                new("InfraError", 70, 3, [Shard(1, "InfraError", 70, InfraKind.Connection, mayStillRun: true)], "lost") { InfraKind = InfraKind.Connection },
                new("InfraError", 70, 3, [], "snapshot") { InfraKind = InfraKind.Snapshot },
                new("NoCapacity", 75, 3, [Shard(1, "NoCapacity", 75)], "none") { Retryable = false, Excluded = [lacks] },
                new("Rejected", 2, 3, [], "refused") { RejectKind = RejectKind.Policy },
                new("TimedOut", 124, 3, [Shard(1, "TimedOut", 124)], "slow"),
                new("Cancelled", 130, 3, [Shard(1, "Cancelled", 130)], "cancelled"),
            ];
            foreach (var r in shardResults)
            {
                yield return ($"shards {r.Status}", NextSteps.ForShards(shardRun, r));
            }

            var dry = Args([.. p.Raw.Take(1), "--dry-run", .. p.Raw.Skip(1)]);
            JobPlan[] plans =
            [
                new(JobPlan.WouldRun, 0, "would run on big-01"),
                new("NoCapacity", 75, "none") { Retryable = true },
                new("NoCapacity", 75, "none") { Retryable = false },
                new("Rejected", 2, "refused") { RejectKind = RejectKind.Secret },
                new("InfraError", 70, "snapshot") { InfraKind = InfraKind.Snapshot },
                new("Cancelled", 130, "cancelled"),
            ];
            foreach (var plan in plans)
            {
                yield return ($"dry-run {plan.Status}", NextSteps.ForPlan(dry, plan));
            }
        }

        var m = Machine(hours: "22:00-07:00");
        var needs = new JobNeeds(["node", "gpu"], 4, false);
        MachineHealth[] rows =
        [
            Up(),
            Down("ssh: connect to host big-01 port 22: Connection refused"),
            Down("connect timeout", asleep: true),
            .. new[] { HoldKind.Paused, HoldKind.OutsideHours, HoldKind.WindowClosing, HoldKind.LowDisk, HoldKind.LowMemory, HoldKind.MemoryPressure, HoldKind.Load, HoldKind.ToolMissing, HoldKind.Unreadable }
                .Select(k => Up(k, k.ToString())),
        ];
        foreach (var h in rows)
        {
            yield return ($"health {h.HoldKind}", NextSteps.ForHealthRow(m, h, null, "fleet.yml"));
            yield return ($"health --needs {h.HoldKind}", NextSteps.ForHealthRow(m, h, Scheduler.Assess([(m, h)], needs)[0], null));
            yield return ($"health --needs node {h.HoldKind}", NextSteps.ForHealthRow(m, h, Scheduler.Assess([(m, h)], new JobNeeds(["node"], null, false))[0], null));
        }

        foreach (var status in new[] { "Rejected", "InfraError", "Cancelled" })
        {
            yield return ($"health error {status}", NextSteps.ForHealthError(status, "message", ["node"], 4, "fleet.yml"));
        }

        foreach (var status in new[] { "Sent", "Awake", "NoAnswer", "Rejected", "InfraError", "Cancelled" })
        {
            yield return ($"wake {status}", NextSteps.ForWake(status, "message", "fleet.yml"));
        }

        yield return ("init Created", NextSteps.ForInit("Created", "/home/dev/machines.yml", "Wrote it", false));
        yield return ("init Rejected exists", NextSteps.ForInit("Rejected", "/home/dev/machines.yml", "exists", true));
        yield return ("init Rejected link", NextSteps.ForInit("Rejected", "/home/dev/machines.yml", "a link", false));
        yield return ("init InfraError", NextSteps.ForInit("InfraError", "/home/dev/machines.yml", "could not write", false));
        foreach (var f in Enum.GetValues<NextSteps.MakereadyFailure>())
        {
            yield return ($"makeready {f}", NextSteps.ForMakereadyError(f, "message", SuggestedCommand.Makeready("big-01", "fleet.yml"), "fleet.yml"));
        }

        var setup = SuggestedCommand.Setup("build@10.20.30.40", MachineOs.Linux, "LANQ_KEY");
        yield return ("makeready --ssh key refused", NextSteps.ForMakereadyError(
            NextSteps.MakereadyFailure.Unreachable, "message", SuggestedCommand.Makeready(null, null, "build@10.20.30.40", MachineOs.Linux), null, setup));
        foreach (var outcome in Enum.GetValues<NextSteps.AddOutcome>())
        {
            var rescan = SuggestedCommand.Makeready(null, null, "build@10.20.30.40", MachineOs.Linux, "LANQ_KEY");
            yield return ($"makeready --add {outcome}", NextSteps.ForMakereadyAdd(outcome, "message", "fleet.yml", rescan, setup, placeholder: true));
        }

        foreach (var verdict in new[] { "OK", "WARN", "FAIL", "held", "unreachable", "error" })
        {
            yield return ($"makeready --all row {verdict}", NextSteps.ForMakereadyRow("big-01", verdict, "problem", "fleet.yml"));
        }

        MakereadyRowJson Row(string verdict) => new("big-01", "linux", verdict, 0, verdict == "FAIL" ? 1 : 0, 0, "problem", null);
        foreach (var (status, verdict) in new[] { ("Ok", "OK"), ("Failing", "FAIL"), ("Held", "held"), ("InfraError", "unreachable") })
        {
            yield return ($"makeready --all {status}", NextSteps.ForMakereadyAll(status, [Row("OK"), Row(verdict)], "fleet.yml"));
        }

        yield return ("arguments", NextSteps.ArgumentsError("Unknown or incomplete option '--bogus'."));
        yield return ("config", NextSteps.ConfigError("fleet.yml", "Config file not found: fleet.yml."));
    }

    private static IEnumerable<NextStepJson> EveryStep() => EveryMapping().SelectMany(x => x.Steps);

    /// <summary>The part of an argv before a bare "--" (lanq's own words), without "lanq".</summary>
    private static List<string> Own(IReadOnlyList<string> argv) => [.. argv.Skip(1).TakeWhile(t => t != "--")];

    [Fact]
    public void EveryStatusOfEveryCommandHasAStep()
    {
        foreach (var (name, steps) in EveryMapping())
        {
            Assert.True(steps.Count >= 1, name);
            Assert.All(steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason), name));
            Assert.All(steps, s => Assert.Equal(s.Argv is not null, s.Command is not null || s.Argv is not null)); // argv whenever a command is suggested
            if (steps.Any(s => s.Action == "none"))
            {
                Assert.Single(steps); // none stands alone
            }
        }
    }

    [Fact]
    public void EverySuggestedCommandParsesAndIsOnTheSafeList()
    {
        var suggested = EveryStep().Where(s => s.Argv is not null).ToList();
        Assert.NotEmpty(suggested);
        foreach (var s in suggested)
        {
            Assert.Equal("lanq", s.Argv![0]);
            var p = ParsedArgs.Parse([.. s.Argv.Skip(1)]);
            Assert.Contains(p.Command, new[] { "run", "health", "doctor", "makeready" });
            var own = Own(s.Argv);
            foreach (var banned in new[] { "--force", "--yes", "--wake", "--add" })
            {
                Assert.DoesNotContain(banned, own);
            }

            // --setup only as lanq makeready --ssh ... --setup (it connects to nothing), and never safe unattended: it is for the developer.
            if (own.Contains("--setup"))
            {
                Assert.True(p.Setup);
                Assert.Equal("makeready", p.Command);
                Assert.NotNull(p.SshTarget);
                Assert.False(s.SafeToRunUnattended);
            }

            Assert.DoesNotContain(own[0], new[] { "unlock", "init", "wake" });
            Assert.False(p.WakeSleepers);
        }

        // The factories refuse even when asked: a run with --force is not a run lanq would suggest again.
        Assert.Null(SuggestedCommand.Rerun(Args("health", "--json")));
    }

    [Theory]
    [InlineData("run", "--json", "--needs", "node", "--", "npx", "jest")]
    [InlineData("run", "--json", "--wait", "30", "--needs", "node", "--", "npx", "jest")]
    [InlineData("run", "--json", "--wait", "0", "--no-wake", "--needs", "node", "--", "npx", "jest")]
    [InlineData("run", "--json", "--wait", "600", "--needs", "node", "--timeout", "30", "--", "npx", "jest")]
    [InlineData("run", "--json", "--needs", "node", "--shards", "auto", "--shard-retries", "2", "--", "npx", "jest", "--shard={shard}/{shards}")]
    [InlineData("run", "--dry-run", "--json", "--needs", "node", "--", "npx", "jest", "--no-wake")]
    public void ARetryNeverAddsSideEffects(params string[] raw)
    {
        var original = Args(raw);
        var reruns = new[]
        {
            SuggestedCommand.Rerun(original),
            SuggestedCommand.Rerun(original, waitSeconds: 120),
            SuggestedCommand.Rerun(original, timeoutMinutes: original.TimeoutMinutes * 2),
            SuggestedCommand.Rerun(original, shardRetries: 1),
            SuggestedCommand.Rerun(original, dropDryRun: true),
        };
        foreach (var rerun in reruns)
        {
            Assert.NotNull(rerun);
            var p = ParsedArgs.Parse([.. rerun.Argv.Skip(1)]);
            Assert.True(rerun.SafeToRunUnattended);

            // It never wakes where the original could not: --wait 0 (or --no-wake) means --no-wake now; --no-wake is never dropped.
            if (original.NoWake || original.WaitSeconds == 0)
            {
                Assert.True(p.NoWake);
            }

            Assert.True(p.WaitSeconds >= original.WaitSeconds);
            Assert.True(p.TimeoutMinutes >= original.TimeoutMinutes);
            Assert.Equal(original.Argv, p.Argv); // the job's own arguments, untouched (a job's "--no-wake" stays the job's)
            Assert.Equal(original.Shards?.Count, p.Shards?.Count);
            Assert.True((p.Shards?.Retries ?? 0) >= (original.Shards?.Retries ?? 0));
            Assert.Equal(original.Json, p.Json);
        }

        // Only --wait, --timeout and --shard-retries are ever raised, never lowered.
        Assert.Equal(original.WaitSeconds, ParsedArgs.Parse([.. SuggestedCommand.Rerun(original, waitSeconds: 0)!.Argv.Skip(1)]).WaitSeconds);
        Assert.Equal(original.TimeoutMinutes, ParsedArgs.Parse([.. SuggestedCommand.Rerun(original, timeoutMinutes: 1)!.Argv.Skip(1)]).TimeoutMinutes);
    }

    [Fact]
    public void TheWaitStepStopsAfter120Seconds()
    {
        static NextStepJson WaitStep(params string[] raw) =>
            Assert.Single(NextSteps.ForRun(Args(raw), Outcome("NoCapacity", 75, retryable: true, excluded: [])), s => s.Action == "wait");

        // --wait 0: 120 s, and --no-wake (the original could not wake a machine).
        var fromZero = WaitStep("run", "--json", "--needs", "node", "--", "npx", "jest");
        Assert.Equal(["lanq", "run", "--json", "--needs", "node", "--wait", "120", "--no-wake", "--", "npx", "jest"], fromZero.Argv);
        Assert.Equal("lanq run --json --needs node --wait 120 --no-wake -- npx jest", fromZero.Command);
        Assert.True(fromZero.SafeToRunUnattended);

        // --wait 60 could already wake: raised to 120 in place, no --no-wake added.
        var fromSixty = WaitStep("run", "--json", "--wait", "60", "--needs", "node", "--", "npx", "jest");
        Assert.Equal(["lanq", "run", "--json", "--wait", "120", "--needs", "node", "--", "npx", "jest"], fromSixty.Argv);

        // 120 s or more already waited: no command, so an agent cannot loop on it.
        foreach (var waited in new[] { "120", "900" })
        {
            var done = WaitStep("run", "--json", "--wait", waited, "--needs", "node", "--", "npx", "jest");
            Assert.Null(done.Argv);
            Assert.Null(done.Command);
            Assert.False(done.SafeToRunUnattended);
        }

        // WindowClosed uses the same rule.
        var closed = Assert.Single(NextSteps.ForRun(Args("run", "--json", "--wait", "200", "--", "npx", "jest"), Outcome("WindowClosed", 124)));
        Assert.Equal("wait", closed.Action);
        Assert.Null(closed.Argv);
    }

    [Fact]
    public void RetryKeepsConfigDirMemFetchAndJobArgs()
    {
        string[] raw =
        [
            "run", "--config", "C:/fleet/machines.yml", "--json", "--needs", "dotnet,linux", "--dir", "tests", "--mem", "2.5", "--wait", "30", "--timeout", "45",
            "--fetch", "TestResults/**/*.trx", "--fetch-dir", "--wait", "--log-tail", "60", "--", "dotnet", "test", "--wait", "5", "--filter", "Category=Unit|Name~Foo",
        ];
        var p = Args(raw);
        Assert.Equal("--wait", p.FetchDir); // "--wait" after --fetch-dir is its value, not the option

        var retry = Assert.Single(NextSteps.ForRun(p, Outcome("TimedOut", 124)), s => s.Action == "retry");
        Assert.Equal(
            [
                "lanq", "run", "--config", "C:/fleet/machines.yml", "--json", "--needs", "dotnet,linux", "--dir", "tests", "--mem", "2.5", "--wait", "30", "--timeout", "90",
                "--fetch", "TestResults/**/*.trx", "--fetch-dir", "--wait", "--log-tail", "60", "--", "dotnet", "test", "--wait", "5", "--filter", "Category=Unit|Name~Foo",
            ],
            retry.Argv);
        Assert.True(retry.SafeToRunUnattended);

        // The read-only checks carry --config too.
        var health = Assert.Single(NextSteps.ForRun(p, Outcome("NoCapacity", 75, retryable: true, excluded: [])), s => s.Action == "run_health");
        Assert.Equal(["lanq", "health", "--needs", "dotnet,linux", "--mem", "2.5", "--json", "--config", "C:/fleet/machines.yml"], health.Argv);
        var doctor = Assert.Single(NextSteps.ForRun(p, Outcome("Rejected", 2, reject: RejectKind.Config)), s => s.Action == "run_doctor");
        Assert.Equal(["lanq", "doctor", "--config", "C:/fleet/machines.yml"], doctor.Argv);

        // --timeout never goes past a week.
        var week = Args("run", "--timeout", "6000", "--", "npx", "jest");
        Assert.Contains("10080", Assert.Single(NextSteps.ForRun(week, Outcome("TimedOut", 124)), s => s.Action == "retry").Argv!);
        Assert.DoesNotContain(NextSteps.ForRun(Args("run", "--timeout", "10080", "--", "npx", "jest"), Outcome("TimedOut", 124)), s => s.Action == "retry");
    }

    [Fact]
    public void ShardPlaceholdersAreQuotedAndASingleQuoteDropsCommandButKeepsArgv()
    {
        var shards = Args("run", "--json", "--shards", "4", "--needs", "node", "--", "npx", "jest", "--shard={shard}/{shards}");
        var lost = new ShardRunResult("InfraError", 70, 4, [Shard(1, "InfraError", 70, InfraKind.Connection)], "lost") { InfraKind = InfraKind.Connection };
        var retry = Assert.Single(NextSteps.ForShards(shards, lost));
        Assert.Equal("retry", retry.Action);
        Assert.Equal(
            "lanq run --json --shards 4 --needs node --shard-retries 1 --no-wake -- npx jest '--shard={shard}/{shards}'", retry.Command);
        Assert.True(retry.SafeToRunUnattended);

        // A shard that may still be running: never re-run unattended.
        var running = new ShardRunResult("InfraError", 70, 4, [Shard(1, "InfraError", 70, InfraKind.Connection, mayStillRun: true)], "lost") { InfraKind = InfraKind.Connection };
        var steps = NextSteps.ForShards(shards, running);
        Assert.DoesNotContain(steps, s => s.Action == "retry");
        Assert.Contains(steps, s => s.Action == "ask_developer" && s.Reason.Contains("may still be running on big-01", StringComparison.Ordinal));

        // A quote the shells could disagree on: argv only.
        foreach (var token in new[] { "it's", "say \"hi\"", "", "--%" })
        {
            var quoted = Args("run", "--json", "--", "npx", "jest", token);
            var again = Assert.Single(NextSteps.ForRun(quoted, Outcome("InfraError", 70, infra: InfraKind.Connection)));
            Assert.Null(again.Command);
            Assert.Equal(["lanq", "run", "--json", "--no-wake", "--", "npx", "jest", token], again.Argv);
            Assert.True(again.SafeToRunUnattended); // the argv is still exact
        }

        Assert.Equal("lanq '@splat' 'a b' x,y", CommandLine.Render(["lanq", "@splat", "a b", "x,y"]));
    }

    [Fact] // Shells.Run guards each shell with its own timeout
    public void CommandSplitsBackToArgv()
    {
        string[][] argvs =
        [
            ["lanq", "run", "--json", "--needs", "dotnet,linux", "--", "dotnet", "test", "--filter", "Category=Unit|Name~Foo"],
            ["lanq", "run", "--", "npx", "jest", "--shard={shard}/{shards}", "a b", "$HOME", "`whoami`", "@args", "a;b", "*.trx", "50%", "-x"],
            ["lanq", "doctor", "--config", @"C:\Users\dev\AppData\Roaming\lanq\machines.yml"],
        ];
        foreach (var argv in argvs)
        {
            var command = CommandLine.Render(argv);
            Assert.NotNull(command);
            Assert.Equal(argv, Split(command));

            // The real shells agree: git echoes each argument it was given, single-quoted, in either shell.
            var echo = "git rev-parse --sq-quote" + command["lanq".Length..];
            if (Shells.Sh is not null)
            {
                Assert.Equal(argv[1..], Split(Shells.Run("linux", echo).Output.Trim()));
            }

            if (Shells.PowerShell is not null)
            {
                Assert.Equal(argv[1..], Split(Shells.Run("windows", echo).Output.Trim()));
            }
        }

        // A POSIX-style split of single-quoted and bare words (git's 'it'\''s' escape included).
        static List<string> Split(string line)
        {
            var words = new List<string>();
            var word = new StringBuilder();
            var inWord = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '\'')
                {
                    var end = line.IndexOf('\'', i + 1);
                    word.Append(line, i + 1, end - i - 1);
                    i = end;
                    inWord = true;
                }
                else if (c == '\\' && i + 1 < line.Length)
                {
                    word.Append(line[++i]);
                    inWord = true;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (inWord)
                    {
                        words.Add(word.ToString());
                        word.Clear();
                        inWord = false;
                    }
                }
                else
                {
                    word.Append(c);
                    inWord = true;
                }
            }

            if (inWord)
            {
                words.Add(word.ToString());
            }

            return words;
        }
    }

    [Fact]
    public void ActionsComeFromTheDocumentedEnum()
    {
        Assert.Equal(Documented, Enum.GetValues<NextAction>().Select(NextSteps.Name));
        Assert.All(EveryStep(), s => Assert.Contains(s.Action, Documented));

        // Every action is used somewhere.
        Assert.Equal(Documented.Order(), EveryStep().Select(s => s.Action).Distinct().Order());
    }

    [Fact]
    public void MakereadyIsNeverSafeUnattended()
    {
        var makeready = EveryStep().Where(s => s.Argv is { } a && a[1] == "makeready").ToList();
        Assert.NotEmpty(makeready);
        Assert.All(makeready, s => Assert.False(s.SafeToRunUnattended));
        Assert.False(SuggestedCommand.Makeready("big-01", null)!.SafeToRunUnattended);
        Assert.False(SuggestedCommand.Makeready(null, null, "build@10.20.30.40", MachineOs.Linux)!.SafeToRunUnattended);

        // For people: "(for the developer)" before such a command.
        var line = Assert.Single(NextSteps.Lines(NextSteps.ForMakereadyRow("big-01", "FAIL", "git missing", null)));
        Assert.StartsWith("Next: (for the developer) big-01 is NOT READY", line, StringComparison.Ordinal);
        Assert.EndsWith(": lanq makeready big-01", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// --setup is suggested only by its own factory (lanq makeready --ssh ... --setup, never safe unattended); --add and --yes never, not even
    /// smuggled in as a value the factories pass on.
    /// </summary>
    [Fact]
    public void SetupIsOnlyEverSuggestedByItsOwnFactoryAndAddAndYesNever()
    {
        var setup = SuggestedCommand.Setup("build@10.20.30.40", MachineOs.Windows, "LANQ_KEY")!;
        Assert.Equal(["lanq", "makeready", "--ssh", "build@10.20.30.40", "--os", "windows", "--setup", "--key-env", "LANQ_KEY"], setup.Argv);
        Assert.False(setup.SafeToRunUnattended);
        Assert.Equal("Next: (for the developer) why: lanq makeready --ssh build@10.20.30.40 --os windows --setup --key-env LANQ_KEY",
            Assert.Single(NextSteps.Lines([NextSteps.Step(NextAction.AskDeveloper, "why", setup)])));

        // A banned word passed as a value never becomes a suggestion.
        Assert.Null(SuggestedCommand.Setup("build@10.20.30.40", MachineOs.Linux, "--add"));
        Assert.Null(SuggestedCommand.Setup("--yes", MachineOs.Linux, null));
        Assert.Null(SuggestedCommand.Makeready(null, null, "build@10.20.30.40", MachineOs.Linux, "--setup"));
        Assert.Null(SuggestedCommand.Makeready("--add", null));
        Assert.Null(SuggestedCommand.Doctor(remote: true, "--yes"));

        // No step anywhere carries --add or --yes; --setup only on that one command.
        foreach (var s in EveryStep().Where(s => s.Argv is not null))
        {
            var own = Own(s.Argv!);
            Assert.DoesNotContain("--add", own);
            Assert.DoesNotContain("--yes", own);
            Assert.True(!own.Contains("--setup") || (own[0] == "makeready" && !s.SafeToRunUnattended));
        }

        Assert.Contains(EveryStep(), s => s.Argv?.Contains("--setup") == true);
    }

    private async Task<(int Code, string Stdout, string Stderr)> Run(PoolSsh ssh, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.Run(
            ParsedArgs.Parse(["run", .. args]), ConfigLoader.Parse(ShardTests.PoolYaml), ssh, new RunnerTests.FakePayloads(""), new HealthProbe(ssh, TimeProvider.System),
            Log, _dir, _dir, o, e, CancellationToken.None);
        return (code, o.ToString(), e.ToString());
    }

    private static string[] Lines(string text) => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];

    [Theory(Timeout = TestTimeout)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextLinesComeBeforeTheStatusLine(bool json)
    {
        var slow = new PoolSsh { OnExecute = (_, _) => Task.FromResult(new SshResult(124, "", TimedOut: true)) };
        var (code, stdout, stderr) = await Run(slow, [.. json ? new[] { "--json" } : [], "--timeout", "30", "--needs", "node", "--", "npx", "jest"]);

        Assert.Equal(124, code);
        var lines = Lines(stderr);
        Assert.StartsWith("[lanq] TimedOut: ", lines[^1], StringComparison.Ordinal); // the status line stays last
        var next = lines.Where(l => l.StartsWith("[lanq] Next: ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, next.Count);
        Assert.StartsWith("[lanq] Next: the job timed out after 30 min", next[0], StringComparison.Ordinal);
        Assert.EndsWith(": lanq run" + (json ? " --json" : "") + " --timeout 60 --needs node --no-wake -- npx jest", next[1], StringComparison.Ordinal);
        Assert.True(Array.IndexOf(lines, next[1]) < lines.Length - 1);
        Assert.DoesNotContain("Next:", stdout, StringComparison.Ordinal);
        if (json)
        {
            using var doc = JsonDocument.Parse(stdout);
            Assert.Equal(["fix_code", "retry"], doc.RootElement.GetProperty("nextSteps").EnumerateArray().Select(s => s.GetProperty("action").GetString()));
        }
        else
        {
            Assert.StartsWith("[lanq] log: ", lines[^2], StringComparison.Ordinal); // Next lines, then the log line, then the status line
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ACompletedRunPrintsNoNextLine()
    {
        var (code, stdout, stderr) = await Run(new PoolSsh(), "--needs", "node", "--", "npx", "jest");
        Assert.Equal(0, code);
        Assert.DoesNotContain("Next:", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Next:", stdout, StringComparison.Ordinal);

        var (_, json, jsonErr) = await Run(new PoolSsh(), "--json", "--needs", "node", "--", "npx", "jest");
        Assert.DoesNotContain("Next:", jsonErr, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(json);
        var step = Assert.Single(doc.RootElement.GetProperty("nextSteps").EnumerateArray());
        Assert.Equal("none", step.GetProperty("action").GetString());
        Assert.False(step.TryGetProperty("argv", out _));
        Assert.False(step.TryGetProperty("command", out _));
        Assert.False(step.GetProperty("safeToRunUnattended").GetBoolean());
    }

    /// <summary>build-01 ok, build-02 paused, build-03 down (connection refused).</summary>
    private static (FleetConfig Config, FakeSsh Ssh) Fleet()
    {
        var config = T.Config();
        config.Machines[1].AllowedHours = null;
        config.Machines.Add(new Machine { Name = "build-03", Os = MachineOs.Linux, Ssh = "user@build-03.lan", WorkRoot = "/srv/lanq", Capabilities = ["dotnet"] });
        var ssh = new FakeSsh
        {
            Handler = (m, _) => m.Name switch
            {
                "build-01" => new SshResult(0, "load=0.25\ndisk_free_gb=120.5\nslots_used=1\nmem_free_gb=3.5\ntool=ok\npaused=0"),
                "build-02" => new SshResult(0, "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\npaused=1"),
                _ => new SshResult(255, "ssh: connect to host build-03.lan port 22: Connection refused\u001b[2J"),
            },
        };
        return (config, ssh);
    }

    [Fact]
    public async Task HealthTextStdoutIsUnchanged()
    {
        var (config, ssh) = Fleet();
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(0, await Cli.Health(config, new HealthProbe(ssh, TimeProvider.System), o, e, CancellationToken.None));

        var stdout = Lines(o.ToString());
        Assert.Equal(3, stdout.Length); // one line per machine, nothing else
        Assert.StartsWith("build-01       OK    load 0.25", stdout[0], StringComparison.Ordinal);
        Assert.StartsWith("build-02       HOLD  load 0.10", stdout[1], StringComparison.Ordinal);
        Assert.EndsWith("(paused on the machine)", stdout[1], StringComparison.Ordinal);
        Assert.StartsWith("build-03       DOWN  ", stdout[2], StringComparison.Ordinal);
        Assert.Contains("Connection refused", stdout[2], StringComparison.Ordinal);
        Assert.DoesNotContain("Next:", o.ToString(), StringComparison.Ordinal);

        // The next steps go to stderr: none for the ok machine, wait for the paused one, the developer and lanq doctor for the down one.
        var next = Lines(e.ToString()).Where(l => l.StartsWith("Next: ", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, next.Count);
        Assert.StartsWith("Next: build-02 is paused", next[0], StringComparison.Ordinal);
        Assert.StartsWith("Next: build-03 is down: ", next[1], StringComparison.Ordinal);
        Assert.EndsWith(": lanq doctor", next[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthRowsCarryNextSteps()
    {
        var (config, ssh) = Fleet();
        var o = new StringWriter();
        Assert.Equal(0, await Cli.Health(config, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None, json: true, configPath: "fleet.yml"));
        using var doc = JsonDocument.Parse(o.ToString());
        List<string?[]> actions = [.. doc.RootElement.EnumerateArray().Select(r => r.GetProperty("nextSteps").EnumerateArray().Select(s => s.GetProperty("action").GetString()).ToArray())];
        Assert.Equal([["none"], ["wait"], ["ask_developer", "run_doctor"]], actions);
        var doctor = doc.RootElement[2].GetProperty("nextSteps")[1];
        Assert.Equal("lanq doctor --config fleet.yml", doctor.GetProperty("command").GetString());
        Assert.True(doctor.GetProperty("safeToRunUnattended").GetBoolean());

        // With --needs every row carries them too: a machine that lacks a capability is for the developer, whatever its state.
        var n = new StringWriter();
        Assert.Equal(0, await Cli.Health(config, new HealthProbe(ssh, TimeProvider.System), n, new StringWriter(), CancellationToken.None, json: true, needs: new JobNeeds(["node"], null, false)));
        using var needs = JsonDocument.Parse(n.ToString());
        Assert.Equal(
            ["none", "ask_developer", "ask_developer"],
            needs.RootElement.EnumerateArray().Select(r => r.GetProperty("nextSteps")[0].GetProperty("action").GetString()));
        Assert.Contains("can never take such a job", needs.RootElement[1].GetProperty("nextSteps")[0].GetProperty("reason").GetString(), StringComparison.Ordinal);

        // A machine that looks asleep: the wake command is named in the reason, never suggested as a command.
        var asleep = Assert.Single(NextSteps.ForHealthRow(config.Machines[0], Down("connect timeout", asleep: true), null, null));
        Assert.Equal("ask_developer", asleep.Action);
        Assert.Contains("lanq wake build-01 --wait", asleep.Reason, StringComparison.Ordinal);
        Assert.Null(asleep.Argv);
    }

    [Theory]
    [InlineData("run", "--json", "--bogus", "--", "npx", "jest")]
    [InlineData("run", "--json", "--shards", "2", "--bogus", "--", "npx", "jest")]
    [InlineData("run", "--json", "--dry-run", "--bogus", "--", "npx", "jest")]
    [InlineData("health", "--json", "--bogus")]
    [InlineData("makeready", "--json", "--bogus")]
    [InlineData("wake", "--json", "--bogus")]
    [InlineData("init", "--json", "--bogus")]
    public async Task EveryErrorObjectCarriesNextSteps(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(2, await Cli.RunAsync(args, o, e, CancellationToken.None));
        using var doc = JsonDocument.Parse(o.ToString());
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
        var step = Assert.Single(doc.RootElement.GetProperty("nextSteps").EnumerateArray());
        Assert.Equal("fix_command", step.GetProperty("action").GetString());
        Assert.Contains("--bogus", step.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Contains("Next: fix the command line: ", e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorObjectsOfEachCommandCarryNextSteps()
    {
        // wake: an unknown machine and one without a wake: setting (no packet is sent for either); makeready: a held machine.
        var (config, ssh) = Fleet();
        var waker = new WakeService(new UdpMagicPacketSender(), TimeProvider.System);
        var o = new StringWriter();
        Assert.Equal(2, await Cli.Wake(Args("wake", "nope", "--json"), config, waker, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None));
        Assert.Equal("fix_command", Steps(o)[0].GetProperty("action").GetString());
        o = new StringWriter();
        Assert.Equal(2, await Cli.Wake(Args("wake", "build-01", "--json"), config, waker, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None));
        Assert.Equal("ask_developer", Steps(o)[0].GetProperty("action").GetString());

        o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(75, await Cli.Makeready(Args("makeready", "build-02", "--json", "--config", "fleet.yml"), () => config, ssh, new HealthProbe(ssh, TimeProvider.System), o, e, CancellationToken.None));
        var held = Steps(o)[0];
        Assert.Equal("wait", held.GetProperty("action").GetString());
        Assert.Equal("lanq makeready build-02 --config fleet.yml", held.GetProperty("command").GetString());
        Assert.False(held.GetProperty("safeToRunUnattended").GetBoolean());
        Assert.False(o.ToString().Contains("Next:", StringComparison.Ordinal));
        Assert.Contains("Next: (for the developer) the machine is paused", e.ToString(), StringComparison.Ordinal);

        static List<JsonElement> Steps(StringWriter stdout)
        {
            using var doc = JsonDocument.Parse(stdout.ToString());
            return [.. doc.RootElement.GetProperty("nextSteps").EnumerateArray().Select(s => s.Clone())];
        }
    }

    [Fact]
    public void ReasonsAreSanitised()
    {
        var evil = "a\u001b[2Jb\u202ec\nd";
        var config = T.Config();
        var all = new List<NextStepJson>();
        all.AddRange(NextSteps.ForHealthRow(config.Machines[0], Down(evil), null, null));
        all.AddRange(NextSteps.ForRun(PlainRun, Outcome("Rejected", 2, reject: RejectKind.Policy) with { Message = evil }));
        all.AddRange(NextSteps.ForRun(PlainRun, Outcome("NoCapacity", 75, retryable: false, excluded: [new ExcludedMachine(evil, "missing_capability", evil, true)])));
        all.AddRange(NextSteps.ForWake("InfraError", evil, null));
        all.AddRange(NextSteps.ArgumentsError(evil));
        Assert.All(all, s => Assert.Equal(JobRunner.SafeLine(s.Reason), s.Reason));
        Assert.All(all, s => Assert.DoesNotContain(s.Reason, c => char.IsControl(c) || c == '\u202e'));
        Assert.All(NextSteps.Lines(all), l => Assert.Equal(JobRunner.SafeLine(l), l));
    }
}
