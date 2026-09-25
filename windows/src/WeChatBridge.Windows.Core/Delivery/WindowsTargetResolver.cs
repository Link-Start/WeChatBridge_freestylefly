using System.Diagnostics;
using WeChatBridge.Windows.Core.Interop;

namespace WeChatBridge.Windows.Core.Delivery;

/// <summary>
/// Turns a <see cref="WindowsForwardTarget"/> into a <see cref="ResolvedTarget"/>:
/// a running process if one exists, otherwise an installed (or PATH-resolvable)
/// executable, otherwise an AUMID — and launches/foregrounds on demand.
///
/// The macOS original asked Launch Services for the bundle URL and called
/// <c>openApplication(activates: true)</c>. Windows has no equivalent registry of
/// identity → path, hence the candidate table: process-name match first (the app
/// may already be running), then install-path probing, then a bare exe name left
/// for the shell to resolve, then <c>shell:AppsFolder</c> for packaged apps.
/// </summary>
public static class WindowsTargetResolver
{
    /// <summary>
    /// How long a cold launch may take to show a window. Longer than the
    /// engine's foreground wait: launching is the expensive part, and the wait
    /// for frontmost only starts once the window exists.
    /// </summary>
    public static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Best resolution without launching anything.</summary>
    public static ResolvedTarget? Resolve(WindowsForwardTarget spec)
    {
        var running = FindRunning(spec);
        if (running is not null)
            return running;
        var exe = FindExecutable(spec);
        if (exe is not null)
            return new ResolvedTarget(spec, null, 0, exe);
        if (spec.Aumid is not null)
            return new ResolvedTarget(spec, null, 0, null);
        return null;
    }

    /// <summary>
    /// Brings the target forward and refreshes what we know about it: a cold
    /// launch returns with the process id and window handle filled in, so the
    /// engine's foreground wait has something exact to compare against.
    /// </summary>
    public static async Task<ResolvedTarget> ActivateAsync(ResolvedTarget target, CancellationToken ct)
    {
        if (target.ProcessId is null && target.MainWindowHandle == 0)
            Launch(target);

        if (target.MainWindowHandle == 0)
            target = await WaitForWindow(target, ct).ConfigureAwait(false) ?? target;

        if (target.MainWindowHandle != 0)
            Win32.BringToForeground(target.MainWindowHandle);
        else if (target.ProcessId is { } pid)
            BringMainWindowForward(pid);
        return target;
    }

    /// <summary>
    /// A running process beats a fresh install lookup: the user expects the app
    /// they already have open, and activation is cheaper than a cold start.
    /// Prefers a process that actually owns a window.
    /// </summary>
    private static ResolvedTarget? FindRunning(WindowsForwardTarget spec)
    {
        ResolvedTarget? windowless = null;
        foreach (var name in spec.ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
            }
            catch
            {
                continue;
            }
            foreach (var process in processes)
            {
                using (process)
                {
                    string? path = null;
                    try { path = process.MainModule?.FileName; }
                    catch
                    {
                        // Elevated or protected process — the window handle still works.
                    }
                    var hwnd = SafeMainWindowHandle(process);
                    if (hwnd != 0)
                        return new ResolvedTarget(spec, process.Id, hwnd, path);
                    windowless ??= new ResolvedTarget(spec, process.Id, 0, path);
                }
            }
        }
        return windowless;
    }

    private static string? FindExecutable(WindowsForwardTarget spec)
    {
        foreach (var candidate in spec.ExeCandidates)
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (Path.IsPathRooted(expanded))
            {
                if (File.Exists(expanded))
                    return Path.GetFullPath(expanded);
                continue;
            }
            // Bare file name: the shell would resolve it through PATH and
            // App Paths at launch time; we only scan PATH — App Paths
            // resolution is delegated to UseShellExecute at launch.
            var found = SearchPath(expanded);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static string? SearchPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // A malformed PATH entry is the user's problem, not ours.
            }
        }
        return null;
    }

    private static void Launch(ResolvedTarget target)
    {
        try
        {
            if (target.ExePath is { } exe)
            {
                // UseShellExecute = false returns a Process with the real id,
                // which is what the foreground wait compares against.
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = false });
            }
            else if (target.Spec.Aumid is { } aumid)
            {
                // The documented way to launch a packaged app by AUMID.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{aumid}",
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
            // A launch that never started is reported by the caller's foreground
            // timeout — the files are already on the clipboard.
        }
    }

    /// <summary>
    /// Polls for the launched (or windowless running) process to show a window,
    /// refreshing the resolved target once it does.
    /// </summary>
    private static async Task<ResolvedTarget?> WaitForWindow(ResolvedTarget target, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)LaunchTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (target.ProcessId is { } pid)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (process is { HasExited: false } && process.MainWindowHandle != 0)
                        return target with { MainWindowHandle = process.MainWindowHandle };
                }
                catch
                {
                    // Exited between resolve and poll — the foreground wait reports it.
                    return null;
                }
            }
            else
            {
                // A bare-name launch or AUMID: the process appears under one of
                // the spec's names once it is up.
                if (FindRunning(target.Spec) is { } running)
                    return running;
            }
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Foregrounds the main window of a known process, waiting briefly for it to appear.</summary>
    private static void BringMainWindowForward(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var hwnd = SafeMainWindowHandle(process);
            if (hwnd != 0)
                Win32.BringToForeground(hwnd);
        }
        catch
        {
            // The process exited mid-forward; the foreground wait reports it.
        }
    }

    private static nint SafeMainWindowHandle(Process process)
    {
        try
        {
            return process.MainWindowHandle;
        }
        catch
        {
            return 0;
        }
    }
}
