# 使用 UTF-8 BOM。仅导出已脱敏白名单，不复制私有 Git 历史、日志或恢复资料。
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Destination,
    [string]$ReleaseDirectory = 'release'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDestination = [IO.Path]::GetFullPath($Destination)
$taskRelease = [IO.Path]::GetFullPath((Join-Path $taskRoot $ReleaseDirectory))
if (Test-Path -LiteralPath $taskDestination) { throw '导出目录必须尚不存在，避免覆盖其他工作。' }
if (-not $taskRelease.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '交付目录超出当前仓库。' }
& (Join-Path $PSScriptRoot 'check-privacy.ps1') -ReleaseDirectory $ReleaseDirectory
if ($LASTEXITCODE -ne 0) { throw '源仓隐私检查未通过。' }
$taskVersion = [string](Get-Content -LiteralPath (Join-Path $taskRelease 'version.json') -Raw -Encoding utf8 | ConvertFrom-Json).version
$taskKind = if ($taskVersion.Contains('-')) { '候选版' } else { '正式版' }
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
Push-Location $taskRoot
try {
    $taskFiles = @(git -c core.quotePath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw '无法枚举导出文件。' }
    $taskCopied = [Collections.Generic.List[string]]::new()
    foreach ($taskFile in $taskFiles) {
        $taskAllow = $taskFile -match '^src/CodexPrefix/.*\.(cs|xaml|csproj|json|manifest)$' -or
            $taskFile -match '^assets/(motion|unicode|defaults|mingcute)/' -or
            $taskFile -match '^assets/app-icon/release-v2/(app\.ico|(?:16|20|24|32|40|48|64|128|256)\.png)$' -or
            $taskFile -match '^scripts/[^/]+\.ps1$' -or
            $taskFile -in @('docs/development.md','docs/usage/发布使用说明.md', ('docs/verification/' + $taskVersion + '.md')) -or
            $taskFile -in @('.github/workflows/verify.yml','.gitattributes','.gitignore','global.json','AGENTS.md','THIRD-PARTY-NOTICES.txt')
        if (-not $taskAllow -or -not (Test-Path -LiteralPath $taskFile -PathType Leaf)) { continue }
        if ($taskFile -match '(^|/)(bin|obj|private|logs|diagnostics|captures|fixture-data)(/|$)') { throw '白名单中出现运行或私密目录，停止导出。' }
        $taskTarget = [IO.Path]::GetFullPath((Join-Path $taskDestination $taskFile))
        if (-not $taskTarget.StartsWith($taskDestination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '导出文件越界。' }
        New-Item -ItemType Directory -Path (Split-Path $taskTarget -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $taskFile -Destination $taskTarget
        $taskCopied.Add($taskFile)
    }
    Copy-Item -LiteralPath $taskRelease -Destination (Join-Path $taskDestination 'release') -Recurse
    $taskReadme = @'
# CodexPrefix · 会话前缀

Windows x64 免安装工具，为 Codex 原生重命名弹窗添加或清除 emoji 前缀。当前分发版本 **{{VERSION}}（{{KIND}}）**。

本仓包含已脱敏源码及必要发布文件，不携带开发仓历史、用户配置、日志或截图。交付目录保持为文件夹，不另行打包。

## 下载与使用

完整复制 [release](./release/) 目录后运行 `CodexPrefix.exe`。无需 API Key 或额外 .NET 环境。用法见 [使用说明](./docs/usage/发布使用说明.md)。

正式入口为“顶部标题菜单 → 重命名 → 原生弹窗保存”，不发送全局重命名快捷键。支持个人列表与默认模板切换、跟随小条、托盘暂停，以及宿主关闭后只能经托盘恢复的收纳流程。用户配置位于系统用户目录，更新不会覆盖。

**验收边界**：已完成构建、隔离自检和工作进程故障验证；真实菜单、托盘点击、焦点、IME 及连续屏幕首帧仍待人工验收。候选版不作为正式版自动更新推荐。详见 [验证记录](./docs/verification/{{VERSION}}.md)。

## 构建与更新

源码在 [src/CodexPrefix](./src/CodexPrefix/)，使用 [global.json](./global.json) 指定的 SDK。在仓库根目录运行：

```powershell
./scripts/publish.ps1
./scripts/check-privacy.ps1
```

应用只在更新检查或下载时访问本公开仓的版本清单与交付文件，不上传会话、配置或凭据。下载固定到对应版本 tag，执行 SHA-256 校验、可恢复文件事务和启动健康握手。也可右键更新按钮选择本地新版发布目录。

第三方许可见 [THIRD-PARTY-NOTICES.txt](./THIRD-PARTY-NOTICES.txt) 与 [release/licenses](./release/licenses/)。
'@
    [IO.File]::WriteAllText((Join-Path $taskDestination 'README.md'), $taskReadme.Replace("`r`n","`n").Replace('{{VERSION}}',$taskVersion).Replace('{{KIND}}',$taskKind) + "`n", [Text.UTF8Encoding]::new($false))
    Write-Host ('公开导出完成：' + $taskCopied.Count + ' 个白名单源码/资源文件及完整交付目录；尚未提交或推送。')
} finally { Pop-Location }
