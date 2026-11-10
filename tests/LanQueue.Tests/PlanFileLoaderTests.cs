using LanQueue.Plan;
using Xunit;

namespace LanQueue.Tests;

/// <summary>lanq.yml (version 1): strict keys with lines, defaults, command forms, and every job expanded to its exact lanq run line.</summary>
public sealed class PlanFileLoaderTests : IDisposable
{
    /// <summary>The design's example plan: a mixed pool of builds and test shards with a serial tail.</summary>
    internal const string Example = """
        version: 1
        defaults:
          needs: [dotnet]
          timeout: 60
        jobs:
          build:
            command: dotnet build -c Release -warnaserror
          unit:
            command: [sh, scripts/lanq/test-shard.sh, tests/Unit, "{shard}", "{shards}"]
            size: [light, medium]
            shards: auto
            target_minutes: 5
            shard_retries: 1
            fail_fast: false
          integration:
            command: [sh, scripts/lanq/test-shard.sh, tests/Integration, "{shard}/{shards}"]
            min_size: heavy
            shards: 4
            on: [big-01, build-01]
            timeout: 120
            fetch: ["TestResults/**/*.trx"]
          serial:
            command: [dotnet, test, tests/Integration, --filter, "Category=Serial"]
            min_size: heavy
            after: [integration]
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-planfile-" + Guid.NewGuid().ToString("N"));

    public PlanFileLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static PlanFileException Refused(string yaml, string? contains = null)
    {
        var ex = Assert.Throws<PlanFileException>(() => PlanFileLoader.Parse(yaml));
        Assert.StartsWith("lanq.yml", ex.Message, StringComparison.Ordinal);
        if (contains is not null)
        {
            Assert.Contains(contains, ex.Message, StringComparison.Ordinal);
        }

        return ex;
    }

    [Fact]
    public void MinimalPlanLoads()
    {
        var plan = PlanFileLoader.Parse("version: 1\njobs:\n  build:\n    command: dotnet build\n");

        Assert.Equal((1, "lanq.yml"), (plan.Version, plan.Path));
        var job = Assert.Single(plan.Jobs);
        Assert.Equal(("build", 3, 0), (job.Name, job.Line, job.Index));
        Assert.Equal(["run", "--", "dotnet", "build"], job.RunArgs);
        Assert.Equal("lanq run -- dotnet build", job.RunCommand);
        Assert.Equal(["dotnet", "build"], job.Args.Argv);
        Assert.Equal(".", job.Args.Directory);
        Assert.Empty(job.After);
        Assert.Empty(plan.Warnings);
        Assert.Same(job, plan.Job("BUILD"));
    }

    [Fact]
    public void TheExampleExpandsAsDocumented()
    {
        var plan = PlanFileLoader.Parse(Example);
        Assert.Equal(["build", "unit", "integration", "serial"], plan.Jobs.Select(j => j.Name));
        Assert.Equal(
            "lanq run --needs dotnet --min-size heavy --on big-01,build-01 --shards 4 --timeout 120 --fetch 'TestResults/**/*.trx' -- sh scripts/lanq/test-shard.sh tests/Integration '{shard}/{shards}'",
            plan.Job("integration")!.RunCommand);
        Assert.Equal(
            "lanq run --needs dotnet --size light,medium --shards auto --target-minutes 5 --shard-retries 1 --timeout 60 -- sh scripts/lanq/test-shard.sh tests/Unit '{shard}' '{shards}'",
            plan.Job("unit")!.RunCommand);
        Assert.Equal(["integration"], plan.Job("serial")!.After);
        var unit = plan.Job("unit")!.Args;
        Assert.Equal((null, 1, false, 5), (unit.Shards!.Count, unit.Shards.Retries, unit.Shards.FailFast, unit.Shards.TargetMinutes));
        Assert.Equal(["TestResults/**/*.trx"], plan.Job("integration")!.Args.Fetch);
        Assert.Equal(120, plan.Job("integration")!.Args.TimeoutMinutes);
        Assert.Equal(60, plan.Job("build")!.Args.TimeoutMinutes);
    }

    [Theory]
    [InlineData("jobs:\n  build:\n    command: dotnet build\n", "version: is required")]
    [InlineData("version: 2\njobs:\n  build:\n    command: dotnet build\n", "this lanq reads lanq.yml version 1; the file says 2: update lanq (line 1)")]
    [InlineData("version: 2\nfuture_key: x\n", "the file says 2: update lanq")] // a newer file is told apart before its keys are judged
    [InlineData("version: \"1\"\njobs:\n  build:\n    command: dotnet build\n", null)]
    [InlineData("version: one\njobs:\n  build:\n    command: dotnet build\n", "version must be the number 1 (line 1)")]
    [InlineData("version: 1.0\njobs:\n  build:\n    command: dotnet build\n", "version must be the number 1")]
    [InlineData("version:\njobs:\n  build:\n    command: dotnet build\n", "version must be the number 1")]
    [InlineData("version: [1]\njobs:\n  build:\n    command: dotnet build\n", "version must be the number 1")]
    [InlineData("version: 0\njobs:\n  build:\n    command: dotnet build\n", "the file says 0")]
    public void VersionIsRequiredAndMustBeOne(string yaml, string? error)
    {
        if (error is null)
        {
            Assert.Equal(1, PlanFileLoader.Parse(yaml).Version); // quoted "1" is the same word
            return;
        }

        Refused(yaml, error);
    }

    [Theory]
    [InlineData("version: 1\njob:\n  build:\n    command: dotnet build\n", "lanq.yml: unknown key 'job' at the top (line 2); did you mean 'jobs'?")]
    [InlineData("version: 1\njobs:\n  build:\n    comand: dotnet build\n", "lanq.yml: job 'build' (line 3): unknown key 'comand' in a job (line 4); did you mean 'command'?")]
    [InlineData("version: 1\njobs:\n  build:\n    command: dotnet build\n    Needs: [dotnet]\n", "unknown key 'Needs' in a job (line 5); did you mean 'needs'?")]
    [InlineData("version: 1\ndefaults:\n  shards: 4\njobs:\n  build:\n    command: dotnet build\n", "lanq.yml: defaults: unknown key 'shards' in defaults: (line 3)")]
    [InlineData("version: 1\ndefaults:\n  min-size: heavy\njobs:\n  build:\n    command: dotnet build\n", "did you mean 'min_size'?")]
    [InlineData("version: 1\njobs:\n  build:\n    command: dotnet build\n    target-minutes: 5\n", "did you mean 'target_minutes'?")]
    public void UnknownKeyNamesKeyJobLineAndSuggests(string yaml, string expected)
    {
        var ex = Refused(yaml, expected);
        Assert.Contains("Keys here: ", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hosts: [10.20.30.41]")]
    [InlineData("ssh: build@10.20.30.41")]
    [InlineData("key_env: LANQ_KEY")]
    [InlineData("env: {TOKEN: x}")]
    [InlineData("password: hunter2")]
    [InlineData("path_prepend: [/opt/bin]")]
    [InlineData("wake: {mac: \"02:00:00:00:00:41\"}")]
    [InlineData("priority: normal")]
    public void HostsSecretsAndEnvKeysAreUnknownKeys(string line)
    {
        foreach (var yaml in new[]
        {
            $"version: 1\njobs:\n  build:\n    command: dotnet build\n    {line}\n",
            $"version: 1\n{line}\njobs:\n  build:\n    command: dotnet build\n",
            $"version: 1\ndefaults:\n  {line}\njobs:\n  build:\n    command: dotnet build\n",
        })
        {
            var ex = Refused(yaml, "unknown key '" + line.Split(':')[0] + "'");
            Assert.Contains("belong in your machines.yml", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal); // the value is never echoed
        }
    }

    [Theory]
    [InlineData("  build:\n    command: dotnet build\n  Build:\n    command: dotnet test\n", "job 'Build' (line 5) has the same name as job 'build' (line 3)")]
    [InlineData("  build:\n    command: dotnet build\n  build:\n    command: dotnet test\n", "a key may appear only once")]
    public void DuplicateJobNameIgnoringCaseIsRefused(string jobs, string expected) =>
        Refused("version: 1\njobs:\n" + jobs, expected);

    [Theory]
    [InlineData("build", true)]
    [InlineData("Build_2-x", true)]
    [InlineData("9lives", true)]
    [InlineData("a234567890123456789012345678901b", true)] // 32 characters
    [InlineData("a2345678901234567890123456789012c", false)] // 33
    [InlineData("-build", false)]
    [InlineData("_build", false)]
    [InlineData("build.all", false)]
    [InlineData("\"build all\"", false)]
    [InlineData("\"bü\"", false)]
    public void JobNameRules(string name, bool ok)
    {
        var yaml = $"version: 1\njobs:\n  {name}:\n    command: dotnet build\n";
        if (ok)
        {
            Assert.Single(PlanFileLoader.Parse(yaml).Jobs);
        }
        else
        {
            Refused(yaml, "must start with a letter or digit and hold only letters, digits, _ and - (at most 32 characters)");
        }
    }

    [Fact]
    public void CommandListAndSimpleString()
    {
        var plan = PlanFileLoader.Parse("""
            version: 1
            jobs:
              list:
                command: [npx, jest, "--shard={shard}/{shards}", "a b", "it's"]
              text:
                command: "npx   jest --shard={shard}/{shards} --ci"
              flow:
                command:
                  - dotnet
                  - test
            """);
        Assert.Equal(["npx", "jest", "--shard={shard}/{shards}", "a b", "it's"], plan.Job("list")!.Args.Argv);
        Assert.Null(plan.Job("list")!.RunCommand); // a single quote cannot be written for both shells; the argv stays exact
        Assert.Equal(["npx", "jest", "--shard={shard}/{shards}", "--ci"], plan.Job("text")!.Args.Argv);
        Assert.Equal(["dotnet", "test"], plan.Job("flow")!.Args.Argv);

        Refused("version: 1\njobs:\n  a:\n    command: []\n", "command: is empty");
        Refused("version: 1\njobs:\n  a:\n    command: \"  \"\n", "command: is empty");
        Refused("version: 1\njobs:\n  a:\n    command: {x: y}\n", "command: must be a list");
        Refused("version: 1\njobs:\n  a:\n    command: [dotnet, [test]]\n", "command: each entry must be a single value");
        Refused("version: 1\njobs:\n  a:\n    dir: src\n", "job 'a' (line 3): command: is required");
    }

    [Theory]
    [InlineData("dotnet test --filter \"Category=Unit\"", "\"Category=Unit\"")]
    [InlineData("sh -c 'echo $HOME'", "'echo")]
    [InlineData("npm test && rm -rf /", "&&")]
    [InlineData("dotnet test > out.txt", ">")]
    [InlineData("dotnet test --filter Category=Unit|Category=Arch", "Category=Unit|Category=Arch")]
    [InlineData("echo $LANQ_SHARD", "$LANQ_SHARD")]
    [InlineData("npx jest {shard*}", "{shard*}")]
    [InlineData("npx jest *.test.js", "*.test.js")]
    public void ShellCharactersNeedTheListForm(string command, string token)
    {
        // A YAML single-quoted scalar holds the text exactly ('' is one quote).
        var yaml = "version: 1\njobs:\n  a:\n    command: '" + command.Replace("'", "''", StringComparison.Ordinal) + "'\n";
        var ex = Refused(yaml, "job 'a' (line 3): write command as a list (quotes, $ and other shell characters are not interpreted): '" + token + "' is not a plain word");
        Assert.Contains("(line 4)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterUnknownSelfAndCycle()
    {
        Refused("version: 1\njobs:\n  a:\n    command: dotnet build\n    after: [b]\n", "job 'a' (line 3): after: names 'b', which is not a job in lanq.yml (jobs: a)");
        Refused("version: 1\njobs:\n  a:\n    command: dotnet build\n    after: a\n", "job 'a' (line 3): after: names the job itself");
        var cycle = Refused(
            "version: 1\njobs:\n  a:\n    command: dotnet build\n    after: [c]\n  b:\n    command: dotnet test\n    after: [a]\n  c:\n    command: npm test\n    after: b\n",
            "after: makes a cycle: a -> c -> b -> a");
        Assert.Contains("(line 3)", cycle.Message, StringComparison.Ordinal);

        // after: matches ignoring case and keeps the file's spelling; a comma string works as a list.
        var plan = PlanFileLoader.Parse("version: 1\njobs:\n  Build:\n    command: dotnet build\n  Lint:\n    command: npm test\n  test:\n    command: dotnet test\n    after: build, LINT\n");
        Assert.Equal(["Build", "Lint"], plan.Job("test")!.After);
    }

    [Fact]
    public void DefaultsApplyAndJobValuesReplaceThem()
    {
        var plan = PlanFileLoader.Parse("""
            version: 1
            defaults:
              needs: [dotnet, linux]
              dir: src
              mem: 2
              size: [light, medium]
              on: [build-01]
              timeout: 30
            jobs:
              plain:
                command: dotnet build
              own:
                command: dotnet test
                needs: node
                dir: .
                mem: 4
                min_size: heavy
                on: big-01
                timeout: 90
            """);
        var plain = plan.Job("plain")!.Args;
        Assert.Equal(["dotnet", "linux"], plain.Needs);
        Assert.Equal(("src", 2.0, 30), (plain.Directory, plain.MemGb, plain.TimeoutMinutes));
        Assert.Equal("--size light,medium", plain.Size!.Text);
        Assert.Equal(["build-01"], plain.On);

        // Replaced, never merged: needs is node only, the size rule is the job's min_size alone, on is big-01 only.
        var own = plan.Job("own")!.Args;
        Assert.Equal(["node"], own.Needs);
        Assert.Equal((".", 4.0, 90), (own.Directory, own.MemGb, own.TimeoutMinutes));
        Assert.Equal("--min-size heavy", own.Size!.Text);
        Assert.Equal(["big-01"], own.On);
        Assert.Equal(["run", "--needs", "node", "--mem", "4", "--min-size", "heavy", "--on", "big-01", "--timeout", "90", "--", "dotnet", "test"], plan.Job("own")!.RunArgs);
    }

    /// <summary>
    /// Parity: each job is exactly the lanq run line it expands to. The job's arguments parse to the same ParsedArgs as typing that line, and a
    /// value the CLI refuses is refused with the CLI's own words after "lanq.yml: job 'x' (line N): ".
    /// </summary>
    [Theory]
    [InlineData("timeout: 0", "--timeout must be a whole number of minutes from 1 to 10080 (one week).")]
    [InlineData("timeout: soon", "--timeout must be a whole number of minutes from 1 to 10080 (one week).")]
    [InlineData("mem: 0", "--mem must be the available memory a machine needs")]
    [InlineData("mem: lots", "--mem must be the available memory a machine needs")]
    [InlineData("shards: 65", "--shards must be a whole number from 1 to 64, or auto.")]
    [InlineData("shards: some", "--shards must be a whole number from 1 to 64, or auto.")]
    [InlineData("shard_retries: 1", "--shard-retries and --fail-fast need --shards.")]
    [InlineData("fail_fast: true", "--shard-retries and --fail-fast need --shards.")]
    [InlineData("shards: 2\n    shard_retries: 9", "--shard-retries must be a whole number from 0 to 3.")]
    [InlineData("shards: 4\n    target_minutes: 5", "--target-minutes goes with --shards auto")]
    [InlineData("shards: auto\n    target_minutes: 0", "--target-minutes must be a whole number of minutes from 1 to 1440.")]
    [InlineData("size: [light, huge]", "--size takes light, medium or heavy")]
    [InlineData("size: [unknown]", "--size takes light, medium or heavy")]
    [InlineData("min_size: [heavy]", "min_size: must be a single value")]
    [InlineData("size: light\n    min_size: heavy", "--size and --min-size do not go together")]
    [InlineData("fetch: \"../secrets/*\"", "--fetch")]
    [InlineData("fail_fast: maybe", "fail_fast: must be true or false")]
    [InlineData("needs: {a: b}", "needs: must be a list or a comma-separated string")]
    [InlineData("on: \" , \"", "on: names nothing")]
    public void EachJobExpandsToTheRunLineAndUsesTheSameParser(string keys, string cliMessage)
    {
        var yaml = $"version: 1\njobs:\n  first:\n    command: dotnet build\n  integration:\n    command: [dotnet, test]\n    {keys}\n";
        var ex = Refused(yaml, cliMessage);
        Assert.StartsWith("lanq.yml: job 'integration' (line 5): ", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("needs: [dotnet, linux]\n    dir: src\n    mem: 2.5\n    timeout: 30", "run --needs dotnet,linux --dir src --mem 2.5 --timeout 30 -- dotnet test")]
    [InlineData("min_size: medium\n    on: [big-01, micro-01]\n    shards: auto\n    target_minutes: 5", "run --min-size medium --on big-01,micro-01 --shards auto --target-minutes 5 -- dotnet test")]
    [InlineData("shards: 3\n    shard_retries: 2\n    fail_fast: true\n    fetch: [\"a/*.trx\", \"b/**\"]", "run --shards 3 --shard-retries 2 --fail-fast --fetch a/*.trx --fetch b/** -- dotnet test")]
    [InlineData("size: medium,heavy\n    fail_fast: false\n    shards: 2", "run --size medium,heavy --shards 2 -- dotnet test")]
    public void EachJobParsesLikeItsRunLine(string keys, string line)
    {
        var job = PlanFileLoader.Parse($"version: 1\njobs:\n  integration:\n    command: [dotnet, test]\n    {keys}\n").Jobs[0];
        var typed = ParsedArgs.Parse(line.Split(' '));

        Assert.Equal(line.Split(' '), job.RunArgs);
        Assert.Equal(typed.Needs, job.Args.Needs);
        Assert.Equal((typed.Directory, typed.MemGb, typed.TimeoutMinutes, typed.Size?.Text), (job.Args.Directory, job.Args.MemGb, job.Args.TimeoutMinutes, job.Args.Size?.Text));
        Assert.Equal(typed.On, job.Args.On);
        Assert.Equal(typed.Shards, job.Args.Shards);
        Assert.Equal(typed.Fetch, job.Args.Fetch);
        Assert.Equal(typed.Argv, job.Args.Argv);
    }

    [Fact]
    public void MaxJobsAndFileSize()
    {
        string Jobs(int n) => "version: 1\njobs:\n" + string.Concat(Enumerable.Range(1, n).Select(i => $"  job-{i}:\n    command: dotnet build\n"));
        Assert.Equal(32, PlanFileLoader.Parse(Jobs(32)).Jobs.Count);
        Refused(Jobs(33), "jobs: holds 33 jobs; a plan file may have at most 32");
        Refused("version: 1\njobs: {}\n", "jobs: is required and must hold at least one job");
        Refused("version: 1\n", "jobs: is required");
        Refused("version: 1\njobs: [build]\n", "jobs: must be a mapping");

        var big = Jobs(1) + "# " + new string('x', PlanFileLoader.MaxBytes) + "\n";
        Refused(big, "a plan file may be at most 256 KB");
        var file = Path.Combine(_dir, "lanq.yml");
        File.WriteAllText(file, big);
        Assert.Contains("at most 256 KB", Assert.Throws<PlanFileException>(() => PlanFileLoader.Load(file)).Message, StringComparison.Ordinal);
        File.WriteAllText(file, Jobs(2));
        Assert.Equal(file, PlanFileLoader.Load(file).Path);
    }

    [Theory]
    [InlineData("version: 1\njobs:\n  a:\n    command: dotnet build\n---\nversion: 1\n", "2 YAML documents")]
    [InlineData("", "is empty")]
    [InlineData("# nothing\n", "is empty")]
    [InlineData("- version: 1\n", "must be a mapping with version: and jobs: keys (line 1)")]
    [InlineData("version: 1\njobs:\n  a:\n    command: !!binary aGk=\n", "the YAML tag 'tag:yaml.org,2002:binary' is not supported (line 4)")]
    [InlineData("version: 1\njobs:\n  a:\n    command: !shell dotnet build\n", "is not supported")]
    [InlineData("version: 1\njobs:\n  a: &x\n    command: dotnet build\n    after: *x\n", "refers to itself through a YAML alias")]
    [InlineData("version: 1\njobs:\n  a:\n    command: &c [dotnet, *c]\n", "refers to itself through a YAML alias")]
    [InlineData("version: 1\njobs:\n  a:\n    command:\n", "'jobs.a.command' has no value")]
    [InlineData("version: 1\njobs:\n  a:\n    command: [dotnet, ~]\n", "has an empty entry")]
    [InlineData("version: 1\njobs:\n  a:\n    command: dotnet build\n  <<: {b: {command: dotnet test}}\n", "merge keys (<<) are not supported")]
    [InlineData("version: 1\njobs:\n  a:\n    <<: {command: dotnet test}\n", "merge keys (<<) are not supported")]
    [InlineData("version: 1\njobs:\n  a:\n    command: [dotnet\n", "not valid YAML")]
    public void OneDocumentOnlyAndTagsAndAliasesAsConfig(string yaml, string expected) => Refused(yaml, expected);

    [Fact]
    public void AnchorsAndStandardTagsStillWork()
    {
        var plan = PlanFileLoader.Parse("version: !!int 1\njobs:\n  a:\n    command: &cmd [dotnet, test]\n    needs: !!str dotnet\n  b:\n    command: *cmd\n    dir: src\n");
        Assert.Equal(["dotnet", "test"], plan.Job("b")!.Args.Argv);
        Assert.Equal(["dotnet"], plan.Job("a")!.Args.Needs);
    }

    [Fact]
    public void WarningsForDuplicateCommandsAndShardsWithoutPlaceholders()
    {
        var plan = PlanFileLoader.Parse("""
            version: 1
            jobs:
              one:
                command: dotnet test
              two:
                command: [dotnet, test]
                dir: ./
              three:
                command: dotnet test
                dir: src
              unit:
                command: [sh, scripts/lanq/test-shard.sh]
                shards: 3
              ok:
                command: [npx, jest, "--shard={shard}/{shards}"]
                shards: auto
            """);
        Assert.Equal(
        [
            "jobs 'one' and 'two' run the same command in the same folder (dotnet test in .)",
            "job 'unit' has shards: but its command has no {shard} or {shards}: each shard runs the same command unless a script reads LANQ_SHARD",
        ],
            plan.Warnings);
        Assert.Empty(PlanFileLoader.Parse(Example).Warnings);
    }

    [Fact]
    public void PlanFileFoundAtTheRepositoryTopOrGiven()
    {
        var repo = Path.Combine(_dir, "app");
        var sub = Path.Combine(repo, "src", "lib");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        var missing = Assert.Throws<PlanFileException>(() => PlanFileLoader.Locate(null, sub));
        Assert.Equal($"No lanq.yml in {repo.Replace('\\', '/')} (the repository top). Write one (see docs/PLANS.md) or pass --plan-file PATH.", missing.Message);

        File.WriteAllText(Path.Combine(repo, "lanq.yml"), "version: 1\njobs:\n  a:\n    command: dotnet build\n");
        Assert.Equal(Path.Combine(repo, "lanq.yml"), PlanFileLoader.Locate(null, sub));

        File.WriteAllText(Path.Combine(sub, "other.yml"), "version: 1\n");
        Assert.Equal(Path.Combine(sub, "other.yml"), PlanFileLoader.Locate("other.yml", sub));
        Assert.Contains("Plan file not found: nope.yml", Assert.Throws<PlanFileException>(() => PlanFileLoader.Locate("nope.yml", sub)).Message, StringComparison.Ordinal);
        Assert.StartsWith("other.yml: ", Assert.Throws<PlanFileException>(() => PlanFileLoader.Load(Path.Combine(sub, "other.yml"))).Message, StringComparison.Ordinal);
    }
}
