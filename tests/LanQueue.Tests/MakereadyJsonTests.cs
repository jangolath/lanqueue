using System.Text.Json;
using LanQueue.Config;
using LanQueue.Health;
using LanQueue.Makeready;
using LanQueue.Ssh;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.9.2: lanq makeready's three JSON paths (one machine, --all rows, --add) print the worker's report through one output-only wrapper
/// (Cli.ReportForJson): every string sanitised, suggestedMachine.path_prepend and env filtered with the text and --add rule, and what was
/// dropped listed in lanqWithheld, which a worker can never send.
/// </summary>
public sealed class MakereadyJsonTests : IDisposable
{
    private const string Healthy = "load=0.1\ndisk_free_gb=50\nslots_used=0\ntool=ok\npaused=0\n";

    /// <summary>ESC, BEL, a bidi override and a zero-width space, as JSON escapes.</summary>
    private const string Bad = "\\u001b[31m\\u0007\\u202e\\u200b";

    private static readonly char[] BadChars = [(char)0x1b, (char)0x07, '‮', '​'];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanq-mrjson-" + Guid.NewGuid().ToString("N"));

    public MakereadyJsonTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, Cli.StarterConfig);
    }

    public void Dispose() => TestTree.Delete(_dir);

    private string ConfigPath => Path.Combine(_dir, "machines.yml");

    /// <summary>A valid report with <see cref="Bad"/> in every free-text string, the env key included.</summary>
    private static string Dirty() => $$"""
        {
          "schema": "lanq-makeready/v1",
          "tool": { "name": "lanq-makeready{{Bad}}", "version": "0.4.0{{Bad}}" },
          "generatedAt": "2026-11-13T06:39:14Z{{Bad}}",
          "host": { "hostname": "pc{{Bad}}", "user": "u{{Bad}}", "os": "Ubuntu{{Bad}}", "arch": "x64{{Bad}}" },
          "verdicts": { "worker_git": "OK", "worker_archive": "OK", "dotnet10": "OK", "docker": "SKIP", "overnight": "WARN" },
          "summary": { "verdict": "READY_WITH_WARNINGS", "text": "ready{{Bad}}", "blockers": 0, "warnings": 1,
            "nextSteps": [{ "step": 1, "id": "reboot{{Bad}}", "title": "Reboot{{Bad}}", "status": "WARN", "blocker": false, "detail": "d{{Bad}}", "action": "a{{Bad}}", "adminToFix": false }] },
          "suitedFor": { "tier": "medium", "containers": false, "text": "medium jobs{{Bad}}" },
          "checks": [
            { "id": "reboot.pending{{Bad}}", "title": "Pending reboot{{Bad}}", "status": "WARN", "detail": "pending{{Bad}}", "advice": "Restart{{Bad}}", "needsAdmin": false }
          ],
          "capabilities": ["dotnet{{Bad}}"],
          "suggestedMachine": { "name": "old-pc{{Bad}}", "os": "linux", "ssh": "u@pc.lan{{Bad}}", "work_root": "/srv/lq{{Bad}}",
            "capabilities": ["dotnet{{Bad}}", "linux"], "max_jobs": 1, "transfer": "git", "allowed_hours": "00:00-07:00{{Bad}}",
            "wake": { "mac": "aa:bb{{Bad}}" }, "path_prepend": ["/opt/dotnet{{Bad}}"], "env": { "DOTNET_ROOT{{Bad}}": "/opt/dotnet{{Bad}}" } }
        }
        """;

    private static FakeSsh Ssh(string report) => new()
    {
        Handler = (_, _) => new SshResult(0, Healthy),
        InputHandler = (_, _, input) => System.Text.Encoding.UTF8.GetString(input).StartsWith(Jobs.RemoteScripts.FindToolchainMarker, StringComparison.Ordinal)
            ? new SshResult(0, "")
            : new SshResult(0, report),
    };

    /// <summary>lanq makeready build-01 --json.</summary>
    private static async Task<string> Single(string report)
    {
        var ssh = Ssh(report);
        var o = new StringWriter();
        var cfg = T.Config();
        await Cli.Makeready(ParsedArgs.Parse(["makeready", "build-01", "--json"]), () => cfg, ssh, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None);
        return o.ToString();
    }

    /// <summary>lanq makeready --all --json.</summary>
    private static async Task<string> All(string report)
    {
        var ssh = Ssh(report);
        var o = new StringWriter();
        var cfg = T.Config();
        await Cli.Makeready(ParsedArgs.Parse(["makeready", "--all", "--json", "--force"]), () => cfg, ssh, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None);
        return o.ToString();
    }

    /// <summary>lanq makeready --ssh ... --add --yes --json.</summary>
    private async Task<string> Add(string report)
    {
        var ssh = Ssh(report);
        var o = new StringWriter();
        var host = new MakereadyHost(_ => null, Windows: false, () => null, null, null);
        await Cli.Makeready(
            ParsedArgs.Parse(["makeready", "--ssh", "build@10.20.30.45", "--os", "linux", "--add", "--name", "build-09", "--yes", "--json", "--config", ConfigPath]),
            () => throw new InvalidOperationException("--add reads machines.yml itself"), ssh, new HealthProbe(ssh, TimeProvider.System), o, new StringWriter(), CancellationToken.None, host);
        return o.ToString();
    }

    /// <summary>Every property name and string value in the document.</summary>
    private static IEnumerable<string> Strings(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    yield return p.Name;
                    foreach (var s in Strings(p.Value))
                    {
                        yield return s;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var s in e.EnumerateArray().SelectMany(Strings))
                {
                    yield return s;
                }

                break;
            case JsonValueKind.String:
                yield return e.GetString()!;
                break;
        }
    }

    /// <summary>Fact A2.3: the JSON paths printed the worker's strings as sent. Every string, env keys included, is now sanitised on all three.</summary>
    [Fact]
    public async Task EveryJsonStringIsSanitised()
    {
        var dirty = new List<string>();
        foreach (var (path, stdout) in new[] { ("single", await Single(Dirty())), ("all", await All(Dirty())), ("add", await Add(Dirty())) })
        {
            using var doc = JsonDocument.Parse(stdout);
            var report = path switch
            {
                "single" => doc.RootElement,
                "all" => doc.RootElement.GetProperty("machines")[0].GetProperty("report"),
                _ => doc.RootElement.GetProperty("report"),
            };
            Assert.Equal("lanq-makeready/v1", report.GetProperty("schema").GetString());
            var strings = Strings(report).ToList();
            dirty.AddRange(strings.Where(s => s.IndexOfAny(BadChars) >= 0).Select(s => path + ": " + s));
            Assert.True(strings.Contains("lanq-makeready?[31m???") || dirty.Count > 0, path);
        }

        Assert.True(dirty.Count == 0, "unsanitised strings per path: " + string.Join(", ", dirty.GroupBy(d => d[..d.IndexOf(':', StringComparison.Ordinal)]).Select(g => $"{g.Key}={g.Count()}")));
    }

    /// <summary>The test report with suggestedMachine.path_prepend and env as given (JSON fragments), and anything extra at the top level.</summary>
    private static string WithDotnet(string pathPrepend, string env, string top = "") =>
        MakereadyTests.Report(extra: top).Replace(
            "\"allowed_hours\": \"00:00-07:00\"",
            $"\"allowed_hours\": \"00:00-07:00\", \"path_prepend\": {pathPrepend}, \"env\": {env}",
            StringComparison.Ordinal);

    private static JsonElement Suggested(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.GetProperty("suggestedMachine").Clone();
    }

    private static List<(string Key, string Reason)> Withheld(JsonElement report) =>
        report.TryGetProperty("lanqWithheld", out var w)
            ? [.. w.EnumerateArray().Select(x => (x.GetProperty("key").GetString()!, x.GetProperty("reason").GetString()!))]
            : [];

    [Fact]
    public async Task JsonDropsAListPathPrepend()
    {
        var s = Suggested(await Single(WithDotnet("[\"/opt/dotnet\", \"/opt/other\"]", "{ \"DOTNET_ROOT\": \"/opt/dotnet\" }")));

        Assert.False(s.TryGetProperty("path_prepend", out _));
        Assert.False(s.TryGetProperty("env", out _));
        Assert.Equal("old-pc", s.GetProperty("name").GetString()); // the rest of the suggestion stays
    }

    [Fact]
    public async Task JsonDropsMismatchedDotnetRoot()
    {
        var s = Suggested(await Single(WithDotnet("[\"/opt/dotnet\"]", "{ \"DOTNET_ROOT\": \"/opt/other\" }")));
        Assert.False(s.TryGetProperty("path_prepend", out _));
        Assert.False(s.TryGetProperty("env", out _));

        // DOTNET_ROOT right, but another variable alongside it.
        s = Suggested(await Single(WithDotnet("[\"/opt/dotnet\"]", "{ \"DOTNET_ROOT\": \"/opt/dotnet\", \"LD_PRELOAD\": \"/tmp/x.so\" }")));
        Assert.False(s.TryGetProperty("path_prepend", out _));
        Assert.False(s.TryGetProperty("env", out _));
    }

    [Fact]
    public async Task JsonDropsARedactedHomeFolder()
    {
        var s = Suggested(await Single(WithDotnet("[\"<redacted>/.dotnet\"]", "{ \"DOTNET_ROOT\": \"<redacted>/.dotnet\" }")));

        Assert.False(s.TryGetProperty("path_prepend", out _));
        Assert.False(s.TryGetProperty("env", out _));
    }

    /// <summary>The same data as the text and --add rule (MakereadyAddTests.DotnetDirs): the JSON keeps exactly what they take.</summary>
    [Theory]
    [MemberData(nameof(MakereadyAddTests.DotnetDirs), MemberType = typeof(MakereadyAddTests))]
    public void JsonKeepsTheOneUsableFolder(string os, string dir, string root, bool taken)
    {
        var report = MakereadyReport.Parse(MakereadyTests.Report()) with
        {
            SuggestedMachine = new MakereadySuggestion("old-pc", os, null, "/srv/lq", ["dotnet"], 1, null, "git", null, null, [dir], new() { ["DOTNET_ROOT"] = root }),
        };

        var json = Cli.ReportForJson(report);

        Assert.Equal(taken ? new List<string> { dir } : null, json.SuggestedMachine!.PathPrepend);
        Assert.Equal(taken ? dir : null, json.SuggestedMachine.Env?["DOTNET_ROOT"]);
        Assert.Equal(taken, json.LanqWithheld is null);
    }

    [Fact]
    public async Task JsonWithheldListsKeyAndReason()
    {
        using (var doc = JsonDocument.Parse(await Single(WithDotnet("[\"/opt/a\", \"/opt/b\"]", "{ \"DOTNET_ROOT\": \"/opt/a\" }"))))
        {
            Assert.Equal(
                [("suggestedMachine.path_prepend", Cli.WithheldPathPrependReason), ("suggestedMachine.env", Cli.WithheldEnvReason)],
                Withheld(doc.RootElement));
            Assert.Equal(
                "not one usable folder with DOTNET_ROOT set to it (a redacted home folder, a list, or characters lanq refuses in path_prepend)",
                Cli.WithheldPathPrependReason);
        }

        // Nothing dropped (no folder, or the one usable folder): no lanqWithheld key at all.
        foreach (var report in new[] { MakereadyTests.Report(), WithDotnet("[\"/opt/dotnet\"]", "{ \"DOTNET_ROOT\": \"/opt/dotnet\" }") })
        {
            using var doc = JsonDocument.Parse(await Single(report));
            Assert.False(doc.RootElement.TryGetProperty("lanqWithheld", out _));
        }

        using (var kept = JsonDocument.Parse(await Single(WithDotnet("[\"/opt/dotnet\"]", "{ \"DOTNET_ROOT\": \"/opt/dotnet\" }"))))
        {
            var s = kept.RootElement.GetProperty("suggestedMachine");
            Assert.Equal("/opt/dotnet", s.GetProperty("path_prepend")[0].GetString());
            Assert.Equal("/opt/dotnet", s.GetProperty("env").GetProperty("DOTNET_ROOT").GetString());
        }
    }

    [Fact]
    public async Task AllRowsFiltered()
    {
        using var doc = JsonDocument.Parse(await All(WithDotnet("[\"<redacted>/.dotnet\"]", "{ \"DOTNET_ROOT\": \"<redacted>/.dotnet\" }")));

        var rows = doc.RootElement.GetProperty("machines").EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var report = row.GetProperty("report");
            Assert.False(report.GetProperty("suggestedMachine").TryGetProperty("path_prepend", out _));
            Assert.False(report.GetProperty("suggestedMachine").TryGetProperty("env", out _));
            Assert.Equal(["suggestedMachine.path_prepend", "suggestedMachine.env"], Withheld(report).Select(w => w.Key));
        }
    }

    [Fact]
    public async Task AddJsonFiltered()
    {
        using var doc = JsonDocument.Parse(await Add(WithDotnet("[\"/opt/a\", \"/opt/b\"]", "{ \"DOTNET_ROOT\": \"/opt/a\" }")));

        Assert.Equal("Added", doc.RootElement.GetProperty("status").GetString());
        var report = doc.RootElement.GetProperty("report");
        Assert.False(report.GetProperty("suggestedMachine").TryGetProperty("path_prepend", out _));
        Assert.False(report.GetProperty("suggestedMachine").TryGetProperty("env", out _));
        Assert.Equal(["suggestedMachine.path_prepend", "suggestedMachine.env"], Withheld(report).Select(w => w.Key));
        Assert.DoesNotContain(doc.RootElement.GetProperty("entry").EnumerateArray(), l => l.GetString()!.StartsWith("    path_prepend:", StringComparison.Ordinal)); // only the comment that the folder is hidden
    }

    /// <summary>lanqWithheld is lanq's: a worker that sends one (at the top or inside suggestedMachine) is ignored, never echoed.</summary>
    [Fact]
    public async Task WorkerSentLanqWithheldIgnored()
    {
        const string Forged = "\"lanqWithheld\": [{ \"key\": \"forged\", \"reason\": \"from the worker\" }],";
        foreach (var report in new[]
        {
            MakereadyTests.Report(extra: Forged),
            MakereadyTests.Report().Replace("\"max_jobs\": 1,", "\"max_jobs\": 1, " + Forged, StringComparison.Ordinal),
        })
        {
            Assert.Contains("forged", report, StringComparison.Ordinal);
            foreach (var stdout in new[] { await Single(report), await All(report), await Add(report) })
            {
                Assert.DoesNotContain("forged", stdout, StringComparison.Ordinal);
                Assert.DoesNotContain("lanqWithheld", stdout, StringComparison.Ordinal);
            }
        }

        // With something to withhold, the list is lanq's own entries only.
        var both = WithDotnet("[\"/opt/a\", \"/opt/b\"]", "{ \"DOTNET_ROOT\": \"/opt/a\" }", top: Forged);
        using var doc = JsonDocument.Parse(await Single(both));
        Assert.Equal(["suggestedMachine.path_prepend", "suggestedMachine.env"], Withheld(doc.RootElement).Select(w => w.Key));
    }

    [Fact]
    public async Task SchemaStaysV1AndNoNextSteps()
    {
        foreach (var report in new[] { MakereadyTests.Report(), WithDotnet("[\"/opt/a\", \"/opt/b\"]", "{ \"DOTNET_ROOT\": \"/opt/a\" }"), MakereadySummaryTests.V020 })
        {
            using var doc = JsonDocument.Parse(await Single(report));
            Assert.Equal("lanq-makeready/v1", doc.RootElement.GetProperty("schema").GetString());
            Assert.False(doc.RootElement.TryGetProperty("nextSteps", out _)); // the report has no lanq nextSteps (summary.nextSteps is the script's)
            Assert.False(doc.RootElement.TryGetProperty("status", out _));
        }
    }
}
