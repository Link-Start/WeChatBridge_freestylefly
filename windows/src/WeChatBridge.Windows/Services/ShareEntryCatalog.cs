using System.IO;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Builds the rows a 「微信流」 share can be answered with — the enabled
/// built-in entries plus every custom target, in the same order the macOS
/// 发给 ▸ menu inlines them. Used by both the main app's fallback picker and
/// the share-target helper, which asks the same question inside its own
/// process so the panel is not queued behind the app's startup.
/// </summary>
public static class ShareEntryCatalog
{
    /// <summary>The picker's option list, in display order.</summary>
    public static List<EntryPickerOption> BuildOptions(
        AppSettings settings,
        IReadOnlyList<ForwardTarget> targets)
    {
        var options = new List<EntryPickerOption>();
        foreach (var action in ShareActions.All)
        {
            // Custom does not appear as its own row — the user's own apps are
            // listed individually below, the same way the macOS 发给 ▸ menu
            // inlines them.
            if (action == ShareAction.Custom || action == ShareAction.Hub)
                continue;
            if (!settings.IsEntryEnabled(action))
                continue;
            options.Add(new EntryPickerOption(action, null, Detail(action, settings, targets)));
        }
        if (settings.IsEntryEnabled(ShareAction.Custom))
        {
            foreach (var target in targets)
                options.Add(new EntryPickerOption(ShareAction.Custom, target, target.BundleIdentifier));
        }
        return options;
    }

    /// <summary>The one-line hint under an entry's name — shared by the picker
    /// rows and the 入口 pane's per-entry detail.</summary>
    public static string Detail(
        ShareAction action,
        AppSettings settings,
        IReadOnlyList<ForwardTarget> targets) => action switch
    {
        ShareAction.Codex => "激活 ChatGPT 并直接粘贴到输入框。",
        ShareAction.Claude => "激活 Claude 并直接粘贴到输入框。",
        ShareAction.Doubao => "激活豆包，把压缩包路径粘贴到输入框。",
        ShareAction.Qwen => "激活千问，把压缩包路径粘贴到输入框。",
        ShareAction.WorkBuddy => "激活 WorkBuddy 并直接粘贴到输入框。",
        ShareAction.WeSight => "激活 WeSight 并直接粘贴到输入框。",
        ShareAction.Obsidian => settings.ObsidianVaultPath is { Length: > 0 } path
            ? $"知识库：{new DirectoryInfo(path).Name}"
            : "未选择知识库",
        ShareAction.Clipboard => "只复制，不自动粘贴",
        ShareAction.Custom => targets.Count == 0 ? "未添加应用" : $"{targets.Count} 个应用",
        _ => string.Empty,
    };
}
