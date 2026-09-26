using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Delivery;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Pill emphasis for skill rows. The app palette only ships neutral/live/warn,
/// so the macOS "bad" tone folds into <see cref="SkillTone.Warn"/>.
/// </summary>
public enum SkillTone
{
    Neutral,
    Live,
    Warn,
}

/// <summary>A skill card's coverage class — the port of SkillCoverageKind.</summary>
public enum SkillCoverage
{
    /// <summary>No agent state, or every state is PackageUnavailable.</summary>
    PackageUnavailable,
    Conflict,
    Update,
    Installed,
    Partial,
    NotInstalled,
}

/// <summary>What the card's single action button does — the port of SkillPrimaryAction.</summary>
public enum SkillRowAction
{
    /// <summary>Install (or update) every automatically-installable gap.</summary>
    Install,

    /// <summary>Write the 手动安装包 zip for the agent's own import UI.</summary>
    ExportArchive,

    /// <summary>Open or close the per-agent detail rows.</summary>
    Details,
}

/// <summary>
/// Aggregate result of an install sweep: how many landed, and the
/// "技能 / Agent（原因）" entries that still need a hand — the list macOS
/// collects as <c>failures</c>.
/// </summary>
public sealed record SkillInstallReport(int Installed, IReadOnlyList<string> Failures)
{
    public static readonly SkillInstallReport Empty = new(0, []);

    /// <summary>Failed count — kept separate so notices word counts, not list plumbing.</summary>
    public int Failed => Failures.Count;
}

/// <summary>
/// One agent line inside a skill card: the agent, its live
/// <see cref="SkillAgentStatus"/>, and the button affordances that status
/// implies — the port of macOS <c>AgentSkillState</c> +
/// <c>SkillAgentRow.actions</c>. Every label and visibility flag is precomputed
/// so the view binds text, not logic.
/// </summary>
public sealed class AgentSkillRow : INotifyPropertyChanged
{
    private SkillAgentStatus _status;
    private bool _isBusy;

    internal AgentSkillRow(AgentId agent, SkillAgentStatus status)
    {
        Agent = agent;
        _status = status;
    }

    /// <summary>The card this line belongs to — set when the row is built.</summary>
    internal SkillRow? Parent { get; set; }

    public AgentId Agent { get; }

    public string AgentName => Agent.DisplayName();

    /// <summary>One-letter stand-in for the macOS AppLogos badge.</summary>
    public string AgentMark => AgentName.Length > 0 ? AgentName[..1] : "?";

    /// <summary>True for agents that read a home-relative skills directory directly.</summary>
    public bool DirectInstall => Agent.DirectSkillRoot() is not null;

    /// <summary>
    /// The SHA-verified status — the installer recomputes the package digest on
    /// every read, so this row reflects the bytes on disk, not a remembered flag.
    /// </summary>
    public SkillAgentStatus Status
    {
        get => _status;
        internal set
        {
            if (Equals(_status, value))
                return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    /// <summary>Set while an install/uninstall/confirmation runs — buttons go quiet meanwhile.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        internal set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    /// <summary>Inverse of <see cref="IsBusy"/> for IsEnabled bindings.</summary>
    public bool Ready => !IsBusy;

    /// <summary>The pill text — same strings as the macOS row.</summary>
    public string StatusText => Status switch
    {
        SkillAgentStatus.PackageUnavailable => "缺技能包",
        SkillAgentStatus.AgentUnavailable => "仅手动",
        SkillAgentStatus.NotInstalled => "未安装",
        SkillAgentStatus.Installed installed => $"已安装 {installed.Version}",
        SkillAgentStatus.UpdateAvailable update =>
            $"可更新 {update.InstalledVersion} → {update.AvailableVersion}",
        SkillAgentStatus.VersionConflict => "版本冲突",
        SkillAgentStatus.ManualOnly => "仅手动安装",
        SkillAgentStatus.ManualConfirmed => "已确认安装",
        _ => "未知",
    };

    /// <summary>
    /// Tooltip/inline detail: the conflict reason, a version pair, or — for
    /// manual-only agents — the per-agent import guide. Null shows nothing.
    /// </summary>
    public string? StatusDetail => Status switch
    {
        SkillAgentStatus.VersionConflict conflict => conflict.Detail,
        SkillAgentStatus.UpdateAvailable update =>
            $"可更新 {update.InstalledVersion} → {update.AvailableVersion}",
        SkillAgentStatus.Installed installed => installed.Version,
        SkillAgentStatus.ManualConfirmed confirmed => confirmed.Version,
        SkillAgentStatus.ManualOnly => Agent.ManualInstallGuide(),
        _ => null,
    };

    public SkillTone Tone => Status switch
    {
        SkillAgentStatus.Installed or SkillAgentStatus.ManualConfirmed => SkillTone.Live,
        SkillAgentStatus.UpdateAvailable or SkillAgentStatus.VersionConflict
            or SkillAgentStatus.AgentUnavailable => SkillTone.Warn,
        _ => SkillTone.Neutral,
    };

    /// <summary>Counts toward the card's 已安装 X / Y — mirrors SkillRecord.isInstalled.</summary>
    public bool IsInstalled => Status is SkillAgentStatus.Installed
        or SkillAgentStatus.UpdateAvailable or SkillAgentStatus.VersionConflict
        or SkillAgentStatus.ManualConfirmed;

    /// <summary>NotInstalled/UpdateAvailable on a direct agent — the installer can fix it.</summary>
    public bool IsAutomaticMissing =>
        DirectInstall && Status is SkillAgentStatus.NotInstalled or SkillAgentStatus.UpdateAvailable;

    /// <summary>A manual-only agent still missing its import.</summary>
    public bool IsManualMissing => Status is SkillAgentStatus.ManualOnly;

    // Per-status affordances, ported from SkillAgentRow.actions.
    public bool ShowInstall => IsAutomaticMissing;
    public string InstallTitle => Status is SkillAgentStatus.UpdateAvailable ? "更新" : "安装";
    public bool ShowUninstall => Status is SkillAgentStatus.Installed;
    public bool ShowReplace => Status is SkillAgentStatus.VersionConflict;
    public bool ShowManualActions =>
        Status is SkillAgentStatus.ManualOnly or SkillAgentStatus.ManualConfirmed;
    public string ConfirmTitle => Status is SkillAgentStatus.ManualConfirmed ? "撤销确认" : "我已安装";
    public bool ShowAgentMissing => Status is SkillAgentStatus.AgentUnavailable;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One card in the skills list: the official skill, a line per supported agent,
/// and the aggregate classification that drives the header pill and the single
/// action button — the port of macOS <c>SkillRecord</c>.
/// </summary>
public sealed class SkillRow : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isBusy;

    internal SkillRow(
        OfficialSkill skill,
        int sceneCount,
        IReadOnlyList<AgentSkillRow> states,
        SkillLibraryState libraryState = SkillLibraryState.Missing)
    {
        Skill = skill;
        SceneCount = sceneCount;
        States = states;
        LibraryState = libraryState;
        foreach (var state in states)
            state.Parent = this;
    }

    public OfficialSkill Skill { get; }

    /// <summary>How many stored scenes reference this skill id (用于 N 个场景 badge).</summary>
    public int SceneCount { get; }

    /// <summary>The badge links to the scenes page only when something references the skill.</summary>
    public bool HasScenes => SceneCount > 0;

    /// <summary>Where the app-owned library copy stands — what path references point at.</summary>
    public SkillLibraryState LibraryState { get; }

    /// <summary>Library badge text; null hides it (a missing package already has its explainer).</summary>
    public string? LibraryText => LibraryState switch
    {
        SkillLibraryState.Ready => "技能库已就绪",
        SkillLibraryState.Conflict => "技能库副本已被修改",
        _ => null,
    };

    public bool LibraryConflict => LibraryState == SkillLibraryState.Conflict;

    public IReadOnlyList<AgentSkillRow> States { get; }

    /// <summary>Details open/closed — the card re-words its action when it flips.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    /// <summary>Set while a whole-card operation (导出 ZIP, 安装缺失项) runs.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        internal set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    public bool Ready => !IsBusy;

    /// <summary>Card glyph, mirroring SkillMark's symbol pick by skill id.</summary>
    public string Mark => Skill.Id.Contains("article", StringComparison.Ordinal) ? "文"
        : Skill.Id.Contains("video", StringComparison.Ordinal) ? "▶"
        : "◇";

    public int TotalAgentCount => States.Count;
    public int InstalledCount => States.Count(s => s.IsInstalled);
    public string CoverageText => $"已安装 {InstalledCount} / {TotalAgentCount}";
    public bool ShowCoverage => TotalAgentCount > 0;

    /// <summary>True when at least one agent line can be fixed by a direct install.</summary>
    public bool HasAutomaticMissing => States.Any(s => s.IsAutomaticMissing);

    /// <summary>True when at least one agent still needs the manual-import path.</summary>
    public bool HasManualMissing => States.Any(s => s.IsManualMissing);

    public SkillCoverage Kind
    {
        get
        {
            if (States.Count == 0
                || States.All(s => s.Status is SkillAgentStatus.PackageUnavailable))
                return SkillCoverage.PackageUnavailable;
            if (States.Any(s => s.Status is SkillAgentStatus.VersionConflict))
                return SkillCoverage.Conflict;
            if (States.Any(s => s.Status is SkillAgentStatus.UpdateAvailable))
                return SkillCoverage.Update;
            if (InstalledCount == TotalAgentCount)
                return SkillCoverage.Installed;
            return InstalledCount > 0 ? SkillCoverage.Partial : SkillCoverage.NotInstalled;
        }
    }

    public string StatusTitle => Kind switch
    {
        SkillCoverage.PackageUnavailable => "缺技能包",
        SkillCoverage.Conflict => "版本冲突",
        SkillCoverage.Update => "有更新",
        SkillCoverage.Installed => "已安装",
        SkillCoverage.Partial => "部分安装",
        _ => "未安装",
    };

    /// <summary>macOS maps installed→live and every other kind to warn/bad; the palette has warn.</summary>
    public SkillTone Tone => Kind == SkillCoverage.Installed ? SkillTone.Live : SkillTone.Warn;

    /// <summary>Shows the 技能包尚未随当前构建提供 explainer inside the details.</summary>
    public bool PackageMissing => Kind == SkillCoverage.PackageUnavailable;

    public SkillRowAction PrimaryAction => Kind switch
    {
        SkillCoverage.PackageUnavailable or SkillCoverage.Conflict or SkillCoverage.Installed
            => SkillRowAction.Details,
        _ => HasAutomaticMissing
            ? SkillRowAction.Install
            : HasManualMissing ? SkillRowAction.ExportArchive : SkillRowAction.Details,
    };

    public string? PrimaryTitle => Kind switch
    {
        SkillCoverage.PackageUnavailable or SkillCoverage.Conflict or SkillCoverage.Installed
            => IsExpanded ? "收起详情" : "查看详情",
        SkillCoverage.Update => HasAutomaticMissing ? "更新" : "查看详情",
        _ => HasAutomaticMissing
            ? (InstalledCount == 0 ? "安装" : "安装缺失项")
            : HasManualMissing ? "导出 ZIP" : (IsExpanded ? "收起详情" : "查看详情"),
    };

    /// <summary>Search over id/name/summary, case-insensitive — mirrors scopedRecords.</summary>
    public bool MatchesQuery(string needle) =>
        needle.Length == 0
        || Skill.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || Skill.Summary.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || Skill.Id.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>The card passes the agent filter when it lists that agent; null means 全部.</summary>
    public bool SupportsAgent(AgentId? agent) =>
        agent is null || States.Any(s => s.Agent == agent.Value);

    /// <summary>Re-raises every derived property after a child status changed.</summary>
    internal void NotifyStateChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The pane's only dependency: loads the official skill catalog, owns the
/// <see cref="SkillInstaller"/>, and keeps one observable
/// <see cref="AgentSkillRow"/> per (skill, agent) — the port of macOS
/// <c>SkillLibrary</c>.
///
/// <para>
/// Like <c>SceneService</c>, this file is compile-linked into the test assembly
/// and must stay free of WPF references (no <c>System.Windows</c>, no
/// Dispatcher). Async work hops through <see cref="Task.Run(Action)"/> and
/// resumes on the caller's context, so UI callers see property changes on the
/// UI thread without the file knowing what a dispatcher is.
/// </para>
/// </summary>
public sealed class SkillService : INotifyPropertyChanged
{
    /// <summary>How far above the app/current directory the resource hunt may climb.</summary>
    private const int MaxAncestorHops = 8;

    private readonly SkillInstaller _installer;
    private readonly Func<AgentId, bool> _agentInstalled;
    private readonly Func<IReadOnlyList<WeChatScene>> _scenes;
    private readonly Dictionary<AgentId, bool> _installedAgents = new();

    private OfficialSkillCatalog _catalog =
        new(OfficialSkillCatalog.CurrentSchemaVersion, []);
    private string? _loadError;
    private string? _libraryIssue;
    private bool _isBusy;

    /// <param name="resourcesRoot">
    /// The directory containing <c>Skills/catalog.json</c>; null auto-detects
    /// via <see cref="FindResourcesRoot()"/>. Tests pass every path explicitly
    /// so nothing touches the real profile.
    /// </param>
    /// <param name="homeDirectory">Agent skill roots are home-relative; null means %USERPROFILE%.</param>
    /// <param name="stateDirectory">Where SkillConfirmations.json lives; null means the Config dir.</param>
    /// <param name="agentInstalled">
    /// The macOS <c>InstalledApp.lookup</c> equivalent; null probes processes
    /// and exe candidates through <see cref="WindowsTargetResolver"/>.
    /// </param>
    /// <param name="scenes">Scene library read for the 用于 N 个场景 badge; null reads the store.</param>
    /// <param name="library">
    /// The app-owned skill library. Null uses the real profile location, or —
    /// when <paramref name="stateDirectory"/> is injected (tests) — Skills and
    /// SkillBackups folders beside it, so nothing escapes the sandbox.
    /// </param>
    public SkillService(
        string? resourcesRoot = null,
        string? homeDirectory = null,
        string? stateDirectory = null,
        Func<AgentId, bool>? agentInstalled = null,
        Func<IReadOnlyList<WeChatScene>>? scenes = null,
        SkillStore? library = null)
    {
        ResourcesRoot = resourcesRoot ?? FindResourcesRoot();
        _installer = new SkillInstaller(homeDirectory, stateDirectory);
        _agentInstalled = agentInstalled ?? DetectAgentInstalled;
        _scenes = scenes ?? LoadScenes;
        Library = library ?? (stateDirectory is null
            ? new SkillStore()
            : new SkillStore(
                root: Path.Combine(stateDirectory, "Skills"),
                configDirectory: stateDirectory,
                backupRoot: Path.Combine(stateDirectory, "SkillBackups")));
        Reload();
    }

    /// <summary>Where <c>Skills/catalog.json</c> was found; null means the build shipped none.</summary>
    public string? ResourcesRoot { get; }

    /// <summary>The app-owned skill library scene prompts point agents at.</summary>
    public SkillStore Library { get; }

    /// <summary>Last library sync problem (a conflict or I/O failure); null when clean.</summary>
    public string? LibraryIssue
    {
        get => _libraryIssue;
        private set => Set(ref _libraryIssue, value);
    }

    /// <summary>One card per catalog skill, in catalog order.</summary>
    public ObservableCollection<SkillRow> Rows { get; } = [];

    /// <summary>Catalog load failure text; null when the catalog is healthy.</summary>
    public string? LoadError
    {
        get => _loadError;
        private set => Set(ref _loadError, value);
    }

    /// <summary>True while an install sweep runs — the bulk button rests.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                OnPropertyChanged(nameof(CanInstallAll));
        }
    }

    public int SkillCount => _catalog.Skills.Count;
    public int PendingInstallCount => Rows.Count(r => r.HasAutomaticMissing);
    public int SupportedAgentCount =>
        _catalog.Skills.SelectMany(s => s.SupportedAgents).Distinct().Count();
    public bool AnyAutomaticMissing => Rows.Any(r => r.HasAutomaticMissing);
    public bool CanInstallAll => AnyAutomaticMissing && !IsBusy;

    /// <summary>Re-reads the catalog and every agent status — mirrors SkillLibrary.reload.</summary>
    public void Reload()
    {
        _installedAgents.Clear();
        if (ResourcesRoot is not { } root)
        {
            _catalog = new OfficialSkillCatalog(OfficialSkillCatalog.CurrentSchemaVersion, []);
            LoadError = "没有找到内置技能清单。";
        }
        else
        {
            try
            {
                _catalog = OfficialSkillCatalog.LoadFrom(root);
                LoadError = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or JsonException or SkillInstallException)
            {
                _catalog = new OfficialSkillCatalog(OfficialSkillCatalog.CurrentSchemaVersion, []);
                LoadError = error.Message;
            }
            SyncLibrary(root);
        }

        var expanded = Rows.Where(r => r.IsExpanded).Select(r => r.Skill.Id).ToHashSet();
        var scenes = _scenes();
        Rows.Clear();
        foreach (var skill in _catalog.Skills)
        {
            var row = new SkillRow(
                skill,
                scenes.Count(s => s.EffectiveSkillIDs().Contains(skill.Id)),
                skill.SupportedAgents
                    .Select(agent => new AgentSkillRow(agent, Status(skill, agent)))
                    .ToList(),
                Library.State(skill.Id));
            row.IsExpanded = expanded.Contains(skill.Id);
            Rows.Add(row);
        }
        OnPropertyChanged(nameof(SkillCount));
        OnPropertyChanged(nameof(SupportedAgentCount));
        OnPropertyChanged(nameof(PendingInstallCount));
        OnPropertyChanged(nameof(AnyAutomaticMissing));
        OnPropertyChanged(nameof(CanInstallAll));
    }

    /// <summary>Every skill a scene can reference: catalog order, then library-only ids.</summary>
    public IReadOnlyList<(string Id, string Name)> ReferenceableSkills() =>
        _catalog.Skills.Select(s => (s.Id, s.Name))
            .Concat(Library.Entries().Keys
                .Where(id => _catalog.Skills.All(s => s.Id != id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => (id, id)))
            .ToList();

    /// <summary>
    /// How <paramref name="id"/> reaches <paramref name="agent"/>: native when the
    /// agent already has it, a pointer to the library's SKILL.md when the agent
    /// can read local files (or the destination is unknown, e.g. a terminal),
    /// otherwise missing. Ids nobody knows resolve as unknown.
    /// </summary>
    public SkillResolution Resolve(string id, AgentId? agent)
    {
        var skill = _catalog.Skills.FirstOrDefault(s => s.Id == id);
        var file = Library.SkillFile(id);
        if (skill is null && file is null)
            return new SkillResolution(id, id, SkillRenderMode.Unknown);
        var name = skill?.Name ?? id;
        if (agent is { } target && skill is not null && skill.SupportedAgents.Contains(target)
            && Status(skill, target) is SkillAgentStatus.Installed
                or SkillAgentStatus.UpdateAvailable or SkillAgentStatus.ManualConfirmed)
            return new SkillResolution(id, name, SkillRenderMode.Native);
        if (file is not null && (agent is null || agent.Value.CanReadLocalFiles()))
            return new SkillResolution(id, name, SkillRenderMode.Path, file);
        return new SkillResolution(id, name, SkillRenderMode.Missing);
    }

    /// <summary>The render context the forward path and the scene preview hand to <see cref="ScenePrompt"/>.</summary>
    public SkillRenderContext PromptContext(AgentId? agent) => new(agent, id => Resolve(id, agent));

    /// <summary>The SHA-verified status of one (skill, agent) pair.</summary>
    public SkillAgentStatus Status(OfficialSkill skill, AgentId agent) =>
        ResourcesRoot is { } root
            ? _installer.Status(skill, agent, root, AgentInstalled(agent))
            : new SkillAgentStatus.PackageUnavailable();

    /// <summary>Where an install would land — direct target, manual guide, or unavailable.</summary>
    public SkillInstallPlan? Plan(OfficialSkill skill, AgentId agent) =>
        ResourcesRoot is { } root
            ? _installer.Plan(skill, agent, root, AgentInstalled(agent))
            : null;

    /// <summary>
    /// Installs or updates one (skill, agent). The installer's errors —
    /// 外部修改, downgrade, foreign directory — propagate verbatim for the pane
    /// to surface; the row's status is refreshed either way.
    /// </summary>
    public async Task InstallAsync(SkillRow row, AgentSkillRow agent, bool replacingExisting = false)
    {
        var root = ResourcesRoot
            ?? throw new SkillInstallException("没有找到内置技能清单。");
        await RunAgentOp(row, agent, () =>
            _installer.Install(row.Skill, agent.Agent, root, replacingExisting));
    }

    /// <summary>移除 — deletes our install, or forgets a manual confirmation on manual agents.</summary>
    public async Task UninstallAsync(SkillRow row, AgentSkillRow agent) =>
        await RunAgentOp(row, agent, () => _installer.Uninstall(row.Skill, agent.Agent));

    /// <summary>我已安装 — records that this exact version was imported by hand.</summary>
    public async Task ConfirmManualAsync(SkillRow row, AgentSkillRow agent) =>
        await RunAgentOp(row, agent, () => _installer.ConfirmManual(row.Skill, agent.Agent));

    /// <summary>撤销确认 — drops the recorded manual import.</summary>
    public async Task RevokeManualAsync(SkillRow row, AgentSkillRow agent) =>
        await RunAgentOp(row, agent, () => _installer.RevokeManualConfirmation(row.Skill, agent.Agent));

    /// <summary>手动安装包 — writes the validated package as a zip the agent's import UI takes.</summary>
    public async Task ExportManualArchiveAsync(SkillRow row, string destination)
    {
        var root = ResourcesRoot
            ?? throw new SkillInstallException("没有找到内置技能资源。");
        await RunRowOp(row, () => _installer.MakeManualArchive(row.Skill, root, destination));
    }

    /// <summary>安装缺失项 — every automatically-installable gap on one card.</summary>
    public async Task<SkillInstallReport> InstallMissingAsync(SkillRow row)
    {
        var installed = 0;
        var failures = new List<string>();
        foreach (var agent in row.States.Where(s => s.IsAutomaticMissing).ToList())
        {
            try
            {
                await InstallAsync(row, agent);
                installed++;
            }
            catch (Exception error)
            {
                // One failed agent must not stop the rest of the card — the
                // failure goes into the summary notice, as on macOS.
                failures.Add($"{row.Skill.Name} / {agent.AgentName}：{error.Message}");
            }
        }
        return new SkillInstallReport(installed, failures);
    }

    /// <summary>安装全部缺失项 — every gap on every card.</summary>
    public async Task<SkillInstallReport> InstallAllMissingAsync()
    {
        IsBusy = true;
        try
        {
            var installed = 0;
            var failures = new List<string>();
            foreach (var row in Rows.ToList())
            {
                var report = await InstallMissingAsync(row);
                installed += report.Installed;
                failures.AddRange(report.Failures);
            }
            return new SkillInstallReport(installed, failures);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The directory holding <c>Skills/catalog.json</c>: macOS walks up from the
    /// bundle's resource dir and the cwd, at most <see cref="MaxAncestorHops"/>
    /// levels. Windows does the same from the app base directory and the current
    /// directory, additionally accepting the repo layout where Skills sits
    /// under <c>Resources/</c> rather than at the root.
    /// </summary>
    public static string? FindResourcesRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            if (FindResourcesRoot(start) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Single-start variant of <see cref="FindResourcesRoot()"/> — the testable seam.</summary>
    internal static string? FindResourcesRoot(string start)
    {
        var candidate = Path.GetFullPath(start);
        for (var hop = 0; hop < MaxAncestorHops; hop++)
        {
            if (File.Exists(Path.Combine(candidate, "Skills", "catalog.json")))
                return candidate;
            var nested = Path.Combine(candidate, "Resources");
            if (File.Exists(Path.Combine(nested, "Skills", "catalog.json")))
                return nested;
            var parent = Path.GetDirectoryName(candidate);
            if (parent is null || string.Equals(parent, candidate, StringComparison.Ordinal))
                break;
            candidate = parent;
        }
        return null;
    }

    /// <summary>Re-reads one card's per-agent statuses after a mutation.</summary>
    internal void RefreshRow(SkillRow row)
    {
        foreach (var state in row.States)
            state.Status = Status(row.Skill, state.Agent);
        row.NotifyStateChanged();
        OnPropertyChanged(nameof(PendingInstallCount));
        OnPropertyChanged(nameof(AnyAutomaticMissing));
        OnPropertyChanged(nameof(CanInstallAll));
    }

    private bool AgentInstalled(AgentId agent) =>
        _installedAgents.TryGetValue(agent, out var hit)
            ? hit
            : _installedAgents[agent] = _agentInstalled(agent);

    /// <summary>
    /// The Windows stand-in for macOS <c>InstalledApp.lookup</c>: agents with a
    /// direct skill root are always usable (the installer creates the folder),
    /// and for manual agents we reuse the forward-target probe — a running
    /// process or resolvable exe. <see cref="WindowsTargetResolver.Resolve"/>
    /// counts a known AUMID as resolved even when nothing was found on disk, so
    /// a packaged app we cannot probe still reads as installed: hiding the
    /// manual-import buttons on a false negative would be worse.
    /// </summary>
    private static bool DetectAgentInstalled(AgentId agent)
    {
        if (agent.DirectSkillRoot() is not null)
            return true;
        try
        {
            foreach (var action in ShareActions.All)
            {
                if (AgentIds.Matching(action) != agent)
                    continue;
                return WindowsForwardTargets.For(action) is not { } spec
                    || WindowsTargetResolver.Resolve(spec) is not null;
            }
        }
        catch (Exception)
        {
            // A failed probe is not a verdict — assume installed so the manual
            // path (导出 ZIP / 我已安装) stays reachable.
        }
        return true;
    }

    /// <summary>
    /// Copies shipped official packages into the library. A failure here must
    /// not take the pane down — it is surfaced through <see cref="LibraryIssue"/>.
    /// </summary>
    private void SyncLibrary(string resourcesRoot)
    {
        try
        {
            var report = Library.SyncOfficial(_catalog, resourcesRoot);
            LibraryIssue = report.Conflicts.Count == 0
                ? null
                : $"技能库中的 {string.Join("、", report.Conflicts)} 被外部修改，未自动更新。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or SkillInstallException)
        {
            LibraryIssue = error.Message;
        }
    }

    private static IReadOnlyList<WeChatScene> LoadScenes()
    {
        try
        {
            return new SceneStore().Load().Scenes;
        }
        catch
        {
            // A broken scenes.json must not take the skills pane with it.
            return [];
        }
    }

    private async Task RunAgentOp(SkillRow row, AgentSkillRow agent, Action operation)
    {
        if (agent.IsBusy)
            return;
        agent.IsBusy = true;
        row.IsBusy = true;
        try
        {
            await Task.Run(operation);
        }
        finally
        {
            agent.IsBusy = false;
            row.IsBusy = false;
            RefreshRow(row);
        }
    }

    private async Task RunRowOp(SkillRow row, Action operation)
    {
        if (row.IsBusy)
            return;
        row.IsBusy = true;
        try
        {
            await Task.Run(operation);
        }
        finally
        {
            row.IsBusy = false;
            RefreshRow(row);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
