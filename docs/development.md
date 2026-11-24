# 开发与验收

本文路径相对仓库根目录。主项目为 `./src/CodexPrefix/CodexPrefix.csproj`。验证代码位于项目的 `./Validation/`，仍编入同一程序集，保持原有内部类型访问与 CLI 行为。

```powershell
# 只发布构建中间结果；不更新正式发布目录。
dotnet publish ./src/CodexPrefix/CodexPrefix.csproj -c Release -o ./.build/manual
# 规则、资源、渲染、配置及物理自检。输出只在中间目录。
./.build/manual/CodexPrefix.exe --self-test
# 无界面的进程故障验证：不创建 UIA，不连接真实窗口。
./.build/manual/CodexPrefix.exe --automation-worker-test
# 有真实桌面时点击开始，保持测试窗口在前台。
./.build/manual/CodexPrefix.exe --bar-test-click-start
./.build/manual/CodexPrefix.exe --emoji-test-click-start
./.build/manual/CodexPrefix.exe --emoji-input-test
```

原生测试使用独立夹具及 `./fixture-data/`，不得复制真实会话或连接真实 Codex 做写入实验。输入升级夹具可使用 `./presets/emoji-settings.json` 的副本，命名为 EXE 同目录的 `./emoji-settings-upgrade-fixture.json`；不能放入正式发布目录。

原生桌面测试受 Windows 前台锁影响，必须使用点击启动入口；后台自动启动失败不等于产品功能失败。自检、WPF 位图和尾帧不能证明真实 DWM 首帧。真实 Codex、IME/系统选择器和多屏边界仍以实际交互证据为准。

HTML 原型入口保留 `./tools/mini-bar-motion-demo/`，运行与 Playwright 说明见其 README。已确认的 50 项参数与旧 HTML 结果位于 `./assets/motion/`，目录整理不更改其内容。

## 0.7.0 非占用验证

新增 `upgrade-validation.json` 是隔离配置、更新事务和未显示窗口的真实 XAML 布局检查。它不创建托盘图标、不激活窗口、不模拟键鼠、不连接真实 Codex。`--data-dir` 隔离运行与正式目录的配置、托盘、更新替换均分开。

构建候选可指定 `./scripts/publish.ps1 -OutputDirectory 'AI Stuff/功能整合-2026-11-24/candidate'`。对应隐私检查使用 `./scripts/check-privacy.ps1 -ReleaseDirectory 'AI Stuff/功能整合-2026-11-24/candidate'`。原生菜单、托盘与焦点必须另行人工验收，当前不自动替换日常发布版。
