# Builds both distributions of the app.
#
#   publish\                      framework-dependent (small; needs the .NET 8 Desktop Runtime installed)
#   dist\MiningAssistant\         self-contained (larger; runs on any Windows 10/11 x64 PC, nothing to install)
#   dist\MiningAssistant.zip      the self-contained folder zipped, to copy to another computer
#
# Existing config\ folders (settings, calibration) inside the outputs are kept.
# Usage:  powershell -ExecutionPolicy Bypass -File publish.ps1

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$app = 'src\MiningAssistant.App'

if (Get-Process MiningAssistant -ErrorAction SilentlyContinue) {
    throw 'MiningAssistant is running. Close it (control panel → Exit app) and run this script again.'
}

dotnet publish $app -c Release -r win-x64 --self-contained false -o publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'framework-dependent publish failed' }

dotnet publish $app -c Release -r win-x64 --self-contained true -o dist\MiningAssistant -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'self-contained publish failed' }

# Zip without any local config / diagnostics, so the other PC starts clean.
$zip = 'dist\MiningAssistant.zip'
if (Test-Path $zip) { Remove-Item -LiteralPath $zip }
$items = Get-ChildItem dist\MiningAssistant | Where-Object { $_.Name -notin 'config', 'diagnostics', 'unknown_signatures' }
Compress-Archive -Path $items.FullName -DestinationPath $zip

"publish\MiningAssistant.exe         (needs .NET 8 Desktop Runtime)"
"dist\MiningAssistant\MiningAssistant.exe   (self-contained)"
"{0}  ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
