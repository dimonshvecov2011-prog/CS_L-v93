$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\CssLauncher.App\CssLauncher.App.csproj'
$out = Join-Path $root 'artifacts\publish\win-x64'

if (-not (Test-Path $project -PathType Leaf)) {
  throw "Не найден проект лаунчера: $project"
}

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $out -Force | Out-Null

dotnet publish $project `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false -o $out
$publishExitCode = $LASTEXITCODE
if ($publishExitCode -ne 0) {
  throw "dotnet publish завершился с кодом $publishExitCode."
}

$launcherExe = Join-Path $out 'CSL.exe'
if (-not (Test-Path $launcherExe -PathType Leaf)) {
  throw "Публикация завершилась без исполняемого файла: $launcherExe"
}

$publishedFiles = Get-ChildItem $out -File -Recurse
if ($publishedFiles.Count -eq 0) {
  throw "Папка публикации пуста: $out"
}

Write-Host "CS:L publish готов: $out ($($publishedFiles.Count) файлов)"
Write-Host "Далее установите Inno Setup и запустите: iscc installer\CSL-Setup.iss"
