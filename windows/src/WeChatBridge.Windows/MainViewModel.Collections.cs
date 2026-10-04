using System.IO;
using System.Collections.ObjectModel;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows;

public sealed partial class MainViewModel
{
    public sealed record CollectionHistoryRow(Guid Id, string Title, string Detail);
    public ObservableCollection<CollectionHistoryRow> CollectionRows { get; } = [];
    private void RefreshCollectionRows()
    {
        CollectionRows.Clear();
        foreach (var c in Collections.Ledger.Collections.AsEnumerable().Reverse())
        {
            var title = c.Name.Length > 0 ? c.Name : L10n.Format($"收集 · {c.CreatedAt.LocalDateTime:MM-dd HH:mm}");
            var status = L10n.Text(c.Status switch { CollectionStatus.Collecting => "收集中", CollectionStatus.Draft => "待发送", CollectionStatus.Delivering => "正在交付", CollectionStatus.Delivered => "已交付", _ => "可重试" });
            var detail = L10n.Format($"{c.BatchIDs.Count} 批 · {status} · {c.TargetName} {c.Detail}");
            if (_query.Length == 0 || (title + detail).Contains(_query, StringComparison.OrdinalIgnoreCase)
                || _batches.Where(b => c.BatchIDs.Contains(b.Id)).Any(b => HistoryLabels.Matches(b, _query)))
                CollectionRows.Add(new(c.Id, title, detail));
        }
        OnPropertyChanged(nameof(CollectionRows));
    }
    private CollectionService? _collections;
    internal CollectionService Collections => _collections ??= new CollectionService(_paths, _reader);
    private CollectionWindow? _collectionWindow;
    public event Action? CollectionsChanged;
    public event Action<string, string, Action>? DeliveryNotificationRequested;

    public string? DeliveryFolderPath
    {
        get => _settings.DeliveryFolderPath;
        set { _settings.DeliveryFolderPath = value; SaveSettings(); OnPropertyChanged(); RebuildEntries(); }
    }
    public string DeliverySubfolder
    {
        get => _settings.DeliverySubfolder;
        set { _settings.DeliverySubfolder = value; SaveSettings(); OnPropertyChanged(); }
    }
    public int LanguageIndex
    {
        get => _settings.Language switch { "zh-Hans" => 1, "en" => 2, _ => 0 };
        set
        {
            _settings.Language = value switch { 1 => "zh-Hans", 2 => "en", _ => "system" };
            SaveSettings(); OnPropertyChanged();
            ShowToast(L10n.Text("语言切换将在重新启动后生效。"));
        }
    }
    public bool OpenObsidianAfterDelivery
    {
        get => _settings.OpenObsidianAfterDelivery;
        set { _settings.OpenObsidianAfterDelivery = value; SaveSettings(); OnPropertyChanged(); }
    }

    // Both manual retries and incoming shares must share the same clipboard queue.
    public Task PerformForward(ReadyBatch batch, ShareAction action, ForwardTarget? target,
        bool freshShare = false, SceneService.ShareContextPrefetch? prefetch = null)
    {
        var predecessor = _pendingForward;
        return _pendingForward = Run();
        async Task Run()
        {
            try { await predecessor; } catch { }
            try { await PerformForwardCore(batch, action, target, freshShare, prefetch); }
            catch (Exception error) { Record(BatchOutcomeKind.Failed, batch.Id, error.Message); ShowToast(error.Message, warning: true); Reload(); }
        }
    }

    public void ShowCollection(Guid? id = null, bool activate = true)
    {
        try
        {
            id ??= Collections.Ledger.Current?.Id ?? Collections.Ledger.Collections.LastOrDefault()?.Id;
            if (id is null) { ShowToast(L10n.Text("从分享入口选择「分批收集到聊天桥」开始收集。")); return; }
            if (_collectionWindow is { } existing)
            {
                existing.SelectCollection(id.Value);
                existing.Show();
                if (activate) existing.Activate();
                return;
            }
            _collectionWindow = new CollectionWindow(this, id.Value);
            _collectionWindow.Closed += (_, _) => _collectionWindow = null;
            _collectionWindow.Show();
            if (activate) _collectionWindow.Activate();
        }
        catch (Exception error) { ShowToast(error.Message, warning: true); }
    }

    public bool RenameCollection(Guid id, string name)
    {
        try
        {
            Collections.Ledger.Rename(id, name);
            Collections.Save();
            Reload();
            return true;
        }
        catch (Exception error) { ShowToast(error.Message, warning: true); return false; }
    }

    public Task DeliverCollection(Guid id, ShareAction action, ForwardTarget? target)
    {
        if (action is ShareAction.Collect or ShareAction.Hub) throw new InvalidOperationException(L10n.Text("请选择交付目标。"));
        if (!_settings.IsEntryEnabled(action)) throw new InvalidOperationException(L10n.Text("该交付入口已停用。"));
        var batches = Collections.Freeze(id);
        CollectionsChanged?.Invoke();
        var previous = _pendingForward;
        return _pendingForward = Run();
        async Task Run()
        {
            var succeeded = false;
            var detail = L10n.Text("交付未完成，原始文件已保留。");
            string? scene = null;
            try
            {
                try { await previous; } catch { }
                // A single scene decision and one paste plan for all original archives.
                var aggregate = batches[0] with
                {
                    Action = action,
                    Items = batches.SelectMany(b => b.Items).ToList(),
                    ChatName = batches.Select(b => b.ChatName).Distinct().Count() == 1 ? batches[0].ChatName : null,
                };
                if (action is ShareAction.Obsidian or ShareAction.Folder)
                {
                    var root = action == ShareAction.Obsidian ? _settings.ObsidianVaultPath : _settings.DeliveryFolderPath;
                    if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException(L10n.Text("请先在入口页设置归档文件夹。"));
                    var notes = new List<string>();
                    foreach (var batch in batches)
                        notes.AddRange(KnowledgeDelivery.Deliver(batch.Items.Select(i => i.FullPath).ToList(), root,
                            action == ShareAction.Obsidian ? _settings.ObsidianSubfolder : _settings.DeliverySubfolder,
                            batch.ChatName, null));
                    succeeded = true;
                    detail = L10n.Format($"已保存 {notes.Distinct().Count()} 篇笔记，原始 ZIP 已保留。");
                    if (action == ShareAction.Obsidian && _settings.OpenObsidianAfterDelivery) OpenInObsidian(root, notes);
                    DeliveryNotificationRequested?.Invoke(detail, action == ShareAction.Obsidian ? L10n.Text("打开笔记") : L10n.Text("查看文件"),
                        () => { if (action == ShareAction.Obsidian) OpenInObsidian(root, notes); else RevealFile(notes[0]); });
                }
                else
                {
                    // Collection delivery is user-initiated: it has no 90-second share expiration.
                    await PerformForwardCore(aggregate, action, target);
                    var outcome = _reader.StateFor(aggregate.Id)?.Outcome;
                    succeeded = outcome?.Kind is BatchOutcomeKind.Delivered or BatchOutcomeKind.Copied;
                    detail = succeeded ? L10n.Text("原始 ZIP 已交付，请确认目标中的附件数量。") : outcome?.Detail ?? detail;
                    scene = _reader.StateFor(aggregate.Id)?.SceneName;
                }
                foreach (var batch in batches)
                {
                    _reader.RecordAction(batch.Id, action);
                    if (action is ShareAction.Obsidian or ShareAction.Folder) _reader.ClearSceneContext(batch.Id);
                    Record(succeeded ? (action == ShareAction.Clipboard ? BatchOutcomeKind.Copied : BatchOutcomeKind.Delivered)
                        : BatchOutcomeKind.Failed, batch.Id, detail, target?.DisplayName ?? action.TargetDisplayName());
                }
            }
            catch (Exception error)
            {
                detail = error.Message;
                CopyItems(batches.SelectMany(b => b.Items).ToList(), false);
                ShowToast(L10n.Format($"{detail}；原始文件已保留，可重试。"), warning: true);
            }
            finally
            {
                Collections.Finish(id, succeeded, target?.DisplayName ?? action.TargetDisplayName(), detail, scene);
                Reload();
            }
        }
    }
}
