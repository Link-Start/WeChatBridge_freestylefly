using System.IO.Compression;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// Ported from SkillInstallerTests.swift. Home, state and resources all live in
/// a throwaway tree so no test touches %USERPROFILE% or %LOCALAPPDATA%.
/// </summary>
public sealed class SkillInstallerTests : IDisposable
{
    private readonly TempInbox _fixture = new();
    private readonly string _resources;
    private readonly string _home;
    private readonly SkillInstaller _installer;

    public SkillInstallerTests()
    {
        _resources = Path.Combine(_fixture.Root, "Resources");
        _home = Path.Combine(_fixture.Root, "Home");
        var state = Path.Combine(_fixture.Root, "State");
        Directory.CreateDirectory(_resources);
        _installer = new SkillInstaller(_home, state);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void InstallPlanCoversDirectAndManualAgents()
    {
        var skill = MakeSkill();
        var direct = new[]
        {
            (AgentId.ChatGptCodex, ".codex/skills"),
            (AgentId.QwenWork, ".qwenworkcn/skills"),
            (AgentId.WorkBuddy, ".workbuddy/skills"),
        };
        foreach (var (agent, root) in direct)
        {
            var plan = _installer.Plan(skill, agent, _resources);
            var method = Assert.IsType<SkillInstallMethod.Direct>(plan.Method);
            // The DirectSkillRoot constants contain '/', so both sides are
            // normalized through GetFullPath before comparing.
            Assert.Equal(
                Path.GetFullPath(Path.Combine(_home, root, skill.Id)),
                Path.GetFullPath(method.Target));
        }

        foreach (var agent in new[] { AgentId.Doubao, AgentId.Claude })
        {
            var plan = _installer.Plan(skill, agent, _resources);
            var manual = Assert.IsType<SkillInstallMethod.Manual>(plan.Method);
            Assert.False(string.IsNullOrEmpty(manual.Guide));
        }
    }

    [Fact]
    public void InstallDetectsUpdatesAndUserModification()
    {
        var skill = MakeSkill(version: "1.0.0");
        Assert.Equal(
            new SkillAgentStatus.NotInstalled(),
            _installer.Status(skill, AgentId.ChatGptCodex, _resources));

        _installer.Install(skill, AgentId.ChatGptCodex, _resources);
        Assert.Equal(
            new SkillAgentStatus.Installed("1.0.0"),
            _installer.Status(skill, AgentId.ChatGptCodex, _resources));

        skill = ReplaceCatalogVersion(skill, "1.1.0");
        Assert.Equal(
            new SkillAgentStatus.UpdateAvailable("1.0.0", "1.1.0"),
            _installer.Status(skill, AgentId.ChatGptCodex, _resources));

        _installer.Install(skill, AgentId.ChatGptCodex, _resources);
        Assert.Equal(
            new SkillAgentStatus.Installed("1.1.0"),
            _installer.Status(skill, AgentId.ChatGptCodex, _resources));

        var installed = Path.Combine(_home, ".codex", "skills", skill.Id);
        File.WriteAllText(Path.Combine(installed, "SKILL.md"), "changed");
        Assert.IsType<SkillAgentStatus.VersionConflict>(
            _installer.Status(skill, AgentId.ChatGptCodex, _resources));

        var error = Assert.Throws<SkillInstallException>(
            () => _installer.Install(skill, AgentId.ChatGptCodex, _resources));
        Assert.Equal("已安装技能被外部修改，未自动覆盖。", error.Message);
    }

    [Fact]
    public void UnmanagedConflictRequiresReplacement()
    {
        var skill = MakeSkill();
        var target = Path.Combine(_home, ".codex", "skills", skill.Id);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "SKILL.md"), "mine");

        var error = Assert.Throws<SkillInstallException>(
            () => _installer.Install(skill, AgentId.ChatGptCodex, _resources));
        Assert.Equal("目标目录已存在且不是由微信流安装。", error.Message);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "SKILL.md")));

        _installer.Install(skill, AgentId.ChatGptCodex, _resources, replacingExisting: true);
        Assert.Equal(
            new SkillAgentStatus.Installed("1.0.0"),
            _installer.Status(skill, AgentId.ChatGptCodex, _resources));
    }

    [Fact]
    public void ManualConfirmationTracksTheExactVersion()
    {
        var skill = MakeSkill(version: "1.0.0");
        Assert.Equal(
            new SkillAgentStatus.ManualOnly(),
            _installer.Status(skill, AgentId.Doubao, _resources));

        _installer.ConfirmManual(skill, AgentId.Doubao);
        Assert.Equal(
            new SkillAgentStatus.ManualConfirmed("1.0.0"),
            _installer.Status(skill, AgentId.Doubao, _resources));

        skill = ReplaceCatalogVersion(skill, "1.1.0");
        Assert.Equal(
            new SkillAgentStatus.ManualOnly(),
            _installer.Status(skill, AgentId.Doubao, _resources));

        _installer.RevokeManualConfirmation(skill, AgentId.Doubao);
        Assert.Equal(
            new SkillAgentStatus.ManualOnly(),
            _installer.Status(skill, AgentId.Doubao, _resources));
    }

    [Fact]
    public void ManualArchiveIsAValidZipWithSkillRoot()
    {
        var skill = MakeSkill();
        var destination = Path.Combine(_fixture.Root, "manual.zip");
        _installer.MakeManualArchive(skill, _resources, destination);

        // unzip -l on macOS ≈ opening the archive and reading the entry names.
        using var archive = ZipFile.OpenRead(destination);
        Assert.Contains(archive.Entries, entry => entry.FullName == $"{skill.Id}/SKILL.md");
    }

    [Fact]
    public void SymlinkPackageIsRejected()
    {
        var skill = MakeSkill();
        var source = PackagePath(skill);
        if (!TryCreateReparsePoint(
                Path.Combine(source, "outside"),
                Path.Combine(_fixture.Root, "outside")))
        {
            // Windows refuses file/directory symlinks without Developer Mode or
            // elevation; the junction fallback can still be policy-blocked.
            // Without any reparse point there is nothing meaningful to assert.
            return;
        }

        var error = Assert.Throws<SkillInstallException>(
            () => _installer.Install(skill, AgentId.ChatGptCodex, _resources));
        Assert.Equal("技能包不能包含符号链接。", error.Message);
    }

    [Fact]
    public void CatalogLoaderRejectsDuplicateIds()
    {
        var skills = Path.Combine(_resources, "Skills");
        Directory.CreateDirectory(skills);
        const string json = """
            {
              "schema_version": 1,
              "skills": [
                {"id":"same","name":"A","summary":"","version":"1.0.0","package":null,"supported_agents":["doubao"]},
                {"id":"same","name":"B","summary":"","version":"1.0.0","package":null,"supported_agents":["claude"]}
              ]
            }
            """;
        File.WriteAllText(Path.Combine(skills, "catalog.json"), json);
        // The contract rejects this file with SkillInstallException — the same
        // type that backs the macOS .invalidCatalog case.
        Assert.Throws<SkillInstallException>(() => OfficialSkillCatalog.LoadFrom(_resources));
    }

    /// <summary>
    /// Windows link creation needs more privilege than the test process may
    /// have, so each mechanism is tried in turn: file symlink, directory
    /// symlink, then a directory junction (which needs neither admin nor
    /// Developer Mode on most systems). All produce the reparse point the
    /// installer must reject.
    /// </summary>
    private static bool TryCreateReparsePoint(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception error) when (IsLinkFailure(error))
        {
        }

        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception error) when (IsLinkFailure(error))
        {
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(
                    "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            process!.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception error) when (IsLinkFailure(error))
        {
            return false;
        }
    }

    private static bool IsLinkFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or NotSupportedException;

    private OfficialSkill MakeSkill(string version = "1.0.0")
    {
        var skill = new OfficialSkill(
            Id: "dev.wechatbridge.test",
            Name: "测试技能",
            Summary: "测试",
            Version: version,
            Package: "test-skill",
            SupportedAgents: AgentIds.All);
        var source = PackagePath(skill);
        Directory.CreateDirectory(Path.Combine(source, "scripts"));
        File.WriteAllText(Path.Combine(source, "SKILL.md"), "# Test Skill\n");
        File.WriteAllText(Path.Combine(source, "scripts", "run.sh"), "#!/bin/sh\n");
        return skill;
    }

    private string PackagePath(OfficialSkill skill) =>
        Path.Combine(_resources, "Skills", skill.Package!);

    private OfficialSkill ReplaceCatalogVersion(OfficialSkill skill, string version)
    {
        File.WriteAllText(
            Path.Combine(PackagePath(skill), "SKILL.md"), $"# Test Skill {version}\n");
        return skill with { Version = version };
    }
}
