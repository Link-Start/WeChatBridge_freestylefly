using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// A target that can carry an Agent Skill. Ported from AgentID.swift; raw values
/// are identical on both platforms because scenes and catalog JSON name them.
/// </summary>
[JsonConverter(typeof(AgentIdConverter))]
public enum AgentId
{
    ChatGptCodex,
    Claude,
    Doubao,
    QwenWork,
    WorkBuddy,
    WeSight,
}

public static class AgentIds
{
    public static string RawValue(this AgentId id) => id switch
    {
        AgentId.ChatGptCodex => "chatGPTCodex",
        AgentId.Claude => "claude",
        AgentId.Doubao => "doubao",
        AgentId.QwenWork => "qwenWork",
        AgentId.WorkBuddy => "workBuddy",
        AgentId.WeSight => "weSight",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static AgentId? FromRawValue(string raw) => raw switch
    {
        "chatGPTCodex" => AgentId.ChatGptCodex,
        "claude" => AgentId.Claude,
        "doubao" => AgentId.Doubao,
        "qwenWork" => AgentId.QwenWork,
        "workBuddy" => AgentId.WorkBuddy,
        "weSight" => AgentId.WeSight,
        _ => null,
    };

    public static string DisplayName(this AgentId id) => id switch
    {
        AgentId.ChatGptCodex => "ChatGPT / Codex",
        AgentId.Claude => "Claude",
        AgentId.Doubao => "豆包",
        AgentId.QwenWork => "千问办公",
        AgentId.WorkBuddy => "WorkBuddy",
        AgentId.WeSight => "WeSight",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    /// <summary>
    /// Home-relative skill directories, identical on Windows: the agents keep the
    /// same layout under the user profile (%USERPROFILE%\.codex\skills etc.).
    /// Null means the app accepts skills only through its own import UI.
    /// </summary>
    public static string? DirectSkillRoot(this AgentId id) => id switch
    {
        AgentId.ChatGptCodex => ".codex/skills",
        AgentId.QwenWork => ".qwenworkcn/skills",
        AgentId.WorkBuddy => ".workbuddy/skills",
        AgentId.Claude or AgentId.Doubao or AgentId.WeSight => null,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static AgentId? Matching(ShareAction action) => action switch
    {
        ShareAction.Codex => AgentId.ChatGptCodex,
        ShareAction.Claude => AgentId.Claude,
        ShareAction.Doubao => AgentId.Doubao,
        ShareAction.Qwen => AgentId.QwenWork,
        ShareAction.WorkBuddy => AgentId.WorkBuddy,
        ShareAction.WeSight => AgentId.WeSight,
        _ => null,
    };

    public static IReadOnlyList<AgentId> All { get; } = Enum.GetValues<AgentId>();
}

public sealed class AgentIdConverter : JsonConverter<AgentId>
{
    public override AgentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return raw is not null && AgentIds.FromRawValue(raw) is { } id
            ? id
            : throw new JsonException($"Unknown agent id: {raw}");
    }

    public override void Write(Utf8JsonWriter writer, AgentId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.RawValue());
}

/// <summary>One official skill package shipped with the app.</summary>
public sealed record OfficialSkill(
    string Id,
    string Name,
    string Summary,
    string Version,
    /// <summary>Directory under Resources/Skills; null = not shipped yet.</summary>
    string? Package,
    IReadOnlyList<AgentId> SupportedAgents);

public sealed record OfficialSkillCatalog(int SchemaVersion, IReadOnlyList<OfficialSkill> Skills)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// catalog.json is authored snake_case (schema_version, supported_agents);
    /// macOS decodes with .convertFromSnakeCase, so this loader must not reuse
    /// the camelCase inbox options.
    /// </summary>
    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new AgentIdConverter() },
    };

    public static OfficialSkillCatalog LoadFrom(string resourcesRoot)
    {
        var path = Path.Combine(resourcesRoot, "Skills", "catalog.json");
        var catalog = JsonSerializer.Deserialize<OfficialSkillCatalog>(
            File.ReadAllText(path), CatalogJsonOptions)
            ?? throw new SkillInstallException("技能清单内容无效。");
        if (catalog.SchemaVersion != CurrentSchemaVersion)
            throw new SkillInstallException("技能清单版本不受支持。");
        if (catalog.Skills.Select(s => s.Id).Distinct().Count() != catalog.Skills.Count
            || catalog.Skills.Any(s => string.IsNullOrEmpty(s.Id)))
            throw new SkillInstallException("技能清单内容无效。");
        return catalog;
    }
}

public sealed class SkillInstallException(string message) : Exception(message);
