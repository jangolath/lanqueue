using System.Diagnostics;
using System.Globalization;
using System.Text;
using LanQueue.Policy;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// PowerShell's tokenizer treats U+0027 and the four typographic single quotes U+2018, U+2019, U+201A and U+201B as single-quote
/// characters. Every one of them must be doubled inside a single-quoted literal, or the argument can close the string early.
/// </summary>
public class PowerShellQuoteTests
{
    public static TheoryData<string> SingleQuotes => new() { "\u0027", "\u2018", "\u2019", "\u201A", "\u201B" };

    [Theory]
    [MemberData(nameof(SingleQuotes))]
    public void EverySingleQuoteCharacterIsDoubled(string q) =>
        Assert.Equal("'a" + q + q + "b'", ShellQuote.PowerShell("a" + q + "b"));

    [Theory]
    [MemberData(nameof(SingleQuotes))]
    public void InjectionPayloadStaysInsideTheLiteral(string q)
    {
        var quoted = ShellQuote.PowerShell("a" + q + "; Write-Output INJECTED; " + q + "b");
        Assert.Contains(q + q + "; Write-Output INJECTED; " + q + q, quoted, StringComparison.Ordinal);
    }

    /// <summary>Round trip through a real powershell.exe (Windows only): the literal must evaluate back to the exact input.</summary>
    [PowerShellTheory]
    [MemberData(nameof(SingleQuotes))]
    public async Task RoundTripsVerbatimThroughRealPowerShell(string q)
    {

        var input = "a" + q + "; Write-Output INJECTED; " + q + "b" + q + q + "'";
        // Print code points, so no console encoding can blur the comparison.
        var script = "$s = " + ShellQuote.PowerShell(input) + "\n[Console]::Out.Write((($s.ToCharArray() | ForEach-Object { [int]$_ }) -join ','))";
        var psi = new ProcessStartInfo(PowerShellTheoryAttribute.Exe!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();

        var expected = string.Join(',', input.Select(c => ((int)c).ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(expected, stdout.Trim());
        Assert.DoesNotContain("INJECTED", stdout, StringComparison.Ordinal);
    }
}

/// <summary>Runs with powershell.exe on Windows, or pwsh where it is on PATH; otherwise the test is reported as skipped.</summary>
public sealed class PowerShellTheoryAttribute : TheoryAttribute
{
    public static readonly string? Exe = Find();

    public PowerShellTheoryAttribute()
    {
        if (Exe is null)
        {
            Skip = "No PowerShell on this machine (powershell.exe on Windows, or pwsh on PATH).";
        }
    }

    private static string? Find()
    {
        if (OperatingSystem.IsWindows())
        {
            return "powershell.exe";
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "pwsh"))
            .FirstOrDefault(File.Exists);
    }
}
