namespace WeChatBridge.Windows.Core;

/// <summary>Persistent collections, frozen delivery snapshots, and recoverable batch removal.</summary>
public sealed class CollectionService
{
    private readonly InboxPaths _paths;
    private readonly InboxReader _reader;
    private string LedgerPath => Path.Combine(_paths.Root, "collections.json");
    private string UndoPath => Path.Combine(_paths.Root, "collection-undo.json");
    public BatchCollectionLedger Ledger { get; private set; }
    public CollectionService(InboxPaths paths, InboxReader reader)
    {
        _paths = paths;
        _reader = reader;
        Ledger = BatchCollectionLedger.Load(LedgerPath);
        RecoverInterruptedRemoval();
        Ledger.RecoverInterruptedDeliveries();
        Save();
    }

    public void Save() => Ledger.Save(LedgerPath);
    public Guid Append(ReadyBatch batch, string? recognizedName)
    {
        var id = Ledger.Append(batch.Id, batch.CreatedAt);
        var group = Ledger.Collections.FirstOrDefault(c => c.Id == id);
        if (group is null) return id; // A removed, already-seen batch must never be collected again.
        var name = batch.ChatName ?? recognizedName ?? group.DefaultChatName;
        if (!string.IsNullOrWhiteSpace(name)) _reader.RecordContext(batch.Id, name);
        Save();
        return id;
    }

    public void SetChatName(Guid id, Guid batchId, string name, bool useAsDefault)
    {
        var group = Ledger.Editable(id);
        if (!group.BatchIDs.Contains(batchId)) throw new InvalidOperationException(L10n.Text("批次不属于这组收集。"));
        _reader.RecordChatName(batchId, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        if (useAsDefault)
        {
            Ledger.SetDefaultChatName(id, name);
            foreach (var member in group.BatchIDs.Where(b => string.IsNullOrWhiteSpace(_reader.StateFor(b)?.ChatName)))
                _reader.RecordContext(member, name.Trim());
        }
        Save();
    }

    public List<ReadyBatch> Freeze(Guid id)
    {
        var group = Ledger.Editable(id);
        var batches = _reader.LoadBatches().ToDictionary(b => b.Id);
        if (group.BatchIDs.Count == 0 || group.BatchIDs.Any(b => !batches.ContainsKey(b)))
            throw new InvalidOperationException(L10n.Text("部分收集文件已不存在，请查看批次。"));
        if (group.BatchIDs.Any(b => string.IsNullOrWhiteSpace(batches[b].ChatName)))
            throw new InvalidOperationException(L10n.Text("请先填写每批的群或联系人名称。"));
        var ids = Ledger.BeginDelivery(id);
        Save(); // Freeze durably before any clipboard write or picker.
        return ids.Select(b => batches[b]).ToList();
    }

    public void Finish(Guid id, bool succeeded, string target, string detail, string? scene = null)
    {
        Ledger.FinishDelivery(id, succeeded, target, detail, scene);
        Save();
    }

    public sealed record RemovedBatch(Guid CollectionId, Guid BatchId, int Position, string StoredDirectory);

    private void RecoverInterruptedRemoval()
    {
        var undo = ConfigStore.Load<RemovedBatch>(_paths.Root, Path.GetFileName(UndoPath));
        if (undo is null) return;
        var expected = Path.Combine(_paths.Root, "RemovedCollectionBatches", undo.BatchId.ToString("D"));
        if (!string.Equals(Path.GetFullPath(expected), Path.GetFullPath(undo.StoredDirectory), StringComparison.OrdinalIgnoreCase)) return;
        var original = Path.Combine(_paths.Ready, undo.BatchId.ToString("D"));
        var member = Ledger.Collections.Any(c => c.BatchIDs.Contains(undo.BatchId));
        // A crash between the filesystem move and ledger commit must leave an accessible batch.
        if (member && Directory.Exists(expected) && !Directory.Exists(original)) Directory.Move(expected, original);
        else if (!member && Directory.Exists(original) && !Directory.Exists(expected))
            Ledger.Restore(undo.CollectionId, undo.BatchId, undo.Position);
    }

    public void Remove(Guid id, Guid batchId)
    {
        var group = Ledger.Editable(id);
        var position = group.BatchIDs.IndexOf(batchId);
        if (position < 0) throw new InvalidOperationException(L10n.Text("批次不属于这组收集。"));
        var original = Path.Combine(_paths.Ready, batchId.ToString("D"));
        var removedRoot = Path.Combine(_paths.Root, "RemovedCollectionBatches");
        Directory.CreateDirectory(removedRoot);
        var stored = Path.Combine(removedRoot, batchId.ToString("D"));
        var undo = new RemovedBatch(id, batchId, position, stored);
        ConfigStore.Save(_paths.Root, Path.GetFileName(UndoPath), undo);
        Directory.Move(original, stored);
        try
        {
            Ledger.Remove(id, batchId);
            Save();
        }
        catch
        {
            Directory.Move(stored, original);
            if (!group.BatchIDs.Contains(batchId)) Ledger.Restore(id, batchId, position);
            throw;
        }
    }

    public bool CanUndo => ConfigStore.Load<RemovedBatch>(_paths.Root, Path.GetFileName(UndoPath)) is { } undo
        && Directory.Exists(undo.StoredDirectory);

    public void Undo()
    {
        var undo = ConfigStore.Load<RemovedBatch>(_paths.Root, Path.GetFileName(UndoPath))
            ?? throw new InvalidOperationException(L10n.Text("没有可以撤销的删除。"));
        var expected = Path.Combine(_paths.Root, "RemovedCollectionBatches", undo.BatchId.ToString("D"));
        if (!string.Equals(Path.GetFullPath(expected), Path.GetFullPath(undo.StoredDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L10n.Text("撤销记录无效。"));
        var group = Ledger.Editable(undo.CollectionId);
        var destination = Path.Combine(_paths.Ready, undo.BatchId.ToString("D"));
        Directory.Move(expected, destination);
        try
        {
            Ledger.Restore(undo.CollectionId, undo.BatchId, undo.Position);
            Save();
        }
        catch
        {
            group.BatchIDs.Remove(undo.BatchId);
            Directory.Move(destination, expected);
            throw;
        }
        File.Delete(UndoPath);
    }
}
