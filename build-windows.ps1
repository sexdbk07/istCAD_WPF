$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$project = Join-Path $root "iStCAD_1-WPF.csproj"
$solution = Join-Path $root "iStCAD_1.sln"
$publishDir = Join-Path $root "artifacts\publish"
$zipPath = Join-Path $root "artifacts\iStCAD_1-v1.0.42-win-x64.zip"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK 8 is required. Install the .NET 8 SDK and Windows Desktop build components, then rerun this script."
}
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^8\.' })) {
    throw ".NET 8 SDK was not found. Installed SDKs: $($sdks -join ', ')"
}
if (-not (Test-Path $project) -or -not (Test-Path $solution)) {
    throw "Run this script from the extracted iStCAD_1 source folder."
}

Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $zipPath) | Out-Null

Write-Host "Publishing iStCAD_1 for Windows x64 (self-contained)..."
& dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    --output $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $publishDir "iStCAD_1.exe"
if (-not (Test-Path $exe)) { throw "Publish completed but iStCAD_1.exe was not found." }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host ""
Write-Host "BUILD SUCCESS"
Write-Host "EXE: $exe"
Write-Host "ZIP: $zipPath"
Write-Host "EXE SHA256: $hash"
