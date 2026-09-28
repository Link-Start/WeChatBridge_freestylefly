import CryptoKit
import Foundation

/// A target that can carry an Agent Skill.
public enum AgentID: String, Codable, CaseIterable, Hashable, Identifiable, Sendable {
    case chatGPTCodex
    case claude
    case doubao
    case qwenWork
    case workBuddy
    case weSight

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .chatGPTCodex: return "ChatGPT / Codex"
        case .claude: return "Claude"
        case .doubao: return L10n.text("豆包")
        case .qwenWork: return L10n.text("千问办公")
        case .workBuddy: return "WorkBuddy"
        case .weSight: return "WeSight"
        }
    }

    public var bundleIdentifier: String {
        switch self {
        case .chatGPTCodex: return "com.openai.codex"
        case .claude: return "com.anthropic.claudefordesktop"
        case .doubao: return "com.bot.pc.doubao"
        case .qwenWork: return "com.alibaba.qwenwork"
        case .workBuddy: return "com.tencent.workbuddy.mac"
        case .weSight: return "ai.wesight.app"
        }
    }

    public var logoSuffix: String {
        switch self {
        case .chatGPTCodex: return "chatgpt"
        case .claude: return "claude"
        case .doubao: return "doubao"
        case .qwenWork: return "qwen"
        case .workBuddy: return "workbuddy"
        case .weSight: return "wesight"
        }
    }

    /// Home-relative roots documented by the product. A nil root means the app
    /// accepts skills only through its own import UI.
    public var directSkillRoot: String? {
        switch self {
        case .chatGPTCodex: return ".codex/skills"
        case .claude, .doubao, .weSight: return nil
        case .qwenWork: return ".qwenworkcn/skills"
        case .workBuddy: return ".workbuddy/skills"
        }
    }

    public var manualInstallGuide: String? {
        switch self {
        case .chatGPTCodex:
            return L10n.text("Codex 模式会读取 ~/.codex/skills；Chat 或 Work 模式请在 Plugins / Skills 中导入。")
        case .claude:
            return L10n.text("在 Claude 的 Settings → Capabilities → Skills 中上传技能包。")
        case .doubao:
            return L10n.text("在豆包的“工作 → 技能·连接器·伙伴 → 我的技能 → 新建 → 上传技能”中导入。")
        case .qwenWork, .workBuddy, .weSight:
            return nil
        }
    }

    public static func matching(bundleIdentifier: String) -> AgentID? {
        allCases.first { $0.bundleIdentifier == bundleIdentifier }
    }

    public static func matching(_ action: ShareAction) -> AgentID? {
        action.targetBundleIdentifier.flatMap(matching(bundleIdentifier:))
    }
}

/// One official skill package shipped with WeChatBridge.
public struct OfficialSkill: Codable, Hashable, Identifiable, Sendable {
    public let id: String
    public let name: String
    public let summary: String
    public let version: String
    /// Directory under `Resources/Skills`. Nil means the package has not been
    /// supplied yet, but scenes may still reference its stable ID.
    public let package: String?
    public let supportedAgents: [AgentID]

    public init(
        id: String,
        name: String,
        summary: String,
        version: String,
        package: String?,
        supportedAgents: [AgentID]
    ) {
        self.id = id
        self.name = name
        self.summary = summary
        self.version = version
        self.package = package
        self.supportedAgents = supportedAgents
    }
}

public struct OfficialSkillCatalog: Codable, Hashable, Sendable {
    public let schemaVersion: Int
    public let skills: [OfficialSkill]

    public init(schemaVersion: Int = 1, skills: [OfficialSkill]) {
        self.schemaVersion = schemaVersion
        self.skills = skills
    }

    public static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return decoder
    }

    public static func load(from resourcesRoot: URL) throws -> OfficialSkillCatalog {
        let url = resourcesRoot
            .appendingPathComponent("Skills", isDirectory: true)
            .appendingPathComponent("catalog.json")
        let data = try Data(contentsOf: url)
        let catalog = try decoder().decode(OfficialSkillCatalog.self, from: data)
        guard catalog.schemaVersion == 1 else {
            throw SkillInstallError.unsupportedCatalog
        }
        var seen = Set<String>()
        for skill in catalog.skills {
            guard !skill.id.isEmpty, seen.insert(skill.id).inserted else {
                throw SkillInstallError.invalidCatalog
            }
            guard SkillId.isValid(skill.id) else {
                throw SkillError(L10n.format(
                    "技能 ID「%@」不符合规范：只能包含小写字母、数字和连字符。",
                    skill.id
                ))
            }
        }
        return catalog
    }
}

public struct SkillInstallPlan: Equatable, Sendable {
    public enum Method: Equatable, Sendable {
        case direct(source: URL, target: URL)
        case manual(guide: String)
        case unavailable
    }

    public let skillID: String
    public let agent: AgentID
    public let agentInstalled: Bool
    public let method: Method
}

public enum SkillAgentStatus: Equatable, Sendable {
    case packageUnavailable
    case agentUnavailable
    case notInstalled
    case installed(version: String)
    case updateAvailable(installed: String, available: String)
    case versionConflict(detail: String)
    case manualOnly
    case manualConfirmed(version: String)
}

public enum SkillInstallError: Error, Equatable, LocalizedError, Sendable {
    case unsupportedCatalog
    case invalidCatalog
    case packageUnavailable
    case invalidPackage(String)
    case unsafePackage(String)
    case manualInstallOnly
    case userModified
    case versionConflict(String)
    case notManaged
    case installedByAnotherPackage

    public var errorDescription: String? {
        switch self {
        case .unsupportedCatalog:
            return L10n.text("技能清单版本不受支持。")
        case .invalidCatalog:
            return L10n.text("技能清单内容无效。")
        case .packageUnavailable:
            return L10n.text("技能包尚未随当前构建提供。")
        case .invalidPackage(let detail):
            return detail
        case .unsafePackage(let detail):
            return detail
        case .manualInstallOnly:
            return L10n.text("这个应用只支持手动导入技能包。")
        case .userModified:
            return L10n.text("已安装技能被外部修改，未自动覆盖。")
        case .versionConflict(let detail):
            return detail
        case .notManaged:
            return L10n.text("这个技能不是由微信流安装的，不能由微信流删除。")
        case .installedByAnotherPackage:
            return L10n.text("目标目录属于另一个技能，未自动覆盖。")
        }
    }
}

public struct SkillInstaller: Sendable {
    public static let metadataFileName = ".wechatbridge-install.json"

    private struct Metadata: Codable, Equatable, Sendable {
        let id: String
        let version: String
        let digest: String
    }

    private struct Confirmation: Codable, Sendable {
        let skillID: String
        let agent: String
        let version: String
        let confirmedAt: Date
    }

    private let homeDirectory: URL
    private let stateDirectory: URL

    public init(
        homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser,
        stateDirectory: URL = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("WeChatBridge", isDirectory: true)
    ) {
        self.homeDirectory = homeDirectory
        self.stateDirectory = stateDirectory
    }

    /// `sourceDirectory` overrides the catalog package lookup — the library
    /// copy of a user-imported skill (which has no bundled package).
    public func plan(
        for skill: OfficialSkill,
        agent: AgentID,
        resourcesRoot: URL,
        sourceDirectory: URL? = nil,
        agentInstalled: Bool = true
    ) -> SkillInstallPlan {
        let source: URL
        if let sourceDirectory {
            source = sourceDirectory
        } else if let package = skill.package {
            source = packageURL(package, resourcesRoot: resourcesRoot)
        } else {
            return SkillInstallPlan(
                skillID: skill.id,
                agent: agent,
                agentInstalled: agentInstalled,
                method: .unavailable
            )
        }
        if let root = agent.directSkillRoot {
            return SkillInstallPlan(
                skillID: skill.id,
                agent: agent,
                agentInstalled: agentInstalled,
                method: .direct(
                    source: source,
                    target: homeDirectory
                        .appendingPathComponent(root, isDirectory: true)
                        .appendingPathComponent(skill.id, isDirectory: true)
                )
            )
        }
        return SkillInstallPlan(
            skillID: skill.id,
            agent: agent,
            agentInstalled: agentInstalled,
            method: .manual(guide: agent.manualInstallGuide ?? L10n.text("请在应用设置中导入技能包。"))
        )
    }

    public func status(
        for skill: OfficialSkill,
        agent: AgentID,
        resourcesRoot: URL,
        sourceDirectory: URL? = nil,
        agentInstalled: Bool = true
    ) -> SkillAgentStatus {
        let source: URL?
        if let sourceDirectory {
            source = sourceDirectory
        } else {
            source = skill.package.map { packageURL($0, resourcesRoot: resourcesRoot) }
        }
        guard let source, FileManager.default.fileExists(
            atPath: source.appendingPathComponent("SKILL.md").path
        ) else {
            return .packageUnavailable
        }
        if !agentInstalled, agent.directSkillRoot == nil {
            return .agentUnavailable
        }
        let plan = plan(
            for: skill,
            agent: agent,
            resourcesRoot: resourcesRoot,
            sourceDirectory: sourceDirectory,
            agentInstalled: agentInstalled
        )
        switch plan.method {
        case .unavailable:
            return .packageUnavailable
        case .manual:
            if let confirmed = confirmations()[key(skill.id, agent)],
               confirmed.version == skill.version {
                return .manualConfirmed(version: confirmed.version)
            }
            return .manualOnly
        case .direct(_, let target):
            guard FileManager.default.fileExists(atPath: target.path) else {
                return .notInstalled
            }
            do {
                let metadata = try readMetadata(at: target)
                guard metadata.id == skill.id else {
                    return .versionConflict(detail: L10n.text("目标目录属于另一个技能。"))
                }
                guard try packageDigest(at: target) == metadata.digest else {
                    return .versionConflict(detail: SkillInstallError.userModified.errorDescription ?? "")
                }
                guard let installed = SceneVersion(metadata.version),
                      let available = SceneVersion(skill.version)
                else {
                    return .versionConflict(detail: L10n.text("版本号格式无效。"))
                }
                if installed < available {
                    return .updateAvailable(installed: metadata.version, available: skill.version)
                }
                if installed > available {
                    return .versionConflict(
                        detail: L10n.format("已安装 %@，内置版本为 %@。", metadata.version, skill.version)
                    )
                }
                return .installed(version: metadata.version)
            } catch {
                return .versionConflict(
                    detail: (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                )
            }
        }
    }

    @discardableResult
    public func install(
        _ skill: OfficialSkill,
        to agent: AgentID,
        resourcesRoot: URL,
        sourceDirectory: URL? = nil,
        replacingExisting: Bool = false
    ) throws -> String {
        guard sourceDirectory != nil || skill.package != nil else {
            throw SkillInstallError.packageUnavailable
        }
        let plan = plan(
            for: skill,
            agent: agent,
            resourcesRoot: resourcesRoot,
            sourceDirectory: sourceDirectory
        )
        guard case .direct(let source, let target) = plan.method else {
            throw SkillInstallError.manualInstallOnly
        }
        let digest = try validatePackage(at: source)
        let fileManager = FileManager.default
        let parent = target.deletingLastPathComponent()
        try fileManager.createDirectory(at: parent, withIntermediateDirectories: true)

        if fileManager.fileExists(atPath: target.path) {
            let metadata = try? readMetadata(at: target)
            if let metadata, metadata.id != skill.id {
                throw SkillInstallError.installedByAnotherPackage
            }
            if metadata == nil, !replacingExisting {
                throw SkillInstallError.versionConflict(
                    L10n.text("目标目录已存在且不是由微信流安装。")
                )
            }
            if let metadata {
                if !replacingExisting {
                    guard try packageDigest(at: target) == metadata.digest else {
                        throw SkillInstallError.userModified
                    }
                }
                if let installed = SceneVersion(metadata.version),
                   let available = SceneVersion(skill.version),
                   installed > available,
                   !replacingExisting {
                    throw SkillInstallError.versionConflict(
                        L10n.format("已安装 %@，内置版本为 %@。", metadata.version, skill.version)
                    )
                }
            }
        }

        let staging = parent.appendingPathComponent(
            ".\(skill.id).staging-\(UUID().uuidString)",
            isDirectory: true
        )
        defer { try? fileManager.removeItem(at: staging) }
        try copyPackage(from: source, to: staging)
        try writeMetadata(
            Metadata(id: skill.id, version: skill.version, digest: digest),
            to: staging
        )

        if fileManager.fileExists(atPath: target.path) {
            let backupName = ".\(skill.id).backup-\(UUID().uuidString)"
            _ = try fileManager.replaceItemAt(
                target,
                withItemAt: staging,
                backupItemName: backupName,
                options: []
            )
            let backup = parent.appendingPathComponent(backupName)
            try? fileManager.removeItem(at: backup)
        } else {
            try fileManager.moveItem(at: staging, to: target)
        }
        return skill.version
    }

    public func uninstall(_ skill: OfficialSkill, from agent: AgentID) throws {
        guard let root = agent.directSkillRoot else {
            revokeManualConfirmation(skill, agent: agent)
            return
        }
        let target = homeDirectory
            .appendingPathComponent(root, isDirectory: true)
            .appendingPathComponent(skill.id, isDirectory: true)
        let metadata = try readMetadata(at: target)
        guard metadata.id == skill.id else {
            throw SkillInstallError.installedByAnotherPackage
        }
        try FileManager.default.removeItem(at: target)
    }

    public func confirmManual(_ skill: OfficialSkill, agent: AgentID) throws {
        guard agent.directSkillRoot == nil else { throw SkillInstallError.invalidPackage("not manual") }
        var all = confirmations()
        all[key(skill.id, agent)] = Confirmation(
            skillID: skill.id,
            agent: agent.rawValue,
            version: skill.version,
            confirmedAt: Date()
        )
        try writeConfirmations(all)
    }

    public func revokeManualConfirmation(_ skill: OfficialSkill, agent: AgentID) {
        var all = confirmations()
        all.removeValue(forKey: key(skill.id, agent))
        try? writeConfirmations(all)
    }

    public func makeManualArchive(
        _ skill: OfficialSkill,
        resourcesRoot: URL,
        sourceDirectory: URL? = nil,
        destination: URL
    ) throws {
        guard let source = sourceDirectory
            ?? skill.package.map({ packageURL($0, resourcesRoot: resourcesRoot) })
        else { throw SkillInstallError.packageUnavailable }
        _ = try validatePackage(at: source)
        try SkillZip.write(directory: source, rootName: skill.id, to: destination)
    }

    private func packageURL(_ package: String, resourcesRoot: URL) -> URL {
        resourcesRoot
            .appendingPathComponent("Skills", isDirectory: true)
            .appendingPathComponent(package, isDirectory: true)
    }

    private func confirmationURL() -> URL {
        stateDirectory.appendingPathComponent("SkillConfirmations.json")
    }

    private func confirmations() -> [String: Confirmation] {
        guard let data = try? Data(contentsOf: confirmationURL()) else { return [:] }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode([String: Confirmation].self, from: data)) ?? [:]
    }

    private func writeConfirmations(_ values: [String: Confirmation]) throws {
        try FileManager.default.createDirectory(at: stateDirectory, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(values).write(to: confirmationURL(), options: .atomic)
    }

    private func key(_ skillID: String, _ agent: AgentID) -> String {
        "\(skillID)|\(agent.rawValue)"
    }

    private func readMetadata(at directory: URL) throws -> Metadata {
        let data = try Data(contentsOf: directory.appendingPathComponent(Self.metadataFileName))
        return try JSONDecoder().decode(Metadata.self, from: data)
    }

    private func writeMetadata(_ metadata: Metadata, to directory: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(metadata).write(
            to: directory.appendingPathComponent(Self.metadataFileName),
            options: .atomic
        )
    }

    private func validatePackage(at root: URL) throws -> String {
        try SkillPackage.validatePackage(at: root)
    }

    private func packageDigest(at root: URL) throws -> String {
        try SkillPackage.packageDigest(at: root)
    }

    private func copyPackage(from source: URL, to destination: URL) throws {
        try SkillPackage.copyPackage(from: source, to: destination)
    }
}

