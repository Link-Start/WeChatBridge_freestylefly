using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.Panes;

/// <summary>
/// The skills pane: one card per catalog skill with per-agent install lines —
/// the port of macOS SkillsPane. All state lives in <see cref="SkillService"/>;
/// this class only routes clicks, owns the search/filter chrome, and shows
/// confirmations (<see cref="MessageBox"/>) and the 导出 ZIP save panel.
/// </summary>
public partial class SkillsPane : UserControl
{
    /// <summary>The status filter chips, mirroring SkillStatusFilter.</summary>
    private enum StatusFilter
    {
        All,
        NeedsInstall,
        Partial,
        Installed,
    }

    private readonly SkillService _service = new();
    private StatusFilter _statusFilter = StatusFilter.All;
    private AgentId? _agentFilter;
    private bool _syncing;
    private bool _viewReady;

    public SkillsPane()
    {
        InitializeComponent();
        Root.DataContext = _service;
        _service.Rows.CollectionChanged += OnRowsChanged;
        Loaded += (_, _) => RefreshView();
        Unloaded += (_, _) => _viewReady = false;
    }

    /// <summary>The service the pane binds to — exposed for the integrator.</summary>
    public SkillService Service => _service;

    /// <summary>用于 N 个场景 was clicked: the host shows the scenes referencing this skill id.</summary>
    public event Action<string>? ScenesRequested;

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshView();

    // MARK: - Filtering (the port of scopedRecords + the status/agent filters)

    private void RefreshView()
    {
        if (!IsLoaded)
            return;
        _viewReady = true;
        RebuildAgentChoices();
        ApplyFilter();
        if (_service.LibraryIssue is { } issue)
            ShowNotice(issue, warning: true);
    }

    private void ApplyFilter()
    {
        if (!_viewReady)
            return;
        var needle = SearchBox.Text.Trim();
        var scoped = _service.Rows
            .Where(r => r.MatchesQuery(needle) && r.SupportsAgent(_agentFilter))
            .ToList();
        var visible = scoped.Where(r => Includes(_statusFilter, r.Kind)).ToList();
        SkillList.ItemsSource = visible;
        SkillList.Visibility = visible.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFilterChips(scoped);
    }

    private void UpdateFilterChips(IReadOnlyList<SkillRow> scoped)
    {
        FilterAll.Content = $"全部 {scoped.Count}";
        FilterMissing.Content = $"未安装 {scoped.Count(r => Includes(StatusFilter.NeedsInstall, r.Kind))}";
        FilterPartial.Content = $"部分安装 {scoped.Count(r => Includes(StatusFilter.Partial, r.Kind))}";
        FilterDone.Content = $"已安装 {scoped.Count(r => Includes(StatusFilter.Installed, r.Kind))}";

        MarkChip(FilterAll, _statusFilter == StatusFilter.All);
        MarkChip(FilterMissing, _statusFilter == StatusFilter.NeedsInstall);
        MarkChip(FilterPartial, _statusFilter == StatusFilter.Partial);
        MarkChip(FilterDone, _statusFilter == StatusFilter.Installed);
    }

    private static void MarkChip(Button chip, bool selected) =>
        chip.Tag = selected ? "Selected" : null;

    private static bool Includes(StatusFilter filter, SkillCoverage kind) => filter switch
    {
        StatusFilter.All => true,
        StatusFilter.NeedsInstall => kind == SkillCoverage.NotInstalled,
        StatusFilter.Partial => kind is SkillCoverage.Partial or SkillCoverage.Update or SkillCoverage.Conflict,
        _ => kind == SkillCoverage.Installed,
    };

    /// <summary>全部 Agent plus every agent at least one skill supports — agentChoices on macOS.</summary>
    private void RebuildAgentChoices()
    {
        _syncing = true;
        try
        {
            var keep = _agentFilter;
            AgentBox.Items.Clear();
            AgentBox.Items.Add(new ComboBoxItem { Content = "全部 Agent" });
            var agents = AgentIds.All.Where(a => _service.Rows.Any(r => r.SupportsAgent(a))).ToList();
            if (keep is { } kept && !agents.Contains(kept))
                keep = null;
            var index = 0;
            foreach (var agent in agents)
            {
                var item = new ComboBoxItem { Content = agent.DisplayName(), Tag = agent };
                AgentBox.Items.Add(item);
                if (keep is { } k && k == agent)
                    index = AgentBox.Items.Count - 1;
            }
            _agentFilter = keep;
            AgentBox.SelectedIndex = index;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Agent_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
            return;
        _agentFilter = (AgentBox.SelectedItem as ComboBoxItem)?.Tag is AgentId agent ? agent : null;
        ApplyFilter();
    }

    private void SetFilter(StatusFilter filter)
    {
        if (_statusFilter == filter)
            return;
        _statusFilter = filter;
        ApplyFilter();
    }

    private void FilterAll_Click(object sender, RoutedEventArgs e) => SetFilter(StatusFilter.All);
    private void FilterMissing_Click(object sender, RoutedEventArgs e) => SetFilter(StatusFilter.NeedsInstall);
    private void FilterPartial_Click(object sender, RoutedEventArgs e) => SetFilter(StatusFilter.Partial);
    private void FilterDone_Click(object sender, RoutedEventArgs e) => SetFilter(StatusFilter.Installed);

    // MARK: - Row actions

    private static SkillRow? CardOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SkillRow;

    private static AgentSkillRow? AgentOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as AgentSkillRow;

    private void SceneBadge_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { HasScenes: true } row)
            ScenesRequested?.Invoke(row.Skill.Id);
    }

    private void ToggleDetails_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } row)
            row.IsExpanded = !row.IsExpanded;
    }

    /// <summary>The card's one button: 安装 / 更新 / 导出 ZIP / 查看详情.</summary>
    private void CardPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } row)
            return;
        switch (row.PrimaryAction)
        {
            case SkillRowAction.Install:
                _ = InstallMissing(row);
                break;
            case SkillRowAction.ExportArchive:
                Export(row);
                break;
            default:
                row.IsExpanded = !row.IsExpanded;
                break;
        }
    }

    private void InstallAll_Click(object sender, RoutedEventArgs e) => _ = InstallAllMissing();

    private void AgentInstall_Click(object sender, RoutedEventArgs e)
    {
        if (AgentOf(sender) is { Parent: { } row } agent)
            _ = RunAgent(() => _service.InstallAsync(row, agent), "技能已安装。");
    }

    /// <summary>替换 — the 替换已有技能目录？ alert, backing up the old directory first.</summary>
    private void AgentReplace_Click(object sender, RoutedEventArgs e)
    {
        if (AgentOf(sender) is not { Parent: { } row } agent)
            return;
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            "微信流会先备份旧目录再替换；外部安装目录也会被替换。",
            "替换已有技能目录？",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK)
            _ = RunAgent(() => _service.InstallAsync(row, agent, replacingExisting: true), "技能已安装。");
    }

    private void AgentUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (AgentOf(sender) is { Parent: { } row } agent)
            _ = RunAgent(() => _service.UninstallAsync(row, agent), "技能已移除。");
    }

    /// <summary>我已安装 / 撤销确认 — the manual-import bookkeeping buttons.</summary>
    private void AgentConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (AgentOf(sender) is not { Parent: { } row } agent)
            return;
        if (agent.Status is SkillAgentStatus.ManualConfirmed)
            _ = RunAgent(() => _service.RevokeManualAsync(row, agent), "已撤销本地确认。");
        else
            _ = RunAgent(() => _service.ConfirmManualAsync(row, agent), "已记录手动安装。");
    }

    private void AgentExport_Click(object sender, RoutedEventArgs e)
    {
        if (AgentOf(sender) is { Parent: { } row })
            Export(row);
    }

    // MARK: - Operations

    /// <summary>Runs one agent op and reports — mirrors the per-action notice lines on macOS.</summary>
    private async Task RunAgent(Func<Task> operation, string success)
    {
        try
        {
            await operation();
            ShowNotice(success, warning: false);
        }
        catch (Exception error)
        {
            ShowNotice(error.Message, warning: true);
        }
        ApplyFilter();
    }

    private async Task InstallMissing(SkillRow row)
    {
        var report = await _service.InstallMissingAsync(row);
        ShowNotice(ReportText(report), warning: report.Failed > 0 || report.Installed == 0);
        ApplyFilter();
    }

    private async Task InstallAllMissing()
    {
        var report = await _service.InstallAllMissingAsync();
        ShowNotice(ReportText(report), warning: report.Failed > 0 || report.Installed == 0);
        ApplyFilter();
    }

    /// <summary>The sweep summary wording, straight from the macOS report().</summary>
    private static string ReportText(SkillInstallReport report) =>
        report switch
        {
            { Installed: 0, Failed: 0 } => "没有可自动安装的缺失项。",
            { Failed: 0 } => $"已安装 {report.Installed} 项。",
            _ => $"已安装 {report.Installed} 项，另有 {report.Failed} 项需要处理。",
        };

    /// <summary>手动安装包 — a Save dialog, then the validated zip the import UI takes.</summary>
    private void Export(SkillRow row)
    {
        if (_service.ResourcesRoot is null)
        {
            ShowNotice("没有找到内置技能资源。", warning: true);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "导出手动安装包",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = $"{row.Skill.Name}-{row.Skill.Version}.zip",
            DefaultExt = ".zip",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        _ = RunAgent(() => _service.ExportManualArchiveAsync(row, dialog.FileName), "技能 ZIP 已导出。");
    }

    private void ShowNotice(string message, bool warning)
    {
        NoticeText.Text = message;
        NoticeBar.SetResourceReference(
            BackgroundProperty, warning ? "WarnFillColor" : "LiveFillColor");
        NoticeText.SetResourceReference(
            TextBlock.ForegroundProperty, warning ? "WarnInkColor" : "LiveInkColor");
        NoticeBar.Visibility = Visibility.Visible;
    }
}
