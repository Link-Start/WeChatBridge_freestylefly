using System.IO;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

/// <summary>
/// The real per-agent marks, shipped loose next to the exe so WPF can bind a
/// plain file path — the Windows counterpart of macOS's bundled
/// <c>Resources/AppLogos/*.png</c> + AppLogos.name(for:).
///
/// Files live in <c>Assets/AppLogos</c> under <see cref="AppContext.BaseDirectory"/>;
/// a missing logo resolves to null and callers fall back to a letter badge.
/// </summary>
public static class AppLogos
{
    private static readonly IReadOnlyDictionary<AgentId, string> ByAgent =
        new Dictionary<AgentId, string>
        {
            [AgentId.ChatGptCodex] = "04-chatgpt.png",
            [AgentId.Claude] = "03-claude.png",
            [AgentId.Doubao] = "01-doubao.png",
            [AgentId.QwenWork] = "02-qwen.png",
            [AgentId.WorkBuddy] = "06-workbuddy.png",
            [AgentId.WeSight] = "07-wesight.png",
        };

    /// <summary>Full path to the agent's logo, or null when the file is absent.</summary>
    public static string? PathFor(AgentId agent) =>
        ByAgent.TryGetValue(agent, out var file) ? Existing(file) : null;

    /// <summary>
    /// Logo for a share action / forward destination: the action's own agent
    /// plus the two pseudo-agents macOS also badges (Obsidian, clipboard has
    /// none).
    /// </summary>
    public static string? PathFor(ShareAction action) =>
        action == ShareAction.Obsidian
            ? Existing("05-obsidian.png")
            : AgentIds.Matching(action) is { } agent ? PathFor(agent) : null;

    /// <summary>Loose lookup by target display name for custom targets.</summary>
    public static string? PathFor(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;
        foreach (var (agent, file) in ByAgent)
            if (displayName.Contains(agent.DisplayName(), StringComparison.OrdinalIgnoreCase))
                return Existing(file);
        if (displayName.Contains("Obsidian", StringComparison.OrdinalIgnoreCase))
            return Existing("05-obsidian.png");
        return null;
    }

    private static string? Existing(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogos", file);
        return File.Exists(path) ? path : null;
    }
}
