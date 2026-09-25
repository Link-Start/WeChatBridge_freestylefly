using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Delivery;
using WeChatBridge.Windows.Services;
using WpfClipboard = System.Windows.Clipboard;

namespace WeChatBridge.Windows;

/// <summary>Which pane the window shows. A subset of the macOS SettingsTab for now.</summary>
public enum AppTab
{
    History,
    General,
    Entries,
    Scenes,
    Skills,
    About,
}

/// <summary>Status pill colour, mirroring StatusPill.Tone on macOS.</summary>
public enum StatusTone
{
    /// <summary>已送达 / 已复制.</summary>
    Live,
    /// <summary>未送达.</summary>
    Warn,
    /// <summary>未执行.</summary>
    Neutral,
}

/// <summary>One row of the 记录 list — a batch as it is drawn, not as it is stored.</summary>
public sealed class BatchRow : INotifyPropertyChanged
{
    private bool _isExpanded;

    public required ReadyBatch Batch { get; init; }

    /// <summary>Whether the item detail under the row is open.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public string Title => HistoryLabels.PaneTitle(Batch);
    public string Subtitle => HistoryLabels.PaneSubtitle(Batch);
    public string Clock => HistoryLabels.ClockTime(Batch.CreatedAt);
    public string Destination => HistoryLabels.Destination(Batch);
    public string StatusText => StatusTextFor(Batch);
    public StatusTone Tone => StatusToneFor(Batch);

    public IReadOnlyList<ReadyItem> Items => Batch.Items;
    public string SizeText => ByteText.Format(Batch.ByteCount);

    private static string StatusTextFor(ReadyBatch batch) => batch.Outcome?.Kind switch
    {
        BatchOutcomeKind.Delivered => $"{HistoryLabels.DestinationName(batch)} · 已送达",
        BatchOutcomeKind.Copied => "已复制",
        BatchOutcomeKind.Failed => batch.Outcome.Detail is { Length: > 0 } detail
            ? $"未送达 · {detail}"
            : "未送达",
        _ => "未执行",
    };

    private static StatusTone StatusToneFor(ReadyBatch batch) => batch.Outcome?.Kind switch
    {
        BatchOutcomeKind.Delivered or BatchOutcomeKind.Copied => StatusTone.Live,
        BatchOutcomeKind.Failed => StatusTone.Warn,
        _ => StatusTone.Neutral,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One day of records under a 今天/昨天/date heading.</summary>
public sealed class HistoryGroup
{
    public required string Title { get; init; }
    public required IReadOnlyList<BatchRow> Rows { get; init; }
}

/// <summary>One entry of a 发给… menu: a built-in entry or one of the user's own apps.</summary>
public sealed class ForwardDestination
{
    public required ShareAction Action { get; init; }
    /// <summary>Null for the built-ins, whose destination the action already names.</summary>
    public ForwardTarget? Target { get; init; }
    /// <summary>The app, not the entry: inside a menu already titled 发给, 「发给 Codex」 says it twice.</summary>
    public string Title => Target?.DisplayName ?? Action.TargetDisplayName();
}

/// <summary>One row of the 入口 pane — a share-menu entry and its switch.</summary>
public sealed class EntryRow
{
    public required ShareAction Action { get; init; }
    public required bool IsEnabled { get; set; }
    public required string Detail { get; init; }
    public required bool DetailIsWarning { get; init; }
    /// <summary>Obsidian 设置… and 发送到自定义 管理… are the only rows with one.</summary>
    public string? ConfigureTitle { get; init; }
    public string Title => Action.EntryTitle();
}

/// <summary>
/// One row of the 「发送到自定义」 editor. The 未安装 pill is said out loud rather
/// than left to fail at forward time — an exe may have been uninstalled since it
/// was added, and this row is the only place that can be noticed calmly.
/// </summary>
public sealed class ForwardTargetRow
{
    public required ForwardTarget Target { get; init; }

    public string DisplayName => Target.DisplayName;
    public string Identifier => Target.BundleIdentifier;
    public bool PastesPathOnly => Target.PastesPathOnly;

    /// <summary>
    /// Exe paths can be checked on disk; an AUMID cannot be resolved without the
    /// packaged-app APIs, so it is trusted rather than flagged.
    /// </summary>
    public bool IsMissing
    {
        get
        {
            var id = Identifier;
            var looksLikePath = Path.IsPathRooted(id)
                || id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            return looksLikePath && !File.Exists(id);
        }
    }
}

/// <summary>
/// Everything the window reads and asks for. Ported from AppModel.swift: the
/// model owns no copy of the truth — <see cref="Reload"/> re-reads Ready and
/// rebuilds both lists, so a lost notification, a crash mid-import or a user
/// deleting a batch in Explorer all recover to the same state.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly InboxPaths _paths;
    private readonly InboxReader _reader;
    private readonly AppSettingsStore _settingsStore;
    private readonly ForwardTargetStore _targetStore;

    private AppSettings _settings;
    private List<ReadyBatch> _batches = [];
    private DeliveryEngine? _delivery;
    private SceneService? _scenes;
    private CustomTargetService? _customTargets;
    private string _query = string.Empty;
    private string? _inboxFailure;

    public MainViewModel(InboxPaths paths)
    {
        _paths = paths;
        _reader = new InboxReader(paths);
        _settingsStore = new AppSettingsStore();
        _targetStore = new ForwardTargetStore();
        _settings = _settingsStore.Load();
        // Residency is the whole point of the background instance: reconcile
        // the stored preference with the Run key at every start so a fresh
        // install self-registers and a user deletion stays deleted.
        if (_settings.LaunchAtLogin != WeChatBridge.Windows.LaunchAtLogin.IsRegistered())
            WeChatBridge.Windows.LaunchAtLogin.Apply(_settings.LaunchAtLogin);
    }

    /// <summary>A status capsule inside the window: message, optional button, its action.</summary>
    public event Action<string, string?, Action?, bool>? ToastRequested;

    /// <summary>A pane asking the window to switch tabs (添加应用… → 入口).</summary>
    public event Action<AppTab>? NavigationRequested;

    public ObservableCollection<HistoryGroup> Groups { get; } = [];
    public ObservableCollection<ForwardTargetRow> TargetRows { get; } = [];
    public ObservableCollection<EntryRow> Entries { get; } = [];

    public IReadOnlyList<ReadyBatch> Batches => _batches;
    public string? InboxFailure { get => _inboxFailure; private set => Set(ref _inboxFailure, value); }
    public string InboxRoot => _paths.Root;

    /// <summary>Created lazily so constructing the view model never touches Win32.</summary>
    /// <summary>
    /// The scene pipeline, lazily built so nothing registers hotkeys or reads
    /// stores for a process that never forwards. The picker callback always
    /// lands on the UI dispatcher — ScenePickerWindow creates a Window.
    /// </summary>
    internal SceneService Scenes => _scenes ??= new SceneService(
        picker: (scenes, ct) => System.Windows.Application.Current?.Dispatcher
                .InvokeAsync(() => ScenePickerWindow.ChooseAsync(
                    scenes, ct, System.Windows.Application.Current.MainWindow))
                .Task.Unwrap()
            ?? Task.FromResult(ScenePickerAnswer.Cancelled),
        hotkeys: new WindowsSceneHotkeySource(),
        notify: text => ToastRequested?.Invoke(text, null, null, false));

    internal CustomTargetService CustomTargets =>
        _customTargets ??= CustomTargetService.CreateWindowed(_targetStore);

    /// <summary>
    /// Releases whatever was lazily built — App.OnExit is the only caller.
    /// Touching the lazy accessors here must not create them.
    /// </summary>
    internal void DisposeServices() => _scenes?.Dispose();

    private DeliveryEngine Delivery => _delivery ??= new DeliveryEngine(
        DeliveryEnvironment.Create(message => InboxLogger.Write(_paths, message)),
        _reader);

    /// <summary>The retention choice indexes <see cref="AppSettings.RetentionChoices"/>.</summary>
    public int RetentionIndex
    {
        get
        {
            var index = RetentionIndexOf(_settings.HistoryRetentionDays);
            return index < 0 ? RetentionIndexOf(AppSettings.DefaultHistoryRetentionDays) : index;
        }
        set
        {
            if (value < 0 || value >= AppSettings.RetentionChoices.Count)
                return;
            var days = AppSettings.RetentionChoices[value];
            if (days == _settings.HistoryRetentionDays)
                return;
            _settings.HistoryRetentionDays = days;
            SaveSettings();
            OnPropertyChanged(nameof(RetentionIndex));
            Reload();
        }
    }

    /// <summary>开机自动启动 — the toggle reads the Run key itself so a registry edit elsewhere is shown, not overwritten.</summary>
    public bool AutoStartEnabled
    {
        get => WeChatBridge.Windows.LaunchAtLogin.IsRegistered();
        set
        {
            WeChatBridge.Windows.LaunchAtLogin.Apply(value);
            _settings.LaunchAtLogin = value;
            SaveSettings();
            OnPropertyChanged(nameof(AutoStartEnabled));
        }
    }

    public string? ObsidianVaultPath
    {
        get => _settings.ObsidianVaultPath;
        set
        {
            if (value == _settings.ObsidianVaultPath)
                return;
            _settings.ObsidianVaultPath = value;
            SaveSettings();
            OnPropertyChanged(nameof(ObsidianVaultPath));
            RebuildEntries();
        }
    }

    public string ObsidianSubfolder
    {
        get => _settings.ObsidianSubfolder;
        set
        {
            if (value == _settings.ObsidianSubfolder)
                return;
            _settings.ObsidianSubfolder = value;
            SaveSettings();
            OnPropertyChanged(nameof(ObsidianSubfolder));
        }
    }

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value))
                RebuildGroups();
        }
    }

    /// <summary>「共 4 条 · 占用 12 MB · 默认保留 7 天」.</summary>
    public string SummaryText =>
        $"共 {_batches.Count} 条 · 占用 {ByteText.Format(_batches.Sum(b => b.ByteCount))} · {RetentionText}";

    public bool HasHistory => _batches.Count > 0;

    private string RetentionText =>
        _settings.HistoryRetentionDays == 0 ? "永久保留" : $"默认保留 {_settings.HistoryRetentionDays} 天";

    private static int RetentionIndexOf(int days)
    {
        for (var i = 0; i < AppSettings.RetentionChoices.Count; i++)
            if (AppSettings.RetentionChoices[i] == days)
                return i;
        return -1;
    }

    // MARK: - Loading

    /// <summary>
    /// Re-reads Ready, carries out anything the app has never processed, then
    /// ages out what the retention window says is finished with.
    /// </summary>
    public void Reload()
    {
        try
        {
            _paths.EnsureCreated();
            InboxFailure = null;
            Publish(_reader.LoadBatches());

            // IsFirstSeen comes from state.json having had to be written, so it
            // is true exactly once for a given batch — across relaunches, rescans
            // and second windows. That is what a forward needs: replaying one
            // hours later would paste into someone else's app.
            var recorded = false;
            foreach (var batch in _batches.Where(b => b.IsFirstSeen))
                recorded = Announce(batch) || recorded;
            if (recorded)
                Publish(_reader.LoadBatches());

            // Read and deleted in one go, so a message is said exactly once
            // however many times the inbox is rescanned.
            foreach (var failure in _reader.ConsumeFailures())
                ToastRequested?.Invoke($"没能接住这次转发：{failure.Message}", null, null, true);

            PruneHistory();
        }
        catch (Exception error)
        {
            InboxFailure = error.Message;
            InboxLogger.Write(_paths, "主程序读取 Inbox 失败", error);
        }
    }

    /// <summary>Returns true when it wrote an outcome, so the caller reloads.</summary>
    private bool Announce(ReadyBatch batch)
    {
        switch (_reader.ConsumeIntent(batch.Id))
        {
            case { Kind: ConsumedIntentKind.Ready, Intent: { } intent }:
                // Fire-and-forget: delivery rewrites the clipboard, activates
                // the target and records its own outcome — Reload at its end
                // publishes the result, so nothing is recorded here.
                _ = PerformForward(batch, intent.Action, ResolveTarget(intent), freshShare: true);
                return false;
            case { Kind: ConsumedIntentKind.Expired, Intent: { } intent }:
                // Only a paste can go stale. A copy was finished by the helper
                // itself, and BatchState.Initial already said 已复制.
                if (intent.Action == ShareAction.Clipboard)
                    return false;
                Record(BatchOutcomeKind.Expired, batch.Id);
                return true;
            default:
                if (batch.Action == ShareAction.Clipboard)
                    return false;
                // A forward whose one-shot request is already gone: consumed by
                // a run that then died, or never written. Nothing can be carried
                // out, and the history must not claim otherwise.
                Record(BatchOutcomeKind.Expired, batch.Id);
                return true;
        }
    }

    private ForwardTarget? ResolveTarget(BatchIntent intent)
    {
        if (intent.TargetBundleIdentifier is not { } id)
            return null;
        return _targetStore.Load().FirstOrDefault(t => t.BundleIdentifier == id)
            ?? new ForwardTarget(id, intent.TargetDisplayName ?? id, DateTimeOffset.UtcNow);
    }

    /// <summary>Ages out finished history. A non-positive window keeps forever.</summary>
    private void PruneHistory()
    {
        if (_settings.HistoryRetentionDays <= 0)
            return;
        var window = TimeSpan.FromDays(_settings.HistoryRetentionDays);
        if (_reader.PruneHistory(window) > 0)
            Publish(_reader.LoadBatches());
    }

    private void Publish(List<ReadyBatch> loaded)
    {
        _batches = loaded;
        RebuildGroups();
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasHistory));
    }

    private void RebuildGroups()
    {
        var filtered = _batches.Where(b => HistoryLabels.Matches(b, _query)).ToList();
        var groups = filtered
            .GroupBy(b => b.CreatedAt.LocalDateTime.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new HistoryGroup
            {
                Title = HistoryLabels.SectionTitle(g.Key),
                Rows = g.Select(b => new BatchRow { Batch = b }).ToList(),
            })
            .ToList();
        Groups.Clear();
        foreach (var group in groups)
            Groups.Add(group);
        OnPropertyChanged(nameof(Groups));
    }

    // MARK: - Actions on a batch

    /// <summary>
    /// 重新发送: what macOS routes through ActionRunner — the same forward the
    /// original share would have run, driven again through the delivery engine.
    /// </summary>
    public async Task Resend(ReadyBatch batch)
    {
        var target = batch.Action == ShareAction.Custom
            ? _targetStore.Load().FirstOrDefault(t => t.BundleIdentifier == batch.TargetName || t.DisplayName == batch.TargetName)
            : null;
        await PerformForward(batch, batch.Action, target);
    }

    /// <summary>复制到剪贴板 from the row menu — the same gesture the helper made.</summary>
    public void CopyBatchToClipboard(ReadyBatch batch)
    {
        if (!CopyItems(batch.Items, pathsOnly: false))
            return;
        Record(BatchOutcomeKind.Copied, batch.Id);
        ToastRequested?.Invoke("已复制到剪贴板", null, null, false);
        Reload();
    }

    /// <summary>
    /// One path for every destination, matching ActionRunner.forward's seam.
    /// Clipboard copies are still done here; real forwards go through
    /// <see cref="DeliveryEngine"/>, which writes the manual payload first so
    /// every failure leaves the user one Ctrl+V away, and records its own
    /// outcome into state.json. <paramref name="freshShare"/> is set only for
    /// the intent-driven path — the one moment reading the WeChat window title
    /// is honest — while resends and 「发给…」 reuse what the batch recorded.
    /// </summary>
    public async Task PerformForward(ReadyBatch batch, ShareAction action, ForwardTarget? target, bool freshShare = false)
    {
        var pathsOnly = action == ShareAction.Custom
            && target is not null
            && (target.PastesPathOnly
                || _targetStore.Load().FirstOrDefault(t => t.BundleIdentifier == target.BundleIdentifier)?.PastesPathOnly == true);

        if (!_settings.IsEntryEnabled(action))
        {
            CopyItems(batch.Items, pathsOnly);
            Record(BatchOutcomeKind.Failed, batch.Id, "入口已停用，文件已在剪贴板", target?.DisplayName);
            ToastRequested?.Invoke($"{action.EntryTitle()} 已在入口设置中停用；文件已复制到剪贴板", null, null, true);
            Reload();
            return;
        }

        if (action == ShareAction.Clipboard)
        {
            if (CopyItems(batch.Items, pathsOnly: false))
                Record(BatchOutcomeKind.Copied, batch.Id);
            Reload();
            return;
        }

        if (action == ShareAction.Obsidian)
        {
            var vault = _settings.ObsidianVaultPath;
            if (string.IsNullOrEmpty(vault))
            {
                Record(BatchOutcomeKind.Failed, batch.Id, "尚未选择 Obsidian 知识库", "Obsidian");
                ToastRequested?.Invoke("还没有选择 Obsidian 知识库", "去入口页设置", () => Navigate(AppTab.Entries), true);
            }
            else
            {
                try
                {
                    var paths = batch.Items.Select(i => i.FullPath).ToList();
                    var written = KnowledgeDelivery.Deliver(
                        paths, vault, _settings.ObsidianSubfolder, batch.ChatName, batch.SceneName);
                    Record(BatchOutcomeKind.Delivered, batch.Id, null, "Obsidian");
                    ToastRequested?.Invoke($"已沉淀到 Obsidian（{written.Count} 篇笔记）", null, null, false);
                }
                catch (KnowledgeDelivery.FailureException error)
                {
                    CopyItems(batch.Items, pathsOnly: false);
                    Record(BatchOutcomeKind.Failed, batch.Id, error.Message, "Obsidian");
                    ToastRequested?.Invoke($"{error.Message} 文件已复制到剪贴板", null, null, true);
                }
            }
            Reload();
            return;
        }

        // 「发送到自定义」 without a bundle id — the share helper cannot carry
        // one — resolves through the picker: zero targets prompts toward the
        // 入口 page, one forwards straight away, many pop the floating panel.
        if (target is null && action == ShareAction.Custom)
        {
            var resolution = await CustomTargets.ResolveAsync();
            switch (resolution.Kind)
            {
                case CustomTargetResolutionKind.Picked when resolution.Target is { } picked:
                    target = picked;
                    break;
                case CustomTargetResolutionKind.NoTargets:
                    Record(BatchOutcomeKind.Failed, batch.Id, "还没有添加自定义应用。");
                    ToastRequested?.Invoke("还没有添加自定义应用。", "去入口页添加", () => Navigate(AppTab.Entries), true);
                    Reload();
                    return;
                case CustomTargetResolutionKind.Expired:
                    Record(BatchOutcomeKind.Expired, batch.Id);
                    Reload();
                    return;
                default:
                    Record(BatchOutcomeKind.Failed, batch.Id, "已取消选择目标。");
                    Reload();
                    return;
            }
        }

        // The scene decision — pending shortcut → group binding → picker →
        // optional default. Null means the picker let the share expire; macOS
        // records that as expired rather than forwarding it bare.
        var choice = await Scenes.ResolveForShareAsync(
            batch.Items.Select(i => i.FullPath).ToList(), captureTitle: freshShare);
        if (choice is null)
        {
            Record(BatchOutcomeKind.Expired, batch.Id);
            Reload();
            return;
        }
        if (choice.GroupName is not null || choice.Scene is not null)
            _reader.RecordContext(batch.Id, choice.GroupName, choice.Scene?.Id, choice.Scene?.Name);
        var prompt = Scenes.RenderPrompt(choice, AgentIds.Matching(action));

        var name = target?.DisplayName ?? action.TargetDisplayName();
        try
        {
            var result = await Delivery.DeliverAsync(batch, target, prompt);
            // advance only on a real landing — the scene watermark must not
            // swallow a batch that never reached its app.
            if (result.Delivered)
                Scenes.CompleteForward(choice);
            ToastRequested?.Invoke(
                result.Delivered
                    ? $"已发给 {name}"
                    : $"{result.Outcome.Detail ?? $"没能发给 {name}"}；文件已在剪贴板",
                null, null, !result.Delivered);
        }
        catch (Exception error)
        {
            CopyItems(batch.Items, pathsOnly);
            Record(BatchOutcomeKind.Failed, batch.Id, error.Message, target?.DisplayName);
            ToastRequested?.Invoke($"转发失败：{error.Message}；文件已在剪贴板", null, null, true);
            InboxLogger.Write(_paths, "投递引擎异常", error);
        }
        Reload();
    }

    /// <summary>发给… menu contents: built-ins first, then the user's own order.</summary>
    public IReadOnlyList<ForwardDestination> Destinations()
    {
        var builtIns = new[]
        {
            ShareAction.Codex, ShareAction.Claude, ShareAction.Doubao, ShareAction.Qwen,
            ShareAction.WorkBuddy, ShareAction.WeSight, ShareAction.Obsidian,
        }.Select(a => new ForwardDestination { Action = a });
        return builtIns
            .Concat(TargetRows.Select(t => new ForwardDestination { Action = ShareAction.Custom, Target = t.Target }))
            .ToList();
    }

    /// <summary>The clipboard write both share and resend go through. UI thread is STA.</summary>
    private bool CopyItems(IReadOnlyList<ReadyItem> items, bool pathsOnly)
    {
        try
        {
            var existing = items.Where(i => File.Exists(i.FullPath)).ToList();
            if (existing.Count == 0)
            {
                ToastRequested?.Invoke("批次文件已不在磁盘上", null, null, true);
                return false;
            }
            if (pathsOnly)
            {
                WpfClipboard.SetText(string.Join(' ', existing.Select(i => $"\"{i.FullPath}\"")));
            }
            else
            {
                var files = new StringCollection();
                foreach (var item in existing)
                    files.Add(item.FullPath);
                WpfClipboard.SetFileDropList(files);
            }
            return true;
        }
        catch (Exception error)
        {
            InboxLogger.Write(_paths, "写入剪贴板失败", error);
            ToastRequested?.Invoke($"写入剪贴板失败：{error.Message}", null, null, true);
            return false;
        }
    }

    private void Record(BatchOutcomeKind kind, Guid batchId, string? detail = null, string? targetName = null)
    {
        try
        {
            _reader.RecordOutcome(
                new BatchOutcome(kind, detail, DateTimeOffset.UtcNow),
                batchId,
                targetName);
        }
        catch (Exception error)
        {
            InboxLogger.Write(_paths, "写入批次状态失败", error);
        }
    }

    // MARK: - Explorer / removal

    /// <summary>在文件夹中显示 — selects the batch's first file in Explorer.</summary>
    public void Reveal(ReadyBatch batch)
    {
        var first = batch.Items.FirstOrDefault(i => File.Exists(i.FullPath));
        if (first is null)
        {
            ToastRequested?.Invoke("批次文件已不在磁盘上", null, null, true);
            return;
        }
        RevealFile(first.FullPath);
    }

    public void RevealItem(ReadyItem item) => RevealFile(item.FullPath);

    public void RevealInbox()
    {
        try
        {
            _paths.EnsureCreated();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.Ready}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            ToastRequested?.Invoke($"无法打开文件夹：{error.Message}", null, null, true);
        }
    }

    private void RevealFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            ToastRequested?.Invoke($"无法打开文件夹：{error.Message}", null, null, true);
        }
    }

    /// <summary>移到回收站 — the archive may be the only copy, so it stays recoverable.</summary>
    public void Discard(ReadyBatch batch)
    {
        try
        {
            _reader.Discard(batch.Id);
        }
        catch (Exception error)
        {
            InboxFailure = error.Message;
        }
        Reload();
    }

    /// <summary>清空记录 — every batch to the Recycle Bin. The caller confirms first.</summary>
    public void DiscardAll()
    {
        foreach (var batch in _batches)
        {
            try { _reader.Discard(batch.Id); }
            catch (Exception error) { InboxFailure = error.Message; }
        }
        Reload();
    }

    // MARK: - Settings

    public bool IsEntryEnabled(ShareAction action) => _settings.IsEntryEnabled(action);

    public void SetEntryEnabled(ShareAction action, bool enabled)
    {
        _settings.SetEntryEnabled(action, enabled);
        SaveSettings();
        RebuildEntries();
    }

    public void RebuildEntries()
    {
        var targets = _targetStore.Load();
        Entries.Clear();
        foreach (var action in ShareActions.All)
        {
            Entries.Add(new EntryRow
            {
                Action = action,
                IsEnabled = _settings.IsEntryEnabled(action),
                Detail = EntryDetail(action, targets),
                DetailIsWarning = EntryDetailIsWarning(action, targets),
                ConfigureTitle = action switch
                {
                    ShareAction.Obsidian => "设置…",
                    ShareAction.Custom => "管理…",
                    _ => null,
                },
            });
        }
        ReloadTargets(targets);
    }

    private string EntryDetail(ShareAction action, IReadOnlyList<ForwardTarget> targets) => action switch
    {
        ShareAction.Codex => "激活 ChatGPT 并直接粘贴到输入框。",
        ShareAction.Claude => "激活 Claude 并直接粘贴到输入框。",
        ShareAction.Doubao => "激活豆包并直接粘贴到输入框。",
        ShareAction.Qwen => "激活千问办公并直接粘贴到输入框。",
        ShareAction.WorkBuddy => "激活 WorkBuddy 并直接粘贴到输入框。",
        ShareAction.WeSight => "激活 WeSight 并直接粘贴到输入框。",
        ShareAction.Obsidian => _settings.ObsidianVaultPath is { Length: > 0 } path
            ? $"知识库：{new DirectoryInfo(path).Name}"
            : "未选择知识库",
        ShareAction.Clipboard => "只复制，不自动粘贴",
        ShareAction.Custom => targets.Count == 0 ? "未添加应用" : $"{targets.Count} 个应用",
        _ => string.Empty,
    };

    private bool EntryDetailIsWarning(ShareAction action, IReadOnlyList<ForwardTarget> targets) => action switch
    {
        ShareAction.Obsidian => string.IsNullOrEmpty(_settings.ObsidianVaultPath),
        ShareAction.Custom => targets.Count == 0,
        _ => false,
    };

    private void ReloadTargets(IReadOnlyList<ForwardTarget>? targets = null)
    {
        TargetRows.Clear();
        foreach (var target in targets ?? _targetStore.Load())
            TargetRows.Add(new ForwardTargetRow { Target = target });
    }

    public void AddForwardTarget(string bundleIdentifier, string displayName)
    {
        bundleIdentifier = bundleIdentifier.Trim();
        displayName = displayName.Trim();
        if (bundleIdentifier.Length == 0)
            return;
        if (displayName.Length == 0)
            displayName = Path.GetFileNameWithoutExtension(bundleIdentifier);
        var targets = _targetStore.Load();
        if (targets.Any(t => t.BundleIdentifier == bundleIdentifier))
        {
            ToastRequested?.Invoke($"「{displayName}」已在列表中", null, null, false);
            return;
        }
        targets.Add(new ForwardTarget(
            bundleIdentifier,
            displayName,
            DateTimeOffset.UtcNow,
            PastesPathOnly: LooksLikeTerminal(bundleIdentifier)));
        _targetStore.Save(targets);
        ReloadTargets(targets);
        RebuildEntries();
    }

    public void RemoveForwardTarget(ForwardTarget target)
    {
        var targets = _targetStore.Load();
        targets.RemoveAll(t => t.BundleIdentifier == target.BundleIdentifier);
        _targetStore.Save(targets);
        ReloadTargets(targets);
        RebuildEntries();
    }

    public void SetPastesPathOnly(ForwardTarget target, bool pastesPathOnly)
    {
        var targets = _targetStore.Load();
        var index = targets.FindIndex(t => t.BundleIdentifier == target.BundleIdentifier);
        if (index < 0 || targets[index].PastesPathOnly == pastesPathOnly)
            return;
        var current = targets[index];
        targets[index] = current with { PastesPathOnly = pastesPathOnly };
        _targetStore.Save(targets);
        ReloadTargets(targets);
    }

    /// <summary>
    /// Terminals are added with 只粘贴文件路径 already on — the Windows mirror of
    /// the macOS terminal bundle-id list: a terminal can never take a pasted file.
    /// </summary>
    private static bool LooksLikeTerminal(string identifier)
    {
        var name = Path.GetFileNameWithoutExtension(identifier);
        return name is "cmd" or "powershell" or "pwsh" or "wt" or "WindowsTerminal"
            or "hyper" or "alacritty" or "wezterm" or "mintty" or "Tabby";
    }

    private void SaveSettings() => _settingsStore.Save(_settings);

    public void Navigate(AppTab tab) => NavigationRequested?.Invoke(tab);

    public void ShowToast(string message, string? actionTitle = null, Action? action = null, bool warning = false) =>
        ToastRequested?.Invoke(message, actionTitle, action, warning);

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
