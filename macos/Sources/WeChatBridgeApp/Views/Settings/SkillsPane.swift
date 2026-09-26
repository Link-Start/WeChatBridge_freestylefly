import AppKit
import SwiftUI
import UniformTypeIdentifiers
import WeChatBridgeCore

struct SkillsPane: View {
    @ObservedObject var skills: SkillLibrary
    @ObservedObject var preferences: Preferences

    @State private var query = ""
    @State private var statusFilter = SkillStatusFilter.all
    @State private var agentFilter = SkillAgentFilter.all
    @State private var expandedSkillIDs: Set<String> = []
    @State private var notice: SkillNotice?
    @State private var replaceRequest: ReplaceRequest?

    var body: some View {
        let records = makeRecords()
        let scopedRecords = scopedRecords(records)
        let visibleRecords = scopedRecords.filter { statusFilter.includes($0.kind) }

        VStack(alignment: .leading, spacing: Space.l) {
            Text(L10n.text("发现、安装和管理各 Agent 可用的技能。"))
                .font(Typo.paneBody)
                .foregroundStyle(Theme.inkSecondary)

            metrics(records)
            controls(allRecords: records, scopedRecords: scopedRecords)

            if visibleRecords.isEmpty {
                emptyState
            } else {
                VStack(spacing: Space.s) {
                    ForEach(visibleRecords) { record in
                        SkillCard(
                            record: record,
                            resourcesRoot: skills.resourcesRoot,
                            sceneCount: preferences.scenes.scenes.filter {
                                $0.requiredSkillIDs.contains(record.skill.id)
                            }.count,
                            expanded: expandedSkillIDs.contains(record.id),
                            toggleDetails: { toggleDetails(record.id) },
                            primaryAction: { performPrimaryAction(for: record) },
                            install: { agent in install(record.skill, to: agent) },
                            replace: { agent in
                                replaceRequest = ReplaceRequest(skill: record.skill, agent: agent)
                            },
                            uninstall: { agent in
                                do {
                                    try skills.uninstall(record.skill, from: agent)
                                    notice = SkillNotice(L10n.text("技能已移除。"), tone: .good)
                                } catch {
                                    notice = SkillNotice(error.localizedDescription, tone: .bad)
                                }
                            },
                            export: { export(record.skill) },
                            confirm: { agent in
                                do {
                                    try skills.confirmManual(record.skill, agent: agent)
                                    notice = SkillNotice(L10n.text("已记录手动安装。"), tone: .good)
                                } catch {
                                    notice = SkillNotice(error.localizedDescription, tone: .bad)
                                }
                            },
                            revoke: { agent in
                                skills.revokeManual(record.skill, agent: agent)
                                notice = SkillNotice(L10n.text("已撤销本地确认。"), tone: .good)
                            }
                        )
                    }
                }
            }

            if let notice {
                Notice(notice.message, tone: notice.tone)
            }
        }
        .frame(maxWidth: 980, alignment: .leading)
        .alert(
            L10n.text("替换已有技能目录？"),
            isPresented: Binding(
                get: { replaceRequest != nil },
                set: { if !$0 { replaceRequest = nil } }
            )
        ) {
            Button(L10n.text("取消"), role: .cancel) { replaceRequest = nil }
            Button(L10n.text("替换"), role: .destructive) {
                guard let request = replaceRequest else { return }
                replaceRequest = nil
                install(request.skill, to: request.agent, replacingExisting: true)
            }
        } message: {
            Text(L10n.text("微信流会先备份旧目录再替换；外部安装目录也会被替换。"))
        }
    }

    private func metrics(_ records: [SkillRecord]) -> some View {
        HStack(spacing: Space.m) {
            SkillMetricCard(
                systemImage: "shippingbox",
                value: L10n.format("%d 个技能", records.count),
                tone: Theme.brandPrimary
            )
            SkillMetricCard(
                systemImage: "exclamationmark.circle",
                value: L10n.format("%d 项待安装", records.filter(\.hasAutomaticMissing).count),
                tone: Theme.warning
            )
            SkillMetricCard(
                systemImage: "person.2",
                value: L10n.format("%d 个 Agent", supportedAgentCount),
                tone: Theme.systemBlue
            )
        }
    }

    private func controls(
        allRecords: [SkillRecord],
        scopedRecords: [SkillRecord]
    ) -> some View {
        VStack(alignment: .leading, spacing: Space.s) {
            SkillSearchField(text: $query)

            HStack(spacing: Space.s) {
                SkillStatusFilterBar(
                    selection: $statusFilter,
                    counts: Dictionary(uniqueKeysWithValues: SkillStatusFilter.allCases.map { filter in
                        (filter, scopedRecords.filter { filter.includes($0.kind) }.count)
                    })
                )

                Spacer(minLength: Space.s)

                SettingsSelect(
                    title: L10n.text("全部 Agent"),
                    selection: $agentFilter,
                    choices: agentChoices,
                    identifier: "skills.agent"
                )
                .frame(width: 170)
            }

            HStack(spacing: Space.s) {
                Button(L10n.text("安装全部缺失项")) { installAllMissing(allRecords) }
                    .buttonStyle(SettingsActionButtonStyle(primary: true, width: nil))
                    .disabled(!allRecords.contains(where: \.hasAutomaticMissing))

                Text(L10n.text("仅处理支持直接安装的 Agent；其余仍需导出 ZIP。"))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkTertiary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var emptyState: some View {
        VStack(spacing: Space.s) {
            Image(systemName: "puzzlepiece.extension")
                .font(.system(size: 26, weight: .regular))
                .foregroundStyle(Theme.inkTertiary)
            Text(L10n.text("没有找到匹配的技能。"))
                .font(Typo.paneBodyStrong)
                .foregroundStyle(Theme.ink)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 54)
    }

    private var supportedAgentCount: Int {
        Set(skills.skills.flatMap(\.supportedAgents)).count
    }

    private var agentChoices: [SettingsChoice<SkillAgentFilter>] {
        let agents = AgentID.allCases.filter { agent in
            skills.skills.contains { $0.supportedAgents.contains(agent) }
        }
        return [SettingsChoice(id: .all, title: L10n.text("全部 Agent"))]
            + agents.map { SettingsChoice(id: .agent($0), title: $0.displayName) }
    }

    private func makeRecords() -> [SkillRecord] {
        skills.skills.map { skill in
            SkillRecord(
                skill: skill,
                states: skill.supportedAgents.map { agent in
                    AgentSkillState(agent: agent, status: skills.status(for: skill, agent: agent))
                }
            )
        }
    }

    private func scopedRecords(_ records: [SkillRecord]) -> [SkillRecord] {
        let needle = query.trimmingCharacters(in: .whitespacesAndNewlines)
        return records.filter { record in
            let matchesQuery = needle.isEmpty
                || record.skill.name.localizedCaseInsensitiveContains(needle)
                || record.skill.summary.localizedCaseInsensitiveContains(needle)
                || record.skill.id.localizedCaseInsensitiveContains(needle)
            guard matchesQuery else { return false }

            switch agentFilter {
            case .all:
                return true
            case .agent(let agent):
                return record.states.contains { $0.agent == agent }
            }
        }
    }

    private func toggleDetails(_ id: String) {
        if expandedSkillIDs.contains(id) {
            expandedSkillIDs.remove(id)
        } else {
            expandedSkillIDs.insert(id)
        }
    }

    private func performPrimaryAction(for record: SkillRecord) {
        switch record.primaryAction {
        case .install:
            installMissing(record)
        case .exportArchive:
            export(record.skill)
        case .details:
            toggleDetails(record.id)
        }
    }

    private func installMissing(_ record: SkillRecord) {
        let missing = record.automaticMissing
        guard !missing.isEmpty else {
            notice = SkillNotice(L10n.text("没有可自动安装的缺失项。"), tone: .warn)
            return
        }

        var installed = 0
        var failures: [String] = []
        for state in missing {
            do {
                try skills.install(record.skill, to: state.agent)
                installed += 1
            } catch {
                failures.append("\(record.skill.name) / \(state.agent.displayName)")
            }
        }
        report(installed: installed, failures: failures)
    }

    private func installAllMissing(_ records: [SkillRecord]) {
        var installed = 0
        var failures: [String] = []
        for record in records {
            for state in record.automaticMissing {
                do {
                    try skills.install(record.skill, to: state.agent)
                    installed += 1
                } catch {
                    failures.append("\(record.skill.name) / \(state.agent.displayName)")
                }
            }
        }
        report(installed: installed, failures: failures)
    }

    private func report(installed: Int, failures: [String]) {
        if installed == 0, failures.isEmpty {
            notice = SkillNotice(L10n.text("没有可自动安装的缺失项。"), tone: .warn)
        } else if failures.isEmpty {
            notice = SkillNotice(L10n.format("已安装 %d 项。", installed), tone: .good)
        } else {
            notice = SkillNotice(
                L10n.format("已安装 %d 项，另有 %d 项需要处理。", installed, failures.count),
                tone: .warn
            )
        }
    }

    private func install(
        _ skill: OfficialSkill,
        to agent: AgentID,
        replacingExisting: Bool = false
    ) {
        do {
            try skills.install(skill, to: agent, replacingExisting: replacingExisting)
            notice = SkillNotice(L10n.text("技能已安装。"), tone: .good)
        } catch {
            notice = SkillNotice(error.localizedDescription, tone: .bad)
        }
    }

    private func export(_ skill: OfficialSkill) {
        guard let resourcesRoot = skills.resourcesRoot else {
            notice = SkillNotice(L10n.text("没有找到内置技能资源。"), tone: .bad)
            return
        }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.zip]
        panel.canCreateDirectories = true
        panel.nameFieldStringValue = "\(skill.name)-\(skill.version).zip"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try skills.installer.makeManualArchive(
                skill,
                resourcesRoot: resourcesRoot,
                destination: url
            )
            notice = SkillNotice(L10n.text("技能 ZIP 已导出。"), tone: .good)
        } catch {
            notice = SkillNotice(error.localizedDescription, tone: .bad)
        }
    }
}

private struct SkillMetricCard: View {
    let systemImage: String
    let value: String
    let tone: Color

    var body: some View {
        HStack(spacing: Space.m) {
            Image(systemName: systemImage)
                .font(.system(size: 18, weight: .medium))
                .foregroundStyle(tone)
                .frame(width: 24)
                .accessibilityHidden(true)
            Text(value)
                .font(Typo.paneBodyStrong)
                .foregroundStyle(Theme.ink)
                .lineLimit(1)
                .minimumScaleFactor(0.9)
        }
        .padding(.horizontal, Space.m)
        .frame(maxWidth: .infinity, minHeight: 58, alignment: .leading)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
        .accessibilityElement(children: .combine)
    }
}

private struct SkillSearchField: View {
    @Binding var text: String
    @FocusState private var focused: Bool
    @State private var hovering = false

    var body: some View {
        HStack(spacing: Space.s) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 12.5, weight: .medium))
                .foregroundStyle(Theme.inkTertiary)
                .accessibilityHidden(true)
            TextField(L10n.text("搜索技能"), text: $text)
                .textFieldStyle(.plain)
                .font(SettingsControlMetrics.font)
                .foregroundStyle(Theme.ink)
                .focused($focused)
                .focusEffectDisabled()
        }
        .padding(.horizontal, SettingsControlMetrics.inset)
        .frame(height: SettingsControlMetrics.height)
        .background(
            hovering && !focused ? Theme.hover : Theme.sunken,
            in: RoundedRectangle(cornerRadius: SettingsControlMetrics.radius)
        )
        .overlay(
            RoundedRectangle(cornerRadius: SettingsControlMetrics.radius)
                .strokeBorder(
                    focused ? Theme.ink : Theme.inputStroke,
                    lineWidth: focused ? Stroke.focus : Stroke.hairline
                )
        )
        .onHover { hovering = $0 }
        .accessibilityLabel(Text(L10n.text("搜索技能")))
    }
}

private struct SkillStatusFilterBar: View {
    @Binding var selection: SkillStatusFilter
    let counts: [SkillStatusFilter: Int]

    var body: some View {
        HStack(spacing: Space.xs) {
            ForEach(SkillStatusFilter.allCases) { filter in
                Button {
                    selection = filter
                } label: {
                    Text("\(filter.title) \(counts[filter, default: 0])")
                        .font(SettingsControlMetrics.font)
                        .foregroundStyle(selection == filter ? Theme.ink : Theme.inkSecondary)
                        .padding(.horizontal, SettingsControlMetrics.inset)
                        .frame(height: SettingsControlMetrics.height)
                        .background(
                            selection == filter ? Theme.brandTint : Theme.sunken,
                            in: RoundedRectangle(cornerRadius: SettingsControlMetrics.radius)
                        )
                        .overlay(
                            RoundedRectangle(cornerRadius: SettingsControlMetrics.radius)
                                .strokeBorder(
                                    selection == filter ? Theme.brandPrimary : Theme.stroke,
                                    lineWidth: Stroke.hairline
                                )
                        )
                }
                .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
                .accessibilityAddTraits(selection == filter ? .isSelected : [])
            }
        }
    }
}

private struct SkillCard: View {
    let record: SkillRecord
    let resourcesRoot: URL?
    let sceneCount: Int
    let expanded: Bool
    let toggleDetails: () -> Void
    let primaryAction: () -> Void
    let install: (AgentID) -> Void
    let replace: (AgentID) -> Void
    let uninstall: (AgentID) -> Void
    let export: () -> Void
    let confirm: (AgentID) -> Void
    let revoke: (AgentID) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: Space.m) {
            HStack(alignment: .top, spacing: Space.m) {
                SkillMark(skill: record.skill)

                VStack(alignment: .leading, spacing: 4) {
                    HStack(alignment: .firstTextBaseline, spacing: Space.s) {
                        Text(record.skill.name)
                            .font(Typo.paneBodyStrong)
                            .foregroundStyle(Theme.ink)
                        Text("v\(record.skill.version)")
                            .font(Typo.paneCaption.monospaced())
                            .foregroundStyle(Theme.inkTertiary)
                    }

                    Text(record.skill.summary)
                        .font(Typo.paneBody)
                        .foregroundStyle(Theme.inkSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                Spacer(minLength: Space.s)

                StatusPill(text: record.statusTitle, tone: record.statusTone)
            }

            HStack(spacing: Space.s) {
                SkillBadge(title: L10n.text("官方技能"), systemImage: "checkmark.seal")
                SkillBadge(
                    title: L10n.format("用于 %d 个场景", sceneCount),
                    systemImage: "square.stack.3d.up"
                )

                Spacer(minLength: Space.s)

                if record.totalAgentCount > 0 {
                    InstalledAgentStack(
                        agents: record.installedAgents,
                        resourcesRoot: resourcesRoot
                    )
                    Text(L10n.format("已安装 %d / %d", record.installedCount, record.totalAgentCount))
                        .font(Typo.paneCaption)
                        .foregroundStyle(Theme.inkTertiary)
                        .fixedSize()
                }

                if let title = record.primaryActionTitle(expanded: expanded) {
                    Button(title, action: primaryAction)
                        .buttonStyle(SettingsActionButtonStyle(
                            primary: record.primaryAction == .install,
                            width: nil
                        ))
                }
            }

            if expanded {
                Divider()

                if record.kind == .packageUnavailable {
                    Notice(
                        L10n.text("技能包尚未随当前构建提供；场景仍可转发，提示词会要求 Agent 在不可用时说明未完成部分。"),
                        tone: .warn
                    )
                }

                VStack(spacing: 0) {
                    ForEach(record.states) { state in
                        SkillAgentRow(
                            state: state,
                            resourcesRoot: resourcesRoot,
                            install: { install(state.agent) },
                            replace: { replace(state.agent) },
                            uninstall: { uninstall(state.agent) },
                            export: export,
                            confirm: { confirm(state.agent) },
                            revoke: { revoke(state.agent) }
                        )
                        if state.id != record.states.last?.id {
                            Divider().padding(.leading, 36)
                        }
                    }
                }
            }
        }
        .padding(Space.m)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
        .accessibilityElement(children: .contain)
    }
}

private struct SkillMark: View {
    let skill: OfficialSkill

    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: 21, weight: .semibold))
            .foregroundStyle(tone)
            .frame(width: 48, height: 48)
            .background(tone.opacity(0.12), in: RoundedRectangle(cornerRadius: Radius.control))
            .accessibilityHidden(true)
    }

    private var symbol: String {
        if skill.id.contains("article") { return "newspaper.fill" }
        if skill.id.contains("video") { return "play.rectangle.fill" }
        return "puzzlepiece.extension.fill"
    }

    private var tone: Color {
        if skill.id.contains("article") { return Theme.brandPrimary }
        if skill.id.contains("video") { return Theme.systemBlue }
        return Theme.warning
    }
}

private struct SkillBadge: View {
    let title: String
    let systemImage: String

    var body: some View {
        Label(title, systemImage: systemImage)
            .font(.system(size: 11.5, weight: .medium))
            .foregroundStyle(Theme.inkSecondary)
            .padding(.horizontal, 9)
            .frame(height: 24)
            .background(Theme.sunken, in: Capsule(style: .continuous))
    }
}

private struct InstalledAgentStack: View {
    let agents: [AgentID]
    let resourcesRoot: URL?

    var body: some View {
        HStack(spacing: -5) {
            ForEach(agents) { agent in
                AgentLogo(agent: agent, resourcesRoot: resourcesRoot, size: 19)
                    .padding(2)
                    .background(Theme.surface, in: RoundedRectangle(cornerRadius: 6))
            }
        }
        .accessibilityElement(children: .contain)
    }
}

private struct SkillAgentRow: View {
    let state: AgentSkillState
    let resourcesRoot: URL?
    let install: () -> Void
    let replace: () -> Void
    let uninstall: () -> Void
    let export: () -> Void
    let confirm: () -> Void
    let revoke: () -> Void

    var body: some View {
        HStack(spacing: Space.s) {
            AgentLogo(agent: state.agent, resourcesRoot: resourcesRoot, size: 22)

            Text(state.agent.displayName)
                .font(Typo.paneBodyStrong)
                .foregroundStyle(Theme.ink)
                .frame(width: 108, alignment: .leading)
                .lineLimit(1)

            StatusPill(text: statusLabel, tone: statusTone)
                .help(statusDetail)

            Spacer(minLength: Space.s)

            actions
        }
        .padding(.vertical, 7)
        .accessibilityElement(children: .contain)
    }

    @ViewBuilder
    private var actions: some View {
        switch state.status {
        case .packageUnavailable:
            EmptyView()
        case .agentUnavailable:
            Text(L10n.text("未检测到应用"))
                .font(Typo.paneCaption)
                .foregroundStyle(Theme.inkTertiary)
        case .notInstalled:
            if state.agent.directSkillRoot != nil {
                actionButton(L10n.text("安装"), action: install)
            } else {
                manualButtons(confirmed: false)
            }
        case .installed:
            actionButton(L10n.text("移除"), action: uninstall)
        case .updateAvailable:
            actionButton(L10n.text("更新"), action: install)
        case .versionConflict:
            actionButton(L10n.text("替换"), action: replace)
        case .manualOnly:
            manualButtons(confirmed: false)
        case .manualConfirmed:
            manualButtons(confirmed: true)
        }
    }

    @ViewBuilder
    private func manualButtons(confirmed: Bool) -> some View {
        HStack(spacing: Space.s) {
            actionButton(L10n.text("导出 ZIP"), action: export)
            if confirmed {
                actionButton(L10n.text("撤销确认"), action: revoke)
            } else {
                actionButton(L10n.text("我已安装"), action: confirm)
            }
        }
    }

    private func actionButton(_ title: String, action: @escaping () -> Void) -> some View {
        Button(title, action: action)
            .buttonStyle(SettingsActionButtonStyle(width: nil))
    }

    private var statusLabel: String {
        switch state.status {
        case .packageUnavailable: L10n.text("缺技能包")
        case .agentUnavailable: L10n.text("仅手动")
        case .notInstalled: L10n.text("未安装")
        case .installed(let version): L10n.format("已安装 %@", version)
        case .updateAvailable(let installed, let available):
            L10n.format("可更新 %@ → %@", installed, available)
        case .versionConflict: L10n.text("版本冲突")
        case .manualOnly: L10n.text("仅手动安装")
        case .manualConfirmed: L10n.text("已确认安装")
        }
    }

    private var statusTone: StatusPill.Tone {
        switch state.status {
        case .installed, .manualConfirmed: .live
        case .updateAvailable: .warn
        case .versionConflict, .agentUnavailable: .bad
        case .packageUnavailable, .notInstalled, .manualOnly: .neutral
        }
    }

    private var statusDetail: String {
        switch state.status {
        case .versionConflict(let detail):
            return detail
        case .updateAvailable(let installed, let available):
            return L10n.format("可更新 %@ → %@", installed, available)
        case .installed(let version), .manualConfirmed(let version):
            return version
        default:
            return ""
        }
    }
}

private struct AgentSkillState: Identifiable {
    let agent: AgentID
    let status: SkillAgentStatus

    var id: AgentID { agent }
}

private struct SkillRecord: Identifiable {
    let skill: OfficialSkill
    let states: [AgentSkillState]

    var id: String { skill.id }

    var totalAgentCount: Int { states.count }

    var installedAgents: [AgentID] {
        states.filter { Self.isInstalled($0.status) }.map(\.agent)
    }

    var installedCount: Int { installedAgents.count }

    var automaticMissing: [AgentSkillState] {
        states.filter {
            $0.agent.directSkillRoot != nil && Self.canInstallAutomatically($0.status)
        }
    }

    var manualMissing: [AgentSkillState] {
        states.filter { Self.isManualMissing($0.status) }
    }

    var hasAutomaticMissing: Bool { !automaticMissing.isEmpty }

    var kind: SkillCoverageKind {
        if states.isEmpty || states.allSatisfy({ Self.isPackageUnavailable($0.status) }) {
            return .packageUnavailable
        }
        if states.contains(where: { Self.isConflict($0.status) }) {
            return .conflict
        }
        if states.contains(where: { Self.isUpdate($0.status) }) {
            return .update
        }
        if installedCount == totalAgentCount {
            return .installed
        }
        if installedCount > 0 {
            return .partial
        }
        return .notInstalled
    }

    var statusTitle: String {
        switch kind {
        case .packageUnavailable: L10n.text("缺技能包")
        case .conflict: L10n.text("版本冲突")
        case .update: L10n.text("有更新")
        case .installed: L10n.text("已安装")
        case .partial: L10n.text("部分安装")
        case .notInstalled: L10n.text("未安装")
        }
    }

    var statusTone: StatusPill.Tone {
        switch kind {
        case .installed: .live
        case .conflict: .bad
        case .packageUnavailable, .update, .partial, .notInstalled: .warn
        }
    }

    var primaryAction: SkillPrimaryAction {
        switch kind {
        case .packageUnavailable, .conflict, .installed:
            return .details
        case .update, .notInstalled, .partial:
            if hasAutomaticMissing { return .install }
            if !manualMissing.isEmpty { return .exportArchive }
            return .details
        }
    }

    func primaryActionTitle(expanded: Bool) -> String? {
        switch kind {
        case .packageUnavailable, .conflict, .installed:
            return expanded ? L10n.text("收起详情") : L10n.text("查看详情")
        case .update:
            return hasAutomaticMissing ? L10n.text("更新") : L10n.text("查看详情")
        case .notInstalled, .partial:
            if hasAutomaticMissing {
                return installedCount == 0 ? L10n.text("安装") : L10n.text("安装缺失项")
            }
            if !manualMissing.isEmpty { return L10n.text("导出 ZIP") }
            return expanded ? L10n.text("收起详情") : L10n.text("查看详情")
        }
    }

    private static func isInstalled(_ status: SkillAgentStatus) -> Bool {
        switch status {
        case .installed, .updateAvailable, .versionConflict, .manualConfirmed:
            return true
        case .packageUnavailable, .agentUnavailable, .notInstalled, .manualOnly:
            return false
        }
    }

    private static func canInstallAutomatically(_ status: SkillAgentStatus) -> Bool {
        switch status {
        case .notInstalled, .updateAvailable:
            return true
        default:
            return false
        }
    }

    private static func isManualMissing(_ status: SkillAgentStatus) -> Bool {
        if case .manualOnly = status { return true }
        return false
    }

    private static func isPackageUnavailable(_ status: SkillAgentStatus) -> Bool {
        if case .packageUnavailable = status { return true }
        return false
    }

    private static func isConflict(_ status: SkillAgentStatus) -> Bool {
        if case .versionConflict = status { return true }
        return false
    }

    private static func isUpdate(_ status: SkillAgentStatus) -> Bool {
        if case .updateAvailable = status { return true }
        return false
    }
}

private enum SkillCoverageKind: Equatable {
    case packageUnavailable
    case conflict
    case update
    case installed
    case partial
    case notInstalled
}

private enum SkillPrimaryAction: Equatable {
    case install
    case exportArchive
    case details
}

private enum SkillStatusFilter: String, CaseIterable, Identifiable {
    case all
    case needsInstall
    case partial
    case installed

    var id: String { rawValue }

    var title: String {
        switch self {
        case .all: L10n.text("全部")
        case .needsInstall: L10n.text("未安装")
        case .partial: L10n.text("部分安装")
        case .installed: L10n.text("已安装")
        }
    }

    func includes(_ kind: SkillCoverageKind) -> Bool {
        switch self {
        case .all:
            return true
        case .needsInstall:
            return kind == .notInstalled
        case .partial:
            return kind == .partial || kind == .update || kind == .conflict
        case .installed:
            return kind == .installed
        }
    }
}

private enum SkillAgentFilter: Hashable {
    case all
    case agent(AgentID)
}

private struct ReplaceRequest {
    let skill: OfficialSkill
    let agent: AgentID
}

private struct SkillNotice {
    enum Tone {
        case good
        case warn
        case bad
    }

    let message: String
    let tone: Notice<EmptyView>.Tone

    init(_ message: String, tone: Tone) {
        self.message = message
        switch tone {
        case .good: self.tone = .good
        case .warn: self.tone = .warn
        case .bad: self.tone = .bad
        }
    }
}
