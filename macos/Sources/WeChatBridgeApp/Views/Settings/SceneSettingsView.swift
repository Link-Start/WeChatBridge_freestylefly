import AppKit
import SwiftUI
import UniformTypeIdentifiers
import WeChatBridgeCore

struct SceneSettingsView: View {
    @ObservedObject var preferences: Preferences
    @ObservedObject var skills: SkillLibrary

    @State private var page = ScenePage.scenes
    @State private var selectedSceneID: String?
    @State private var selectedGroupKey: String?
    @State private var sceneSearch = ""
    @State private var groupSearch = ""
    @State private var importing = false
    @State private var pendingImport: ScenePackage?
    @State private var deferredImports: [ScenePackage] = []
    @State private var notice: SceneNotice?
    @State private var dropTargeted = false

    var body: some View {
        VStack(alignment: .leading, spacing: Space.l) {
            Text(L10n.text("管理转发时附加的提示词，以及它们适用的 Agent。"))
                .font(Typo.paneBody)
                .foregroundStyle(Theme.inkSecondary)

            pagePicker

            Group {
                switch page {
                case .scenes: scenePage
                case .groups: groupPage
                }
            }
            .frame(minHeight: 500, alignment: .top)

            if let notice { Notice(notice.message, tone: notice.tone) }
        }
        .fileImporter(
            isPresented: $importing,
            allowedContentTypes: [.json],
            allowsMultipleSelection: true
        ) { result in
            switch result {
            case .success(let urls): importFiles(urls)
            case .failure(let error): notice = SceneNotice(error.localizedDescription, tone: .bad)
            }
        }
        .alert(L10n.text("场景版本已存在"), isPresented: Binding(
            get: { pendingImport != nil },
            set: {
                if !$0 {
                    pendingImport = nil
                    importDeferredPackages()
                }
            }
        )) {
            Button(L10n.text("取消"), role: .cancel) {
                pendingImport = nil
                importDeferredPackages()
            }
            Button(L10n.text("覆盖")) {
                let package = pendingImport
                pendingImport = nil
                if let package { apply(package, force: true) }
                importDeferredPackages()
            }
        } message: {
            Text(L10n.text("同版本包将覆盖场景内容，本地的启用状态和群绑定会保留。"))
        }
        .onAppear {
            selectedSceneID = selectedSceneID ?? preferences.scenes.scenes.first?.id
            selectedGroupKey = selectedGroupKey ?? allGroupKeys.first
        }
        .onChange(of: preferences.scenes.scenes.map(\.id)) { _, ids in
            if let selectedSceneID, ids.contains(selectedSceneID) { return }
            selectedSceneID = ids.first
        }
        .onChange(of: preferences.groupMemory.keys.sorted()) { _, keys in
            if let selectedGroupKey, keys.contains(selectedGroupKey) { return }
            selectedGroupKey = keys.first
        }
    }

    private var pagePicker: some View {
        HStack(spacing: 2) {
            ForEach(ScenePage.allCases) { item in
                Button {
                    page = item
                } label: {
                    Text(item.title)
                        .font(Typo.paneBodyStrong)
                        .foregroundStyle(page == item ? Theme.ink : Theme.inkSecondary)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 8)
                        .background(
                            page == item ? Theme.brandTint : Color.clear,
                            in: RoundedRectangle(cornerRadius: Radius.row, style: .continuous)
                        )
                }
                .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
            }
        }
        .padding(2)
        .frame(width: 300)
        .background(Theme.sunken, in: RoundedRectangle(cornerRadius: Radius.control, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.control, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
    }

    private var scenePage: some View {
        HStack(alignment: .top, spacing: Space.s) {
            sceneList.frame(width: 250)

            if let scene = selectedSceneBinding {
                SceneEditor(
                    scene: scene,
                    enabled: enabledBinding(for: scene.wrappedValue.id),
                    resourcesRoot: skills.resourcesRoot,
                    duplicate: { duplicate(scene.wrappedValue) },
                    moveUp: { move(scene.wrappedValue, by: -1) },
                    moveDown: { move(scene.wrappedValue, by: 1) },
                    export: { export(scene.wrappedValue) },
                    remove: { remove(scene.wrappedValue) }
                )
                .id(scene.wrappedValue.id)
            } else {
                EmptyPanel(
                    title: L10n.text("选择一个场景"),
                    detail: L10n.text("在左侧选择场景后，可以查看说明、启停和编辑。")
                )
            }
        }
    }

    private var sceneList: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: Space.s) {
                Text(L10n.text("场景")).font(Typo.rowTitle)
                Text(L10n.format("%d 个", preferences.scenes.scenes.count))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkTertiary)
                Spacer(minLength: 0)
                Button { addScene() } label: {
                    Label(L10n.text("新建"), systemImage: "plus")
                }
                .buttonStyle(SettingsActionButtonStyle(primary: true, width: nil))
            }
            .padding(Space.m)

            TextField(L10n.text("搜索场景"), text: $sceneSearch)
                .textFieldStyle(SettingsTextFieldStyle())
                .padding(.horizontal, Space.m)
                .padding(.bottom, Space.s)

            Divider()
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(filteredSceneIndices, id: \.self) { index in
                        let scene = preferences.scenes.scenes[index]
                        SceneListRow(
                            scene: scene,
                            selected: selectedSceneID == scene.id,
                            select: { selectedSceneID = scene.id }
                        )
                        if index != filteredSceneIndices.last { Divider() }
                    }
                }
            }
            .scrollBounceBehavior(.basedOnSize)
            Spacer(minLength: Space.s)
            Button(L10n.text("导入 JSON")) { importing = true }
                .buttonStyle(.link)
                .padding(Space.m)
        }
        .frame(maxHeight: .infinity, alignment: .top)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(panelBorder)
        .background(
            dropTargeted ? Theme.accentSoft : Color.clear,
            in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
        )
        .onDrop(of: [UTType.fileURL.identifier], isTargeted: $dropTargeted) { importDropped($0) }
    }

    private var groupPage: some View {
        HStack(alignment: .top, spacing: Space.s) {
            groupList.frame(width: 300)

            if let key = selectedGroupKey, let memory = preferences.groupMemory[key] {
                GroupBindingEditor(
                    memory: memory,
                    enabledScenes: preferences.scenes.enabledScenes,
                    disabledScenes: preferences.scenes.scenes.filter { !$0.enabled },
                    boundScenes: preferences.scenes.scenes(ids: memory.boundSceneIDs),
                    toggle: { toggleBinding(sceneID: $0, groupKey: key) },
                    clear: { clearBinding(groupKey: key) }
                )
                .id(key)
            } else {
                EmptyPanel(
                    title: L10n.text("还没有群聊记录"),
                    detail: L10n.text("从微信转发一次后，就可以在这里为群聊绑定场景。")
                )
            }
        }
    }

    private var groupList: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: Space.s) {
                Text(L10n.text("群聊")).font(Typo.rowTitle)
                Text(L10n.format("%d 个已绑定", boundGroupCount))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkTertiary)
                Spacer(minLength: 0)
            }
            .padding(Space.m)

            TextField(L10n.text("搜索群聊"), text: $groupSearch)
                .textFieldStyle(SettingsTextFieldStyle())
                .padding(.horizontal, Space.m)
                .padding(.bottom, Space.s)

            Divider()
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    if !boundGroupKeys.isEmpty { groupSection(title: L10n.text("已绑定"), keys: boundGroupKeys) }
                    if !unboundGroupKeys.isEmpty { groupSection(title: L10n.text("尚未绑定"), keys: unboundGroupKeys) }
                }
            }
            .scrollBounceBehavior(.basedOnSize)
        }
        .frame(maxHeight: .infinity, alignment: .top)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(panelBorder)
    }

    private func groupSection(title: String, keys: [String]) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(title)
                .font(Typo.paneCaption)
                .foregroundStyle(Theme.inkTertiary)
                .padding(.horizontal, Space.m)
                .padding(.top, Space.m)
                .padding(.bottom, Space.xs)
            ForEach(keys, id: \.self) { key in
                if let memory = preferences.groupMemory[key] {
                    GroupListRow(
                        name: memory.displayName,
                        sceneNames: preferences.scenes.scenes(ids: memory.boundSceneIDs).map(\.name),
                        selected: selectedGroupKey == key,
                        select: { selectedGroupKey = key }
                    )
                }
            }
        }
    }

    private var panelBorder: some View {
        RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
            .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
    }

    private var filteredSceneIndices: [Int] {
        preferences.scenes.scenes.indices.filter { index in
            let query = sceneSearch.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !query.isEmpty else { return true }
            let scene = preferences.scenes.scenes[index]
            return scene.name.localizedCaseInsensitiveContains(query)
                || scene.summary.localizedCaseInsensitiveContains(query)
        }
    }

    private var allGroupKeys: [String] {
        preferences.groupMemory.keys.sorted {
            let left = preferences.groupMemory[$0]?.displayName ?? $0
            let right = preferences.groupMemory[$1]?.displayName ?? $1
            return left.localizedStandardCompare(right) == .orderedAscending
        }
    }

    private var filteredGroupKeys: [String] {
        let query = groupSearch.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return allGroupKeys }
        return allGroupKeys.filter {
            preferences.groupMemory[$0]?.displayName.localizedCaseInsensitiveContains(query) == true
        }
    }

    private var boundGroupKeys: [String] {
        filteredGroupKeys.filter { !(preferences.groupMemory[$0]?.boundSceneIDs.isEmpty ?? true) }
    }

    private var unboundGroupKeys: [String] {
        filteredGroupKeys.filter { preferences.groupMemory[$0]?.boundSceneIDs.isEmpty ?? true }
    }

    private var boundGroupCount: Int {
        preferences.groupMemory.values.filter { !$0.boundSceneIDs.isEmpty }.count
    }

    private var selectedSceneBinding: Binding<WeChatScene>? {
        guard let selectedSceneID,
              let index = preferences.scenes.scenes.firstIndex(where: { $0.id == selectedSceneID })
        else { return nil }
        return $preferences.scenes.scenes[index]
    }

    private func enabledBinding(for id: String) -> Binding<Bool> {
        Binding(
            get: { preferences.scenes.scenes.first { $0.id == id }?.enabled ?? false },
            set: { enabled in
                guard let index = preferences.scenes.scenes.firstIndex(where: { $0.id == id }) else { return }
                preferences.scenes.scenes[index].enabled = enabled
                guard !enabled else { return }
                if preferences.scenes.defaultSceneID == id { preferences.scenes.defaultSceneID = nil }
                removeSceneFromBindings(id)
            }
        )
    }

    private func addScene() {
        let scene = WeChatScene(name: L10n.text("新场景"), instruction: "", outputSpec: "", enabled: true)
        preferences.scenes.add(scene)
        selectedSceneID = scene.id
    }

    private func duplicate(_ scene: WeChatScene) {
        let copy = preferences.scenes.copiedAsUserTask(scene)
        preferences.scenes.add(copy)
        selectedSceneID = copy.id
        notice = SceneNotice(L10n.text("已复制为我的场景。"), tone: .good)
    }

    private func move(_ scene: WeChatScene, by offset: Int) {
        guard let index = preferences.scenes.scenes.firstIndex(where: { $0.id == scene.id }) else { return }
        let target = index + offset
        guard preferences.scenes.scenes.indices.contains(target) else { return }
        preferences.scenes.scenes.swapAt(index, target)
    }

    private func remove(_ scene: WeChatScene) {
        guard let index = preferences.scenes.scenes.firstIndex(where: { $0.id == scene.id }) else { return }
        preferences.scenes.remove(id: scene.id)
        removeSceneFromBindings(scene.id)
        for key in Array(preferences.groupMemory.keys) where preferences.groupMemory[key]?.lastSceneID == scene.id {
            preferences.groupMemory[key]?.lastSceneID = nil
        }
        selectedSceneID = preferences.scenes.scenes.indices.contains(index)
            ? preferences.scenes.scenes[index].id
            : preferences.scenes.scenes.last?.id
    }

    private func removeSceneFromBindings(_ id: String) {
        for key in Array(preferences.groupMemory.keys) {
            preferences.groupMemory[key]?.boundSceneIDs.removeAll { $0 == id }
        }
    }

    private func toggleBinding(sceneID: String, groupKey: String) {
        guard var memory = preferences.groupMemory[groupKey] else { return }
        if memory.boundSceneIDs.contains(sceneID) {
            memory.boundSceneIDs.removeAll { $0 == sceneID }
        } else {
            memory.boundSceneIDs.append(sceneID)
        }
        let selected = Set(memory.boundSceneIDs)
        memory.boundSceneIDs = preferences.scenes.scenes
            .filter { $0.enabled && selected.contains($0.id) }
            .map(\.id)
        memory.updatedAt = Date()
        preferences.groupMemory[groupKey] = memory
    }

    private func clearBinding(groupKey: String) {
        preferences.groupMemory[groupKey]?.boundSceneIDs = []
    }

    private func export(_ scene: WeChatScene) {
        guard SceneVersion(scene.packageVersion) != nil else {
            notice = SceneNotice(L10n.text("版本号必须是 1.0.0 这样的数字格式。"), tone: .bad)
            return
        }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.canCreateDirectories = true
        panel.nameFieldStringValue = "\(scene.name).wechatflow-scene.json"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let data = try ScenePackage.encoder().encode(ScenePackage(scene: scene))
            try data.write(to: url, options: .atomic)
            notice = SceneNotice(L10n.text("场景包已导出。"), tone: .good)
        } catch {
            notice = SceneNotice(error.localizedDescription, tone: .bad)
        }
    }

    private func importDropped(_ providers: [NSItemProvider]) -> Bool {
        let group = DispatchGroup()
        let box = URLBox()
        for provider in providers where provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) {
            group.enter()
            provider.loadItem(forTypeIdentifier: UTType.fileURL.identifier, options: nil) { item, _ in
                defer { group.leave() }
                let url = (item as? URL)
                    ?? (item as? Data).flatMap { URL(dataRepresentation: $0, relativeTo: nil) }
                guard let url else { return }
                box.append(url)
            }
        }
        group.notify(queue: .main) { importFiles(box.urls) }
        return !providers.isEmpty
    }

    private func importFiles(_ urls: [URL]) {
        var imported: [ScenePackage] = []
        for url in urls {
            do {
                guard url.pathExtension.lowercased() == "json" else { continue }
                let package = try ScenePackage.decoder().decode(ScenePackage.self, from: Data(contentsOf: url))
                try validate(package)
                imported.append(package)
            } catch {
                notice = SceneNotice(error.localizedDescription, tone: .bad)
                return
            }
        }
        guard !imported.isEmpty else {
            notice = SceneNotice(L10n.text("没有找到可导入的场景包。"), tone: .bad)
            return
        }
        for package in imported { apply(package, force: false) }
        if pendingImport == nil {
            notice = SceneNotice(L10n.format("已导入 %d 个场景。", imported.count), tone: .good)
        }
    }

    private func validate(_ package: ScenePackage) throws {
        guard (1...ScenePackage.currentSchemaVersion).contains(package.schemaVersion) else {
            throw SceneImportError.unsupportedSchema
        }
        guard !package.id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !package.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !package.instruction.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              SceneVersion(package.version) != nil
        else { throw SceneImportError.invalidPackage }
    }

    private func apply(_ package: ScenePackage, force: Bool) {
        guard force || pendingImport == nil else {
            deferredImports.append(package)
            return
        }
        let localVersion = preferences.scenes.scenes
            .first(where: { $0.id == package.id })
            .flatMap { SceneVersion($0.packageVersion) }
        let incomingVersion = SceneVersion(package.version)!
        guard force || localVersion == nil || incomingVersion > localVersion! else {
            if incomingVersion == localVersion! {
                pendingImport = package
                return
            }
            notice = SceneNotice(L10n.text("已安装的场景版本更新，未导入较旧版本。"), tone: .bad)
            return
        }
        var scene = package.scene
        scene.enabled = true
        preferences.scenes.replace(scene)
        selectedSceneID = scene.id
    }

    private func importDeferredPackages() {
        while pendingImport == nil, !deferredImports.isEmpty {
            apply(deferredImports.removeFirst(), force: false)
        }
    }
}

private enum ScenePage: String, CaseIterable, Identifiable {
    case scenes
    case groups
    var id: String { rawValue }
    var title: String { self == .scenes ? L10n.text("场景管理") : L10n.text("群聊匹配") }
}

private struct SceneListRow: View {
    let scene: WeChatScene
    let selected: Bool
    let select: () -> Void

    var body: some View {
        Button(action: select) {
            HStack(alignment: .top, spacing: 10) {
                Circle()
                    .fill(scene.enabled ? Theme.brandPrimary : Theme.disabled)
                    .frame(width: 7, height: 7)
                    .padding(.top, 6)
                VStack(alignment: .leading, spacing: 3) {
                    Text(scene.name.isEmpty ? L10n.text("未命名场景") : scene.name)
                        .font(Typo.paneBodyStrong)
                        .foregroundStyle(Theme.ink)
                        .lineLimit(1)
                    Text(scene.summary.isEmpty ? L10n.text("没有一句话说明") : scene.summary)
                        .font(Typo.paneCaption)
                        .foregroundStyle(Theme.inkSecondary)
                        .lineLimit(2)
                        .multilineTextAlignment(.leading)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, Space.m)
            .padding(.vertical, 11)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(selected ? Theme.brandTint : Color.clear)
            .contentShape(Rectangle())
        }
        .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
        .accessibilityIdentifier("scene.row.\(scene.id)")
    }
}

private struct SceneEditor: View {
    @Binding var scene: WeChatScene
    @Binding var enabled: Bool
    let resourcesRoot: URL?
    let duplicate: () -> Void
    let moveUp: () -> Void
    let moveDown: () -> Void
    let export: () -> Void
    let remove: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: Space.l) {
            HStack(alignment: .center, spacing: Space.s) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(scene.name.isEmpty ? L10n.text("未命名场景") : scene.name).font(Typo.paneTitle)
                    Text(enabled ? L10n.text("已启用") : L10n.text("已停用"))
                        .font(Typo.paneCaption)
                        .foregroundStyle(enabled ? Theme.positive : Theme.inkTertiary)
                }
                Spacer(minLength: Space.s)
                Toggle(L10n.text("启用"), isOn: $enabled).toggleStyle(SwitchToggleStyle())
                menu
            }

            if scene.isOfficial {
                VStack(alignment: .leading, spacing: Space.l) {
                    ReadOnlySceneField(title: L10n.text("名称"), text: scene.name)
                    ReadOnlySceneField(title: L10n.text("说明"), text: scene.summary)
                    ReadOnlySceneField(title: L10n.text("提示词"), text: promptText)
                }
            } else {
                SceneField(title: L10n.text("名称"), text: $scene.name)
                SceneField(title: L10n.text("说明"), text: $scene.summary)
                SceneField(title: L10n.text("提示词"), text: promptBinding, lines: 5...9)
            }

            VStack(alignment: .leading, spacing: Space.s) {
                Text(L10n.text("适用 Agent"))
                    .font(Typo.captionStrong)
                    .foregroundStyle(Theme.inkSecondary)
                Text(L10n.text("仅在转发到已选择的 Agent 时使用这个提示词。"))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkTertiary)
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: Space.s), count: 3), spacing: Space.s) {
                    ForEach(AgentID.allCases) { agent in
                        AgentChoice(
                            agent: agent,
                            selected: scene.compatibleAgents.contains(agent),
                            editable: !scene.isOfficial,
                            resourcesRoot: resourcesRoot,
                            toggle: { toggle(agent) }
                        )
                    }
                }
            }

            if scene.isOfficial {
                HStack {
                    Text(L10n.text("官方模板保持只读，复制后可以修改。"))
                        .font(Typo.paneCaption)
                        .foregroundStyle(Theme.inkSecondary)
                    Spacer(minLength: Space.s)
                    Button(L10n.text("复制并编辑"), action: duplicate)
                        .buttonStyle(SettingsActionButtonStyle(primary: true, width: nil))
                }
            }
            Spacer(minLength: 0)
        }
        .padding(Space.l)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
    }

    private var menu: some View {
        Menu {
            if scene.isOfficial { Button(L10n.text("复制为我的场景"), action: duplicate) }
            Button(L10n.text("导出场景包"), action: export)
            Divider()
            Button(L10n.text("上移"), action: moveUp)
            Button(L10n.text("下移"), action: moveDown)
            if !scene.isOfficial {
                Divider()
                Button(L10n.text("删除场景"), role: .destructive, action: remove)
            }
        } label: { Image(systemName: "ellipsis") }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .accessibilityLabel(Text(L10n.text("更多")))
    }

    private var promptText: String {
        [scene.instruction, scene.outputSpec.isEmpty ? "" : L10n.format("输出规范：\n%@", scene.outputSpec)]
            .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .joined(separator: "\n\n")
    }

    private var promptBinding: Binding<String> {
        Binding(
            get: { promptText },
            set: {
                scene.instruction = $0
                scene.outputSpec = ""
            }
        )
    }

    private func toggle(_ agent: AgentID) {
        if scene.compatibleAgents.contains(agent) {
            scene.compatibleAgents.removeAll { $0 == agent }
        } else {
            scene.compatibleAgents.append(agent)
        }
    }
}

private struct AgentChoice: View {
    let agent: AgentID
    let selected: Bool
    let editable: Bool
    let resourcesRoot: URL?
    let toggle: () -> Void

    var body: some View {
        Button(action: { if editable { toggle() } }) {
            HStack(spacing: Space.s) {
                AgentLogo(agent: agent, resourcesRoot: resourcesRoot, size: 28)
                Text(agent.displayName)
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.ink)
                    .lineLimit(1)
                Spacer(minLength: 0)
                Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                    .foregroundStyle(selected ? Theme.brandPrimary : Theme.inkTertiary)
            }
            .padding(9)
            .background(selected ? Theme.brandTint : Theme.sunken, in: RoundedRectangle(cornerRadius: Radius.row))
            .overlay(
                RoundedRectangle(cornerRadius: Radius.row)
                    .strokeBorder(selected ? Theme.brandPrimary : Theme.stroke, lineWidth: Stroke.hairline)
            )
        }
        .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

private struct GroupListRow: View {
    let name: String
    let sceneNames: [String]
    let selected: Bool
    let select: () -> Void

    var body: some View {
        Button(action: select) {
            VStack(alignment: .leading, spacing: 3) {
                Text(name).font(Typo.paneBodyStrong).foregroundStyle(Theme.ink).lineLimit(1)
                Text(sceneNames.isEmpty ? L10n.text("转发时选择或直接转发") : sceneNames.joined(separator: "、"))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkSecondary)
                    .lineLimit(2)
            }
            .padding(.horizontal, Space.m)
            .padding(.vertical, 11)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(selected ? Theme.brandTint : Color.clear)
            .overlay(alignment: .leading) {
                if selected { Rectangle().fill(Theme.brandPrimary).frame(width: 3) }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
    }
}

private struct GroupBindingEditor: View {
    let memory: GroupMemory
    let enabledScenes: [WeChatScene]
    let disabledScenes: [WeChatScene]
    let boundScenes: [WeChatScene]
    let toggle: (String) -> Void
    let clear: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: Space.l) {
            VStack(alignment: .leading, spacing: 3) {
                Text(memory.displayName).font(Typo.paneTitle)
                Text(L10n.format("已关联 %d 个可选场景", boundScenes.count))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkSecondary)
            }
            Notice(L10n.text("每次转发只加载其中一个场景；不适用当前 Agent 的场景会自动跳过。"))
            VStack(alignment: .leading, spacing: 3) {
                Text(L10n.text("可选场景")).font(Typo.rowTitle)
                Text(L10n.text("勾选这个群聊可使用的场景；转发时再选择其中一个。"))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkSecondary)
            }

            VStack(spacing: 0) {
                ForEach(enabledScenes) { scene in
                    let selected = memory.boundSceneIDs.contains(scene.id)
                    Button { toggle(scene.id) } label: {
                        HStack(spacing: Space.m) {
                            Image(systemName: selected ? "checkmark.square.fill" : "square")
                                .font(.system(size: 15, weight: .medium))
                                .foregroundStyle(selected ? Theme.brandPrimary : Theme.inkTertiary)
                            VStack(alignment: .leading, spacing: 3) {
                                Text(scene.name).font(Typo.paneBodyStrong).foregroundStyle(Theme.ink)
                                Text(scene.summary.isEmpty ? L10n.text("没有一句话说明") : scene.summary)
                                    .font(Typo.paneCaption)
                                    .foregroundStyle(Theme.inkSecondary)
                                    .lineLimit(1)
                                Text(scene.compatibleAgents.map(\.displayName).joined(separator: " · "))
                                    .font(Typo.micro)
                                    .foregroundStyle(Theme.inkTertiary)
                                    .lineLimit(1)
                            }
                            Spacer(minLength: 0)
                        }
                        .padding(.horizontal, Space.m)
                        .padding(.vertical, 10)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
                    if scene.id != enabledScenes.last?.id { Divider() }
                }
            }
            .overlay(
                RoundedRectangle(cornerRadius: Radius.control)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
            )

            if !disabledScenes.isEmpty {
                VStack(alignment: .leading, spacing: 3) {
                    Text(L10n.text("已关闭的场景")).font(Typo.captionStrong)
                    Text(disabledScenes.map(\.name).joined(separator: "、"))
                        .font(Typo.paneCaption)
                        .foregroundStyle(Theme.inkSecondary)
                    Text(L10n.text("启用后才能绑定到群聊。"))
                        .font(Typo.paneCaption)
                        .foregroundStyle(Theme.inkTertiary)
                }
            }

            Spacer(minLength: 0)
            if !memory.boundSceneIDs.isEmpty {
                HStack {
                    Spacer()
                    Button(L10n.text("取消绑定"), role: .destructive, action: clear)
                        .buttonStyle(.borderless)
                }
            }
        }
        .padding(Space.l)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
    }

}

private struct EmptyPanel: View {
    let title: String
    let detail: String

    var body: some View {
        VStack(spacing: Space.s) {
            Image(systemName: "square.stack.3d.up")
                .font(.system(size: 24))
                .foregroundStyle(Theme.inkTertiary)
            Text(title).font(Typo.paneBodyStrong)
            Text(detail)
                .font(Typo.paneCaption)
                .foregroundStyle(Theme.inkSecondary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
    }
}

private struct SceneField: View {
    let title: String
    @Binding var text: String
    var lines: ClosedRange<Int> = 1...1

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title).font(Typo.captionStrong).foregroundStyle(Theme.inkSecondary)
            if lines.upperBound == 1 {
                TextField(title, text: $text).textFieldStyle(SettingsTextFieldStyle())
            } else {
                TextField(title, text: $text, axis: .vertical)
                    .lineLimit(lines)
                    .textFieldStyle(SettingsTextFieldStyle(multiline: true))
            }
        }
    }
}

private struct ReadOnlySceneField: View {
    let title: String
    let text: String

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title).font(Typo.captionStrong).foregroundStyle(Theme.inkSecondary)
            Text(text.isEmpty ? L10n.text("无") : text)
                .font(Typo.paneBody)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 10)
                .padding(.vertical, 8)
                .background(Theme.sunken, in: RoundedRectangle(cornerRadius: Radius.row))
        }
    }
}

private struct SceneNotice {
    let message: String
    let tone: Notice<EmptyView>.Tone
    enum Tone { case good, bad }
    init(_ message: String, tone: Tone) {
        self.message = message
        self.tone = tone == .good ? .good : .bad
    }
}

private enum SceneImportError: LocalizedError {
    case unsupportedSchema
    case invalidPackage
    var errorDescription: String? {
        switch self {
        case .unsupportedSchema: L10n.text("这个场景包由更新版本生成，当前微信流无法导入。")
        case .invalidPackage: L10n.text("场景包缺少 id、名称、版本、指令或版本格式无效。")
        }
    }
}

private final class URLBox: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [URL] = []
    var urls: [URL] {
        lock.lock(); defer { lock.unlock() }
        return storage
    }
    func append(_ url: URL) {
        lock.lock(); storage.append(url); lock.unlock()
    }
}
