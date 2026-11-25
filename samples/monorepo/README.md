# Sample monorepo

A small .NET + React repository inside lanqueue that you can point lanq at to see sharding, plans, memory routing, wait
budgets and test results on your own pool. It has no secrets and talks to no service of its own. Restoring it needs
nuget.org and the npm registry. The Docker part is opt-in.

It is not part of `LanQueue.slnx`: `dotnet test` at the repository top does not run it. CI builds it and runs its tests on
Linux and Windows (the `sample` job), so it stays working.

## Layout

| Path | What it is |
| --- | --- |
| `Sample.slnx` | Core, unit, integration and serial tests. The Hang and Docker projects are not in it. Needs SDK 9.0.200+ (`.slnx`); the projects target .NET 10. |
| `src/Sample.Core` | Small classes: money and pricing, inventory, a tokenizer, a CSV line parser, an order state machine, slugs, Roman numerals, Luhn, paging, a JSON file store, a loopback line server. |
| `tests/Sample.UnitTests` | Fast tests in many classes. `OrderBatchTests` takes a few seconds, and `SlowReportTests` (`Category=Slow`) takes about 20 s, so a balanced split differs visibly from a hash split. |
| `tests/Sample.IntegrationTests` | Temp-file I/O, JSON round trips, a TCP server on `127.0.0.1` with a random port. |
| `tests/Sample.SerialTests` | Wall-clock sensitive (`Category=Serial`): the plan runs it alone (`exclusive: true`). |
| `tests/Sample.HangTests` | Opt-in: one test that never returns. |
| `tests/Sample.DockerTests` | Opt-in: Testcontainers starts a throwaway nginx container (pinned by digest) on a random port. |
| `web/` | React 19 components rendered with `react-dom/server`, Vitest tests (one slow file, about 10 s), TypeScript. |
| `lanq.yml` | The default plan for Windows workers. |
| `lanq.linux.yml` | The same plan for Linux workers. |
| `lanq.extras.yml` | Opt-in jobs: `hang`, `zero-tests`, `docker`. |

## Run it on your pool

From the lanqueue repository top:

```sh
lanq run --plan --plan-file samples/monorepo/lanq.yml --json --no-wake        # Windows workers
lanq run --plan --plan-file samples/monorepo/lanq.linux.yml --json --no-wake  # Linux workers
lanq run --plan --plan-file samples/monorepo/lanq.yml --dry-run               # what it would do now, sending nothing
```

Why two plan files: `lanq.yml` has no conditions, so one plan cannot choose between the PowerShell and the sh shard
script. Each file names one script and asks for that OS (`needs: [dotnet, windows]` or `needs: [dotnet, linux]`), so a
`.ps1` never lands on Linux and an `.sh` never on Windows. The web jobs need only `node` and run on either OS.

Your `machines.yml` policy must allow these commands and folders. These are the exact strings; never allow a bare
`powershell` or `sh`:

```yaml
policy:
  allowed_commands:
    - "dotnet build"
    - "dotnet test"
    - "npm run"
    - "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/lanq/dotnet-test-shard.ps1"  # lanq.yml
    - "sh scripts/lanq/dotnet-test-shard.sh"                                                   # lanq.linux.yml
  allowed_directories: [".", "samples/monorepo/web"]
```

Tag workers `dotnet` plus `windows` or `linux`, and `node` for the web jobs (`docker` for the Docker extra).

## What each job shows

| Job | Shows |
| --- | --- |
| `build` | A compile gate. Every later job still builds again in its own fresh worktree; that repeated cost is what warm workers will remove. |
| `unit` (3 shards) | Sharding with the shipped `scripts/lanq/dotnet-test-shard.*` scripts, TRX results counted per shard. |
| `integration` (2 shards) | Loopback networking and temp files on workers. |
| `serial-tail` | `after: [integration]` and `exclusive: true`: nothing else of lanq runs on that machine meanwhile. |
| `web-test` (2 shards) | Vitest `--shard={shard}/{shards}` with JUnit results. Each shard runs `npm ci` first (20-60 s, network). |
| `web-types` | `tsc --noEmit`. |

Every job declares `mem: 2` (GB), so memory-aware placement can be tried by giving a worker a small free-memory floor
or a larger per-job size.

Extras (`lanq.extras.yml`), one at a time with `--only`:

- `hang`: ends TimedOut after 3 minutes.
- `zero-tests`: a filter that matches no test; lanq warns that no tests ran.
- `docker`: needs a worker tagged `docker` with a working Docker engine.

## Run it locally

```sh
dotnet test samples/monorepo/Sample.slnx -c Release
cd samples/monorepo/web && npm ci && npm test && npm run typecheck
```
