using System.Globalization;
using System.Text;
using LanQueue.Config;
using LanQueue.Policy;

namespace LanQueue.Jobs;

/// <summary>Builds the remote command strings (sh on Linux, PowerShell on Windows). Pure functions, unit tested.</summary>
public static class RemoteScripts
{
    public static string Wrap(Machine m, string script) => m.Os == MachineOs.Windows
        ? "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'\n" + script))
        : "sh -c " + ShellQuote.Posix(script);

    public static string Probe(Machine m) => m.Os == MachineOs.Windows
        ? $$"""
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}
            $inv = [cultureinfo]::InvariantCulture
            $cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
            if ($null -eq $cpu) { $cpu = 0 }
            $drive = New-Object System.IO.DriveInfo ($root.Substring(0,1))
            $slots = @(Get-ChildItem -Path "$root/locks" -Filter 'slot-*' -ErrorAction SilentlyContinue).Count
            'load=' + ([double]$cpu / 100).ToString('0.###', $inv)
            'disk_free_gb=' + ($drive.AvailableFreeSpace / 1GB).ToString('0.#', $inv)
            'slots_used=' + $slots
            """
        : $$"""
            R={{ShellQuote.Posix(m.WorkRoot)}}
            cpus=$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 1)
            l=$(cut -d' ' -f1 /proc/loadavg 2>/dev/null || echo 0)
            d="$R"; [ -d "$d" ] || d=/
            echo "load=$(awk -v l="$l" -v c="$cpus" 'BEGIN{printf "%.3f", l/c}')"
            echo "disk_free_gb=$(df -Pk "$d" | awk 'NR==2{printf "%.1f", $4/1048576}')"
            echo "slots_used=$(ls -d "$R"/locks/slot-* 2>/dev/null | wc -l | tr -d ' ')"
            """;

    /// <summary>Atomically claims one of max_jobs slot files. Prints SLOT=n or SLOT=FULL.</summary>
    public static string AcquireSlot(Machine m, string jobId) => m.Os == MachineOs.Windows
        ? $$"""
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}
            New-Item -ItemType Directory -Force -Path "$root/locks" | Out-Null
            for ($i = 1; $i -le {{m.MaxJobs.ToString(CultureInfo.InvariantCulture)}}; $i++) {
              try {
                $fs = [System.IO.File]::Open("$root/locks/slot-$i", 'CreateNew', 'Write', 'None')
                $b = [Text.Encoding]::UTF8.GetBytes({{ShellQuote.PowerShell(jobId)}}); $fs.Write($b, 0, $b.Length); $fs.Close()
                'SLOT=' + $i; exit 0
              } catch [System.IO.IOException] { }
            }
            'SLOT=FULL'
            """
        : $$"""
            R={{ShellQuote.Posix(m.WorkRoot)}}
            mkdir -p "$R/locks"
            i=1
            while [ "$i" -le {{m.MaxJobs.ToString(CultureInfo.InvariantCulture)}} ]; do
              if ( set -C; echo {{ShellQuote.Posix(jobId)}} > "$R/locks/slot-$i" ) 2>/dev/null; then echo "SLOT=$i"; exit 0; fi
              i=$((i+1))
            done
            echo "SLOT=FULL"
            """;

    /// <summary>Fetches the uploaded bundle into a bare cache repo and adds a detached per-job worktree.</summary>
    public static string Prepare(Machine m, string jobId) => m.Os == MachineOs.Windows
        ? $$"""
            $ErrorActionPreference = 'Stop'
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}; $id = {{ShellQuote.PowerShell(jobId)}}
            function G { & git -c core.longpaths=true @args; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
            New-Item -ItemType Directory -Force -Path "$root/jobs" | Out-Null
            if (-not (Test-Path "$root/repo.git")) { G init --bare -q "$root/repo.git" }
            G -C "$root/repo.git" fetch -q "$root/incoming/$id.bundle" "refs/lanq/${id}:refs/lanq/${id}"
            G -C "$root/repo.git" worktree add -q --detach "$root/jobs/$id" "refs/lanq/$id"
            """
        : $$"""
            set -e
            R={{ShellQuote.Posix(m.WorkRoot)}}; ID={{ShellQuote.Posix(jobId)}}
            mkdir -p "$R/jobs"
            [ -d "$R/repo.git" ] || git init --bare -q "$R/repo.git"
            git -C "$R/repo.git" fetch -q "$R/incoming/$ID.bundle" "refs/lanq/$ID:refs/lanq/$ID"
            git -C "$R/repo.git" worktree add -q --detach "$R/jobs/$ID" "refs/lanq/$ID"
            """;

    public static string Incoming(Machine m, string jobId) => $"{m.WorkRoot}/incoming/{jobId}.bundle";

    public static string EnsureIncoming(Machine m) => m.Os == MachineOs.Windows
        ? $"New-Item -ItemType Directory -Force -Path ({ShellQuote.PowerShell(m.WorkRoot + "/incoming")}) | Out-Null"
        : $"mkdir -p {ShellQuote.Posix(m.WorkRoot + "/incoming")}";

    /// <summary>Runs the allowlisted argv inside the job worktree. Each argument is individually quoted.</summary>
    public static string Execute(Machine m, string jobId, string directory, IReadOnlyList<string> argv)
    {
        var dir = $"{m.WorkRoot}/jobs/{jobId}" + (directory is "." or "" ? "" : "/" + directory);
        if (m.Os == MachineOs.Windows)
        {
            var parts = string.Join(' ', argv.Select(ShellQuote.PowerShell));
            return $"Set-Location -LiteralPath {ShellQuote.PowerShell(dir)}\n& {parts}\nexit $LASTEXITCODE";
        }

        return $"cd {ShellQuote.Posix(dir)} || exit 126\nexec {string.Join(' ', argv.Select(ShellQuote.Posix))}";
    }

    /// <summary>Best-effort teardown: worktree, ref, bundle, slot. Never fails the job.</summary>
    public static string Cleanup(Machine m, string jobId, string slot) => m.Os == MachineOs.Windows
        ? $$"""
            $root = {{ShellQuote.PowerShell(m.WorkRoot)}}; $id = {{ShellQuote.PowerShell(jobId)}}
            & git -C "$root/repo.git" worktree remove --force "$root/jobs/$id" 2>$null
            if (Test-Path "$root/jobs/$id") { cmd /c rd /s /q ("$root/jobs/$id").Replace('/','\') 2>$null }
            & git -C "$root/repo.git" worktree prune 2>$null
            & git -C "$root/repo.git" update-ref -d "refs/lanq/$id" 2>$null
            Remove-Item -Force -ErrorAction SilentlyContinue "$root/incoming/$id.bundle"
            Remove-Item -Force -ErrorAction SilentlyContinue "$root/locks/slot-{{SafeSlot(slot)}}"
            exit 0
            """
        : $$"""
            R={{ShellQuote.Posix(m.WorkRoot)}}; ID={{ShellQuote.Posix(jobId)}}
            git -C "$R/repo.git" worktree remove --force "$R/jobs/$ID" 2>/dev/null
            rm -rf "$R/jobs/$ID"
            git -C "$R/repo.git" worktree prune 2>/dev/null
            git -C "$R/repo.git" update-ref -d "refs/lanq/$ID" 2>/dev/null
            rm -f "$R/incoming/$ID.bundle" "$R/locks/slot-{{SafeSlot(slot)}}"
            exit 0
            """;

    private static string SafeSlot(string slot) =>
        int.TryParse(slot, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n.ToString(CultureInfo.InvariantCulture)
            : throw new ArgumentException("Invalid slot id.", nameof(slot));
}
