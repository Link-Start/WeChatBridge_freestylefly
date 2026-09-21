import AppKit
import WeChatBridgeCore
import SwiftUI

/// The version out of the bundle. A plain `swift run` build has no Info.plist at
/// all, so both fields carry a placeholder rather than crashing the pane.
enum AppVersion {
    static var short: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.1.0"
    }

    static var build: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "—"
    }
}

struct AboutPane: View {
    @ObservedObject var updater: AppUpdater

    var body: some View {
        VStack(alignment: .leading, spacing: Space.section) {
            HStack(spacing: 14) {
                Image(nsImage: NSApp.applicationIconImage ?? NSImage())
                    .resizable()
                    .interpolation(.high)
                    .frame(width: 44, height: 44)
                    .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: 6) {
                    Text(verbatim: "微信流")
                        .font(Typo.paneTitle)
                        .foregroundStyle(Theme.ink)
                    HStack(spacing: Space.s) {
                        StatusPill(text: L10n.format("版本 %@ (%@)", AppVersion.short, AppVersion.build))
                        Text(L10n.text("此分支未启用自动更新"))
                            .font(Typo.paneCaption)
                            .foregroundStyle(Theme.inkTertiary)
                    }
                }
            }

            SettingsSection(title: L10n.text("WeChatBridge 做什么"), systemImage: "info.circle", spacing: 10) {
                paragraph(L10n.text("在微信的转发菜单里，直接把聊天记录送到你要用的地方。"))
                paragraph(L10n.text("也是聊天记录的本地备份工具：导出只走微信自带的转发界面，不读数据库、不解密、不注入。"))
                paragraph(L10n.text("聊天文件在本机处理，微信流不会上传聊天内容。文件会按清理设置保留，可随时在「记录」中管理。"))
            }

            SettingsSection(title: L10n.text("链接"), systemImage: "link") {
                SettingRow(title: L10n.text("原项目源码与问题反馈"), detail: "GitHub · qzz0518/Dukou", alignment: .center) {
                    Button { AppLinks.open(AppLinks.github) } label: {
                        Label(L10n.text("打开"), systemImage: "arrow.up.right")
                    }
                        .buttonStyle(SettingsActionButtonStyle())
                        .accessibilityLabel(Text(L10n.text("源码与问题反馈")))
                }

                SettingRow(title: L10n.text("作者"), detail: "X · @zerah_eth", alignment: .center) {
                    Button { AppLinks.open(AppLinks.x) } label: {
                        Label(L10n.text("打开"), systemImage: "arrow.up.right")
                    }
                        .buttonStyle(SettingsActionButtonStyle())
                        .accessibilityLabel(Text(verbatim: "X · @zerah_eth"))
                }
            }

            SettingsSection(title: L10n.text("开源署名"), systemImage: "heart") {
                paragraph(L10n.text("基于开源项目 Dukou（渡口）二次开发，感谢原作者 qzz0518。"))
                paragraph(L10n.text("原项目采用 MIT 许可证；本分支保留其许可证和第三方声明。"))
            }
        }
    }

    private func paragraph(_ text: String) -> some View {
        Text(text)
            .font(Typo.paneBody)
            .foregroundStyle(Theme.inkSecondary)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: 460, alignment: .leading)
    }
}
