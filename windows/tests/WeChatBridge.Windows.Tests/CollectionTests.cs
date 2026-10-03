using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

public sealed class CollectionTests
{
    private static async Task<ReadyBatch> AddBatch(TempInbox fixture, InboxReader reader, string name = "聊天记录.zip")
    {
        var source = fixture.WriteSource(name, "original-archive-bytes");
        var committed = await InboxWriter.CommitAsync(fixture.Paths,
            [new InboxSourceFile(source, name, "application/zip", 0, 0)], action: ShareAction.Collect);
        return reader.LoadBatches().Single(b => b.Id == committed.BatchId);
    }

    [Fact]
    public async Task FreezeKeepsOriginalOrderAndLateShareStartsANewCollection()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var service = new CollectionService(fixture.Paths, reader);
        var first = await AddBatch(fixture, reader, "one.zip");
        var second = await AddBatch(fixture, reader, "two.zip");
        var group = service.Append(first, "项目群");
        Assert.Equal(group, service.Append(second, "项目群"));
        Assert.Equal(group, service.Append(first, "项目群"));
        var frozen = service.Freeze(group);
        Assert.Equal([first.Id, second.Id], frozen.Select(b => b.Id));
        Assert.All(frozen, b => Assert.Equal("original-archive-bytes", File.ReadAllText(b.Items[0].FullPath)));
        Assert.Throws<InvalidOperationException>(() => service.Ledger.Remove(group, first.Id));
        var late = await AddBatch(fixture, reader, "late.zip");
        Assert.NotEqual(group, service.Append(late, "另一群"));
        service.Finish(group, false, "Codex", "附件未确认");
        Assert.Equal(CollectionStatus.Retry, service.Ledger.Editable(group).Status);
    }

    [Fact]
    public async Task RestartRecoversInterruptedDeliveryWithoutRecollectingBatch()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var service = new CollectionService(fixture.Paths, reader);
        var batch = await AddBatch(fixture, reader);
        var group = service.Append(batch, "项目群");
        service.Freeze(group);
        var restarted = new CollectionService(fixture.Paths, reader);
        Assert.Equal(CollectionStatus.Retry, restarted.Ledger.Editable(group).Status);
        Assert.Contains(batch.Id, restarted.Ledger.ProtectedBatchIDs);
        restarted.Append(batch, null);
        Assert.Single(restarted.Ledger.Collections);
        Assert.Single(restarted.Ledger.Collections[0].BatchIDs);
    }

    [Fact]
    public async Task DefaultNameRequiresExplicitOptInAndDoesNotOverwriteKnownNames()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var service = new CollectionService(fixture.Paths, reader);
        var first = await AddBatch(fixture, reader, "one.zip");
        var second = await AddBatch(fixture, reader, "two.zip");
        var group = service.Append(first, "甲群");
        service.Append(second, null);
        Assert.Throws<InvalidOperationException>(() => service.Freeze(group));
        service.SetChatName(group, first.Id, "甲群", useAsDefault: true);
        Assert.Equal("甲群", reader.StateFor(second.Id)?.ChatName);
        var third = await AddBatch(fixture, reader, "three.zip");
        service.Append(third, "乙群");
        Assert.Equal("乙群", reader.StateFor(third.Id)?.ChatName);
        var fourth = await AddBatch(fixture, reader, "four.zip");
        service.Append(fourth, null);
        Assert.Equal("甲群", reader.StateFor(fourth.Id)?.ChatName);
    }

    [Fact]
    public async Task RemoveAndUndoAcrossRestartRestoreFilesAndMemberPosition()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var service = new CollectionService(fixture.Paths, reader);
        var first = await AddBatch(fixture, reader, "one.zip");
        var second = await AddBatch(fixture, reader, "two.zip");
        var group = service.Append(first, "甲群");
        service.Append(second, "甲群");
        service.Remove(group, first.Id);
        Assert.False(File.Exists(first.Items[0].FullPath));
        service.Append(first, null); // An inbox rescan must not attach an already-seen removed batch.
        Assert.Equal([second.Id], service.Ledger.Editable(group).BatchIDs);
        var restarted = new CollectionService(fixture.Paths, reader);
        Assert.True(restarted.CanUndo);
        restarted.Undo();
        Assert.Equal([first.Id, second.Id], restarted.Ledger.Editable(group).BatchIDs);
        Assert.Equal("original-archive-bytes", File.ReadAllText(first.Items[0].FullPath));
        Assert.False(restarted.CanUndo);
    }

    [Fact]
    public async Task ClearingNameIsPersistedAndInterruptedRemovalRestoresOriginal()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var service = new CollectionService(fixture.Paths, reader);
        var batch = await AddBatch(fixture, reader);
        var group = service.Append(batch, "甲群");
        service.SetChatName(group, batch.Id, "", false);
        Assert.Null(reader.StateFor(batch.Id)?.ChatName);
        Assert.Throws<InvalidOperationException>(() => service.Freeze(group));
        var stored = Path.Combine(fixture.Paths.Root, "RemovedCollectionBatches", batch.Id.ToString("D"));
        Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
        ConfigStore.Save(fixture.Paths.Root, "collection-undo.json", new CollectionService.RemovedBatch(group, batch.Id, 0, stored));
        Directory.Move(batch.Directory, stored);
        var restarted = new CollectionService(fixture.Paths, reader);
        Assert.True(Directory.Exists(batch.Directory));
        Assert.False(restarted.CanUndo);
        Assert.Equal([batch.Id], restarted.Ledger.Editable(group).BatchIDs);
    }

    [Fact]
    public async Task RetentionProtectsDraftRetryAndUnreadableCollectionMembers()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var batch = await AddBatch(fixture, reader);
        var service = new CollectionService(fixture.Paths, reader);
        var group = service.Append(batch, "甲群");
        service.Ledger.ParkCurrent();
        service.Save();
        Assert.Equal(0, reader.PruneHistory(TimeSpan.FromDays(1), DateTimeOffset.UtcNow.AddDays(30), service.Ledger.ProtectedBatchIDs));
        File.WriteAllText(Path.Combine(batch.Directory, "manifest.json"), "broken");
        Assert.Equal(0, reader.PruneHistory(TimeSpan.FromDays(1), DateTimeOffset.UtcNow.AddDays(30), service.Ledger.ProtectedBatchIDs));
        Assert.True(Directory.Exists(batch.Directory));
        Assert.Throws<InvalidOperationException>(() => service.Freeze(group));
    }

    [Fact]
    public void CorruptLedgerIsRejectedWithoutReplacingOriginal()
    {
        using var fixture = new TempInbox();
        var path = Path.Combine(fixture.Root, "collections.json");
        var invalid = "{\"schemaVersion\":8,\"collections\":[],\"seenBatchIDs\":[]}";
        File.WriteAllText(path, invalid);
        Assert.Throws<InvalidDataException>(() => new CollectionService(fixture.Paths, new InboxReader(fixture.Paths)));
        Assert.Equal(invalid, File.ReadAllText(path));
    }

    [Fact]
    public void LedgerReadsMacOSStatusAndWritesMacOSContract()
    {
        var ledger = new BatchCollectionLedger();
        var batch = Guid.NewGuid();
        ledger.Append(batch);
        var json = JsonSerializer.Serialize(ledger, BatchManifest.JsonOptions);
        Assert.Contains("\"status\": \"collecting\"", json);
        Assert.DoesNotContain("protectedBatchIDs", json);
        Assert.DoesNotContain("current", json);
        var roundTrip = JsonSerializer.Deserialize<BatchCollectionLedger>(json, BatchManifest.JsonOptions)!;
        Assert.Contains(batch, roundTrip.Current!.BatchIDs);
    }

    [Fact]
    public void BoundaryMetadataCountsOverlapsWithoutChangingOriginalsAndKeepsMinuteOrder()
    {
        using var fixture = new TempInbox();
        var first = CreateZip(fixture, "first.zip", "·甲\n2026年9月20日 09:10\n首条\n\n·乙\n2026年9月20日 09:10\n末条\n");
        var second = CreateZip(fixture, "second.zip", "·乙\n2026年9月20日 09:10\n末条\n");
        var metadata = CollectionBatchMetadata.Read([first, second]);
        Assert.Equal(3, metadata.Count);
        Assert.Equal("首条", metadata.First?.Text);
        Assert.Equal("末条", metadata.Last?.Text);
        var damaged = fixture.WriteSource("broken.zip", "broken");
        var partial = CollectionBatchMetadata.Read([first, damaged]);
        Assert.Null(partial.Count);
        Assert.Equal("首条", partial.First?.Text);
    }

    internal static string CreateZip(TempInbox fixture, string name, string transcript)
    {
        var path = fixture.WriteSource(name, "");
        using var stream = File.Open(path, FileMode.Create);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("聊天记录.txt", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false));
        writer.Write(transcript);
        return path;
    }

    [Fact]
    public void FolderDeliveryUsesNestedSubfoldersAndMergesRepeatedConversation()
    {
        using var fixture = new TempInbox();
        var source = CreateZip(fixture, "one.zip", "·甲\n2026年9月20日 09:10\n第一条\n");
        var destination = Path.Combine(fixture.Root, "notes");
        Directory.CreateDirectory(destination);
        var notes = KnowledgeDelivery.Deliver([source], destination, "项目/周报", "甲群", null);
        Assert.StartsWith(Path.Combine(destination, "项目", "周报"), notes.Single());
        var repeated = KnowledgeDelivery.Deliver([source], destination, "项目/周报", "甲群", null);
        Assert.Equal(notes, repeated);
        Assert.Single(Directory.GetFiles(Path.Combine(destination, "项目", "周报"), "*.md"));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(destination, "项目", "周报", "附件"), "*.zip"));
    }

    [Fact]
    public void AllMacOSShareActionsRoundTripIncludingNewDestinations()
    {
        Assert.Equal(12, ShareActions.All.Count);
        foreach (var action in ShareActions.All)
            Assert.Equal(action, JsonSerializer.Deserialize<ShareAction>(JsonSerializer.Serialize(action, BatchManifest.JsonOptions), BatchManifest.JsonOptions));
        Assert.Equal(AgentId.DeepSeekHarness, AgentIds.Matching(ShareAction.DeepSeekHarness));
    }
}
