using System.Runtime.InteropServices;

namespace LanQueue.Jobs;

/// <summary>
/// Discards what was typed at the terminal and not yet read (final review L3), so the fallback prompt never takes typeahead as its answer:
/// FlushConsoleInputBuffer on Windows, tcflush(0, TCIFLUSH) on Linux and macOS. Best effort: when stdin is not a console or terminal (ask never
/// prompts then) or the call is missing, nothing happens.
/// </summary>
internal static class ConsoleInput
{
    public static void Discard()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var handle = GetStdHandle(StdInputHandle);
                if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                {
                    _ = FlushConsoleInputBuffer(handle);
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                _ = Tcflush(0, 0); // TCIFLUSH is 0 on Linux
            }
            else if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                _ = Tcflush(0, 1); // TCIFLUSH is 1 on the BSDs
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // no such call here: nothing to discard with
        }
    }

    private const int StdInputHandle = -10;

#pragma warning disable SYSLIB1054 // blittable signatures with no marshalling: DllImport is AOT-safe here and needs no unsafe code
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushConsoleInputBuffer(IntPtr handle);

    [DllImport("libc", EntryPoint = "tcflush", SetLastError = true)]
    private static extern int Tcflush(int fd, int queueSelector);
#pragma warning restore SYSLIB1054
}
