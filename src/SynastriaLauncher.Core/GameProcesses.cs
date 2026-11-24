using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SynastriaLauncher.Core;

// Whether this game folder's client is running. Only a WowExt.exe or Wow.exe started from the folder counts: retail
// WoW (also Wow.exe) or another server's client elsewhere doesn't hold this folder's files.
public static class GameProcesses
{
    private static readonly string[] Names = ["WowExt", "Wow"];

    public static bool IsRunning(string? gameFolder)
    {
        var found = false;
        foreach (var process in Names.SelectMany(Process.GetProcessesByName))
        {
            using (process)
            {
                found = found || (!HasExited(process) && (gameFolder is null || BelongsTo(ImagePath(process.Id), gameFolder)));
            }
        }
        return found;
    }

    // A closed game can stay in the process list for minutes while a driver finishes with it (seen with DXVK); it has
    // exited by then. One the launcher can't ask about counts as running.
    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Lets closed games that are stuck finish closing. One has been seen with its last thread suspended once and
    // never resumed (2026-11-24, cause unknown), which kept it from going away until something resumed it. Only
    // this folder's processes that have already exited are touched, so a running game is never affected. Returns
    // how many threads it resumed.
    public static int ReleaseClosed(string gameFolder)
    {
        var resumed = 0;
        foreach (var process in Names.SelectMany(Process.GetProcessesByName))
        {
            using (process)
            {
                if (HasExited(process) && BelongsTo(ImagePath(process.Id), gameFolder))
                {
                    foreach (ProcessThread thread in process.Threads)
                    {
                        resumed += Resume(thread.Id);
                    }
                }
            }
        }
        return resumed;
    }

    private static int Resume(int threadId)
    {
        var handle = OpenThread(SuspendResume, false, (uint)threadId);
        var resumed = 0;
        if (handle != IntPtr.Zero)
        {
            var previous = ResumeThread(handle);
            for (var i = 0; previous > 1 && i < 64; i++)
            {
                previous = ResumeThread(handle);
            }
            resumed = previous > 0 ? 1 : 0;
            CloseHandle(handle);
        }
        return resumed;
    }

    private const uint SuspendResume = 0x0002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResumeThread(IntPtr thread);

    // An exe the launcher can't read the path of counts as this folder's, so files aren't changed under it.
    public static bool BelongsTo(string? imagePath, string gameFolder) =>
        imagePath is null || string.Equals(Normalize(Path.GetDirectoryName(imagePath) ?? ""), Normalize(gameFolder),
            StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, char[] name, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    internal static string? ImagePath(int processId)
    {
        string? path = null;
        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle != IntPtr.Zero)
        {
            var buffer = new char[32768];
            var size = (uint)buffer.Length;
            path = QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
            CloseHandle(handle);
        }
        return path;
    }
}
