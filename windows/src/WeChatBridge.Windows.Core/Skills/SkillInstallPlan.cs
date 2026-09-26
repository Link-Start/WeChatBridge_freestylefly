namespace WeChatBridge.Windows.Core;

/// <summary>
/// Where one skill would land for one agent. Ported from <c>SkillInstallPlan</c>
/// in AgentID.swift: <see cref="SkillInstallMethod.Direct"/> carries the resolved
/// source/target pair, <see cref="SkillInstallMethod.Manual"/> the per-agent
/// import guide, and <see cref="SkillInstallMethod.Unavailable"/> means the
/// package was not shipped in this build. The Swift enum cases become nested
/// records so payloads stay type-safe and value equality matches the Swift
/// <c>Equatable</c> conformance.
/// </summary>
public sealed record SkillInstallPlan(
    string SkillId,
    AgentId Agent,
    bool AgentInstalled,
    SkillInstallMethod Method);

public abstract record SkillInstallMethod
{
    private SkillInstallMethod() { }

    /// <summary>
    /// Copy the package directory into the agent's home-relative skill root.
    /// <paramref name="Source"/> is under <c>Resources/Skills</c>;
    /// <paramref name="Target"/> is the agent's skill directory for this skill.
    /// </summary>
    public sealed record Direct(string Source, string Target) : SkillInstallMethod;

    /// <summary>The agent only accepts skills through its own import UI.</summary>
    public sealed record Manual(string Guide) : SkillInstallMethod;

    /// <summary>No package is bundled for this skill yet.</summary>
    public sealed record Unavailable : SkillInstallMethod;
}

/// <summary>
/// The install state of one skill on one agent. Ported from
/// <c>SkillAgentStatus</c>; like the Swift enum the cases compare by value, so
/// tests assert whole values rather than inspecting flags.
/// </summary>
public abstract record SkillAgentStatus
{
    private SkillAgentStatus() { }

    public sealed record PackageUnavailable : SkillAgentStatus;

    /// <summary>A manual-only agent that is itself not installed.</summary>
    public sealed record AgentUnavailable : SkillAgentStatus;

    public sealed record NotInstalled : SkillAgentStatus;

    public sealed record Installed(string Version) : SkillAgentStatus;

    /// <remarks>
    /// The parameters can't be named Installed/Available verbatim — Installed is
    /// already a sibling nested type, which breaks the positional-property rule.
    /// </remarks>
    public sealed record UpdateAvailable(
        string InstalledVersion,
        string AvailableVersion) : SkillAgentStatus;

    public sealed record VersionConflict(string Detail) : SkillAgentStatus;

    public sealed record ManualOnly : SkillAgentStatus;

    public sealed record ManualConfirmed(string Version) : SkillAgentStatus;
}

public static class AgentSkillGuides
{
    /// <summary>
    /// Per-agent import instructions, ported verbatim from
    /// <c>AgentID.manualInstallGuide</c>. The extension lives next to the
    /// installer rather than inside <see cref="AgentIds"/> because AgentID.cs is
    /// a fixed contract file owned by the core port.
    /// </summary>
    public static string? ManualInstallGuide(this AgentId id) => id switch
    {
        AgentId.ChatGptCodex =>
            "Codex 模式会读取 ~/.codex/skills；Chat 或 Work 模式请在 Plugins / Skills 中导入。",
        AgentId.Claude =>
            "在 Claude 的 Settings → Capabilities → Skills 中上传技能包。",
        AgentId.Doubao =>
            "在豆包的“工作 → 技能·连接器·伙伴 → 我的技能 → 新建 → 上传技能”中导入。",
        AgentId.QwenWork or AgentId.WorkBuddy or AgentId.WeSight => null,
        _ => null,
    };

    /// <summary>
    /// Whether the agent can open an arbitrary local file named in the prompt —
    /// the gate for pointing it at the library's SKILL.md. Desktop agents with
    /// a local workspace can; chat-first apps generally cannot. 待实测: these
    /// defaults are conservative until each agent is verified on a real machine.
    /// </summary>
    public static bool CanReadLocalFiles(this AgentId id) => id switch
    {
        AgentId.ChatGptCodex or AgentId.QwenWork or AgentId.WorkBuddy => true,
        _ => false,
    };
}
