using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core;

public sealed record InboxSourceFile(
    string SourcePath,
    string DisplayName,
    string? ContentType,
    int ItemIndex,
    int AttachmentIndex);

public sealed record ManifestItem(
    Guid Id,
    string DisplayName,
    string RelativePath,
    string? ContentType,
    long ByteCount,
    int ItemIndex,
    int AttachmentIndex);

public sealed record BatchManifest(
    int SchemaVersion,
    Guid BatchId,
    DateTimeOffset CreatedAt,
    string Action,
    IReadOnlyList<ManifestItem> Items)
{
    public const int CurrentSchemaVersion = 1;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed record BatchCommitResult(Guid BatchId, string BatchDirectory, BatchManifest Manifest);

public sealed class InboxPaths
{
    public InboxPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatBridge",
            "Inbox");
    }

    public string Root { get; }
    public string Staging => Path.Combine(Root, "Staging");
    public string Ready => Path.Combine(Root, "Ready");
    public string Failed => Path.Combine(Root, "Failed");
    public string Logs => Path.Combine(Root, "Logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(Ready);
        Directory.CreateDirectory(Failed);
        Directory.CreateDirectory(Logs);
    }
}

public static class InboxWriter
{
    public const long MaxFileBytes = 1_073_741_824;
    public const long MaxBatchBytes = 1_073_741_824;

    public static async Task<BatchCommitResult> CommitAsync(
        InboxPaths paths,
        IReadOnlyList<InboxSourceFile> sources,
        CancellationToken cancellationToken = default)
    {
        if (sources.Count == 0)
            throw new InboxValidationException("分享中没有可处理的文件。");

        paths.EnsureCreated();
        var batchId = Guid.NewGuid();
        var staging = Path.Combine(paths.Staging, $"{batchId:N}.staging");
        var ready = Path.Combine(paths.Ready, batchId.ToString("D"));
        var filesDirectory = Path.Combine(staging, "files");
        var committedItems = new List<ManifestItem>(sources.Count);
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        try
        {
            Directory.CreateDirectory(filesDirectory);

            for (var index = 0; index < sources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[index];
                ValidateSource(source);
                var sourceInfo = new FileInfo(source.SourcePath);
                if (sourceInfo.Length > MaxFileBytes)
                    throw new InboxValidationException($"文件过大：{source.DisplayName}");

                totalBytes = checked(totalBytes + sourceInfo.Length);
                if (totalBytes > MaxBatchBytes)
                    throw new InboxValidationException("分享批次过大。");

                var safeName = MakeUniqueFileName(SanitizeFileName(source.DisplayName), usedFileNames);
                var relativePath = Path.Combine("files", safeName).Replace('\\', '/');
                var destination = Path.Combine(filesDirectory, safeName);

                await using (var input = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                {
                    await input.CopyToAsync(output, cancellationToken);
                }

                committedItems.Add(new ManifestItem(
                    Guid.NewGuid(),
                    safeName,
                    relativePath,
                    source.ContentType,
                    sourceInfo.Length,
                    source.ItemIndex,
                    source.AttachmentIndex));
            }

            var manifest = new BatchManifest(
                BatchManifest.CurrentSchemaVersion,
                batchId,
                DateTimeOffset.UtcNow,
                "clipboard",
                committedItems);
            var manifestPath = Path.Combine(staging, "manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, BatchManifest.JsonOptions),
                cancellationToken);

            Directory.Move(staging, ready);
            return new BatchCommitResult(batchId, ready, manifest);
        }
        catch (Exception error)
        {
            TryDeleteDirectory(staging);
            TryWriteFailure(paths, batchId, error);
            throw;
        }
    }

    private static void ValidateSource(InboxSourceFile source)
    {
        if (string.IsNullOrWhiteSpace(source.SourcePath) || !File.Exists(source.SourcePath))
            throw new InboxValidationException($"找不到分享文件：{source.DisplayName}");
        if (Directory.Exists(source.SourcePath))
            throw new InboxValidationException($"分享项不是文件：{source.DisplayName}");
        var attributes = File.GetAttributes(source.SourcePath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InboxValidationException($"不接受符号链接或重解析点：{source.DisplayName}");
        if (!source.DisplayName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InboxValidationException($"PoC 只接收 ZIP：{source.DisplayName}");
    }

    private static string SanitizeFileName(string name)
    {
        var leaf = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(leaf))
            throw new InboxValidationException("分享文件名为空。");
        if (leaf.Any(char.IsControl) || leaf is "." or "..")
            throw new InboxValidationException("分享文件名不安全。");

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(leaf.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
            throw new InboxValidationException("分享文件名不安全。");
        return sanitized;
    }

    private static string MakeUniqueFileName(string name, ISet<string> usedNames)
    {
        if (usedNames.Add(name))
            return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 2; ; index++)
        {
            var candidate = $"{stem} ({index}){extension}";
            if (usedNames.Add(candidate))
                return candidate;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cleanup must never hide the original share failure.
        }
    }

    private static void TryWriteFailure(InboxPaths paths, Guid batchId, Exception error)
    {
        try
        {
            paths.EnsureCreated();
            var failurePath = Path.Combine(paths.Failed, $"{batchId:D}.json");
            var payload = new
            {
                schemaVersion = BatchManifest.CurrentSchemaVersion,
                batchId,
                createdAt = DateTimeOffset.UtcNow,
                message = error.Message
            };
            File.WriteAllText(failurePath, JsonSerializer.Serialize(payload, BatchManifest.JsonOptions));
        }
        catch
        {
            // Diagnostics are best effort; the caller still receives the original error.
        }
    }
}

public sealed class InboxValidationException(string message) : Exception(message);

public static class InboxLogger
{
    private static readonly object Gate = new();

    public static void Write(InboxPaths paths, string message, Exception? error = null)
    {
        try
        {
            paths.EnsureCreated();
            var line = $"{DateTimeOffset.UtcNow:O} {message}" + (error is null ? string.Empty : $" | {error}");
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(paths.Logs, "windows.log"), line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must not break the share path.
        }
    }
}
