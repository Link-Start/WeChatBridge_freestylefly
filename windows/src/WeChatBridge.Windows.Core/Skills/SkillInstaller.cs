using System.IO.Compression;
using System.Text.Json;
using static WeChatBridge.Windows.Core.SkillPackage;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// Installs the official SKILL.md packages into agent skill directories and
/// tracks manual confirmations. Ported from the <c>SkillInstaller</c> struct in
/// AgentID.swift. The on-disk contract is identical on both platforms: the
/// <see cref="MetadataFileName"/> sidecar, a SHA-256 digest over
/// <c>relativePath bytes + 0x00 + file bytes + 0x00</c> per file in sorted
/// order (skipping the sidecar), and <c>SkillConfirmations.json</c> in the
/// state directory.
///
/// Two macOS facilities have no direct Windows equivalent and are emulated:
/// <c>FileManager.replaceItemAt</c> becomes staging directory + move-aside +
/// move-in, and the hand-rolled zip writer becomes <see cref="ZipArchive"/>
/// with stored (uncompressed) entries, matching the macOS output format.
/// </summary>
public sealed class SkillInstaller
{
    public const string MetadataFileName = ".wechatbridge-install.json";

    private const string ConfirmationsFileName = "SkillConfirmations.json";
    private const string NotManagedMessage = "这个技能不是由微信流安装的，不能由微信流删除。";

    private sealed record Metadata(string Id, string Version, string Digest);

    /// <remarks>
    /// The property is spelled <c>SkillID</c> (not <c>SkillId</c>) because the
    /// shared camelCase JSON policy then emits "skillID" — the exact key the
    /// macOS Confirmation struct encodes — keeping the state file identical.
    /// </remarks>
    private sealed record Confirmation(
        string SkillID,
        string Agent,
        string Version,
        DateTimeOffset ConfirmedAt);

    private readonly string _homeDirectory;
    private readonly string _stateDirectory;

    /// <summary>
    /// <paramref name="homeDirectory"/> defaults to %USERPROFILE% (the agent
    /// skill roots are home-relative). <paramref name="stateDirectory"/>
    /// defaults to <see cref="ConfigStore.DefaultDirectory"/>, the Windows
    /// equivalent of ~/Library/Application Support/WeChatBridge.
    /// </summary>
    public SkillInstaller(string? homeDirectory = null, string? stateDirectory = null)
    {
        _homeDirectory = homeDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _stateDirectory = stateDirectory ?? ConfigStore.DefaultDirectory;
    }

    public SkillInstallPlan Plan(
        OfficialSkill skill,
        AgentId agent,
        string resourcesRoot,
        bool agentInstalled = true)
    {
        if (skill.Package is not { } package)
        {
            return new SkillInstallPlan(
                skill.Id, agent, agentInstalled, new SkillInstallMethod.Unavailable());
        }

        var source = PackagePath(package, resourcesRoot);
        if (agent.DirectSkillRoot() is { } root)
        {
            return new SkillInstallPlan(
                skill.Id,
                agent,
                agentInstalled,
                new SkillInstallMethod.Direct(
                    source,
                    Path.Combine(_homeDirectory, root, skill.Id)));
        }

        return new SkillInstallPlan(
            skill.Id,
            agent,
            agentInstalled,
            new SkillInstallMethod.Manual(
                agent.ManualInstallGuide() ?? "请在应用设置中导入技能包。"));
    }

    public SkillAgentStatus Status(
        OfficialSkill skill,
        AgentId agent,
        string resourcesRoot,
        bool agentInstalled = true)
    {
        if (skill.Package is null)
            return new SkillAgentStatus.PackageUnavailable();
        var source = PackagePath(skill.Package, resourcesRoot);
        if (!File.Exists(Path.Combine(source, "SKILL.md")))
            return new SkillAgentStatus.PackageUnavailable();
        if (!agentInstalled && agent.DirectSkillRoot() is null)
            return new SkillAgentStatus.AgentUnavailable();

        switch (Plan(skill, agent, resourcesRoot, agentInstalled).Method)
        {
            case SkillInstallMethod.Unavailable:
                return new SkillAgentStatus.PackageUnavailable();

            case SkillInstallMethod.Manual:
            {
                var confirmations = Confirmations();
                return confirmations.TryGetValue(ConfirmationKey(skill.Id, agent), out var confirmed)
                       && confirmed.Version == skill.Version
                    ? new SkillAgentStatus.ManualConfirmed(confirmed.Version)
                    : new SkillAgentStatus.ManualOnly();
            }

            case SkillInstallMethod.Direct { Target: var target }:
            {
                // macOS fileExists(atPath:) is true for a file OR a directory —
                // a stray file at the target must surface as a conflict, not
                // "not installed".
                if (!PathExists(target))
                    return new SkillAgentStatus.NotInstalled();
                try
                {
                    var metadata = ReadMetadata(target);
                    if (metadata.Id != skill.Id)
                        return new SkillAgentStatus.VersionConflict("目标目录属于另一个技能。");
                    if (PackageDigest(target) != metadata.Digest)
                        return new SkillAgentStatus.VersionConflict("已安装技能被外部修改，未自动覆盖。");
                    var installed = SceneVersion.Parse(metadata.Version);
                    var available = SceneVersion.Parse(skill.Version);
                    if (installed is null || available is null)
                        return new SkillAgentStatus.VersionConflict("版本号格式无效。");
                    if (installed.Value < available.Value)
                        return new SkillAgentStatus.UpdateAvailable(metadata.Version, skill.Version);
                    if (installed.Value > available.Value)
                        return new SkillAgentStatus.VersionConflict(
                            $"已安装 {metadata.Version}，内置版本为 {skill.Version}。");
                    return new SkillAgentStatus.Installed(metadata.Version);
                }
                catch (Exception error) when (IsFileSystemError(error)
                    || error is JsonException or SkillInstallException)
                {
                    // macOS funnels every read/parse failure here into a conflict.
                    return new SkillAgentStatus.VersionConflict(error.Message);
                }
            }

            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    /// <summary>
    /// Copies the validated package into the agent's skill root and returns the
    /// version that was written. The safety checks mirror macOS verbatim: the
    /// target must be empty, ours, or explicitly replaced; a digest mismatch or
    /// a downgrade is refused unless the caller opted into replacement.
    /// </summary>
    public string Install(
        OfficialSkill skill,
        AgentId agent,
        string resourcesRoot,
        bool replacingExisting = false)
    {
        if (skill.Package is null)
            throw new SkillInstallException("技能包尚未随当前构建提供。");
        if (Plan(skill, agent, resourcesRoot).Method is not SkillInstallMethod.Direct direct)
            throw new SkillInstallException("这个应用只支持手动导入技能包。");

        var digest = ValidatePackage(direct.Source);
        var target = direct.Target;
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);

        if (PathExists(target))
        {
            var metadata = TryReadMetadata(target);
            if (metadata is not null && metadata.Id != skill.Id)
                throw new SkillInstallException("目标目录属于另一个技能，未自动覆盖。");
            if (metadata is null && !replacingExisting)
                throw new SkillInstallException("目标目录已存在且不是由微信流安装。");
            if (metadata is not null)
            {
                if (!replacingExisting && PackageDigest(target) != metadata.Digest)
                    throw new SkillInstallException("已安装技能被外部修改，未自动覆盖。");
                var installed = SceneVersion.Parse(metadata.Version);
                var available = SceneVersion.Parse(skill.Version);
                if (installed is { } i && available is { } a && i > a && !replacingExisting)
                {
                    throw new SkillInstallException(
                        $"已安装 {metadata.Version}，内置版本为 {skill.Version}。");
                }
            }
        }

        // Windows has no FileManager.replaceItemAt. A staging directory plus a
        // backup-and-move gives the same all-or-nothing shape: the target path
        // only ever contains a complete tree.
        var staging = Path.Combine(parent, $".{skill.Id}.staging-{Guid.NewGuid():N}");
        try
        {
            CopyPackage(direct.Source, staging);
            WriteMetadata(new Metadata(skill.Id, skill.Version, digest), staging);

            if (PathExists(target))
            {
                var backup = Path.Combine(parent, $".{skill.Id}.backup-{Guid.NewGuid():N}");
                MoveAny(target, backup);
                try
                {
                    Directory.Move(staging, target);
                }
                catch
                {
                    // Put the untouched original back before reporting failure.
                    MoveAny(backup, target);
                    throw;
                }
                try { DeleteAny(backup); } catch (Exception error) when (IsFileSystemError(error)) { }
            }
            else
            {
                Directory.Move(staging, target);
            }
        }
        finally
        {
            try { DeleteAny(staging); } catch (Exception error) when (IsFileSystemError(error)) { }
        }
        return skill.Version;
    }

    /// <summary>
    /// Removes our install. For manual-only agents there is nothing on disk, so
    /// — as on macOS — uninstalling just forgets the confirmation.
    /// </summary>
    public void Uninstall(OfficialSkill skill, AgentId agent)
    {
        if (agent.DirectSkillRoot() is not { } root)
        {
            RevokeManualConfirmation(skill, agent);
            return;
        }

        var target = Path.Combine(_homeDirectory, root, skill.Id);
        var metadata = ReadMetadata(target);
        if (metadata.Id != skill.Id)
            throw new SkillInstallException("目标目录属于另一个技能，未自动覆盖。");
        DeleteAny(target);
    }

    /// <summary>
    /// Records that the user imported this exact version through the agent's
    /// own UI. Manual-only agents have no directory to inspect, so trust rests
    /// on this explicit confirmation.
    /// </summary>
    public void ConfirmManual(OfficialSkill skill, AgentId agent)
    {
        if (agent.DirectSkillRoot() is not null)
            throw new SkillInstallException("not manual");
        var all = Confirmations();
        all[ConfirmationKey(skill.Id, agent)] = new Confirmation(
            skill.Id, agent.RawValue(), skill.Version, DateTimeOffset.UtcNow);
        WriteConfirmations(all);
    }

    public void RevokeManualConfirmation(OfficialSkill skill, AgentId agent)
    {
        var all = Confirmations();
        all.Remove(ConfirmationKey(skill.Id, agent));
        try
        {
            WriteConfirmations(all);
        }
        catch (Exception error) when (IsFileSystemError(error))
        {
            // macOS swallows the write failure here (try?); a stale record is
            // harmlessly version-checked on the next read.
        }
    }

    /// <summary>
    /// Writes the package as a zip rooted at the skill id so the agent's import
    /// UI receives the same shape macOS produces with its hand-rolled writer:
    /// entries "<c>{id}/{relativePath}</c>", stored uncompressed.
    /// </summary>
    public void MakeManualArchive(OfficialSkill skill, string resourcesRoot, string destination)
    {
        if (skill.Package is null)
            throw new SkillInstallException("技能包尚未随当前构建提供。");
        var source = PackagePath(skill.Package, resourcesRoot);
        ValidatePackage(source);
        var files = EnumeratePackageFiles(source);

        var fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);

        // Write beside the destination then move it over, the .atomic-style
        // completion macOS gets from Data.write(options: .atomic).
        var staging = fullDestination + ".tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    var entry = archive.CreateEntry(
                        $"{skill.Id}/{file.RelativePath}", CompressionLevel.NoCompression);
                    using var entryStream = entry.Open();
                    using var input = new FileStream(
                        file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    input.CopyTo(entryStream);
                }
            }
            File.Move(staging, fullDestination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); }
            catch (Exception error) when (IsFileSystemError(error)) { }
        }
    }

    private static string PackagePath(string package, string resourcesRoot) =>
        Path.Combine(resourcesRoot, "Skills", package);

    private static string ConfirmationKey(string skillId, AgentId agent) =>
        $"{skillId}|{agent.RawValue()}";

    /// <summary>
    /// A corrupt or missing file reads as empty, matching the macOS try? decode.
    /// Records written under a legacy dotted skill id are re-keyed on read.
    /// </summary>
    private Dictionary<string, Confirmation> Confirmations()
    {
        var stored = ConfigStore.Load<Dictionary<string, Confirmation>>(_stateDirectory, ConfirmationsFileName);
        var result = new Dictionary<string, Confirmation>(StringComparer.Ordinal);
        if (stored is null)
            return result;
        foreach (var confirmation in stored.Values)
        {
            var migrated = confirmation with { SkillID = SkillId.Migrate(confirmation.SkillID) };
            result[$"{migrated.SkillID}|{migrated.Agent}"] = migrated;
        }
        return result;
    }

    private void WriteConfirmations(Dictionary<string, Confirmation> values) =>
        ConfigStore.Save(
            _stateDirectory,
            ConfirmationsFileName,
            // SortedDictionary keeps the JSON keys in the sorted order macOS
            // emits with .sortedKeys, so the state file stays diff-stable.
            new SortedDictionary<string, Confirmation>(values, StringComparer.Ordinal));

    private static Metadata ReadMetadata(string directory)
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(directory, MetadataFileName));
            return JsonSerializer.Deserialize<Metadata>(json, BatchManifest.JsonOptions)
                ?? throw new SkillInstallException(NotManagedMessage);
        }
        catch (Exception error) when (IsFileSystemError(error))
        {
            // A missing or unreadable sidecar means the directory is not ours.
            throw new SkillInstallException(NotManagedMessage);
        }
    }

    /// <summary>The install pre-flight uses try? semantics: unreadable metadata is "not ours".</summary>
    private static Metadata? TryReadMetadata(string directory)
    {
        try
        {
            return ReadMetadata(directory);
        }
        catch (Exception error) when (IsFileSystemError(error)
            || error is JsonException or SkillInstallException)
        {
            return null;
        }
    }

    private static void WriteMetadata(Metadata metadata, string directory) =>
        File.WriteAllText(
            Path.Combine(directory, MetadataFileName),
            JsonSerializer.Serialize(metadata, BatchManifest.JsonOptions));
}
