namespace WeChatBridge.Windows.Core;

/// <summary>How one referenced skill reaches the destination agent.</summary>
public enum SkillRenderMode
{
    /// <summary>Installed (or confirmed) in the agent's own skill system — name it.</summary>
    Native,

    /// <summary>Not installed natively, but the library copy is readable — point at its SKILL.md.</summary>
    Path,

    /// <summary>Known skill the agent cannot use — ask it to proceed and flag gaps.</summary>
    Missing,

    /// <summary>Neither the catalog nor the library knows this id.</summary>
    Unknown,
}

/// <summary>One skill resolved for one destination.</summary>
public sealed record SkillResolution(
    string Id,
    string DisplayName,
    SkillRenderMode Mode,
    string? SkillFile = null);

/// <summary>
/// Everything <see cref="ScenePrompt"/> needs to turn <c>{{skill:id}}</c> into
/// agent-facing text: the destination (null = clipboard / custom app with no
/// known agent) and a resolver that knows the catalog, library and installs.
/// </summary>
public sealed class SkillRenderContext(AgentId? agent, Func<string, SkillResolution> resolve)
{
    public AgentId? Agent { get; } = agent;

    public SkillResolution Resolve(string id) => resolve(id);

    /// <summary>
    /// The rendered phrase. It reads naturally after a verb ("用 …提取"), so the
    /// same text works inline and as a bullet in the 技能要求 section.
    /// </summary>
    public string Phrase(string id)
    {
        var skill = Resolve(id);
        return skill.Mode switch
        {
            SkillRenderMode.Native => $"「{skill.DisplayName}」技能（{skill.Id}）",
            SkillRenderMode.Path =>
                $"「{skill.DisplayName}」技能（技能说明：`{skill.SkillFile}`，请先阅读并严格按其执行）",
            SkillRenderMode.Missing when Agent is null => $"「{skill.DisplayName}」技能",
            SkillRenderMode.Missing =>
                $"「{skill.DisplayName}」技能（本机未安装，请直接完成，并注明未验证的部分）",
            _ => $"「{skill.Id}」技能（未找到该技能）",
        };
    }
}
