# CodexPrefix · 会话前缀

Windows x64 免安装工具，为 Codex 原生重命名弹窗添加或清除 emoji 前缀。当前分发版本 **0.7.0-rc.1（候选版）**。

本仓包含已脱敏源码及必要发布文件，不携带开发仓历史、用户配置、日志或截图。交付目录保持为文件夹，不另行打包。

## 下载与使用

完整复制 [release](./release/) 目录后运行 `CodexPrefix.exe`。无需 API Key 或额外 .NET 环境。用法见 [使用说明](./docs/usage/发布使用说明.md)。

正式入口为“顶部标题菜单 → 重命名 → 原生弹窗保存”，不发送全局重命名快捷键。支持个人列表与默认模板切换、跟随小条、托盘暂停，以及宿主关闭后只能经托盘恢复的收纳流程。用户配置位于系统用户目录，更新不会覆盖。

**验收边界**：已完成构建、隔离自检和工作进程故障验证；真实菜单、托盘点击、焦点、IME 及连续屏幕首帧仍待人工验收。候选版不作为正式版自动更新推荐。详见 [验证记录](./docs/verification/0.7.0-rc.1.md)。

## 构建与更新

源码在 [src/CodexPrefix](./src/CodexPrefix/)，使用 [global.json](./global.json) 指定的 SDK。在仓库根目录运行：

```powershell
./scripts/publish.ps1
./scripts/check-privacy.ps1
```

应用只在更新检查或下载时访问本公开仓的版本清单与交付文件，不上传会话、配置或凭据。下载固定到对应版本 tag，执行 SHA-256 校验、可恢复文件事务和启动健康握手。也可右键更新按钮选择本地新版发布目录。

第三方许可见 [THIRD-PARTY-NOTICES.txt](./THIRD-PARTY-NOTICES.txt) 与 [release/licenses](./release/licenses/)。
