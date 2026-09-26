import Foundation
import XCTest
@testable import WeChatBridgeCore

final class SkillInstallerTests: XCTestCase {
    private var root: URL!
    private var resources: URL!
    private var home: URL!
    private var state: URL!
    private var installer: SkillInstaller!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("SkillInstallerTests-\(UUID().uuidString)", isDirectory: true)
        resources = root.appendingPathComponent("Resources", isDirectory: true)
        home = root.appendingPathComponent("Home", isDirectory: true)
        state = root.appendingPathComponent("State", isDirectory: true)
        try FileManager.default.createDirectory(at: resources, withIntermediateDirectories: true)
        installer = SkillInstaller(homeDirectory: home, stateDirectory: state)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    func testInstallPlanCoversDirectAndManualAgents() throws {
        let skill = try makeSkill()
        let direct: [(AgentID, String)] = [
            (.chatGPTCodex, ".codex/skills"),
            (.qwenWork, ".qwenworkcn/skills"),
            (.workBuddy, ".workbuddy/skills"),
        ]
        for (agent, path) in direct {
            let plan = installer.plan(for: skill, agent: agent, resourcesRoot: resources)
            guard case .direct(_, let target) = plan.method else {
                return XCTFail("expected direct install for \(agent)")
            }
            XCTAssertEqual(
                target.path,
                home.appendingPathComponent(path).appendingPathComponent(skill.id).path
            )
        }

        for agent in [AgentID.doubao, .claude] {
            let plan = installer.plan(for: skill, agent: agent, resourcesRoot: resources)
            guard case .manual(let guide) = plan.method else {
                return XCTFail("expected manual install for \(agent)")
            }
            XCTAssertFalse(guide.isEmpty)
        }
    }

    func testInstallDetectsUpdatesAndUserModification() throws {
        var skill = try makeSkill(version: "1.0.0")
        XCTAssertEqual(
            installer.status(for: skill, agent: .chatGPTCodex, resourcesRoot: resources),
            .notInstalled
        )
        try installer.install(skill, to: .chatGPTCodex, resourcesRoot: resources)
        XCTAssertEqual(
            installer.status(for: skill, agent: .chatGPTCodex, resourcesRoot: resources),
            .installed(version: "1.0.0")
        )

        skill = try replaceCatalogVersion(skill, version: "1.1.0")
        XCTAssertEqual(
            installer.status(for: skill, agent: .chatGPTCodex, resourcesRoot: resources),
            .updateAvailable(installed: "1.0.0", available: "1.1.0")
        )
        try installer.install(skill, to: .chatGPTCodex, resourcesRoot: resources)
        XCTAssertEqual(
            installer.status(for: skill, agent: .chatGPTCodex, resourcesRoot: resources),
            .installed(version: "1.1.0")
        )

        let installed = home
            .appendingPathComponent(".codex/skills", isDirectory: true)
            .appendingPathComponent(skill.id, isDirectory: true)
        try Data("changed".utf8).write(to: installed.appendingPathComponent("SKILL.md"))
        guard case .versionConflict = installer.status(
            for: skill,
            agent: .chatGPTCodex,
            resourcesRoot: resources
        ) else {
            return XCTFail("expected a user-modification conflict")
        }
        XCTAssertThrowsError(
            try installer.install(skill, to: .chatGPTCodex, resourcesRoot: resources)
        ) { error in
            XCTAssertEqual(error as? SkillInstallError, .userModified)
        }
    }

    func testUnmanagedConflictRequiresReplacement() throws {
        let skill = try makeSkill()
        let target = home
            .appendingPathComponent(".codex/skills", isDirectory: true)
            .appendingPathComponent(skill.id, isDirectory: true)
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        try Data("mine".utf8).write(to: target.appendingPathComponent("SKILL.md"))

        XCTAssertThrowsError(
            try installer.install(skill, to: .chatGPTCodex, resourcesRoot: resources)
        ) { error in
            guard case .versionConflict = error as? SkillInstallError else {
                return XCTFail("expected conflict, got \(error)")
            }
        }
        XCTAssertEqual(
            try String(contentsOf: target.appendingPathComponent("SKILL.md"), encoding: .utf8),
            "mine"
        )

        try installer.install(
            skill,
            to: .chatGPTCodex,
            resourcesRoot: resources,
            replacingExisting: true
        )
        XCTAssertEqual(
            installer.status(for: skill, agent: .chatGPTCodex, resourcesRoot: resources),
            .installed(version: "1.0.0")
        )
    }

    func testManualConfirmationTracksTheExactVersion() throws {
        var skill = try makeSkill(version: "1.0.0")
        XCTAssertEqual(
            installer.status(for: skill, agent: .doubao, resourcesRoot: resources),
            .manualOnly
        )
        try installer.confirmManual(skill, agent: .doubao)
        XCTAssertEqual(
            installer.status(for: skill, agent: .doubao, resourcesRoot: resources),
            .manualConfirmed(version: "1.0.0")
        )

        skill = try replaceCatalogVersion(skill, version: "1.1.0")
        XCTAssertEqual(
            installer.status(for: skill, agent: .doubao, resourcesRoot: resources),
            .manualOnly
        )
        installer.revokeManualConfirmation(skill, agent: .doubao)
        XCTAssertEqual(
            installer.status(for: skill, agent: .doubao, resourcesRoot: resources),
            .manualOnly
        )
    }

    func testManualArchiveIsAValidZipWithSkillRoot() throws {
        let skill = try makeSkill()
        let destination = root.appendingPathComponent("manual.zip")
        try installer.makeManualArchive(
            skill,
            resourcesRoot: resources,
            destination: destination
        )

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        process.arguments = ["-l", destination.path]
        let output = Pipe()
        process.standardOutput = output
        process.standardError = output
        try process.run()
        process.waitUntilExit()
        let text = String(data: output.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        XCTAssertEqual(process.terminationStatus, 0, text)
        XCTAssertTrue(text.contains("\(skill.id)/SKILL.md"), text)
    }

    func testSymlinkPackageIsRejected() throws {
        let skill = try makeSkill()
        let source = try packageURL(skill)
        try FileManager.default.createSymbolicLink(
            at: source.appendingPathComponent("outside"),
            withDestinationURL: root.appendingPathComponent("outside")
        )
        XCTAssertThrowsError(
            try installer.install(skill, to: .chatGPTCodex, resourcesRoot: resources)
        ) { error in
            guard case .unsafePackage = error as? SkillInstallError else {
                return XCTFail("expected unsafe package, got \(error)")
            }
        }
    }

    func testCatalogLoaderRejectsDuplicateIDs() throws {
        let skills = resources.appendingPathComponent("Skills", isDirectory: true)
        try FileManager.default.createDirectory(at: skills, withIntermediateDirectories: true)
        let json = """
        {
          "schema_version": 1,
          "skills": [
            {"id":"same","name":"A","summary":"","version":"1.0.0","package":null,"supported_agents":["doubao"]},
            {"id":"same","name":"B","summary":"","version":"1.0.0","package":null,"supported_agents":["claude"]}
          ]
        }
        """
        try Data(json.utf8).write(to: skills.appendingPathComponent("catalog.json"))
        XCTAssertThrowsError(try OfficialSkillCatalog.load(from: resources)) { error in
            XCTAssertEqual(error as? SkillInstallError, .invalidCatalog)
        }
    }

    private func makeSkill(version: String = "1.0.0") throws -> OfficialSkill {
        let skill = OfficialSkill(
            id: "dev.wechatbridge.test",
            name: "测试技能",
            summary: "测试",
            version: version,
            package: "test-skill",
            supportedAgents: AgentID.allCases
        )
        let source = try packageURL(skill)
        try FileManager.default.createDirectory(
            at: source.appendingPathComponent("scripts", isDirectory: true),
            withIntermediateDirectories: true
        )
        try Data("# Test Skill\n".utf8).write(to: source.appendingPathComponent("SKILL.md"))
        try Data("#!/bin/sh\n".utf8).write(to: source.appendingPathComponent("scripts/run.sh"))
        return skill
    }

    private func packageURL(_ skill: OfficialSkill) throws -> URL {
        let package = try XCTUnwrap(skill.package)
        return resources
            .appendingPathComponent("Skills", isDirectory: true)
            .appendingPathComponent(package, isDirectory: true)
    }

    private func replaceCatalogVersion(
        _ skill: OfficialSkill,
        version: String
    ) throws -> OfficialSkill {
        let source = try packageURL(skill)
        try Data("# Test Skill \(version)\n".utf8).write(to: source.appendingPathComponent("SKILL.md"))
        return OfficialSkill(
            id: skill.id,
            name: skill.name,
            summary: skill.summary,
            version: version,
            package: skill.package,
            supportedAgents: skill.supportedAgents
        )
    }
}
