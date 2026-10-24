# 重新建置共用程式庫（OpenFleet.Contracts、OpenFleet.Layout），並複製到 Unity 專案。
# 修改 backend/src/OpenFleet.Contracts 或 backend/src/OpenFleet.Layout 之後執行：powershell -File unity/sync-contracts.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $PSScriptRoot 'Openfleet/Assets/OpenFleet/Plugins'

foreach ($name in @('OpenFleet.Contracts', 'OpenFleet.Layout')) {
    $project = Join-Path $root "backend/src/$name"

    dotnet build $project -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Copy-Item (Join-Path $project "bin/Release/netstandard2.1/$name.dll") $target -Force
    Write-Host "Copied $name.dll to $target"
}
