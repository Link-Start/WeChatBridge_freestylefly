using System.Threading;
using System.Windows;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

public partial class App : Application
{
    private const string MutexName = "Local\\WeChatBridge.Windows.Main";
    private const string ChangeEventName = "Local\\WeChatBridge.Windows.InboxChanged";
    /// <summary>
    /// A second *interactive* launch asks for the window. Kept separate from
    /// <see cref="ChangeEventName"/>: a share must never pull our window to the
    /// front — the delivery engine is busy foregrounding the target app, and a
    /// window that grabs focus mid-paste steals the Ctrl+V.
    /// </summary>
    private const string ForegroundEventName = "Local\\WeChatBridge.Windows.Foreground";

    private Mutex? _mutex;
    private EventWaitHandle? _changeEvent;
    private EventWaitHandle? _foregroundEvent;
    private CancellationTokenSource? _shutdown;
    private InboxPaths? _paths;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _paths = new InboxPaths();
        _paths.EnsureCreated();

        var background = e.Args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // A share that arrives while we are running spawns a second
            // --background instance (the helper always launches us). It must
            // exit quietly — foregrounding belongs to interactive launches
            // only, otherwise every share would steal focus mid-delivery.
            if (!background)
                SignalExistingInstance();
            Shutdown();
            return;
        }

        // Debris from a helper that was killed mid-copy. Swept by the owning instance
        // only, and it runs long after any such copy would have died. Matches the macOS
        // app, which prunes on start for the same reason.
        _paths.PruneStaging();

        _changeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ChangeEventName);
        _foregroundEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ForegroundEventName);
        _shutdown = new CancellationTokenSource();
        var requestedBatch = ReadArgument(e.Args, "--batch-id");
        var model = new MainViewModel(_paths);
        var window = new MainWindow(model, requestedBatch);
        MainWindow = window;
        // The share helper launches us with --background purely so the fresh
        // intent is consumed and the forward runs. Showing the window here
        // would fight the target app for the foreground.
        if (!background)
            window.Show();
        // MainWindow's inbox load rides the Loaded event — a hidden window
        // never raises it, so a background launch must load explicitly or it
        // would idle forever with intents unconsumed (seen live 2026-09-25:
        // helper relaunched us hidden and two shares were never delivered).
        Dispatcher.BeginInvoke(() =>
        {
            model.RebuildEntries();
            window.RefreshBatches();
        });
        _ = WaitForChangesAsync(_shutdown.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown?.Cancel();
        _changeEvent?.Dispose();
        _foregroundEvent?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    private async Task WaitForChangesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var signalled = await Task.Run(
                    () => WaitHandle.WaitAny(new WaitHandle?[] { _changeEvent, _foregroundEvent }
                        .OfType<WaitHandle>().ToArray(), 500),
                    cancellationToken);
                if (signalled == WaitHandle.WaitTimeout || cancellationToken.IsCancellationRequested)
                    continue;

                Dispatcher.Invoke(() =>
                {
                    if (MainWindow is not MainWindow window)
                        return;
                    window.RefreshBatches();
                    // Only an explicit second launch pulls the window forward —
                    // the share helper's inbox signal just refreshes the list.
                    if (signalled == 1)
                        window.BringToFront();
                });
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                if (_paths is not null)
                    InboxLogger.Write(_paths, "监听 Inbox 失败", error);
                return;
            }
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ForegroundEventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }
}
