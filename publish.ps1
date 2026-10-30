$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $root 'artifacts\portable\Slice-win-x64'
$zipPath = Join-Path $root 'artifacts\portable\Slice-win-x64.zip'

# Always rebuild WinUI/XAML generated artifacts from a clean state.
Remove-Item (Join-Path $root 'bin'), (Join-Path $root 'obj') -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

dotnet publish (Join-Path $root 'Slice.csproj') `
  -c Release `
  -r win-x64 `
  -p:Platform=x64 `
  --self-contained true `
  -o $publishDir

if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Portable folder: $publishDir"
Write-Host "Portable ZIP:    $zipPath"
