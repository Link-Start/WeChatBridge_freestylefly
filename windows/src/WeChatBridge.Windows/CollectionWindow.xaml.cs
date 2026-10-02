using System.IO;
using System.Windows;
using System.Windows.Controls;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

public partial class CollectionWindow : Window
{
    private readonly MainViewModel _model;
    private Guid _id;
    private bool _refreshing;
    private int _revision;
    private List<CollectionBatchRow> _rows = [];
    private sealed record CollectionOption(Guid Id, string Label);
    public sealed class CollectionBatchRow : System.ComponentModel.INotifyPropertyChanged
    {
        public required ReadyBatch Batch { get; init; }
        public string ChatName { get; set; } = "";
        public bool IsDefault { get; set; }
        public string Title { get; init; } = "";
        private string _boundaries = L10n.Text("正在读取…");
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
        Loaded += (_, _) => Refresh();
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
        CollectionName.Text = group?.Name ?? "";
        var busy = group?.Status == CollectionStatus.Delivering;
        CollectionName.IsEnabled = !busy;
        BatchList.IsEnabled = !busy;
        DeliverButton.IsEnabled = group?.BatchIDs.Count > 0 && !busy;
        ResumeButton.IsEnabled = group?.BatchIDs.Count > 0 && !busy;
        UndoButton.IsEnabled = !busy && _model.Collections.CanUndo;
        Status.Text = group is null ? L10n.Text("收集不存在。") : L10n.Format($"{StatusName(group.Status)} · {group.BatchIDs.Count} 批\n{group.Detail}");
        var batches = _model.Batches.ToDictionary(b => b.Id);
        var focused = (System.Windows.Input.Keyboard.FocusedElement as TextBox)?.DataContext as CollectionBatchRow;
        _rows = (group?.BatchIDs ?? []).Where(batches.ContainsKey).Select((id, index) => new CollectionBatchRow
        {
            Batch = batches[id], ChatName = focused?.Batch.Id == id ? focused.ChatName : batches[id].ChatName ?? "",
            IsDefault = group?.DefaultChatName is { Length: > 0 } defaultName && batches[id].ChatName == defaultName,
            Title = L10n.Format($"第 {index + 1} 批 · {batches[id].Items.Count} 个原始文件 · {ByteText.Format(batches[id].ByteCount)}"),
        }).ToList();
        BatchList.ItemsSource = _rows;
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
                }
            });
        }
        catch (Exception error)
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(() => { if (IsLoaded) Message.Text = error.Message; });
        }
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
                catch (Exception error) { Show(); Message.Text = error.Message; }
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = DeliverButton;
        menu.IsOpen = true;
    }
}
