using LanQueue.Jobs;
using Xunit;

namespace LanQueue.Tests;

/// <summary>
/// 0.9.2 (A3, lanq side): the POSIX ACL part of lanq's own Linux safe-folder rule (<see cref="RemoteScripts.LinuxAclCheck"/>, used by the
/// toolchain search before a folder is proposed for path_prepend). The cases mirror lanq-makeready 0.4.1's acl_ok tests: the function runs in
/// sh with only fakes of getfacl, ls and uname on PATH, as uid 1000 with private group 1000.
/// </summary>
public sealed class AclCheckTests
{
    private const string Tab = "\t";

    /// <summary>(name, expected safe/open, with or without getfacl, the fake ACL, fake env settings).</summary>
    private static readonly (string Name, string Expect, string Bin, string Acl, string Env)[] Cases =
    [
        ("PlainModesUnchanged", "safe", "with", "user::rwx\ngroup::r-x\nother::r-x", ""),
        ("PlainModesNoGetfaclNoMarker", "safe", "without", "", "FAKE_LS='drwxr-xr-x  2 root root 4096 Jan  1 00:00 x'"),
        ("NamedUserWithWriteIsOpen", "open", "with", "user::rwx\nuser:1001:rwx\ngroup::r-x\nmask::rwx\nother::r-x", ""),
        ("NamedUserWithReadOnlyIsFine", "safe", "with", "user::rwx\nuser:1001:r-x\ngroup::r-x\nmask::r-x\nother::r-x", ""),
        ("NamedGroupWithWriteIsOpen", "open", "with", "user::rwx\ngroup::r-x\ngroup:1002:rwx\nmask::rwx\nother::r-x", ""),
        ("MaskWithoutWriteStaysSafe", "safe", "with", $"user::rwx\nuser:1001:rwx{Tab}{Tab}#effective:r-x\ngroup::r-x\ngroup:1002:rwx{Tab}{Tab}#effective:r-x\nmask::r-x\nother::r-x", ""),
        ("MaskWithWriteKeepsANamedEntryOpen", "open", "with", $"user::rwx\nuser:1001:rwx{Tab}#effective:rw-\ngroup::r-x\nmask::rw-\nother::r-x", ""),
        ("TrustedNamedEntriesAreFine", "safe", "with", "user::rwx\nuser:0:rwx\nuser:1000:rwx\ngroup::r-x\ngroup:0:rwx\ngroup:1000:rwx\nmask::rwx\nother::r-x", ""),
        ("OwnerEntriesAreNotNamed", "safe", "with", "user::rwx\ngroup::rwx\nmask::rwx\nother::r-x", ""),
        ("DefaultUserWithWriteIsOpen", "open", "with", "user::rwx\ngroup::r-x\nother::r-x\ndefault:user::rwx\ndefault:user:1001:rwx\ndefault:group::r-x\ndefault:mask::rwx\ndefault:other::r-x", ""),
        ("DefaultGroupWithWriteIsOpen", "open", "with", $"user::rwx\ngroup::r-x\nother::r-x\ndefault:user::rwx\ndefault:group::r-x\ndefault:group:1002:rwx{Tab}#effective:r-x\ndefault:mask::r-x\ndefault:other::r-x", ""),
        ("DefaultEntriesTrustedOrReadOnlyAreFine", "safe", "with", "user::rwx\ngroup::r-x\nother::r-x\ndefault:user::rwx\ndefault:user:0:rwx\ndefault:user:1000:rwx\ndefault:user:1001:r-x\ndefault:group::rwx\ndefault:group:1000:rwx\ndefault:mask::rwx\ndefault:other::r-x", ""),
        ("GetfaclFailingIsOpen", "open", "with", "user::rwx\ngroup::r-x\nother::r-x", "FAKE_ACL_RC=1"),
        ("EmptyGetfaclOutputIsOpen", "open", "with", "", ""),
        ("UnrecognisedGetfaclOutputIsOpen", "open", "with", "getfacl: something else\nnothing: here", ""),
        ("AclMarkerWithoutGetfaclIsOpen", "open", "without", "", "FAKE_LS='drwxrwxr-x+ 2 root root 4096 Jan  1 00:00 x'"),
        ("LsFailingWithoutGetfaclIsOpen", "open", "without", "", "FAKE_LS_RC=2"),
        ("LsEmptyOutputWithoutGetfaclIsOpen", "open", "without", "", "FAKE_LS="),
        ("BusyboxLsGroupWriteIsOpen", "open", "without", "", "FAKE_LS='drwxrwxr-x 2 root root 4096 Jan  1 00:00 x' FAKE_LS_HELP='BusyBox v1.36.1 (2024-01-01) multi-call binary.'"),
        ("BusyboxLsWithoutGroupWriteIsFine", "safe", "without", "", "FAKE_LS='drwxr-xr-x 2 root root 4096 Jan  1 00:00 x' FAKE_LS_HELP='BusyBox v1.36.1 (2024-01-01) multi-call binary.'"),
        ("GnuLsGroupWriteWithoutMarkerIsLeftToModes", "safe", "without", "", "FAKE_LS='drwxrwxr-x  2 root root 4096 Jan  1 00:00 x'"),
        ("DarwinSkipsTheAclCheckWithGetfacl", "safe", "with", "user::rwx\nuser:1001:rwx\nmask::rwx\nother::r-x", "FAKE_UNAME=Darwin"),
        ("DarwinSkipsTheAclCheckWithMarker", "safe", "without", "", "FAKE_UNAME=Darwin FAKE_LS='drwxr-xr-x+ 2 me staff 64 Jan  1 00:00 x'"),
    ];

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [ShFact]
    public void AclRuleMatchesLanqMakeready041()
    {
        var script = new System.Text.StringBuilder();
        script.Append("""
            set -u
            ab=$(mktemp -d)
            trap 'rm -rf "$ab"' EXIT
            mkdir -p "$ab/with" "$ab/without"
            for t in awk cut sh sleep timeout gtimeout; do p=$(command -v "$t") || continue; for b in with without; do printf '#!/bin/sh\nexec "%s" "$@"\n' "$p" > "$ab/$b/$t"; done; done
            for b in with without; do
              printf '#!/bin/sh\n[ "$1" = -s ] && echo "${FAKE_UNAME:-Linux}"\nexit 0\n' > "$ab/$b/uname"
              printf '#!/bin/sh\nif [ "$1" = --help ]; then printf "%%s\\n" "${FAKE_LS_HELP:-Usage: ls [OPTION]... [FILE]...}"; exit 0; fi\nprintf "%%s\\n" "${FAKE_LS-drwxr-xr-x  2 root root 4096 Jan  1 00:00 x}"\nexit "${FAKE_LS_RC:-0}"\n' > "$ab/$b/ls"
            done
            printf '#!/bin/sh\n[ "$1" = -cpn ] && [ "$2" = -- ] || exit 9\nprintf "%%s\\n" "$FAKE_ACL"\nexit "${FAKE_ACL_RC:-0}"\n' > "$ab/with/getfacl"
            chmod +x "$ab/with/"* "$ab/without/"*

            """);
        script.Append(RemoteScripts.LinuxAclCheck).Append('\n');
        foreach (var c in Cases)
        {
            // Each case in a subshell with only the fakes on PATH, as uid 1000 with private group 1000.
            script.Append($"if ( PATH=$ab/{c.Bin}; FAKE_ACL={Quote(c.Acl)}; export FAKE_ACL; {(c.Env.Length > 0 ? "export " + c.Env + "; " : "")}u=1000; g=1000; acl /opt/dotnet ); then r=safe; else r=open; fi; echo \"{c.Name}=$r\"\n");
        }

        var (code, output) = Shells.Run("linux", script.ToString());

        Assert.True(code == 0, output);
        var got = output.Replace("\r", "", StringComparison.Ordinal).Split('\n').Where(l => l.Contains('=', StringComparison.Ordinal)).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.All(Cases, c => Assert.True(got.TryGetValue(c.Name, out var r) && r == c.Expect, $"{c.Name}: expected {c.Expect}, got {(got.TryGetValue(c.Name, out var x) ? x : "nothing")}\n{output}"));
    }

    /// <summary>The search uses the ACL check on every folder it judges and on every parent that is not a link or sticky, with the trusted ids right.</summary>
    [Fact]
    public void TheSearchCallsTheAclCheckWithTheRightTrustedIds()
    {
        var script = RemoteScripts.FindToolchain(T.Linux(), "dotnet");

        Assert.StartsWith(RemoteScripts.FindToolchainMarker, script, StringComparison.Ordinal);
        Assert.Contains(RemoteScripts.LinuxAclCheck.TrimEnd(), script, StringComparison.Ordinal);
        Assert.Contains("u=$(id -u); g=0; [ \"$(id -gn)\" = \"$(id -un)\" ] && g=$(id -g)", script, StringComparison.Ordinal);
        Assert.Contains("    acl \"$x\" || return 1", script, StringComparison.Ordinal);
        Assert.Contains("[ -L \"$a\" ] || [ -n \"$(find \"$a\" -maxdepth 0 -perm -1000 2>/dev/null)\" ] || acl \"$a\" || return 1", script, StringComparison.Ordinal);
        Assert.Contains("tm getfacl -cpn -- \"$1\"", script, StringComparison.Ordinal);
    }
}
