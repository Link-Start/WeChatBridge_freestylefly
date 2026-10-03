using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

public partial class CollectionWindow : Window
{
    private readonly MainViewModel _model;
    private Guid _id;
    private bool _refreshing;
    private int _revision;
    private bool _positioned;
    private List<CollectionBatchRow> _rows = [];
    private sealed record CollectionOption(Guid Id, string Label);
    public sealed class CollectionBatchRow : System.ComponentModel.INotifyPropertyChanged
    {
        public required ReadyBatch Batch { get; init; }
        public string ChatName { get; set; } = "";
        public bool IsDefault { get; set; }
        public string Title { get; init; } = "";
        private string _boundaries = L10n.Text("正在读取…");
        private CollectionBatchMetadata? _metadata;
        public string FirstPreview => Preview(_metadata?.First);
        public string LastPreview => Preview(_metadata?.Last);
        private string Preview(WeChatTranscriptRecord? record) => _metadata is null ? L10n.Text("正在读取…")
            : record is null ? L10n.Text("无法读取边界，原始 ZIP 已保留。")
            : $"{record.Date.LocalDateTime:HH:mm} · {record.Sender} · {string.Join(" ", record.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}";
        public void SetMetadata(CollectionBatchMetadata metadata)
        {
            _metadata = metadata;
            PropertyChanged?.Invoke(this, new(nameof(FirstPreview)));
            PropertyChanged?.Invoke(this, new(nameof(LastPreview)));
        }
        public string Boundaries
        {
            get => _boundaries;
            set { _boundaries = value; PropertyChanged?.Invoke(this, new(nameof(Boundaries))); }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public CollectionWindow(MainViewModel model, Guid id)
    {
        _model = model;
        _id = id;
        InitializeComponent();
        _model.CollectionsChanged += Refresh;
        Loaded += (_, _) =>
        {
            Refresh();
            if (IsVisible) Dispatcher.BeginInvoke(PositionAtCorner, DispatcherPriority.Loaded);
        };
        SizeChanged += (_, _) => { if (IsVisible && _positioned) KeepOnScreen(); };
        Closed += (_, _) =>
        {
            _model.CollectionsChanged -= Refresh;
            try
            {
                if (_model.Collections.Ledger.Current?.Id == _id)
                {
                    _model.Collections.Ledger.ParkCurrent();
                    _model.Collections.Save();
                    _model.Reload();
                }
            }
            catch (Exception error) { _model.ShowToast(error.Message, warning: true); }
        };
    }

    public void SelectCollection(Guid id) { _id = id; Refresh(); }

    private async void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(Refresh); return; }
        if (!IsInitialized || _refreshing) return;
        _refreshing = true;
        var revision = ++_revision;
        var group = _model.Collections.Ledger.Collections.FirstOrDefault(c => c.Id == _id);
        CollectionList.ItemsSource = _model.Collections.Ledger.Collections.AsEnumerable().Reverse()
            .Select(c => new CollectionOption(c.Id, L10n.Format($"{(c.Name.Length > 0 ? c.Name : c.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm"))} · {c.BatchIDs.Count} 批 · {StatusName(c.Status)}"))).ToList();
        CollectionList.SelectedValue = _id;
        if (!CollectionName.IsKeyboardFocusWithin) CollectionName.Text = group?.Name ?? "";
        var busy = group?.Status == CollectionStatus.Delivering;
        CollectionName.IsEnabled = !busy;
        BatchList.IsEnabled = !busy;
        DeliverButton.IsEnabled = group?.BatchIDs.Count > 0 && !busy;
        ResumeButton.IsEnabled = group?.BatchIDs.Count > 0 && !busy && group?.Status != CollectionStatus.Collecting;
        UndoButton.IsEnabled = !busy && _model.Collections.CanUndo;
        CloseButton.IsEnabled = !busy;
        var batches = _model.Batches.ToDictionary(b => b.Id);
        var focused = (System.Windows.Input.Keyboard.FocusedElement as TextBox)?.DataContext as CollectionBatchRow;
        _rows = (group?.BatchIDs ?? []).Where(batches.ContainsKey).Select((id, index) => new CollectionBatchRow
        {
            Batch = batches[id], ChatName = focused?.Batch.Id == id ? focused.ChatName : batches[id].ChatName ?? "",
            IsDefault = group?.DefaultChatName is { Length: > 0 } defaultName && batches[id].ChatName == defaultName,
            Title = L10n.Format($"第 {index + 1} 批 · {batches[id].Items.Count} 个原始文件 · {ByteText.Format(batches[id].ByteCount)}"),
        }).ToList();
        BatchList.ItemsSource = _rows;
        var last = _rows.LastOrDefault();
        LatestReference.DataContext = last;
        LatestChat.Text = last?.ChatName is { Length: > 0 } chat ? chat : L10n.Text("未命名聊天");
        LatestBatch.Text = last is null ? "" : L10n.Format($"最近 · 第 {_rows.Count} 批");
        Status.Text = group is null ? L10n.Text("收集不存在。")
            : L10n.Format($"{StatusName(group.Status)} · {group.BatchIDs.Count} 批 · {ByteText.Format(_rows.Sum(r => r.Batch.ByteCount))}");
        var unnamed = _rows.Count(r => string.IsNullOrWhiteSpace(r.ChatName));
        Hint.Text = !string.IsNullOrWhiteSpace(group?.Detail) ? group.Detail
            : unnamed > 0 ? L10n.Format($"有 {unnamed} 批未命名，请在管理批次中补充。")
            : L10n.Text("继续在微信分享下一批，收齐后统一发送。");
        _refreshing = false;
        try
        {
            var rows = _rows;
            var metadata = await Task.Run(() => rows.Select(r => CollectionBatchMetadata.Read(r.Batch.Items.Select(i => i.FullPath))).ToList());
            if (Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (revision != _revision || !IsLoaded) return;
                for (var i = 0; i < rows.Count; i++)
                {
                    var info = metadata[i];
                    static string Describe(WeChatTranscriptRecord? record) => record is null ? L10n.Text("无法读取边界，原始 ZIP 已保留。")
                        : $"{record.Date.LocalDateTime:yyyy-MM-dd HH:mm} · {record.Sender}\n{record.Text}";
                    rows[i].Boundaries = L10n.Format($"消息数：{info.Count?.ToString() ?? L10n.Text("未知")}\n\n首条：{Describe(info.First)}\n\n末条：{Describe(info.Last)}");
                    rows[i].SetMetadata(info);
                }
            });
        }
        catch (Exception error)
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(() => { if (IsLoaded) Message.Text = error.Message; });
        }
    }

    private void PositionAtCorner()
    {
        if (_positioned) return;
        _positioned = true;
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 20;
        Top = area.Bottom - ActualHeight - 24;
        KeepOnScreen();
    }
    private void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The close button shares the drag strip but must retain its normal click.
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is Button) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Close_Click(object sender, RoutedEventArgs e) { Keyboard.ClearFocus(); Close(); }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && CloseButton.IsEnabled) { Keyboard.ClearFocus(); Close(); e.Handled = true; }
    }
    private void Manage_Click(object sender, RoutedEventArgs e) => SetManagement(ManagementPanel.Visibility != Visibility.Visible);
    private void SetManagement(bool expanded)
    {
        ManagementPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ManageButton.Content = L10n.Text(expanded ? "收起详情" : "管理批次");
        var area = SystemParameters.WorkArea;
        BatchScroll.MaxHeight = Math.Min(220, Math.Max(80, area.Height - 460));
        if (IsVisible) Dispatcher.BeginInvoke(KeepOnScreen, DispatcherPriority.Loaded);
    }

    private static string StatusName(CollectionStatus status) => status switch
    {
        CollectionStatus.Collecting => L10n.Text("收集中"), CollectionStatus.Draft => L10n.Text("已暂停"),
        CollectionStatus.Delivering => L10n.Text("正在交付"), CollectionStatus.Delivered => L10n.Text("已交付"),
        _ => L10n.Text("可重试"),
    };
    private void Try(Action action)
    {
        try { action(); Message.Text = ""; _model.Reload(); }
        catch (Exception error) { Message.Text = error.Message; }
    }
    private void CollectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && CollectionList.SelectedValue is Guid id) SelectCollection(id);
    }
    private void CollectionName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_refreshing) Try(() => { _model.Collections.Ledger.Rename(_id, CollectionName.Text); _model.Collections.Save(); });
    }
    private void ChatName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_refreshing && (sender as FrameworkElement)?.DataContext is CollectionBatchRow row)
            Try(() => _model.Collections.SetChatName(_id, row.Batch.Id, row.ChatName, false));
    }
    private void DefaultName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CollectionBatchRow row } check)
            Try(() =>
            {
                if (check.IsChecked == true) _model.Collections.SetChatName(_id, row.Batch.Id, row.ChatName, true);
                else { _model.Collections.Ledger.Editable(_id).DefaultChatName = null; _model.Collections.Save(); }
            });
    }
    private void Resume_Click(object sender, RoutedEventArgs e) => Try(() => { _model.Collections.Ledger.Resume(_id); _model.Collections.Save(); });
    private void Undo_Click(object sender, RoutedEventArgs e) => Try(() => _model.Collections.Undo());
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CollectionBatchRow row)
            Try(() => _model.Collections.Remove(_id, row.Batch.Id));
    }
    private void Reveal_Click(object sender, RoutedEventArgs e) => _model.RevealInbox();
    private void Deliver_Click(object sender, RoutedEventArgs e)
    {
        // Commit the current editor before deciding whether this group is ready.
        Keyboard.ClearFocus();
        if (_rows.Any(row => string.IsNullOrWhiteSpace(row.ChatName)))
        {
            SetManagement(true);
            Message.Text = L10n.Text("请先填写每批的群或联系人名称。");
            return;
        }
        var menu = new ContextMenu();
        foreach (var destination in _model.Destinations().Where(d => _model.IsEntryEnabled(d.Action))
                     .Append(new ForwardDestination { Action = ShareAction.Clipboard }).Where(d => _model.IsEntryEnabled(d.Action)))
        {
            var item = new MenuItem { Header = destination.Title };
            item.Click += async (_, _) =>
            {
                try
                {
                    Hide(); // Let the destination own the foreground while the serialized paste runs.
                    await _model.DeliverCollection(_id, destination.Action, destination.Target);
                    Refresh();
                    if (_model.Collections.Ledger.Collections.First(c => c.Id == _id).Status == CollectionStatus.Retry) Show();
                }
                catch (Exception error) { Show(); SetManagement(true); Message.Text = error.Message; }
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = DeliverButton;
        menu.IsOpen = true;
    }
}
