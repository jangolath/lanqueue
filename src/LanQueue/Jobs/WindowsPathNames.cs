using System.Runtime.InteropServices;

namespace LanQueue.Jobs;

/// <summary>
/// Windows (final review L5): every name the file system knows a folder by, for the fallback's overlap check. The deepest existing folder of
/// the path is opened and its final path read from the handle (GetFinalPathNameByHandle), which sees through subst drives, 8.3 short names,
/// links and junctions; then every place its volume is mounted (its drive letters and mount-point folders, GetVolumePathNamesForVolumeName)
/// gives one more name. A part of the path that does not exist yet is kept as written after each name. More names only make the check refuse
/// more, never less.
/// </summary>
/// <remarks>
/// Not covered, and documented: a volume mounted inside a folder of another volume that is itself mounted only at a folder is named as far as
/// Windows lists its mount points; a network share reached by two different server names (\\host and \\host.lan) is two paths.
/// </remarks>
internal static class WindowsPathNames
{
    /// <summary>The names of <paramref name="full"/> (an absolute path); empty off Windows or when nothing on the path could be opened.</summary>
    public static IReadOnlyList<string> Of(string full)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            // The deepest folder that exists and can be opened; the rest is kept as written.
            var existing = Path.TrimEndingDirectorySeparator(full);
            while (true)
            {
                if (Directory.Exists(existing) && Final(existing, VolumeNameGuid) is { } guidPath)
                {
                    var tail = full.Length > existing.Length ? full[existing.Length..].TrimStart('\\', '/') : "";
                    return NamesOf(existing, guidPath, tail);
                }

                var parent = Path.GetDirectoryName(existing);
                if (parent is null || parent.Length >= existing.Length)
                {
                    return [];
                }

                existing = parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    private static List<string> NamesOf(string existing, string guidPath, string tail)
    {
        string With(string folder) => tail.Length == 0 ? folder : Path.Join(folder, tail);
        var names = new List<string> { With(guidPath) };
        if (Final(existing, VolumeNameDos) is { } dos)
        {
            names.Add(With(dos));
        }

        // \\?\Volume{guid}\rest: every mount point of that volume + rest.
        const string prefix = @"\\?\Volume{";
        var close = guidPath.IndexOf("}\\", StringComparison.Ordinal);
        if (guidPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && close > 0)
        {
            var volume = guidPath[..(close + 2)];
            var rest = guidPath[(close + 2)..];
            names.AddRange(MountPoints(volume).Select(m => With(Path.Join(m, rest))));
        }

        return names;
    }

    /// <summary>The final path of an existing folder, without the \\?\ prefix for a drive or share path; null when it cannot be read.</summary>
    private static string? Final(string folder, uint volumeName)
    {
        var path = Marshal.StringToHGlobalUni(Path.GetFullPath(folder));
        var handle = CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        Marshal.FreeHGlobal(path);
        if (handle == InvalidHandle)
        {
            return null;
        }

        const int chars = 32_768;
        var buffer = Marshal.AllocHGlobal(chars * 2);
        try
        {
            var n = GetFinalPathNameByHandleW(handle, buffer, chars, FileNameNormalized | volumeName);
            if (n == 0 || n >= chars)
            {
                return null;
            }

            var s = Marshal.PtrToStringUni(buffer, (int)n);
            return volumeName == VolumeNameGuid ? s
                : s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + s[8..]
                : s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..]
                : s;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            _ = CloseHandle(handle);
        }
    }

    /// <summary>Every drive letter and mount-point folder of a volume (\\?\Volume{guid}\), each ending in a separator.</summary>
    private static List<string> MountPoints(string volume)
    {
        const int chars = 32_768;
        var name = Marshal.StringToHGlobalUni(volume);
        var buffer = Marshal.AllocHGlobal(chars * 2);
        try
        {
            if (!GetVolumePathNamesForVolumeNameW(name, buffer, chars, out var used) || used == 0)
            {
                return [];
            }

            // A list of strings, each NUL-ended, the list ended by one more NUL.
            return [.. Marshal.PtrToStringUni(buffer, (int)Math.Min(used, chars)).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(name);
        }
    }

    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000; // needed to open a folder
    private const uint FileNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;
    private const uint VolumeNameGuid = 0x1;
    private static readonly IntPtr InvalidHandle = new(-1);

#pragma warning disable SYSLIB1054 // blittable signatures with no marshalling: DllImport is AOT-safe here and needs no unsafe code
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateFileW(IntPtr name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr file, IntPtr buffer, uint chars, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeNameW(IntPtr volume, IntPtr buffer, uint chars, out uint used);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
#pragma warning restore SYSLIB1054
}
