using System.Diagnostics;
using System.Text;
using LanQueue.Config;

namespace LanQueue.Tests;

/// <summary>
/// A throwaway "worker" on this machine: a work root in the temp folder, and the generated scripts run by the local shell
/// (PowerShell on Windows, sh elsewhere), exactly as the remote shell would run them.
/// </summary>
internal sealed class LocalWorker : IDisposable
{
    public LocalWorker()
    {
        Root = Path.Combine(Path.GetTempPath(), "lanq-worker-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
        Directory.CreateDirectory(Path.Combine(Root, "incoming"));
        var template = OperatingSystem.IsWindows() ? T.Windows() : T.Linux();
        Machine = new Machine
        {
            Name = template.Name,
            Os = template.Os,
            Ssh = template.Ssh,
            WorkRoot = Root,
            MaxJobs = 2,
            Capabilities = template.Capabilities,
        };
    }

    public string Root { get; }

    public Machine Machine { get; }

    public (int Code, string Output) Run(string script)
    {
        if (Machine.Os == MachineOs.Windows)
        {
            var ps1 = Path.Combine(Path.GetTempPath(), "lanq-local-" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(ps1, script, new UTF8Encoding(true));
            try
            {
                return Exec("powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ps1);
            }
            finally
            {
                File.Delete(ps1);
            }
        }

        return Exec("sh", "-c", script);
    }

    public void Dispose()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }

        Directory.Delete(Root, recursive: true);
    }

    private static (int Code, string Output) Exec(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + err.Result);
    }
}

/// <summary>A scratch git repository for payload tests.</summary>
internal sealed class TempRepo : IDisposable
{
    public TempRepo()
    {
        Dir = Path.Combine(Path.GetTempPath(), "lanq-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Git("init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Dir, ".gitignore"), ".out/\n");
        Commit("a.txt", "one", "init");
    }

    public string Dir { get; }

    public string Out => Path.Combine(Dir, ".out");

    public string Commit(string file, string content, string message)
    {
        var full = Path.Combine(Dir, file);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
        return Git("rev-parse", "HEAD").Trim();
    }

    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {err.Result}");
        }

        return o;
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }

        Directory.Delete(Dir, recursive: true);
    }
}
