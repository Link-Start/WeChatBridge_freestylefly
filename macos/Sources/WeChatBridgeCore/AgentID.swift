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

    public func plan(
        for skill: OfficialSkill,
        agent: AgentID,
        resourcesRoot: URL,
        agentInstalled: Bool = true
    ) -> SkillInstallPlan {
        guard let package = skill.package else {
            return SkillInstallPlan(
                skillID: skill.id,
                agent: agent,
                agentInstalled: agentInstalled,
                method: .unavailable
            )
        }
        let source = packageURL(package, resourcesRoot: resourcesRoot)
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
        agentInstalled: Bool = true
    ) -> SkillAgentStatus {
        guard let package = skill.package else { return .packageUnavailable }
        let source = packageURL(package, resourcesRoot: resourcesRoot)
        guard FileManager.default.fileExists(
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
        replacingExisting: Bool = false
    ) throws -> String {
        guard skill.package != nil else { throw SkillInstallError.packageUnavailable }
        let plan = plan(for: skill, agent: agent, resourcesRoot: resourcesRoot)
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
        destination: URL
    ) throws {
        guard let package = skill.package else { throw SkillInstallError.packageUnavailable }
        let source = packageURL(package, resourcesRoot: resourcesRoot)
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
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: root.path, isDirectory: &isDirectory),
              isDirectory.boolValue
        else {
            throw SkillInstallError.invalidPackage(L10n.text("技能包目录不存在。"))
        }
        let skill = root.appendingPathComponent("SKILL.md")
        guard FileManager.default.fileExists(atPath: skill.path) else {
            throw SkillInstallError.invalidPackage(L10n.text("技能包缺少 SKILL.md。"))
        }
        return try packageDigest(at: root)
    }

    private func packageDigest(at root: URL) throws -> String {
        var hasher = SHA256()
        for file in try SkillPackageFiles.enumerate(root: root) {
            if file.relativePath == Self.metadataFileName { continue }
            hasher.update(data: Data(file.relativePath.utf8))
            hasher.update(data: [0])
            hasher.update(data: try Data(contentsOf: file.url))
            hasher.update(data: [0])
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private func copyPackage(from source: URL, to destination: URL) throws {
        let fileManager = FileManager.default
        try fileManager.createDirectory(at: destination, withIntermediateDirectories: true)
        for file in try SkillPackageFiles.enumerate(root: source) {
            let target = destination.appendingPathComponent(file.relativePath)
            try fileManager.createDirectory(
                at: target.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            try fileManager.copyItem(at: file.url, to: target)
        }
    }
}

private enum SkillPackageFiles {
    struct File {
        let url: URL
        let relativePath: String
    }

    static func enumerate(root: URL) throws -> [File] {
        let fileManager = FileManager.default
        let keys: [URLResourceKey] = [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey]
        let rootValues = try root.resourceValues(forKeys: Set(keys))
        if rootValues.isSymbolicLink == true {
            throw SkillInstallError.unsafePackage(L10n.text("技能包不能包含符号链接。"))
        }
        guard rootValues.isDirectory == true else {
            throw SkillInstallError.invalidPackage(L10n.text("技能包目录无效。"))
        }
        guard let enumerator = fileManager.enumerator(
            at: root,
            includingPropertiesForKeys: keys,
            options: [],
            errorHandler: { _, error in
                assertionFailure(error.localizedDescription)
                return false
            }
        ) else {
            throw SkillInstallError.invalidPackage(L10n.text("无法读取技能包。"))
        }

        let base = root.standardizedFileURL.path
        var files: [File] = []
        for case let url as URL in enumerator {
            let values = try url.resourceValues(forKeys: Set(keys))
            if values.isSymbolicLink == true {
                throw SkillInstallError.unsafePackage(L10n.text("技能包不能包含符号链接。"))
            }
            if values.isDirectory == true { continue }
            guard values.isRegularFile == true else {
                throw SkillInstallError.unsafePackage(L10n.text("技能包只能包含普通文件和目录。"))
            }
            let path = url.standardizedFileURL.path
            guard path.hasPrefix(base + "/") else {
                throw SkillInstallError.unsafePackage(L10n.text("技能包包含越界路径。"))
            }
            files.append(File(url: url, relativePath: String(path.dropFirst(base.count + 1))))
        }
        return files.sorted { $0.relativePath < $1.relativePath }
    }
}

private enum SkillZip {
    static func write(directory: URL, rootName: String, to destination: URL) throws {
        let files = try SkillPackageFiles.enumerate(root: directory)
        try FileManager.default.createDirectory(
            at: destination.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        var archive = Data()
        var central = Data()

        for file in files {
            let name = "\(rootName)/\(file.relativePath)"
            let nameData = Data(name.utf8)
            let contents = try Data(contentsOf: file.url)
            let crc = CRC32.checksum(contents)
            let offset = UInt32(archive.count)
            archive.appendLE(UInt32(0x04034b50))
            archive.appendLE(UInt16(20))
            archive.appendLE(UInt16(0x0800))
            archive.appendLE(UInt16(0))
            archive.appendLE(UInt16(0))
            archive.appendLE(UInt16(0))
            archive.appendLE(crc)
            archive.appendLE(UInt32(contents.count))
            archive.appendLE(UInt32(contents.count))
            archive.appendLE(UInt16(nameData.count))
            archive.appendLE(UInt16(0))
            archive.append(nameData)
            archive.append(contents)

            central.appendLE(UInt32(0x02014b50))
            central.appendLE(UInt16(20))
            central.appendLE(UInt16(20))
            central.appendLE(UInt16(0x0800))
            central.appendLE(UInt16(0))
            central.appendLE(UInt16(0))
            central.appendLE(UInt16(0))
            central.appendLE(crc)
            central.appendLE(UInt32(contents.count))
            central.appendLE(UInt32(contents.count))
            central.appendLE(UInt16(nameData.count))
            central.appendLE(UInt16(0))
            central.appendLE(UInt16(0))
            central.appendLE(UInt16(0))
            central.appendLE(UInt16(0))
            central.appendLE(UInt32(0))
            central.appendLE(offset)
            central.append(nameData)
        }

        let centralOffset = UInt32(archive.count)
        archive.append(central)
        archive.appendLE(UInt32(0x06054b50))
        archive.appendLE(UInt16(0))
        archive.appendLE(UInt16(0))
        archive.appendLE(UInt16(files.count))
        archive.appendLE(UInt16(files.count))
        archive.appendLE(UInt32(central.count))
        archive.appendLE(centralOffset)
        archive.appendLE(UInt16(0))
        try archive.write(to: destination, options: .atomic)
    }
}

private enum CRC32 {
    static let table: [UInt32] = (0..<256).map { value in
        var crc = UInt32(value)
        for _ in 0..<8 {
            crc = (crc & 1) == 1 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1
        }
        return crc
    }

    static func checksum(_ data: Data) -> UInt32 {
        var crc: UInt32 = 0xFFFFFFFF
        for byte in data {
            crc = table[Int((crc ^ UInt32(byte)) & 0xFF)] ^ (crc >> 8)
        }
        return crc ^ 0xFFFFFFFF
    }
}

private extension Data {
    mutating func appendLE(_ value: UInt16) {
        append(UInt8(value & 0xFF))
        append(UInt8((value >> 8) & 0xFF))
    }

    mutating func appendLE(_ value: UInt32) {
        append(UInt8(value & 0xFF))
        append(UInt8((value >> 8) & 0xFF))
        append(UInt8((value >> 16) & 0xFF))
        append(UInt8((value >> 24) & 0xFF))
    }
}
