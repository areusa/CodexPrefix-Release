# 使用 UTF-8 BOM；只发布白名单文件，不读取或覆盖用户配置，不生成压缩包。
[CmdletBinding()]
param([string]$OutputDirectory = 'release')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskBuild = Join-Path $taskRoot '.build'
$taskRelease = [IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))
$taskRun = Join-Path $taskBuild ([Guid]::NewGuid().ToString('N'))
$taskStage = Join-Path $taskRun 'release'
$taskOutput = Join-Path $taskRun 'publish'
$taskPrevious = Join-Path $taskRun 'previous-release'
function Assert-WorkspacePath([string]$Path) {
    $taskFull = [IO.Path]::GetFullPath($Path)
    if (-not $taskFull.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '发布路径超出仓库，已停止。' }
}
foreach ($taskPath in @($taskBuild,$taskRun,$taskStage,$taskOutput,$taskPrevious,$taskRelease)) { Assert-WorkspacePath $taskPath }
New-Item -ItemType Directory -Path $taskOutput,$taskStage -Force | Out-Null
$taskLock = $null
try {
    # 互斥发布，避免两个构建同时替换固定目录。
    $taskLock = [IO.File]::Open((Join-Path $taskBuild 'publish.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    Push-Location $taskRoot
    try {
        & (Join-Path $PSScriptRoot 'build-icon.ps1')
        dotnet restore ./src/CodexPrefix/CodexPrefix.csproj --locked-mode
        if ($LASTEXITCODE -ne 0) { throw '依赖锁校验失败。修改依赖后先明确更新锁文件。' }
        dotnet publish ./src/CodexPrefix/CodexPrefix.csproj -c Release --no-restore -o $taskOutput
        if ($LASTEXITCODE -ne 0) { throw '构建失败，原发布目录未替换。' }
        $taskExe = Join-Path $taskOutput 'CodexPrefix.exe'
        $taskTest = Start-Process -FilePath $taskExe -ArgumentList '--self-test' -PassThru -WindowStyle Hidden
        if (-not $taskTest.WaitForExit(120000)) { $taskTest.Kill(); throw '隔离自检超时，原发布目录未替换。' }
        $taskReport = Join-Path $taskOutput 'self-test.txt'
        if ($taskTest.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskReport)) { throw '隔离自检失败，原发布目录未替换。' }
        $taskResult = Get-Content -LiteralPath $taskReport -Raw -Encoding utf8
        if ($taskResult -match '失败' -or $taskResult -notmatch '通过：\d+ 项') { throw '隔离自检报告不完整，原发布目录未替换。' }
        Write-Host $taskResult
        # 只运行无界面的管道故障夹具，不连接真实 Codex 或执行键鼠操作。
        $taskWorkerTest = Start-Process -FilePath $taskExe -ArgumentList '--automation-worker-test' -PassThru -WindowStyle Hidden
        if (-not $taskWorkerTest.WaitForExit(60000)) { $taskWorkerTest.Kill($true); throw '隔离工作进程验证超时，原发布目录未替换。' }
        $taskWorkerReport = Join-Path $taskOutput 'automation-worker-validation.json'
        if ($taskWorkerTest.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskWorkerReport)) { throw '隔离工作进程验证失败，原发布目录未替换。' }
        $taskWorkerResult = Get-Content -LiteralPath $taskWorkerReport -Raw -Encoding utf8 | ConvertFrom-Json
        if ($taskWorkerResult.passed -ne $true -or @($taskWorkerResult.checks).Count -eq 0) { throw '隔离工作进程验证报告不完整，原发布目录未替换。' }
        Write-Host ('隔离工作进程验证通过：' + @($taskWorkerResult.checks).Count + ' 项。')
        Copy-Item -LiteralPath $taskExe -Destination $taskStage
        Copy-Item -LiteralPath (Join-Path $taskRoot 'assets/defaults/emoji-defaults.json') -Destination (Join-Path $taskStage 'default-emojis.json')
        Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/usage/发布使用说明.md') -Destination (Join-Path $taskStage '使用说明.md')
        Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD-PARTY-NOTICES.txt') -Destination $taskStage
        $taskLicense = Join-Path $taskStage 'licenses'
        New-Item -ItemType Directory -Path $taskLicense | Out-Null
        Copy-Item -LiteralPath (Join-Path $taskRoot 'assets/mingcute/LICENSE') -Destination (Join-Path $taskLicense 'MingCute-LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $taskRoot 'assets/unicode/17.0/LICENSE.txt') -Destination (Join-Path $taskLicense 'Unicode-LICENSE.txt')
        [xml]$taskProject = Get-Content -LiteralPath ./src/CodexPrefix/CodexPrefix.csproj -Raw -Encoding utf8
        $taskVersion = [string]($taskProject.Project.PropertyGroup.Version | Where-Object {$_} | Select-Object -First 1)
        $taskRuntime = [string]($taskProject.Project.PropertyGroup.RuntimeFrameworkVersion | Where-Object {$_} | Select-Object -First 1)
        $taskNugetText = dotnet nuget locals global-packages --list
        if ($LASTEXITCODE -ne 0) { throw '无法确定依赖许可目录。' }
        $taskNuget = ($taskNugetText -replace '^.*?:\s*','').Trim()
        $taskRuntimeFiles = @{
            'Runtime-LICENSE.txt' = "microsoft.netcore.app.runtime.win-x64/$taskRuntime/LICENSE.TXT"
            'Runtime-NOTICES.txt' = "microsoft.netcore.app.runtime.win-x64/$taskRuntime/THIRD-PARTY-NOTICES.TXT"
            'WindowsDesktop-LICENSE.txt' = "microsoft.windowsdesktop.app.runtime.win-x64/$taskRuntime/LICENSE"
        }
        foreach ($taskName in $taskRuntimeFiles.Keys) { Copy-Item -LiteralPath (Join-Path $taskNuget $taskRuntimeFiles[$taskName]) -Destination (Join-Path $taskLicense $taskName) }
        $taskHash = (Get-FileHash -LiteralPath (Join-Path $taskStage 'CodexPrefix.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        $taskManifest = [ordered]@{ name='CodexPrefix'; version=$taskVersion; platform='win-x64'; executable='CodexPrefix.exe'; sha256=$taskHash; selfTest='passed'; automationWorkerTest='passed'; configuration='系统 LocalApplicationData 下 ./CodexPrefix/emoji-settings.json；发布不覆盖' }
        [IO.File]::WriteAllText((Join-Path $taskStage 'version.json'), ($taskManifest | ConvertTo-Json -Depth 5).Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
        $taskChecksums = Get-ChildItem -LiteralPath $taskStage -File -Recurse | Sort-Object FullName | ForEach-Object {
            $taskRelative = $_.FullName.Substring($taskStage.Length + 1).Replace('\','/')
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $taskRelative
        }
        [IO.File]::WriteAllText((Join-Path $taskStage 'SHA256SUMS.txt'), ($taskChecksums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
        # 同卷重命名替换；若目录被占用或替换失败则恢复旧目录。
        New-Item -ItemType Directory -Path (Split-Path $taskRelease -Parent) -Force | Out-Null
        if (Test-Path -LiteralPath $taskRelease) { Move-Item -LiteralPath $taskRelease -Destination $taskPrevious }
        try { Move-Item -LiteralPath $taskStage -Destination $taskRelease }
        catch {
            if (Test-Path -LiteralPath $taskPrevious) { Move-Item -LiteralPath $taskPrevious -Destination $taskRelease }
            throw
        }
        Write-Host ('发布完成：./' + [IO.Path]::GetRelativePath($taskRoot, $taskRelease).Replace('\','/') + '/CodexPrefix.exe，版本 ' + $taskVersion + '；校验和与第三方许可已更新。')
        Write-Host '旧发布及测试输出保留在忽略的 ./.build/ 本次目录中，不会提交。'
    } finally { Pop-Location }
} finally { if ($null -ne $taskLock) { $taskLock.Dispose() } }
