import AppKit
import Combine
import Foundation
import SwiftUI
import WeChatBridgeCore

/// The shipped skill catalogue plus the one installer that writes or confirms
/// skills for this user. Views read from here; no row reaches into the file
/// system itself.
@MainActor
final class SkillLibrary: ObservableObject {
    let resourcesRoot: URL?
    let installer: SkillInstaller

    @Published private(set) var catalog = OfficialSkillCatalog(skills: [])
    @Published private(set) var loadError: String?
    @Published private(set) var revision = 0

    var skills: [OfficialSkill] { catalog.skills }

    init(
        resourcesRoot: URL? = SkillLibrary.findResourcesRoot(),
        installer: SkillInstaller = SkillInstaller()
    ) {
        self.resourcesRoot = resourcesRoot
        self.installer = installer
        reload()
    }

    func reload() {
        guard let resourcesRoot else {
            loadError = L10n.text("没有找到内置技能清单。")
            return
        }
        do {
            catalog = try OfficialSkillCatalog.load(from: resourcesRoot)
            loadError = nil
            revision += 1
        } catch {
            loadError = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }

    func skill(id: String) -> OfficialSkill? {
        catalog.skills.first { $0.id == id }
    }

    func skillNames(for scene: WeChatScene) -> [String: String] {
        Dictionary(uniqueKeysWithValues: scene.requiredSkillIDs.compactMap { id in
            skill(id: id).map { (id, $0.name) }
        })
    }

    func isAgentInstalled(_ agent: AgentID) -> Bool {
        InstalledApp.lookup(agent.bundleIdentifier).isInstalled
    }

    func status(for skill: OfficialSkill, agent: AgentID) -> SkillAgentStatus {
        guard let resourcesRoot else { return .packageUnavailable }
        return installer.status(
            for: skill,
            agent: agent,
            resourcesRoot: resourcesRoot,
            agentInstalled: isAgentInstalled(agent)
        )
    }

    func plan(for skill: OfficialSkill, agent: AgentID) -> SkillInstallPlan? {
        guard let resourcesRoot else { return nil }
        return installer.plan(
            for: skill,
            agent: agent,
            resourcesRoot: resourcesRoot,
            agentInstalled: isAgentInstalled(agent)
        )
    }

    func install(
        _ skill: OfficialSkill,
        to agent: AgentID,
        replacingExisting: Bool = false
    ) throws {
        guard let resourcesRoot else { throw SkillInstallError.packageUnavailable }
        try installer.install(
            skill,
            to: agent,
            resourcesRoot: resourcesRoot,
            replacingExisting: replacingExisting
        )
        revision += 1
    }

    func uninstall(_ skill: OfficialSkill, from agent: AgentID) throws {
        try installer.uninstall(skill, from: agent)
        revision += 1
    }

    func confirmManual(_ skill: OfficialSkill, agent: AgentID) throws {
        try installer.confirmManual(skill, agent: agent)
        revision += 1
    }

    func revokeManual(_ skill: OfficialSkill, agent: AgentID) {
        installer.revokeManualConfirmation(skill, agent: agent)
        revision += 1
    }

    nonisolated private static func findResourcesRoot() -> URL? {
        let fileManager = FileManager.default
        var starts = [Bundle.main.resourceURL, URL(fileURLWithPath: fileManager.currentDirectoryPath)]
            .compactMap { $0 }
        if starts.isEmpty {
            starts = [Bundle.main.bundleURL]
        }
        for start in starts {
            var candidate = start.standardizedFileURL
            for _ in 0..<8 {
                let catalog = candidate
                    .appendingPathComponent("Skills", isDirectory: true)
                    .appendingPathComponent("catalog.json")
                if fileManager.fileExists(atPath: catalog.path) {
                    return candidate
                }
                let parent = candidate.deletingLastPathComponent()
                if parent == candidate { break }
                candidate = parent
            }
        }
        return nil
    }
}

struct AgentLogo: View {
    let agent: AgentID
    let resourcesRoot: URL?
    var size: CGFloat = 18

    var body: some View {
        Group {
            if let image = AgentLogoCache.image(for: agent, resourcesRoot: resourcesRoot) {
                Image(nsImage: image)
                    .resizable()
                    .interpolation(.high)
                    .aspectRatio(contentMode: .fit)
            } else {
                Image(systemName: "sparkles")
                    .font(.system(size: size * 0.72, weight: .medium))
                    .foregroundStyle(Theme.accent)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Theme.accentSoft)
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: size * 0.22, style: .continuous))
        .accessibilityLabel(Text(agent.displayName))
    }
}

@MainActor
private enum AgentLogoCache {
    private static var cache: [String: NSImage?] = [:]

    static func image(for agent: AgentID, resourcesRoot: URL?) -> NSImage? {
        guard let resourcesRoot else { return nil }
        let key = "\(resourcesRoot.path)|\(agent.logoSuffix)"
        if let hit = cache[key] { return hit }
        let directory = resourcesRoot.appendingPathComponent("AppLogos", isDirectory: true)
        let files = (try? FileManager.default.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: nil
        )) ?? []
        let match = files.first {
            $0.lastPathComponent.lowercased().contains(agent.logoSuffix.lowercased())
        }
        let image = match.flatMap(NSImage.init(contentsOf:))
        cache[key] = image
        return image
    }
}
