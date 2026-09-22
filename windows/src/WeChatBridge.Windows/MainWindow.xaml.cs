using System.IO;
using System.Text.Json;
using System.Windows;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

public partial class MainWindow : Window
{
    private readonly InboxPaths _paths;
    private readonly string? _requestedBatch;

    public MainWindow(InboxPaths paths, string? requestedBatch)
    {
        _paths = paths;
        _requestedBatch = requestedBatch;
        InitializeComponent();
        RefreshBatches();
    }

    public void RefreshBatches()
    {
        try
        {
            _paths.EnsureCreated();
            var batches = Directory.EnumerateDirectories(_paths.Ready)
                .Select(ReadBatch)
                .Where(batch => batch is not null)
                .Cast<BatchSummary>()
                .OrderByDescending(batch => batch.CreatedAt)
                .ToList();
            BatchList.ItemsSource = batches;
            var failure = Directory.EnumerateFiles(_paths.Failed, "*.json")
                .Select(ReadFailure)
                .Where(item => item is not null)
                .Cast<FailureSummary>()
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault();
            StatusText.Text = batches.Count == 0 && failure is null
                ? "等待微信分享。"
                : $"已发现 {batches.Count} 个已提交批次。" +
                  (_requestedBatch is null ? string.Empty : $" 请求批次：{_requestedBatch}") +
                  (failure is null ? string.Empty : $" 最近失败：{failure.Message}");
            InboxText.Text = $"Inbox：{_paths.Root}";
        }
        catch (Exception error)
        {
            StatusText.Text = $"读取 Inbox 失败：{error.Message}";
            InboxLogger.Write(_paths, "主程序读取 Inbox 失败", error);
        }
    }

    private static BatchSummary? ReadBatch(string directory)
    {
        try
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            var manifest = JsonSerializer.Deserialize<BatchManifest>(
                File.ReadAllText(manifestPath), BatchManifest.JsonOptions);
            if (manifest is null)
                return null;
            return new BatchSummary(
                manifest.BatchId.ToString("D"),
                manifest.CreatedAt.ToLocalTime(),
                string.Join(", ", manifest.Items.Select(item => item.DisplayName)));
        }
        catch
        {
            return null;
        }
    }

    private static FailureSummary? ReadFailure(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var createdAt = root.TryGetProperty("createdAt", out var created)
                ? created.GetDateTimeOffset()
                : File.GetLastWriteTimeUtc(path);
            var message = root.TryGetProperty("message", out var error)
                ? error.GetString() ?? "未知错误"
                : "未知错误";
            return new FailureSummary(createdAt.ToLocalTime(), message);
        }
        catch
        {
            return null;
        }
    }

    private sealed record BatchSummary(string BatchId, DateTimeOffset CreatedAt, string Files)
    {
        public string Display => $"{CreatedAt:yyyy-MM-dd HH:mm:ss}  {Files}\n{BatchId}";
    }

    private sealed record FailureSummary(DateTimeOffset CreatedAt, string Message);
}
