using System.IO;
using System.IO.Compression;
using System.Text;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Delivery;
using WeChatBridge.Windows.Services;
using WeChatBridge.Windows.Tests;

namespace WeChatBridge.Windows.AppTests;

public sealed class ForwardingTests
{
    private static async Task<ReadyBatch> Add(TempInbox fixture, InboxReader reader, string name, string chatName)
    {
        var source = fixture.WriteSource(name, "");
        using (var stream = File.Open(source, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("聊天记录.txt", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)))
            writer.Write("·甲\n2026年9月20日 09:10\n第一条\n");
        var committed = await InboxWriter.CommitAsync(fixture.Paths,
            [new InboxSourceFile(source, name, "application/zip", 0, 0)]);
        reader.RecordContext(committed.BatchId, chatName);
        return reader.LoadBatches().Single(b => b.Id == committed.BatchId);
    }

    private static SceneService Scenes(string directory)
    {
        var service = new SceneService(directory, titleReader: _ => Task.FromResult<GroupTitleParser.Title?>(null));
        var settings = service.LoadSettings();
        foreach (var scene in settings.Scenes) scene.Enabled = false;
        service.Scenes.Save(settings);
        return service;
    }

    private static DeliveryEngine Engine(InboxReader reader, Action<PastePayload> copied) => new(new DeliveryEnvironment
    {
        ResolveTarget = spec => new ResolvedTarget(spec, 123, 456, null),
        ActivateAsync = (target, _) => Task.FromResult(target),
        ForegroundWindow = () => 456,
        WindowProcessId = _ => 123,
        ThisProcessId = () => 999,
        WriteFileDropList = paths => { copied(new PastePayload.Files(paths)); return true; },
        WriteClipboardText = text => { copied(new PastePayload.Text(text)); return true; },
        SendCtrlV = () => true,
        DelayAsync = (_, _) => Task.CompletedTask,
    }, reader);

    [Fact]
    public async Task ChangingHubDestinationResolvesSelectedAppAndRecordsSelectedAction()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var batch = await Add(fixture, reader, "one.zip", "项目群");
        var pasted = new List<PastePayload>();
        using var scenes = Scenes(Path.Combine(fixture.Root, "config"));
        var model = new MainViewModel(fixture.Paths, Path.Combine(fixture.Root, "config"), false,
            Engine(reader, pasted.Add), scenes, pasted.Add);
        await model.PerformForward(batch with { Action = ShareAction.Hub }, ShareAction.DeepSeekHarness, null);
        Assert.Equal(ShareAction.DeepSeekHarness, reader.StateFor(batch.Id)?.Action);
        Assert.Equal(BatchOutcomeKind.Delivered, reader.StateFor(batch.Id)?.Outcome?.Kind);
        Assert.NotEmpty(pasted);
        Assert.Contains(pasted, payload => payload is PastePayload.Files);
    }

    [Fact]
    public async Task ObsidianDeliveryIsQuietAndClearsOldSceneContext()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var batch = await Add(fixture, reader, "one.zip", "项目群");
        reader.RecordContext(batch.Id, sceneId: "old-scene", sceneName: "旧场景");
        var vault = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(vault);
        var config = Path.Combine(fixture.Root, "config");
        new AppSettingsStore(config).Save(new AppSettings { ObsidianVaultPath = vault });
        using var scenes = Scenes(config);
        var model = new MainViewModel(fixture.Paths, config, false, scenes: scenes, copyPayload: _ => { });
        var notifications = new List<string>();
        model.DeliveryNotificationRequested += (_, button, _) => notifications.Add(button);
        Assert.False(model.OpenObsidianAfterDelivery);
        await model.PerformForward(batch, ShareAction.Obsidian, null);
        Assert.Equal(["打开笔记"], notifications);
        Assert.Null(reader.StateFor(batch.Id)?.SceneID);
        Assert.Equal("项目群", reader.StateFor(batch.Id)?.ChatName);
        Assert.Single(Directory.GetFiles(vault, "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CollectionDeliversAllOriginalsInOnePlanAndRecordsEveryMember()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var first = await Add(fixture, reader, "one.zip", "项目群");
        var second = await Add(fixture, reader, "two.zip", "项目群");
        var config = Path.Combine(fixture.Root, "config");
        using var scenes = Scenes(config);
        var payloads = new List<PastePayload>();
        var model = new MainViewModel(fixture.Paths, config, false, Engine(reader, payloads.Add), scenes, payloads.Add);
        var group = model.Collections.Append(first, null);
        model.Collections.Append(second, null);
        await model.DeliverCollection(group, ShareAction.Codex, null);
        Assert.Equal(CollectionStatus.Delivered, model.Collections.Ledger.Editable(group).Status);
        Assert.All(payloads.OfType<PastePayload.Files>(), files => Assert.Equal([first.Items[0].FullPath, second.Items[0].FullPath], files.Paths));
        Assert.Equal(BatchOutcomeKind.Delivered, reader.StateFor(first.Id)?.Outcome?.Kind);
        Assert.Equal(BatchOutcomeKind.Delivered, reader.StateFor(second.Id)?.Outcome?.Kind);
    }

    [Fact]
    public async Task CollectionArchivingPreservesDistinctConversationNamesAndMissingFolderIsRetryable()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var first = await Add(fixture, reader, "one.zip", "甲群");
        var second = await Add(fixture, reader, "two.zip", "乙群");
        var config = Path.Combine(fixture.Root, "config");
        using var scenes = Scenes(config);
        var model = new MainViewModel(fixture.Paths, config, false, scenes: scenes, copyPayload: _ => { });
        var group = model.Collections.Append(first, null);
        model.Collections.Append(second, null);
        await model.DeliverCollection(group, ShareAction.Folder, null);
        Assert.Equal(CollectionStatus.Retry, model.Collections.Ledger.Editable(group).Status);
        Assert.All(new[] { first, second }, batch => Assert.True(File.Exists(batch.Items[0].FullPath)));
        var notes = Path.Combine(fixture.Root, "notes");
        Directory.CreateDirectory(notes);
        model.DeliveryFolderPath = notes;
        model.DeliverySubfolder = "项目/周报";
        await model.DeliverCollection(group, ShareAction.Folder, null);
        var names = Directory.GetFiles(notes, "*.md", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.Contains("甲群的聊天.md", names);
        Assert.Contains("乙群的聊天.md", names);
        Assert.Equal(CollectionStatus.Delivered, model.Collections.Ledger.Editable(group).Status);
    }
}
