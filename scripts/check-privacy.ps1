# 使用 UTF-8 BOM；只报告文件名与类别，不回显可能敏感的原文。
[CmdletBinding()]
param([string]$ReleaseDirectory = 'release')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $taskRoot
try {
    $taskFiles = @(git -c core.quotePath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw '请先在本目录初始化 Git 仓库。' }
    $taskProblems = [Collections.Generic.List[string]]::new()
    $taskStrictUtf8 = [Text.UTF8Encoding]::new($false,$true)
    foreach ($taskFile in $taskFiles) {
        $taskFile = $taskFile.Trim('"')
        if (-not (Test-Path -LiteralPath $taskFile -PathType Leaf)) { continue }
        if ($taskFile -match '(^|/)(bin|obj|artifacts|dist|fixture-data|captures|diagnostics|logs)(/|$)|\.(zip|7z|pdb|pfx|pem|key|log|bak)$|(^|/)\.env($|\.)') { $taskProblems.Add("运行或敏感文件：$taskFile") }
        if ($taskFile -match '(^|/)(settings|emoji-settings)\.json$' -and $taskFile -ne 'presets/emoji-settings.json') { $taskProblems.Add("未授权配置：$taskFile") }
        if ([IO.Path]::GetExtension($taskFile) -notin @('.cs','.xaml','.csproj','.json','.md','.ps1','.mjs','.js','.html','.css','.yml','.txt','.manifest','.svg','') -and $taskFile -notin @('.gitignore','.gitattributes')) { continue }
        # Unicode 原始数据及第三方许可保留原始归属信息。
        if ($taskFile -match '^release/licenses/|^assets/unicode/|(^|/)LICENSE($|\.)') { continue }
        try { $taskText = $taskStrictUtf8.GetString([IO.File]::ReadAllBytes((Join-Path $taskRoot $taskFile))) }
        catch { $taskProblems.Add("UTF-8 解码失败：$taskFile"); continue }
        $taskPatterns = @{
            '私人绝对路径' = '(?i)(?<![a-z0-9_])[a-z]:[\\/](?!/)|/(?:Users|home)/[^/\s]+/'
            '访问令牌' = '(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{24,})'
            '私钥' = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
        }
        foreach ($taskKind in $taskPatterns.Keys) { if ($taskText -match $taskPatterns[$taskKind]) { $taskProblems.Add("${taskKind}：$taskFile") } }
        if ($taskText.Contains([char]0xFFFD)) { $taskProblems.Add("存在替代乱码字符：$taskFile") }
    }
    $taskRelease = [IO.Path]::GetFullPath((Join-Path $taskRoot $ReleaseDirectory))
    if (-not $taskRelease.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '校验目录超出仓库。' }
    $taskExpected = @('CodexPrefix.exe','使用说明.md','THIRD-PARTY-NOTICES.txt','version.json','SHA256SUMS.txt','licenses/MingCute-LICENSE.txt','licenses/Unicode-LICENSE.txt','licenses/Runtime-LICENSE.txt','licenses/Runtime-NOTICES.txt','licenses/WindowsDesktop-LICENSE.txt')
    $taskManifest = Get-Content -LiteralPath (Join-Path $taskRelease 'version.json') -Raw -Encoding utf8 | ConvertFrom-Json
    if ([version]($taskManifest.version -split '-')[0] -ge [version]'0.7.0') { $taskExpected += 'default-emojis.json' }
    foreach ($taskItem in Get-ChildItem -LiteralPath $taskRelease -Recurse -File) {
        $taskRelative = $taskItem.FullName.Substring($taskRelease.Length + 1).Replace('\','/')
        if ($taskRelative -notin $taskExpected) { $taskProblems.Add("发布目录多余文件：$taskRelative") }
    }
    foreach ($taskExpectedFile in $taskExpected) { if (-not (Test-Path -LiteralPath (Join-Path $taskRelease $taskExpectedFile))) { $taskProblems.Add("发布文件缺失：$taskExpectedFile") } }
    # 发布文件按字节保存，校验和必须在 Git 检出后仍然有效。
    foreach ($taskChecksum in Get-Content -LiteralPath (Join-Path $taskRelease 'SHA256SUMS.txt') -Encoding utf8) {
        if ($taskChecksum -notmatch '^([0-9a-f]{64})  (.+)$') { $taskProblems.Add('发布校验和格式无效。'); continue }
        $taskDigest = $Matches[1]
        $taskName = $Matches[2]
        if ($taskName -notin $taskExpected) { $taskProblems.Add('发布校验和含非白名单路径。'); continue }
        if ((Get-FileHash -LiteralPath (Join-Path $taskRelease $taskName) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskDigest) { $taskProblems.Add("发布校验和不一致：$taskName") }
    }
    if ($taskProblems.Count -gt 0) { $taskProblems | ForEach-Object { Write-Output $_ }; throw ('提交前检查未通过：' + $taskProblems.Count + ' 项。') }
    Write-Host ('提交前检查通过，共 ' + $taskFiles.Count + ' 个文件；发布目录符合白名单。')
} finally { Pop-Location }
