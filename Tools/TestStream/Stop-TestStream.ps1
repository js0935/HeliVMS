# 停止 HeliVMS 虛擬 RTSP 測試流（含 mediamtx）
$ErrorActionPreference = "SilentlyContinue"
Get-Process -Name ffmpeg | Stop-Process -Force
Stop-Process -Name mediamtx -Force
Write-Host "測試流已停止。"