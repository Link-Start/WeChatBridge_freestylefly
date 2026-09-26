# 官方技能包

`catalog.json` 是随 App 发布的官方技能清单。每个技能的 `package` 指向本目录下
的包目录；包目录必须至少包含 `SKILL.md`，也可以包含 `scripts/`、`references/`
和 `assets/`。

当前两个官方场景已经引用稳定技能 ID，但技能包尚未放入仓库，因此 `package`
保持为 `null`。把用户提供的技能目录放到本目录后，将 `package` 改为目录名即可：

```text
shared/Skills/wechatbridge.wechat-article-extract/SKILL.md
shared/Skills/wechatbridge.video-information-reading/SKILL.md
```

打包时 macOS 会把它复制进 `.app` 的 `Resources/Skills`，Windows 会发布到输出目录的
`Resources/Skills`——两端运行期读的都是这个名字。

微信流不会解析技能的执行结果，也不会自动安装 Homebrew、pip 等外部依赖。
