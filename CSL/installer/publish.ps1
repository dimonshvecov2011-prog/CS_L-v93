$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\publish\win-x64'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $out -Force | Out-Null
dotnet publish (Join-Path $root 'src\CssLauncher.App\CssLauncher.App.csproj') `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false -o $out
Write-Host "CS:L publish готов: $out"
Write-Host "Далее установите Inno Setup и запустите: iscc installer\CSL-Setup.iss"
