# Windows Share Target PoC 验证清单

- [ ] Windows CI restore/build/test 通过。
- [ ] MakeAppx 可以打包 sparse manifest。
- [ ] 本地开发证书签名和 Add-AppxPackage 注册成功。
- [ ] `Get-AppxPackage -Name WeChatBridge.Windows.ShareTarget` 可查到包。
- [ ] 微信 4.1.13.65 的分享菜单出现“微信流 Windows”。
- [ ] 单个 ZIP 成功进入 `%LOCALAPPDATA%\WeChatBridge\Inbox\Ready`。
- [ ] 多个 ZIP 全部成功后一次性提交。
- [ ] 非 ZIP、空分享和复制失败不会留下半成品批次。
- [ ] FileDropList 可在文件管理器中手动粘贴。
- [ ] 主程序未运行时能被 helper 启动。
- [ ] 主程序已运行时能刷新 Ready 批次。
- [ ] 卸载后 Share Target 从微信分享菜单消失。
