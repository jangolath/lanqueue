# Parallel development: many worktrees, one quiet dev machine

Several developers or AI coding agents can work on one PC at once, each in its own git worktree, while every build,
test run and format check goes to the lanq pool. The dev PC only edits files and runs git. It stays free for the person
who uses it and for whatever else they are building there.

```bash
git worktree add ../app-parser -b fix/parser       # one worktree per agent or task
cd ../app-parser
lanq run --json --no-wake --needs dotnet -- dotnet test --filter "FullyQualifiedName~Parser"
```

This page covers how to lay out the worktrees, how to send every kind of check to the pool, how many tasks a pool can
carry, what sharding does and does not buy you here, the pitfalls, and a short rule set for AI agents. Setting up the
pool itself is in [TEAM-QUICKSTART.md](TEAM-QUICKSTART.md); plan files are in [PLANS.md](PLANS.md) and sharding in
[SHARDING.md](SHARDING.md).

## Worktrees: one per agent or task

```bash
git worktree add ../app-parser  -b fix/parser
git worktree add ../app-docs    -b docs/install
git worktree add ../app-export  -b feature/export
git worktree list
```

- **lanq needs no setup per worktree.** Each `lanq run` snapshots the worktree it is started from: its HEAD plus
  uncommitted and untracked, non-ignored files. Every run gets its own job id, temporary ref and snapshot, so runs
  from several worktrees of one repository at the same time do not get in each other's way. The per-repository
  `.lanq/` folder (fetched files) is per worktree too.
- **One worktree, one owner.** Never switch branches, reset, rebase or stash in a worktree someone else (a person or
  another agent) is using: their next snapshot would ship your state, not theirs. Make a new worktree instead.
- **Clean up when a task is done:** `git worktree remove ../app-parser` (and `git worktree prune` for folders deleted
  by hand).

## Offload everything

The point is that nothing heavy runs on the dev PC. That means the quick checks too, not only the full suite.

### Targeted tests while you work

Run only the tests that cover the change, as often as you like:

```bash
lanq run --json --log-tail 40 --no-wake --needs dotnet -- dotnet test --filter "FullyQualifiedName~Parser|FullyQualifiedName~Lexer"
```

The quotes are for your shell; lanq receives `FullyQualifiedName~Parser|FullyQualifiedName~Lexer` as one argument.

### Format and lint checks

A check that fails only in CI costs a whole CI round. Run it on the pool before you push:

```bash
lanq run --json --no-wake --needs dotnet -- dotnet format --verify-no-changes
lanq run --json --no-wake --needs dotnet --min-size heavy -- dotnet build -c Release -warnaserror
```

`--min-size heavy` keeps a big build off small machines (see [Routing jobs](../README.md#routing-jobs)).

### The full suite

When the targeted runs pass, run the repository's whole plan once:

```bash
lanq run --plan --json --log-tail 40 --no-wake
```

### Allowlist the checkers up front

lanq runs only commands in `policy.allowed_commands`. The list matches **leading tokens**: an entry `dotnet test`
allows `dotnet test --filter ...`, `dotnet test tests/Unit -c Release` and every other argument after those two
words. So list each tool once, before the agents start:

```yaml
policy:
  allowed_commands:
    - dotnet build
    - dotnet test
    - dotnet format
```

A checker that is not listed is refused (`Rejected`, exit 2, nothing sent). An agent then cannot run it, and must not
add it, so the problem only shows up in CI. Decide which linters, formatters and analysers your CI runs, and allow
the same ones.

### Windows workers and special characters

On a Windows worker, arguments that contain `"`, `<`, `>`, `^` or `%` are refused, because PowerShell 5.1 and
`cmd.exe` would re-parse them. `&` and `|` are allowed only for a native `.exe` (such as `dotnet`), never for a `.cmd`
shim such as `npm`. The full rule is in [SECURITY.md](../SECURITY.md) and the README's
[Security model](../README.md#security-model). A test filter with `|` therefore works with `dotnet test` on Windows; one
that needs `%` or a literal `"` goes to Linux workers only (`--needs linux`), or is rewritten.

## Capacity planning

### Count the slots

Each worker takes at most `max_jobs` jobs at once. Add them up and compare with the number of agents:

```bash
lanq health --json      # freeSlots per machine; the sum is what the pool can take now
```

A pool of two machines with `max_jobs: 2` and `max_jobs: 1` has three slots. Three agents running targeted tests fit;
a fourth waits.

### A plan can take several slots

A plan's jobs run side by side, and a job with `shards:` takes one slot per shard. A plan with a guard job and two
shards can hold three slots at once, which is a whole three-slot pool. So:

1. Run targeted tests while you work. Each takes one slot for a minute or two.
2. Run the full plan only once they pass, before you push or open a pull request.
3. With several agents, expect full plans to queue behind each other. That is fine; a plan that fails fast because of
   a broken targeted test is not.

### NoCapacity means back off

`NoCapacity` (exit 75) with `retryable: true` means every slot is busy. Do not retry in a loop: each attempt probes
every machine. Instead:

- give the run a `--wait` (seconds) so lanq queues it for you, checking every 15 seconds:
  `lanq run --json --no-wake --wait 600 --needs dotnet -- dotnet test`;
- or run the `wait` step in `nextSteps` once, then tell the developer.

`retryable: false` means no machine can ever take the job as asked (a missing capability, a size no machine has):
waiting does not help.

### Waking machines

`--no-wake` keeps lanq from waking a sleeping worker. Use it by default, and always for agents, unless the developer
has said waking is fine. When the pool is too small for the day's work, the developer can wake machines once
(`lanq wake <machine>`) rather than each run doing it.

## A local container worker

A Linux worker in Docker on the dev PC ([WORKERS.md, A Linux worker in Docker on your own
PC](WORKERS.md#a-linux-worker-in-docker-on-your-own-pc)) is handy for Linux coverage, but it runs on the same PC you
are trying to keep quiet. A job there is a heavy job on your PC like any other. Keep it small:

```yaml
  - name: linux-local    # excerpt of the machines.yml entry
    os: linux
    max_jobs: 1          # one job at a time on your own PC
    size: medium         # a declared size wins over the probe, so --min-size heavy work stays off it
```

The setup script's `-Register` step writes these values. Send it Linux-only work (`--needs linux`), and keep `--min-size heavy` on the
big builds so they go to real workers.

## Sharding and speed

Sharding splits one suite over several slots. Whether it saves time depends on where the time goes. In a .NET suite
where restore and compile take most of each run, every shard pays that cost again, so two shards do not halve the wall
time. Sharding then mainly **spreads the load** over machines rather than making the run much faster.

One example, from one .NET repository with about 3,000 tests on two Windows workers (your numbers will differ):

| Run | Wall time |
| --- | --- |
| Targeted test run (a few dozen to a few hundred tests, compile included) | about 40 to 110 s |
| `dotnet format --verify-no-changes` | about 60 s |
| Full suite as a plan: a guard job and 2 shards | about 145 s (median) |
| Full suite as one unsharded job | about 165 s |

The targeted runs are what an agent runs dozens of times, so they matter most. Three agents each ran full suites
repeatedly in parallel with no queueing and no local build at all. Use `lanq stats --json` (`timing` per command) to see
where your own time goes before you pick a shard count; [SHARDING.md](SHARDING.md#choosing-n-from-history---target-minutes)
explains `--target-minutes`.

## Pitfalls

- **A wrapper that swallows `--`.** A PowerShell function or script that forwards its arguments to lanq can lose the
  `--` before the command: the parameter binder treats `--` as its own end-of-parameters marker. lanq then refuses
  the run locally (nothing is sent). Build the arguments as an explicit array and pass that:

  ```powershell
  $lanqArgs = @('run', '--json', '--no-wake', '--needs', 'dotnet', '--', 'dotnet', 'test', '--filter', 'FullyQualifiedName~Parser')
  & lanq $lanqArgs
  ```

  In bash, forward with `"$@"`, which keeps `--`.
- **A `lanq.yml` newer than someone's lanq.** `lanq.yml` is strict: a key an older lanq does not know (such as
  `results:`, added in 0.11.0) is refused, and `lanq run --plan` fails for everyone on that version. When you add a
  newer key, say which lanq version the repository needs (in its README or contributing guide) and have everyone
  update. Until they do, they can still run single jobs with `lanq run -- ...`.
- **Fetched files riding along.** `--fetch` writes to `.lanq/artifacts/` by default, which is never sent. If you pass
  `--fetch-dir`, keep that folder gitignored or under `.lanq/`: otherwise the next snapshot ships the fetched files
  back to the pool (a TRX file also names the worker's user and host).
- **Committed sample keys.** The secret scan allows sample keys in test fixtures (`fixtures/`, `testdata/`,
  `*.example`, `*.sample`), but a private key whose body was changed in place in a tracked text file is refused,
  sample keys included (0.10.1). An agent that edits such a fixture gets `Rejected`. Commit fixture key changes
  yourself, or generate test keys at test time.

## For AI agents

The rules, in short. They match [AGENTS.md](../AGENTS.md) and the snippets in [agents/](agents/).

- Never run heavy builds or tests on the dev PC. Use `lanq run --json --no-wake ...` for every build, test and check.
- Work only in your own worktree. Never check out, switch, reset or stash in a worktree another agent or person uses.
- Run targeted tests while you work; run the full plan (`lanq run --plan --json --no-wake`) once, when they pass.
- On `NoCapacity` with `retryable: true`, use `--wait` or the `wait` step once; never retry in a loop.
- Never edit `machines.yml`, `lanq.yml` or the allowlists. A checker that is refused: ask the developer to allow it.

### Measuring it

An orchestrator that runs several agents can check that the setup pays off. Have each agent append one line per lanq
run to a log in its worktree (keep it gitignored or under `.lanq/`):

```text
utc_start             wall_s  kind      machines         status     tests
2026-01-10T09:12:03Z  58.2    targeted  big-01           Completed  P187/F0
2026-01-10T09:20:41Z  147.0   full      big-01,small-01  Completed  P2950/F0/S113
```

`durationSeconds`, `machine` and `status` come from the `--json` object; with `--results` the counts come from
`results`. `lanq stats --json` gives the same per-command medians from lanq's own job log, across all worktrees.

## Worked example: three agents, three slots

The pool: `big-01` (`max_jobs: 2`) and `small-01` (`max_jobs: 1`), three slots. The repository's `lanq.yml` has a
guard job and a two-shard test job. The developer has allowlisted `dotnet build`, `dotnet test` and `dotnet format`.

```bash
git worktree add ../app-parser -b fix/parser          # agent A
git worktree add ../app-export -b feature/export      # agent B
git worktree add ../app-docs   -b docs/install        # agent C
```

1. A and B each edit code and run targeted tests from their own worktree, about once every few minutes:
   `lanq run --json --no-wake --wait 600 --needs dotnet -- dotnet test --filter "FullyQualifiedName~Parser"`.
   They hold one slot each for a minute or two.
2. C changes documentation and sample code, and runs `dotnet format --verify-no-changes` on the pool: the third slot.
3. A's targeted tests pass. A runs `lanq run --plan --json --no-wake --wait 600`. The guard and two shards need all
   three slots, so the plan's jobs start as B's and C's runs finish; meanwhile B's next targeted run waits within its
   `--wait`.
4. The plan completes. A reports the result and pushes. B and C carry on.

At no point did the dev PC build or test anything: it held three editors, git and three short-lived lanq processes.

## See also

- [AGENTS.md](../AGENTS.md) and [AGENTS-GUIDE.md](AGENTS-GUIDE.md): the agent rules and setup
- [PLANS.md](PLANS.md): `lanq.yml` and `lanq run --plan`
- [SHARDING.md](SHARDING.md): splitting suites, sizing machines
- [WORKERS.md](WORKERS.md): setting up workers, including the Docker Linux worker
