# 本脚本使用 UTF-8 BOM，仅将现有 PNG 帧封装为 ICO，不重绘或修改图案。
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $resourceFolder = Join-Path $PSScriptRoot '../assets/app-icon/release-v2'
    $frameSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $framePayloads = @()
    foreach ($frameSize in $frameSizes) {
        $framePath = Join-Path $resourceFolder ($frameSize.ToString() + '.png')
        if (-not (Test-Path -LiteralPath $framePath)) { throw ('缺少图标帧：' + $frameSize) }
        $frameBytes = [System.IO.File]::ReadAllBytes($framePath)
        # PNG 文件头和 IHDR 尺寸均做校验，禁止误封装 JPEG 或错误尺寸。
        if ($frameBytes.Length -lt 33 -or [BitConverter]::ToString($frameBytes[0..7]).Replace('-', '') -ne '89504E470D0A1A0A') { throw '图标帧不是有效 PNG。' }
        $frameWidth = $frameBytes[16] * 16777216 + $frameBytes[17] * 65536 + $frameBytes[18] * 256 + $frameBytes[19]
        $frameHeight = $frameBytes[20] * 16777216 + $frameBytes[21] * 65536 + $frameBytes[22] * 256 + $frameBytes[23]
        if ($frameWidth -ne $frameSize -or $frameHeight -ne $frameSize -or $frameBytes[25] -ne 6) { throw '图标帧必须为对应尺寸的 RGBA PNG。' }
        $framePayloads += ,$frameBytes
    }
    $iconPath = Join-Path $resourceFolder 'app.ico'
    $iconStream = [System.IO.MemoryStream]::new()
    $iconWriter = [System.IO.BinaryWriter]::new($iconStream)
    try {
        $iconWriter.Write([uint16]0)
        $iconWriter.Write([uint16]1)
        $iconWriter.Write([uint16]$frameSizes.Count)
        $frameOffset = 6 + 16 * $frameSizes.Count
        for ($frameIndex = 0; $frameIndex -lt $frameSizes.Count; $frameIndex++) {
            $directorySize = if ($frameSizes[$frameIndex] -eq 256) { 0 } else { $frameSizes[$frameIndex] }
            $iconWriter.Write([byte]$directorySize)
            $iconWriter.Write([byte]$directorySize)
            $iconWriter.Write([byte]0)
            $iconWriter.Write([byte]0)
            $iconWriter.Write([uint16]1)
            $iconWriter.Write([uint16]32)
            $iconWriter.Write([uint32]$framePayloads[$frameIndex].Length)
            $iconWriter.Write([uint32]$frameOffset)
            $frameOffset += $framePayloads[$frameIndex].Length
        }
        foreach ($framePayload in $framePayloads) { $iconWriter.Write([byte[]]$framePayload) }
        $iconWriter.Flush()
        [System.IO.File]::WriteAllBytes($iconPath, $iconStream.ToArray())
    } finally { $iconWriter.Dispose(); $iconStream.Dispose() }
    Write-Host '正式 ICO 已生成：./assets/app-icon/release-v2/app.ico（16、20、24、32、40、48、64、128、256 像素，32 位透明）。'
} finally { Pop-Location }
