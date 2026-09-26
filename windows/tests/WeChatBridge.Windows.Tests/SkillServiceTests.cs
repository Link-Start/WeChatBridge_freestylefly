using System.IO.Compression;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The pane-facing half of the skill system: <c>Services/SkillService</c> is
/// compile-linked from the WPF project (kept WPF-free on purpose, like
/// <c>SceneService</c>), so these tests drive the real facade. Home, state and
/// resources all live under a throwaway tree — nothing touches the real
/// %USERPROFILE%\.codex or %LOCALAPPDATA%.
/// </summary>
public sealed class SkillServiceTests : IDisposable
{
    private readonly TempInbox _fixture = new();
    private readonly string _resources;
    private readonly string _home;
    private readonly string _state;

    public SkillServiceTests()
    {
        _resources = Path.Combine(_fixture.Root, "Resources");
        _home = Path.Combine(_fixture.Root, "Home");
        _state = Path.Combine(_fixture.Root, "State");
        Directory.CreateDirectory(_resources);
        Directory.CreateDirectory(_home);
    }

    public void Dispose() => _fixture.Dispose();

    private SkillService MakeService(
        Func<AgentId, bool>? agentInstalled = null,
        IReadOnlyList<WeChatScene>? scenes = null) =>
        new(_resources, _home, _state,
            agentInstalled ?? (_ => true),
            scenes is null ? null : () => scenes);

    private static OfficialSkill Skill(
        string id, string? package, AgentId[] agents, string version = "1.0.0") =>
        new(id, $"技能 {id}", "测试用技能", version, package, agents);

    private void WriteCatalog(params OfficialSkill[] skills)
    {
        var directory = Path.Combine(_resources, "Skills");
        Directory.CreateDirectory(directory);
        var entries = skills.Select(SkillJson);
        File.WriteAllText(
            Path.Combine(directory, "catalog.json"),
            $$"""{"schema_version":1,"skills":[{{string.Join(",", entries)}}]}""");
    }

    private static string SkillJson(OfficialSkill skill) =>
        $$"""{"id":"{{skill.Id}}","name":"{{skill.Name}}","summary":"{{skill.Summary}}","version":"{{skill.Version}}","package":{{(skill.Package is null ? "null" : $"\"{skill.Package}\"")}},"supported_agents":[{{string.Join(",", skill.SupportedAgents.Select(a => $"\"{a.RawValue()}\""))}}]}""";

    private void WritePackage(string package, string content = "# Test Skill\n") =>
        File.WriteAllText(
            Path.Combine(Directory.CreateDirectory(
                Path.Combine(_resources, "Skills", package)).FullName, "SKILL.md"),
            content);

    // MARK: - Catalog loading

    [Fact]
    public void MissingCatalogReadsAsLoadError()
    {
        var service = MakeService();
        Assert.NotNull(service.LoadError);
        Assert.Empty(service.Rows);
        Assert.Equal(0, service.SkillCount);
        Assert.Equal(0, service.SupportedAgentCount);
    }

    [Fact]
    public void InvalidCatalogReadsAsLoadError()
    {
        var directory = Path.Combine(_resources, "Skills");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "catalog.json"),
            """
            {
              "schema_version": 1,
              "skills": [
                {"id":"same","name":"A","summary":"","version":"1.0.0","package":null,"supported_agents":["doubao"]},
                {"id":"same","name":"B","summary":"","version":"1.0.0","package":null,"supported_agents":["claude"]}
              ]
            }
            """);
        var service = MakeService();
        Assert.Equal("技能清单内容无效。", service.LoadError);
        Assert.Empty(service.Rows);
    }

    [Fact]
    public void CatalogProducesOneRowPerSkillWithPerAgentStatus()
    {
        WriteCatalog(
            Skill("wechatbridge-packaged", "pkg",
                [AgentId.ChatGptCodex, AgentId.Doubao, AgentId.WorkBuddy]),
            Skill("wechatbridge-unshipped", null, [AgentId.ChatGptCodex, AgentId.Claude]));
        WritePackage("pkg");
        var service = MakeService(scenes:
            [new WeChatScene { RequiredSkillIDs = ["wechatbridge-packaged"] }]);

        Assert.Equal(2, service.Rows.Count);
        Assert.Equal(2, service.SkillCount);
        Assert.Equal(4, service.SupportedAgentCount);
        Assert.Equal(1, service.PendingInstallCount);
        Assert.True(service.CanInstallAll);

        var packaged = service.Rows[0];
        Assert.Equal(1, packaged.SceneCount);
        Assert.Equal("已安装 0 / 3", packaged.CoverageText);
        Assert.Equal(SkillCoverage.NotInstalled, packaged.Kind);
        Assert.Equal("安装", packaged.PrimaryTitle);
        Assert.Equal(SkillRowAction.Install, packaged.PrimaryAction);
        Assert.Equal(new SkillAgentStatus.NotInstalled(),
            packaged.States.Single(s => s.Agent == AgentId.ChatGptCodex).Status);
        Assert.Equal(new SkillAgentStatus.ManualOnly(),
            packaged.States.Single(s => s.Agent == AgentId.Doubao).Status);

        var unshipped = service.Rows[1];
        Assert.Equal(SkillCoverage.PackageUnavailable, unshipped.Kind);
        Assert.Equal("缺技能包", unshipped.StatusTitle);
        Assert.True(unshipped.PackageMissing);
        Assert.Equal("查看详情", unshipped.PrimaryTitle);
        Assert.All(unshipped.States,
            s => Assert.IsType<SkillAgentStatus.PackageUnavailable>(s.Status));
    }

    // MARK: - Direct installs

    [Fact]
    public async Task InstallMarksTheAgentRowAndRefreshesTheCard()
    {
        var skill = Skill("wechatbridge-test", "pkg", [AgentId.ChatGptCodex, AgentId.Doubao]);
        WriteCatalog(skill);
        WritePackage("pkg");
        var service = MakeService();
        var row = Assert.Single(service.Rows);
        var codex = row.States.Single(s => s.Agent == AgentId.ChatGptCodex);
        var doubao = row.States.Single(s => s.Agent == AgentId.Doubao);

        Assert.True(codex.ShowInstall);
        Assert.Equal("安装", codex.InstallTitle);
        Assert.True(doubao.ShowManualActions);

        await service.InstallAsync(row, codex);

        Assert.Equal(new SkillAgentStatus.Installed("1.0.0"), codex.Status);
        Assert.True(codex.ShowUninstall);
        Assert.Equal(SkillCoverage.Partial, row.Kind);
        Assert.Equal("部分安装", row.StatusTitle);
        Assert.True(File.Exists(
            Path.Combine(_home, ".codex", "skills", skill.Id, "SKILL.md")));
        Assert.True(File.Exists(
            Path.Combine(_home, ".codex", "skills", skill.Id, SkillInstaller.MetadataFileName)));

        await service.UninstallAsync(row, codex);
        Assert.Equal(new SkillAgentStatus.NotInstalled(), codex.Status);
        Assert.False(Directory.Exists(Path.Combine(_home, ".codex", "skills", skill.Id)));
    }

    [Fact]
    public async Task VersionBumpsReportUpdateThenInstall()
    {
        var skill = Skill("wechatbridge-test", "pkg", [AgentId.ChatGptCodex]);
        WriteCatalog(skill);
        WritePackage("pkg", "# v1\n");
        var service = MakeService();
        var row = Assert.Single(service.Rows);
        var codex = row.States[0];
        await service.InstallAsync(row, codex);

        // Bump the shipped package: same id, newer version, new content.
        WriteCatalog(skill with { Version = "1.1.0" });
        WritePackage("pkg", "# v1.1\n");
        service.Reload();
        row = Assert.Single(service.Rows);
        codex = row.States[0];

        Assert.Equal(new SkillAgentStatus.UpdateAvailable("1.0.0", "1.1.0"), codex.Status);
        Assert.Equal("更新", codex.InstallTitle);
        Assert.Equal(SkillCoverage.Update, row.Kind);
        Assert.Equal("有更新", row.StatusTitle);

        await service.InstallAsync(row, codex);
        Assert.Equal(new SkillAgentStatus.Installed("1.1.0"), codex.Status);
        Assert.Equal(SkillCoverage.Installed, row.Kind);
    }

    [Fact]
    public async Task ForeignDirectoryConflictsUntilExplicitlyReplaced()
    {
        var skill = Skill("wechatbridge-test", "pkg", [AgentId.ChatGptCodex]);
        WriteCatalog(skill);
        WritePackage("pkg");
        var target = Path.Combine(_home, ".codex", "skills", skill.Id);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "SKILL.md"), "mine");

        var service = MakeService();
        var row = Assert.Single(service.Rows);
        var codex = row.States[0];
        Assert.IsType<SkillAgentStatus.VersionConflict>(codex.Status);
        Assert.True(codex.ShowReplace);
        Assert.Equal(SkillCoverage.Conflict, row.Kind);
        Assert.Equal("查看详情", row.PrimaryTitle);

        await Assert.ThrowsAsync<SkillInstallException>(() => service.InstallAsync(row, codex));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "SKILL.md")));

        await service.InstallAsync(row, codex, replacingExisting: true);
        Assert.Equal(new SkillAgentStatus.Installed("1.0.0"), codex.Status);
    }

    [Fact]
    public async Task ExternalEditsReadAsConflict()
    {
        var skill = Skill("wechatbridge-test", "pkg", [AgentId.ChatGptCodex]);
        WriteCatalog(skill);
        WritePackage("pkg");
        var service = MakeService();
        var row = Assert.Single(service.Rows);
        var codex = row.States[0];
        await service.InstallAsync(row, codex);

        // The status is digest-verified: an edit under the agent dir flips the
        // row to a conflict without a reload.
        File.WriteAllText(
            Path.Combine(_home, ".codex", "skills", skill.Id, "SKILL.md"), "changed");
        await Assert.ThrowsAsync<SkillInstallException>(() => service.InstallAsync(row, codex));
        Assert.IsType<SkillAgentStatus.VersionConflict>(codex.Status);
    }

    // MARK: - Manual agents

    [Fact]
    public async Task ManualAgentsRoundTripConfirmations()
    {
        var skill = Skill("wechatbridge-test", "pkg", [AgentId.Doubao]);
        WriteCatalog(skill);
        WritePackage("pkg");
        var service = MakeService();
        var row = Assert.Single(service.Rows);
        var doubao = row.States[0];

        Assert.Equal(new SkillAgentStatus.ManualOnly(), doubao.Status);
        Assert.True(doubao.ShowManualActions);
        Assert.Equal("我已安装", doubao.ConfirmTitle);
        Assert.NotNull(doubao.StatusDetail); // the per-agent import guide

        await service.ConfirmManualAsync(row, doubao);
        Assert.Equal(new SkillAgentStatus.ManualConfirmed("1.0.0"), doubao.Status);
        Assert.Equal("撤销确认", doubao.ConfirmTitle);
        Assert.Equal("已确认安装", doubao.StatusText);
        Assert.True(File.Exists(Path.Combine(_state, "SkillConfirmations.json")));

        await service.RevokeManualAsync(row, doubao);
        Assert.Equal(new SkillAgentStatus.ManualOnly(), doubao.Status);
    }

    [Fact]
    public void UninstalledManualAgentReadsAsUnavailable()
    {
        var skill = Skill("wechatbridge-test", "pkg", [AgentId.ChatGptCodex, AgentId.Claude]);
        WriteCatalog(skill);
        WritePackage("pkg");
        var service = MakeService(agentInstalled: agent => agent != AgentId.Claude);
        var row = Assert.Single(service.Rows);

        var claude = row.States.Single(s => s.Agent == AgentId.Claude);
        Assert.Equal(new SkillAgentStatus.AgentUnavailable(), claude.Status);
        Assert.True(claude.ShowAgentMissing);
        Assert.Equal("仅手动", claude.StatusText);

        // Direct agents don't gate on app detection — the folder is ours to make.
        Assert.Equal(new SkillAgentStatus.NotInstalled(),
            row.States.Single(s => s.Agent == AgentId.ChatGptCodex).Status);
    }

    // MARK: - Sweeps and export

    [Fact]
    public async Task InstallAllMissingOnlyTouchesDirectAgents()
    {
        WriteCatalog(Skill("wechatbridge-test", "pkg",
            [AgentId.ChatGptCodex, AgentId.WorkBuddy, AgentId.Doubao]));
        WritePackage("pkg");
        var service = MakeService();

        var report = await service.InstallAllMissingAsync();

        Assert.Equal(2, report.Installed);
        Assert.True(report.Failed == 0, string.Join(" | ", report.Failures));
        Assert.False(service.AnyAutomaticMissing);
        Assert.False(service.CanInstallAll);
        var row = Assert.Single(service.Rows);
        Assert.Equal(new SkillAgentStatus.ManualOnly(),
            row.States.Single(s => s.Agent == AgentId.Doubao).Status);
        Assert.Equal(SkillCoverage.Partial, row.Kind); // doubao still manual-missing
    }

    [Fact]
    public async Task ManualArchiveIsAValidZipWithSkillRoot()
    {
        var skill = Skill("wechatbridge-test", "pkg", [AgentId.Claude]);
        WriteCatalog(skill);
        WritePackage("pkg");
        var service = MakeService();
        var destination = Path.Combine(_fixture.Root, "out", "manual.zip");

        await service.ExportManualArchiveAsync(service.Rows[0], destination);

        using var archive = ZipFile.OpenRead(destination);
        Assert.Contains(archive.Entries, entry => entry.FullName == $"{skill.Id}/SKILL.md");
    }

    // MARK: - Resource root detection

    [Fact]
    public void FindResourcesRootWalksUpAndAcceptsTheRepoLayout()
    {
        var start = Path.Combine(_fixture.Root, "app", "bin", "debug");
        Directory.CreateDirectory(start);
        Assert.Null(SkillService.FindResourcesRoot(start));

        // Repo layout: <root>/Resources/Skills/catalog.json.
        WriteCatalog(Skill("a", null, [AgentId.Doubao]));
        Assert.Equal(
            Path.GetFullPath(_resources),
            Path.GetFullPath(SkillService.FindResourcesRoot(start)!));

        // Flat layout wins at the same level: <root>/Skills/catalog.json.
        var flat = Path.Combine(_fixture.Root, "Skills");
        Directory.CreateDirectory(flat);
        File.WriteAllText(Path.Combine(flat, "catalog.json"), "{}");
        Assert.Equal(
            Path.GetFullPath(_fixture.Root),
            Path.GetFullPath(SkillService.FindResourcesRoot(start)!));
    }

    [Fact]
    public void SceneBadgeCountsOnlyMatchingScenes()
    {
        WriteCatalog(Skill("wechatbridge-test", null, [AgentId.Doubao]));
        var service = MakeService(scenes:
        [
            new WeChatScene { RequiredSkillIDs = ["wechatbridge-test"] },
            new WeChatScene { RequiredSkillIDs = ["other", "wechatbridge-test"] },
            new WeChatScene { RequiredSkillIDs = ["other"] },
        ]);
        Assert.Equal(2, Assert.Single(service.Rows).SceneCount);
    }
}
