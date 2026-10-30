# Builds installer\out\HaloBar-Setup-<version>.exe: a self-contained HaloBar (no .NET needed on
# the target PC) packed by Inno Setup. Run from anywhere:  powershell -File installer\build.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\HaloBar\HaloBar.csproj"
$app = Join-Path $PSScriptRoot "out\app"

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "HaloBar $version"

if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $app
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Get-ChildItem $app -Filter *.pdb -Recurse | Remove-Item

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found: winget install JRSoftware.InnoSetup" }
& $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot "HaloBar.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Get-ChildItem (Join-Path $PSScriptRoot "out") -Filter "HaloBar-Setup-*.exe" | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
