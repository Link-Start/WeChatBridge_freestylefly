using System.Threading;
using System.Windows;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

public partial class App : Application
{
    private const string MutexName = "Local\\WeChatBridge.Windows.Main";
    private const string ChangeEventName = "Local\\WeChatBridge.Windows.InboxChanged";

    private Mutex? _mutex;
    private EventWaitHandle? _changeEvent;
    private CancellationTokenSource? _shutdown;
    private InboxPaths? _paths;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _paths = new InboxPaths();
        _paths.EnsureCreated();

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        // Debris from a helper that was killed mid-copy. Swept by the owning instance
        // only, and it runs long after any such copy would have died. Matches the macOS
        // app, which prunes on start for the same reason.
        _paths.PruneStaging();

        _changeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ChangeEventName);
        _shutdown = new CancellationTokenSource();
        var requestedBatch = ReadArgument(e.Args, "--batch-id");
        var window = new MainWindow(_paths, requestedBatch);
        MainWindow = window;
        window.Show();
        _ = WaitForChangesAsync(_shutdown.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown?.Cancel();
        _changeEvent?.Dispose();
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
                await Task.Run(() => _changeEvent?.WaitOne(500), cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                    Dispatcher.Invoke(() => (MainWindow as MainWindow)?.RefreshBatches());
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
            using var signal = EventWaitHandle.OpenExisting(ChangeEventName);
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
