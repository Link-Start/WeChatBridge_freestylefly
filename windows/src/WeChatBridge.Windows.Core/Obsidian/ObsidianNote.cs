using System.Globalization;
using System.Text;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// Turns one verified WeChat archive into a self-contained Markdown note.
/// Ported from <c>ObsidianNote.swift</c>.
/// </summary>
public static class ObsidianNote
{
    /// <summary>
    /// Extensions macOS resolves through <c>UTType.conforms(to:)</c> — an
    /// embedded <c>![[..]]</c> link only makes sense for media Obsidian can
    /// render, everything else is a plain attachment link.
    /// </summary>
    private static readonly HashSet<string> EmbeddableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // image
        "png", "jpg", "jpeg", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "avif", "svg", "ico",
        // movie
        "mp4", "mov", "m4v", "avi", "mkv", "wmv", "flv", "webm", "mpg", "mpeg", "3gp", "ts",
        // audio
        "mp3", "m4a", "aac", "wav", "flac", "ogg", "opus", "amr", "wma", "caf",
        // pdf
        "pdf",
    };

    /// <summary>
    /// A readable note title for Obsidian. The name captured from WeChat's title
    /// bar wins, matching the 群名 shown in 记录; transcript participants are
    /// only a fallback when OCR found nothing.
    /// </summary>
    public static string Title(
        string? chatName,
        WeChatNativeArchive.Transcript? transcript,
        string archiveName)
    {
        if (Usable(chatName) is { } usable)
            return usable.EndsWith("的聊天", StringComparison.Ordinal) ? usable : $"{usable}的聊天";
        var participants = OrderedParticipants(transcript);
        switch (participants.Count)
        {
            case 1:
                return $"{participants[0]}的聊天";
            case 2:
                return $"{participants[0]}与{participants[1]}的聊天";
            case > 2:
                return $"{participants[0]}等{participants.Count}人的聊天";
            default:
                break;
        }
        return Path.GetFileNameWithoutExtension(archiveName);
    }

    public static string Render(
        string title,
        string? chatName,
        string? sceneName,
        DateTimeOffset createdAt,
        WeChatNativeArchive.Transcript? transcript,
        string archiveName,
        IReadOnlyDictionary<string, string>? attachments = null,
        TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        string Stamp(DateTimeOffset value) =>
            TimeZoneInfo.ConvertTime(value, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        var yaml = new List<string>
        {
            "---",
            $"title: {Quoted(title)}",
            "source: WeChat",
        };
        if (!string.IsNullOrEmpty(chatName))
            yaml.Add($"chat: {Quoted(chatName)}");
        if (!string.IsNullOrEmpty(sceneName))
            yaml.Add($"scene: {Quoted(sceneName)}");
        yaml.Add($"exported: {Stamp(createdAt)}");
        if (transcript?.Records is { } counted)
            yaml.Add($"messages: {counted.Count}");
        yaml.Add($"archive: {Quoted($"附件/{archiveName}")}");
        yaml.Add("---");

        var lines = new List<string>(yaml)
        {
            "",
            $"# {title}",
            "",
            $"> 来源：微信 · 原始归档：[[附件/{archiveName}]]",
        };

        if (transcript is not null)
        {
            lines.Add("");
            lines.Add("## 聊天记录");
            if (transcript.Records is { Count: > 0 } records)
            {
                foreach (var record in records)
                {
                    lines.Add("");
                    lines.Add($"**{record.Sender}** · {Stamp(record.Date)}");
                    lines.Add("");
                    lines.Add(record.Text);
                    foreach (var attachment in ReferencedAttachments(record.Text, attachments))
                    {
                        lines.Add("");
                        lines.Add(AttachmentLink(attachment));
                    }
                }
            }
            else
            {
                lines.Add("");
                lines.Add(transcript.Body);
            }
        }
        else
        {
            lines.Add("");
            lines.Add("未能从原始归档中解析聊天文本。原始 ZIP 已保留，可在附件中打开。");
        }
        lines.Add("");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Longest UTF-8 name wins: 图片_1.jpg must not steal the hit that belongs
    /// to 图片_11.jpg sitting inside the same message.
    /// </summary>
    private static List<string> ReferencedAttachments(
        string text,
        IReadOnlyDictionary<string, string>? available)
    {
        var result = new List<string>();
        if (available is null || available.Count == 0)
            return result;
        var remaining = text;
        var ordered = available.Keys
            .OrderByDescending(k => Encoding.UTF8.GetByteCount(k))
            .ThenBy(k => k, StringComparer.Ordinal);
        foreach (var name in ordered)
        {
            if (name.Length == 0
                || !remaining.Contains(name, StringComparison.Ordinal)
                || !available.TryGetValue(name, out var saved))
            {
                continue;
            }
            result.Add(saved);
            remaining = remaining.Replace(name, "", StringComparison.Ordinal);
        }
        return result;
    }

    private static string AttachmentLink(string name)
    {
        var path = $"附件/{name}"
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("#", "\\#", StringComparison.Ordinal)
            .Replace("^", "\\^", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
        var extension = Path.GetExtension(name).TrimStart('.');
        return EmbeddableExtensions.Contains(extension) ? $"![[{path}]]" : $"[[{path}]]";
    }

    private static string Quoted(string value) =>
        "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static List<string> OrderedParticipants(WeChatNativeArchive.Transcript? transcript)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var participants = new List<string>();
        foreach (var record in transcript?.Records ?? [])
        {
            var sender = record.Sender.Trim();
            if (sender.Length > 0 && seen.Add(sender))
                participants.Add(sender);
        }
        return participants;
    }

    private static string? Usable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
