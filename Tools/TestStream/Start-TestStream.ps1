# 啟動 HeliVMS 開發用虛擬 RTSP 測試流（mediamtx + FFmpeg）
# 用法：powershell -ExecutionPolicy Bypass -File Tools\TestStream\Start-TestStream.ps1

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$MediaServer = Join-Path $Root "Tools\media-server\mediamtx.exe"
$Ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source

# 檢查是否已有近期啟用的測試流發佈程序
$publishers = @(Get-Process -Name ffmpeg -ErrorAction SilentlyContinue |
    Where-Object { $_.StartTime -gt (Get-Date).AddMinutes(-3) })
if ($publishers.Count -ge 1) {
    Write-Host "已有測試流發佈程序在執行，請先執行 Stop-TestStream.ps1"
    exit 1
}

# 1. 啟動 mediamtx（若未運行）
if (-not (Get-Process -Name mediamtx -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $MediaServer -WindowStyle Hidden -WorkingDirectory (Split-Path $MediaServer)
    Start-Sleep -Seconds 2
    Write-Host "mediamtx 已啟動 (RTSP :8554)"
} else {
    Write-Host "mediamtx 已在運行"
}

# 2. 定義雙流發佈參數
$specs = @(
    @{
        Name = "main"
        Url  = "rtsp://127.0.0.1:8554/main"
        Args = @(
            "-re", "-stream_loop", "-1",
            "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=15",
            "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=44100",
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-pix_fmt", "yuv420p",
            "-g", "30", "-keyint_min", "30", "-sc_threshold", "0",
            "-c:a", "aac", "-b:a", "128k",
            "-f", "rtsp", "-rtsp_transport", "tcp", "rtsp://127.0.0.1:8554/main"
        )
    },
    @{
        Name = "sub"
        Url  = "rtsp://127.0.0.1:8554/sub"
Args = @(
            "-re", "-stream_loop", "-1",
            "-f", "lavfi", "-i", "testsrc=size=640x360:rate=15",
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-pix_fmt", "yuv420p",
            "-g", "30", "-keyint_min", "30", "-sc_threshold", "0",
            "-an",
            "-f", "rtsp", "-rtsp_transport", "tcp", "rtsp://127.0.0.1:8554/sub"
        )
    }
)

foreach ($spec in $specs) {
    Start-Process -FilePath $Ffmpeg -ArgumentList $spec.Args -WindowStyle Hidden
    Write-Host "已發布 [$($spec.Name)] $($spec.Url)"
    Start-Sleep -Milliseconds 500
}

Write-Host "測試流就緒。"
Write-Host "  主流: rtsp://127.0.0.1:8554/main (1280x720 H.264 + AAC)"
Write-Host "  次流: rtsp://127.0.0.1:8554/sub (640x360 H.264)"