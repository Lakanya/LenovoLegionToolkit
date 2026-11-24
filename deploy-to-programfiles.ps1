#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$source = 'C:\Users\natha\.gemini\antigravity\scratch\LenovoLegionToolkit\BuildLLT\publish'
$target = 'C:\Program Files\LenovoLegionToolkit'
$backup = 'C:\Program Files\LenovoLegionToolkit.bak'

Write-Host "Stopping running Lenovo Legion Toolkit..."
Stop-Process -Name 'Lenovo Legion Toolkit' -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

if (-not (Test-Path $backup)) {
    Write-Host "Creating backup at $backup..."
    Copy-Item -Path $target -Destination $backup -Recurse -Force
}

Write-Host "Copying optimized ReadyToRun v2.38.3.0 build to $target..."
Copy-Item -Path "$source\*" -Destination $target -Recurse -Force

# Disable UpdateChecker overwriting custom fork unless manually triggered
$updateCfgPath = "$env:LOCALAPPDATA\LenovoLegionToolkit\update_settings.json"
if (Test-Path $updateCfgPath) {
    Write-Host "Keeping update_settings intact (driver/BIOS auto-checker still runs)."
}

Write-Host "Starting Lenovo Legion Toolkit..."
Start-Process -FilePath "$target\Lenovo Legion Toolkit.exe"
Write-Host "Done! Optimized Lenovo Legion Toolkit v2.38.3.0 is now live."
