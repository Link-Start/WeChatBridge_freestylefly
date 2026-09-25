using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using WeChatBridge.Windows.Core;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WpfClipboard = System.Windows.Clipboard;

namespace WeChatBridge.ShareTarget;

internal static class Program
{
    private const string ChangeEventName = "Local\\WeChatBridge.Windows.InboxChanged";

    [STAThread]
    private static async Task Main(string[] args)
    {
        var paths = new InboxPaths();

        try
        {
            // The sparse package gives this process package identity, so the inbox
            // Windows.ApplicationModel.AppInstance API delivers the activation
            // arguments directly. The WAS AppLifecycle equivalent needs a
            // WindowsAppRuntime PackageDependency that the sparse manifest does not
            // declare and fails with REGDB_E_CLASSNOTREG.
            var activated = AppInstance.GetActivatedEventArgs();

            if (activated is null)
            {
                InboxLogger.Write(paths, "未取得 Share Target 激活参数。");
                return;
            }

            if (activated.Kind != ActivationKind.ShareTarget)
            {
                InboxLogger.Write(paths, $"收到非分享激活：{activated.Kind}");
                return;
            }

            // For share activation the returned args object itself implements
            // IShareTargetActivatedEventArgs.
            if (activated is not IShareTargetActivatedEventArgs shareArgs)
            {
                InboxLogger.Write(paths, $"Share Target 激活参数类型不匹配：{activated.GetType().FullName}");
                return;
            }

            await HandleShareAsync(shareArgs);
        }
        catch (Exception error)
        {
            InboxLogger.Write(paths, "Share Target 激活初始化失败", error);
        }
    }

    private static async Task HandleShareAsync(IShareTargetActivatedEventArgs args)
    {
        var paths = new InboxPaths();
        var operation = args.ShareOperation;
        operation.ReportStarted();

        try
        {
            if (!operation.Data.Contains(StandardDataFormats.StorageItems))
                throw new InboxValidationException("PoC 只接收文件型分享，当前分享不包含 StorageItems。");

            var storageItems = await operation.Data.GetStorageItemsAsync();
            if (storageItems.Count == 0)
                throw new InboxValidationException("分享中没有文件。");

            var sources = new List<InboxSourceFile>(storageItems.Count);
            for (var itemIndex = 0; itemIndex < storageItems.Count; itemIndex++)
            {
                if (storageItems[itemIndex] is not StorageFile file)
                    throw new InboxValidationException("分享项不是可读取的文件。");

                var sourcePath = file.Path;
                if (string.IsNullOrWhiteSpace(sourcePath))
                    throw new InboxValidationException($"无法读取分享文件：{file.Name}");

                sources.Add(new InboxSourceFile(
                    sourcePath,
                    file.Name,
                    file.ContentType,
                    itemIndex,
                    0));
            }

            var committed = await InboxWriter.CommitAsync(paths, sources);
            // The batch is durable at this point, so everything below is an
            // optimisation that is allowed to fail. Sweeping here is what keeps
            // debris from a share that was killed mid-copy from accumulating.
            paths.PruneStaging();
            var clipboardWritten = TryWriteClipboard(committed.Manifest.Items, committed.BatchDirectory, paths);
            SignalMainProcess();
            StartMainProcess(committed.BatchId);
            InboxLogger.Write(paths, $"分享批次已提交：{committed.BatchId}; clipboard={clipboardWritten}");
            operation.ReportDataRetrieved();
            operation.ReportCompleted();
        }
        catch (Exception error)
        {
            InboxLogger.Write(paths, "Share Target 处理失败", error);
            operation.ReportError(error.Message);
        }
    }

    private static bool TryWriteClipboard(
        IReadOnlyList<ManifestItem> items,
        string batchDirectory,
        InboxPaths paths)
    {
        try
        {
            var files = new StringCollection();
            foreach (var item in items)
            {
                var path = Path.GetFullPath(Path.Combine(batchDirectory, item.RelativePath));
                if (!File.Exists(path))
                    throw new FileNotFoundException("批次文件不存在", path);
                files.Add(path);
            }

            Exception? clipboardError = null;
            var clipboardThread = new Thread(() =>
            {
                try
                {
                    WpfClipboard.SetFileDropList(files);
                }
                catch (Exception error)
                {
                    clipboardError = error;
                }
            });
            clipboardThread.SetApartmentState(ApartmentState.STA);
            clipboardThread.Start();
            clipboardThread.Join();

            if (clipboardError is not null)
                ExceptionDispatchInfo.Capture(clipboardError).Throw();

            return true;
        }
        catch (Exception error)
        {
            InboxLogger.Write(paths, "写入 FileDropList 失败；批次已保留", error);
            return false;
        }
    }

    private static void SignalMainProcess()
    {
        try
        {
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ChangeEventName);
            signal.Set();
        }
        catch
        {
            // The main process will discover Ready batches during its next scan.
        }
    }

    private static void StartMainProcess(Guid batchId)
    {
        var root = Directory.GetParent(AppContext.BaseDirectory)?.FullName;
        if (root is null)
            return;

        var mainPath = Path.Combine(root, "WeChatBridge.Windows.exe");
        if (!File.Exists(mainPath))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = mainPath,
            Arguments = $"--background --batch-id {batchId:D}",
            WorkingDirectory = root,
            UseShellExecute = true
        });
    }
}
