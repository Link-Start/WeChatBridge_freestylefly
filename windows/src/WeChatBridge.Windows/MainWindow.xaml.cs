using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WeChatBridge.Windows.Panes;

namespace WeChatBridge.Windows;

/// <summary>
/// 微信流's only real window: a quiet column of navigation on the left, one
/// scrolling pane on the right — the port of the macOS settings window, with
/// 记录 as the landing pane because the window is the app's whole surface here.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>No action: long enough to read four words.</summary>
    private static readonly TimeSpan PlainToastDuration = TimeSpan.FromSeconds(1.8);
    /// <summary>Something to click has to outlive a glance.</summary>
    private static readonly TimeSpan ActionableToastDuration = TimeSpan.FromSeconds(8);

    private readonly MainViewModel _model;
    private readonly string? _requestedBatch;
    private readonly DispatcherTimer _toastTimer = new();
    private DateTimeOffset _toastDeadline;
    private TimeSpan _toastRemaining;
    private Action? _toastAction;
    private bool _toastHovering;
    private bool _batchSelected;

    private readonly Dictionary<AppTab, (string Title, UIElement Pane)> _panes;

    public MainWindow(MainViewModel model, string? requestedBatch)
    {
        _model = model;
        _requestedBatch = requestedBatch;
        InitializeComponent();

        var scenesPane = new ScenesPane();
        _panes = new Dictionary<AppTab, (string, UIElement)>
        {
            [AppTab.History] = ("记录", new HistoryPane()),
            [AppTab.General] = ("通用", new GeneralPane()),
            [AppTab.Entries] = ("入口", new EntriesPane()),
            [AppTab.Scenes] = ("场景", scenesPane),
            [AppTab.Skills] = ("技能", new SkillsPane()),
            [AppTab.About] = ("关于", new AboutPane()),
        };
        scenesPane.Bind(model.Scenes);

        DataContext = _model;
        PaneHost.Content = _panes[AppTab.History].Pane;
        NavList.SelectedIndex = 0;

        _model.ToastRequested += ShowToast;
        _model.NavigationRequested += SelectTab;
        _toastTimer.Tick += (_, _) => { if (!_toastHovering) HideToast(); };

        Loaded += (_, _) =>
        {
            _model.RebuildEntries();
            RefreshBatches();
            SelectRequestedBatch();
        };

        // Close hides to the tray, mirroring the macOS menu-bar app — the
        // resident process must keep consuming shares. The tray's 退出 calls
        // Application.Shutdown, which proceeds despite this cancel.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    public MainViewModel Model => _model;

    /// <summary>Called by App when the inbox changed or a second instance was launched.</summary>
    public void RefreshBatches() => _model.Reload();

    /// <summary>Second launch foregrounds the existing window.</summary>
    public void BringToFront()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        // Topmost flick: Activate alone cannot steal focus from another app.
        Topmost = true;
        Activate();
        Topmost = false;
        Focus();
    }

    private void SelectTab(AppTab tab)
    {
        if (!_panes.TryGetValue(tab, out var pane))
            return;
        PaneTitle.Text = pane.Title;
        PaneHost.Content = pane.Pane;
        foreach (var item in NavList.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is string tag && Enum.TryParse<AppTab>(tag, out var t) && t == tab)
            {
                NavList.SelectedItem = item;
                break;
            }
        }
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is ListBoxItem { Tag: string tag }
            && Enum.TryParse<AppTab>(tag, out var tab))
        {
            SelectTab(tab);
        }
    }

    /// <summary>The helper launches us with --batch-id: land the user on that row.</summary>
    private void SelectRequestedBatch()
    {
        if (_requestedBatch is null || _batchSelected)
            return;
        if (!Guid.TryParse(_requestedBatch, out var id))
            return;
        foreach (var row in _model.Groups.SelectMany(g => g.Rows))
        {
            if (row.Batch.Id == id)
            {
                row.IsExpanded = true;
                _batchSelected = true;
                return;
            }
        }
    }

    // MARK: - Toast

    /// <summary>
    /// The in-window port of Toast.swift: a capsule in the top-right corner.
    /// Nothing here reports success for forwards — but a desktop window the user
    /// may not be watching still needs somewhere to say a share never arrived.
    /// </summary>
    private void ShowToast(string message, string? actionTitle, Action? action, bool warning)
    {
        _toastTimer.Stop();
        ToastText.Text = message;
        ToastText.Foreground = warning
            ? (System.Windows.Media.Brush)FindResource("WarnInkColor")
            : (System.Windows.Media.Brush)FindResource("InkColor");
        _toastAction = action;
        ToastAction.Content = actionTitle;
        ToastAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        Toast.Visibility = Visibility.Visible;

        _toastHovering = false;
        _toastRemaining = action is null ? PlainToastDuration : ActionableToastDuration;
        ResumeToastTimer();
    }

    private void ResumeToastTimer()
    {
        if (_toastHovering || _toastRemaining <= TimeSpan.Zero)
            return;
        _toastDeadline = DateTimeOffset.UtcNow + _toastRemaining;
        _toastTimer.Interval = _toastRemaining;
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.Visibility = Visibility.Collapsed;
        _toastAction = null;
    }

    /// <summary>The countdown stops while the pointer is on the toast.</summary>
    private void Toast_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _toastHovering = true;
        _toastTimer.Stop();
        _toastRemaining = _toastDeadline - DateTimeOffset.UtcNow;
    }

    private void Toast_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _toastHovering = false;
        ResumeToastTimer();
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _toastAction;
        HideToast();
        action?.Invoke();
    }
}
