# WeChatBridge 0.1.14

本次版本同步最新 `main` 分支，并重新生成经过 Developer ID 签名和 Apple 公证的 Universal 2 安装包。

## 更新内容

- 同步已合并的 macOS 14 窗口尺寸兼容改进，保持首次引导和设置窗口显示稳定。
- README 新增微信交流群入口，以及“关于我们”和开发者联系方式。
- 完善公开下载说明，方便用户从 GitHub Releases 获取最新安装包。

## 安装说明

- 支持 Apple Silicon 和 Intel Mac。
- 系统要求：macOS 14 Sonoma 或更高版本。
- 下载 DMG 后，将“微信流”拖入“应用程序”即可。

## 隐私

- 聊天内容只来自微信主动导出的文件，并保存在本机。
- Share Extension 在 macOS 沙盒内运行且没有网络权限。
- 应用不读取微信数据库，不解密、不注入、不修改微信进程。
