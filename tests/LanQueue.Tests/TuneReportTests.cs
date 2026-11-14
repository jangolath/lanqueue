using System.Text.RegularExpressions;
using LanQueue.Config;
using LanQueue.Makeready;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// lanq-makeready-tune reports (0.12.0) are untrusted input from a worker: size, depth, schema, items, ids, statuses and the summary are
/// checked; strings are sanitised and cut; fixId comes from lanq's own table and security from lanq's own set.
/// </summary>
public class TuneReportTests
{
    internal static string Item(string id, string status, string fixId = "null", bool security = false, string title = "A title", string detail = "a detail", string advice = "some advice") =>
        $$"""{ "id": "{{id}}", "title": "{{title}}", "status": "{{status}}", "detail": "{{detail}}", "advice": "{{advice}}", "needsAdmin": false, "fixId": {{fixId}}, "security": {{(security ? "true" : "false")}} }""";

    /// <summary>A tune report with <paramref name="items"/> and a summary counted from them (unless <paramref name="summary"/> is given).</summary>
    internal static string Report(string[] items, string? summary = null, string schema = "lanq-makeready-tune/v1", string tool = "lanq-makeready-tune", string extra = "", string arch = "x64")
    {
        int N(string s) => items.Count(i => i.Contains($"\"status\": \"{s}\"", StringComparison.Ordinal));
        summary ??= $$"""{ "ok": {{N("OK")}}, "info": {{N("INFO")}}, "warn": {{N("WARN")}}, "skip": {{N("SKIP")}}, "seconds": 12 }""";
        return $$"""
            {
              "schema": "{{schema}}",
              "tool": { "name": "{{tool}}", "version": "0.5.0" },
              "generatedAt": "2026-11-14T06:00:00Z",{{extra}}
              "host": { "hostname": "<redacted>", "user": "<redacted>", "os": "windows", "arch": "{{arch}}" },
              "context": { "container": false, "elevated": false, "workRoot": "C:/lq", "hours": "22:00-07:00" },
              "items": [ {{string.Join(", ", items)}} ],
              "summary": {{summary}}
            }
            """;
    }

    internal static string Windows() => Report(
    [
        Item("power.plan", "WARN", "\"power.cpu\""),
        Item("sysmain", "INFO", "\"sysmain\""),
        Item("defender.paths", "WARN", security: true),
        Item("search.index", "INFO"),
        Item("visual.fx", "OK"),
        Item("pagefile", "SKIP"),
    ]);

    [Fact]
    public void AValidReportParsesAndKeepsTheTablesFixIdsAndSecurity()
    {
        var r = TuneReport.Parse(Windows()).ForJson(MachineOs.Windows);
        Assert.Equal("power.cpu", r.Items!.Single(i => i.Id == "power.plan").FixId);
        Assert.Equal("sysmain", r.Items!.Single(i => i.Id == "sysmain").FixId);
        Assert.True(r.Items!.Single(i => i.Id == "defender.paths").Security);
        Assert.Null(r.Items!.Single(i => i.Id == "search.index").FixId);
        Assert.Equal(12, r.Summary!.Seconds);
    }

    public static TheoryData<string, string> Unusable() => new()
    {
        { "", "not valid JSON" },
        { "not json", "not valid JSON" },
        { Report([Item("sysmain", "OK")], schema: "lanq-makeready-tune/v2"), "schema" },
        { Report([Item("sysmain", "OK")], tool: "lanq-makeready"), "schema" },
        { Report([Item("sysmain", "FAIL")]), "status" },
        { Report([Item("sysmain", "warn")]), "status" },
        { Report([Item("sysmain", "rm -rf /")]), "status" },
        { Report([Item("Sysmain", "OK")]), "id" },
        { Report([Item("a.b.c.d", "OK")]), "id" },
        { Report([Item("abcdefghijklmnopq", "OK")]), "id" },
        { Report([Item("sysmain\\n", "OK")]), "id" },
        { Report([Item("sys main", "OK")]), "id" },
        { Report([Item("sysmain", "OK")], summary: """{ "ok": 2, "info": 0, "warn": 0, "skip": 0 }"""), "summary" },
        { Report([Item("sysmain", "OK")], summary: "null"), "summary" },
        { Report([.. Enumerable.Range(0, 65).Select(n => Item($"x{n}", "OK"))]), "64" },
        { Report([Item("sysmain", "OK")]).Replace("\"items\"", "\"things\"", StringComparison.Ordinal), "items" },
        { Report([Item("sysmain", "OK")])[..200], "not valid JSON" }, // truncated output
        { Report([Item("sysmain", "OK")], extra: " \"deep\": " + new string('[', 10) + new string(']', 10) + ","), "not valid JSON" },
        { Report([Item("sysmain", "OK").Replace("\"needsAdmin\": false", "\"needsAdmin\": \"yes\"", StringComparison.Ordinal)]), "not valid JSON" },
    };

    [Theory]
    [MemberData(nameof(Unusable))]
    public void AnUnusableReportIsRefusedWithoutEchoingIt(string json, string reason)
    {
        var ex = Assert.Throws<FormatException>(() => TuneReport.Parse(json));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -rf", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopq", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOversizedReportIsRefused()
    {
        var big = Report([Item("sysmain", "OK", detail: new string('x', TuneReport.MaxChars))]);
        Assert.Contains("256 KB", Assert.Throws<FormatException>(() => TuneReport.Parse(big)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SixtyFourItemsAndShallowUnknownPropertiesAreFine()
    {
        var r = TuneReport.Parse(Report([.. Enumerable.Range(0, 64).Select(n => Item($"x{n}", "OK"))], extra: " \"extra\": [[1]],"));
        Assert.Equal(64, r.Items!.Count);
    }

    [Fact]
    public void AnUnknownIdIsKeptWithoutAFixIdOrSecurity()
    {
        var r = TuneReport.Parse(Report([Item("new.check", "WARN", "\"new.fix\"", security: true)])).ForJson(MachineOs.Windows);
        var i = Assert.Single(r.Items!);
        Assert.Equal("new.check", i.Id);
        Assert.Null(i.FixId);
        Assert.False(i.Security);
    }

    [Theory]
    [InlineData("power.plan", "WARN", "\"power.plan\"", null)] // the worker's value is not the table's
    [InlineData("power.plan", "OK", "\"power.cpu\"", null)] // only INFO and WARN
    [InlineData("power.plan", "SKIP", "\"power.cpu\"", null)]
    [InlineData("power.usbsuspend", "INFO", "\"power.usbsuspend\"", null)] // WARN only
    [InlineData("power.usbsuspend", "WARN", "\"power.usbsuspend\"", "power.usbsuspend")]
    [InlineData("do.mode", "INFO", "null", null)] // a policy sets it: the script sends none
    [InlineData("do.mode", "INFO", "\"do.mode\"", "do.mode")]
    [InlineData("defender.schedule", "INFO", "\"defender.scantime\"", "defender.scantime")]
    [InlineData("search.index", "INFO", "\"search.index\"", null)] // report only: not in the table
    [InlineData("sysctl.inotify", "WARN", "\"sysctl.tune\"", null)] // a Linux id on Windows
    public void AFixIdIsKeptOnlyWhenTheWorkerAndTheTableAgree(string id, string status, string fixId, string? expected)
    {
        var r = TuneReport.Parse(Report([Item(id, status, fixId)])).ForJson(MachineOs.Windows);
        Assert.Equal(expected, Assert.Single(r.Items!).FixId);
    }

    [Theory]
    [InlineData(MachineOs.Windows, "defender.paths", true, true)]
    [InlineData(MachineOs.Windows, "av.other", true, true)]
    [InlineData(MachineOs.Windows, "autologon.plain", true, true)]
    [InlineData(MachineOs.Windows, "autologon.plain", false, false)] // the worker must say so too
    [InlineData(MachineOs.Windows, "defender.schedule", true, false)] // not in lanq's set
    [InlineData(MachineOs.Windows, "sysmain", true, false)]
    [InlineData(MachineOs.Linux, "services.unused", true, true)]
    [InlineData(MachineOs.Linux, "services.unused", false, false)]
    [InlineData(MachineOs.Linux, "defender.paths", true, false)]
    public void SecurityIsTheWorkersFlagAndLanqsSet(MachineOs os, string id, bool claimed, bool expected)
    {
        var r = TuneReport.Parse(Report([Item(id, "WARN", security: claimed)])).ForJson(os);
        Assert.Equal(expected, Assert.Single(r.Items!).Security);
    }

    [Fact]
    public void StringsAreSanitisedAndCutNotRefused()
    {
        var evil = "\\u001b]0;pwned\\u0007\\u202e\\u2028x";
        var json = Report(
            [Item("sysmain", "INFO", "\"sysmain\"", title: evil + new string('t', 300), detail: evil + new string('d', 3000), advice: evil + new string('a', 3000))],
            arch: new string('a', 100));
        var r = TuneReport.Parse(json).ForJson(MachineOs.Windows);
        var i = Assert.Single(r.Items!);
        Assert.True(i.Title!.Length <= TuneReport.TitleMax);
        Assert.True(i.Detail!.Length <= TuneReport.TextMax);
        Assert.True(i.Advice!.Length <= TuneReport.TextMax);
        Assert.True(r.Host!.Arch!.Length <= TuneReport.ArchMax);
        foreach (var c in new[] { (char)27, (char)7, '\u202e', '\u2028' })
        {
            Assert.DoesNotContain(c, i.Title);
            Assert.DoesNotContain(c, i.Advice);
            Assert.DoesNotContain(c, i.Detail);
        }
    }

    /// <summary>lanq's fix table equals the fix ids written in the embedded tune scripts (catches a fix id renamed upstream).</summary>
    [Fact]
    public void TheFixTableMatchesTheEmbeddedScripts()
    {
        var ps1 = System.Text.Encoding.UTF8.GetString(Cli.TuneScript(MachineOs.Windows));
        var sh = System.Text.Encoding.UTF8.GetString(Cli.TuneScript(MachineOs.Linux));

        // Windows: New-TuneItem 'id' ... $true|$false 'fix' on one line, or $fx = 'fix' for do.mode and defender.schedule.
        var winPairs = Regex.Matches(ps1, @"New-TuneItem '([a-z.]+)' \$t '([A-Z]+)'.*?\$(?:true|false) '([a-z.]+)'\)", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(m => (Id: m.Groups[1].Value, Status: m.Groups[2].Value, Fix: m.Groups[3].Value)).ToList();
        var winFx = Regex.Matches(ps1, @"\$fx = '([a-z.]+)'", RegexOptions.None, TimeSpan.FromSeconds(5)).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(TuneReport.FixIds[MachineOs.Windows].Order(StringComparer.Ordinal), winPairs.Select(p => p.Fix).Concat(winFx).Distinct().Order(StringComparer.Ordinal));
        foreach (var (id, status, fix) in winPairs)
        {
            Assert.Equal(fix, TuneReport.TuneFix(MachineOs.Windows, id, status));
        }

        Assert.Equal("do.mode", TuneReport.TuneFix(MachineOs.Windows, "do.mode", "INFO"));
        Assert.Equal("defender.scantime", TuneReport.TuneFix(MachineOs.Windows, "defender.schedule", "INFO"));

        // Linux: add_item id "title" STATUS ... false|true fix false|true, or _fix=fix for services.unused.
        var linPairs = Regex.Matches(sh, @"add_item ([a-z.]+) ""[^""]*"" ([A-Z]+) .* (?:true|false) ([a-z][a-z.]+) (?:true|false)", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(m => (Id: m.Groups[1].Value, Status: m.Groups[2].Value, Fix: m.Groups[3].Value)).ToList();
        var linFix = Regex.Matches(sh, @"_fix=([a-z][a-z.]+)", RegexOptions.None, TimeSpan.FromSeconds(5)).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(TuneReport.FixIds[MachineOs.Linux].Order(StringComparer.Ordinal), linPairs.Select(p => p.Fix).Concat(linFix).Distinct().Order(StringComparer.Ordinal));
        foreach (var (id, status, fix) in linPairs)
        {
            Assert.Equal(fix, TuneReport.TuneFix(MachineOs.Linux, id, status));
        }

        Assert.Equal("services.unused", TuneReport.TuneFix(MachineOs.Linux, "services.unused", "INFO"));
    }
}
