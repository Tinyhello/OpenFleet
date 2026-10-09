# 重新建置共用契約 OpenFleet.Contracts，並複製到 Unity 專案。
# 修改 backend/src/OpenFleet.Contracts 之後執行：powershell -File unity/sync-contracts.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'backend/src/OpenFleet.Contracts'
$target = Join-Path $PSScriptRoot 'Openfleet/Assets/OpenFleet/Plugins'

dotnet build $project -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item (Join-Path $project 'bin/Release/netstandard2.1/OpenFleet.Contracts.dll') $target -Force
Write-Host "Copied OpenFleet.Contracts.dll to $target"
