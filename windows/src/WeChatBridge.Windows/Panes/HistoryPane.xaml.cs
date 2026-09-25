using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Panes;

/// <summary>
/// Every batch still on disk, newest first — the port of macOS HistoryPane.
/// A batch stays here after it has been forwarded, because "did that actually
/// arrive?" is the question the record answers.
/// </summary>
public partial class HistoryPane : UserControl
{
    public HistoryPane()
    {
        InitializeComponent();
        Loaded += (_, _) => HookModel();
        DataContextChanged += (_, _) => HookModel();
    }

    private MainViewModel? Model => DataContext as MainViewModel;
    private MainViewModel? _hooked;

    private void HookModel()
    {
        if (ReferenceEquals(_hooked, Model))
        {
            UpdateEmptyState();
            return;
        }
        if (_hooked is not null)
        {
            _hooked.Groups.CollectionChanged -= OnGroupsChanged;
            _hooked.PropertyChanged -= OnModelChanged;
        }
        _hooked = Model;
        if (_hooked is not null)
        {
            _hooked.Groups.CollectionChanged += OnGroupsChanged;
            _hooked.PropertyChanged += OnModelChanged;
        }
        UpdateEmptyState();
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HasHistory) or nameof(MainViewModel.SummaryText))
            UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (!IsLoaded || Model is null)
            return;
        var searching = Model.Query.Trim().Length > 0;
        var any = Model.Groups.Count > 0;
        EmptyAll.Visibility = !any && !searching ? Visibility.Visible : Visibility.Collapsed;
        EmptyFilter.Visibility = !any && searching ? Visibility.Visible : Visibility.Collapsed;
        RecordList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = Model.HasHistory;
    }

    private static BatchRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as BatchRow;

    private void ToggleDetail_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            row.IsExpanded = !row.IsExpanded;
    }

    private void Resend_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && RowOf(sender) is { } row)
            _ = model.Resend(row.Batch);
    }

    /// <summary>
    /// 发给… — one row per destination, flattened because a WPF menu inside a
    /// list row has no business nesting a submenu either.
    /// </summary>
    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || RowOf(sender) is not { } row)
            return;
        var menu = new ContextMenu();
        foreach (var destination in model.Destinations())
        {
            var item = new MenuItem { Header = $"发给 {destination.Title}" };
            var picked = destination;
            item.Click += (_, _) =>
            {
                // Delivery drives its own recording and reloads when it ends.
                _ = model.PerformForward(row.Batch, picked.Action, picked.Target);
            };
            menu.Items.Add(item);
        }
        if (model.TargetRows.Count == 0)
        {
            menu.Items.Add(new Separator());
            var add = new MenuItem { Header = "添加应用…" };
            add.Click += (_, _) => model.Navigate(AppTab.Entries);
            menu.Items.Add(add);
        }
        OpenMenu(sender, menu);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || RowOf(sender) is not { } row)
            return;
        var menu = new ContextMenu();

        var copy = new MenuItem { Header = "复制到剪贴板" };
        copy.Click += (_, _) => model.CopyBatchToClipboard(row.Batch);
        menu.Items.Add(copy);

        var reveal = new MenuItem { Header = "在文件夹中显示" };
        reveal.Click += (_, _) => model.Reveal(row.Batch);
        menu.Items.Add(reveal);

        menu.Items.Add(new Separator());

        var discard = new MenuItem { Header = "移到回收站" };
        discard.Click += (_, _) => model.Discard(row.Batch);
        menu.Items.Add(discard);

        OpenMenu(sender, menu);
    }

    private static void OpenMenu(object sender, ContextMenu menu)
    {
        if (sender is not FrameworkElement anchor)
            return;
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void RevealItem_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && (sender as FrameworkElement)?.DataContext is ReadyItem item)
            model.RevealItem(item);
    }

    private void RevealInbox_Click(object sender, RoutedEventArgs e) => Model?.RevealInbox();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || !model.HasHistory)
            return;
        var message =
            $"将 {model.Batches.Count} 条记录（{ByteText.Format(model.Batches.Sum(b => b.ByteCount))}）移到回收站，可从回收站恢复。";
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            message,
            "清空全部记录？",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK)
            model.DiscardAll();
    }
}
