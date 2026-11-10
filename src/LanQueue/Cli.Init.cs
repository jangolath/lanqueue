using System.Text;
using System.Text.Json;
using LanQueue.Jobs;
using LanQueue.Output;

namespace LanQueue;

public static partial class Cli
{
    /// <summary>
    /// What lanq init writes: a commented, minimal machines.yml that loads as it is. One example machine is live (a config with no machine
    /// does not load); a Windows and a Linux example below it are commented out and load too once uncommented (pinned by a test). Example
    /// addresses only (10.20.30.x). LF line endings.
    /// </summary>
    internal const string StarterConfig = """
        # machines.yml for lanq, written by `lanq init`. It names your hosts: keep it private and out of git.
        # Every key is explained in machines.example.yml and docs/TEAM-QUICKSTART.md (https://github.com/jangolath/lanqueue).
        #
        # lanq reads this file only when you pass --config FILE or set LANQ_CONFIG, or when it sits at the per-user path
        # (%APPDATA%\lanq\machines.yml on Windows, ~/.config/lanq/machines.yml elsewhere). Never from the current folder by itself.
        #
        # Authentication is key-only; there is no password option:
        #   - ssh-agent (preferred): leave key_env out and ssh uses the keys in your agent (ssh-add -l lists them).
        #   - a key file: key_env is the NAME of an environment variable holding the key file's path (see the Windows example).
        # Every worker's host key must already be in your known_hosts (lanq uses StrictHostKeyChecking=yes): connect once with
        # `ssh user@host`, check the fingerprint with the machine's owner and answer yes.

        policy:
          load_threshold: 0.80         # hold a machine busier than this (0..1)
          min_free_disk_gb: 5          # hold a machine with less free disk than this
          min_free_mem_gb: 1.5         # hold a machine with less available memory (GB); small PCs set their own below
          max_mem_pressure: 20         # Linux: hold while more than 20% of the time is spent waiting on memory
          allowed_commands:            # a job's command must start with one of these; arguments may follow
            - dotnet build             # on a small PC run: dotnet build -m:1 (one build process at a time)
            - dotnet test              # likewise: dotnet test -m:1
          allowed_directories:         # folders (from the repository's top) a job may run in; "src" also allows "src/App"
            - "."
            - src
            - tests

        machines:
          # Replace this example with a real worker: `lanq makeready --ssh user@host --os linux` prints an entry to paste.
          - name: example-01
            os: linux
            ssh: build@10.20.30.11     # user@host
            work_root: /srv/lanq       # absolute, forward slashes, no spaces
            capabilities: [dotnet, linux]
            max_jobs: 1                # 1 under 6 GB of RAM, 2 under 12 GB; lanq makeready suggests it
            transfer: git              # git (default) needs git on the worker; archive needs only tar

          # A Windows 10/11 micro PC with 4 GB of RAM: one job at a time, a memory floor and a capped .NET heap.
          # - name: micro-win-01
          #   os: windows
          #   ssh: build@10.20.30.21
          #   key_env: LANQ_KEY          # NAME of an environment variable holding your key file's path
          #   work_root: C:/lanq
          #   capabilities: [dotnet, windows]
          #   max_jobs: 1
          #   transfer: archive          # the built-in tar.exe is enough; use git if git is installed
          #   min_free_mem_gb: 1.5       # hold it below 1.5 GB available (2 on a PC with 6 GB or more)
          #   env:
          #     DOTNET_GCHeapHardLimitPercent: "0x32"   # each .NET process: GC heap at most 50% of RAM (hexadecimal)

          # A Linux family PC used during the day: overnight only, woken by Wake-on-LAN when a job needs it.
          # - name: family-linux
          #   os: linux
          #   ssh: build@10.20.30.31
          #   work_root: /srv/lanq
          #   capabilities: [dotnet, node, linux]
          #   max_jobs: 1
          #   allowed_hours:             # the worker's own clock; a window may cross midnight
          #     - "Mon-Fri 22:00-07:00"
          #     - "Sat,Sun 01:00-09:00"
          #   wake:                      # needs Wake-on-LAN on in the BIOS/UEFI and the network card (docs/WORKERS.md)
          #     mac: "aa:bb:cc:dd:ee:ff"
          #     broadcast: 10.20.30.255
          #     wait_seconds: 120

        """;

    /// <summary>
    /// lanq init [--config PATH] [--json]: writes <see cref="StarterConfig"/> to PATH (default machines.yml in the current folder), creating
    /// its folder; never over an existing file. Exit 0 written, 2 the file exists (or bad arguments), 70 it could not be written. With
    /// --json, stdout is one object (status, exitCode, path, message) and the next steps go to stderr.
    /// </summary>
    internal static async Task<int> Init(
        ParsedArgs p, string currentDir, Func<string, string?> env, bool windows, TextWriter stdout, TextWriter stderr, Action? beforeWrite = null)
    {
        var path = Path.GetFullPath(p.ConfigPath ?? "machines.yml", currentDir);
        var exists = $"{path} already exists; lanq init never overwrites a file. Edit it, or pass --config with a new path.";
        async Task<int> Done(string status, int code, string message, IEnumerable<string>? steps = null)
        {
            var people = p.Json || code != 0 ? stderr : stdout;
            await people.WriteLineAsync(JobRunner.SafeLine(message)).ConfigureAwait(false);
            foreach (var line in steps ?? [])
            {
                await people.WriteLineAsync(line).ConfigureAwait(false);
            }

            // A regular file already there: check it with lanq doctor --config (a link or folder needs another --config path).
            var next = NextSteps.ForInit(status, path, JobRunner.SafeLine(message), fileExists: message == exists && File.Exists(path) && LinkTarget(path) is null);
            await NextSteps.WriteAsync(stderr, next).ConfigureAwait(false);
            if (p.Json)
            {
                await stdout.WriteLineAsync(JsonSerializer.Serialize(new InitJson(status, code, path, JobRunner.SafeLine(message)) { NextSteps = next }, LanqJson.Default.InitJson))
                    .ConfigureAwait(false);
            }

            return code;
        }

        // A link counts as taken even when its target is missing: writing through it would create a file wherever it points.
        if (LinkTarget(path) is { } target)
        {
            return await Done("Rejected", 2, $"{path} is a link (to {target}); lanq init never writes through a link. Pass --config with a new path.")
                .ConfigureAwait(false);
        }

        if (Directory.Exists(path))
        {
            return await Done("Rejected", 2, $"{path} is a folder; pass --config with a file path such as {Path.Combine(path, "machines.yml")}.")
                .ConfigureAwait(false);
        }

        if (File.Exists(path))
        {
            return await Done("Rejected", 2, exists).ConfigureAwait(false);
        }

        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
            }

            beforeWrite?.Invoke();

            // CreateNew: a file (or link) that appeared since the checks above is still never overwritten or written through.
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            await file.WriteAsync(new UTF8Encoding(false).GetBytes(StarterConfig)).ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(path) || Directory.Exists(path) || LinkTarget(path) is not null)
        {
            return await Done("Rejected", 2, exists).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await Done("InfraError", 70, $"could not write {path}: {ex.Message}").ConfigureAwait(false);
        }

        return await Done("Created", 0, $"Wrote {path} (one example machine to replace; a Windows and a Linux example are commented out).", InitSteps(path, env, windows))
            .ConfigureAwait(false);
    }

    /// <summary>The target of a symbolic link or junction at <paramref name="path"/>, or null when there is no link there.</summary>
    private static string? LinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> InitSteps(string path, Func<string, string?> env, bool windows)
    {
        var userFile = UserConfigPath(env, windows);
        var isDefault = string.Equals(Path.GetFullPath(userFile), path, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var config = isDefault ? "" : " --config " + (path.Contains(' ', StringComparison.Ordinal) ? "\"" + path + "\"" : path);
        yield return "";
        yield return "Next steps:";
        yield return "  1. On each worker, install and start the OpenSSH server, create a build user and add your public key:";
        yield return "     docs/WORKERS.md (https://github.com/jangolath/lanqueue/blob/main/docs/WORKERS.md); a team walk-through is in";
        yield return "     docs/TEAM-QUICKSTART.md.";
        yield return "  2. Connect once to each worker so its host key is in known_hosts: ssh user@host";
        yield return "  3. Check each worker and get its machines.yml entry: lanq makeready --ssh user@host --os windows|linux";
        yield return $"  4. Paste the entries under machines: in {path}, replacing example-01.";
        yield return $"  5. Check your setup and every worker: lanq doctor --remote{config}";
        yield return $"  6. Check them all at once later: lanq makeready --all{config}";
        if (!isDefault)
        {
            yield return "";
            yield return $"lanq reads this file only with --config (as above) or LANQ_CONFIG={path}, or move it to {userFile}.";
        }
    }
}
