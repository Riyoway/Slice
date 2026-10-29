$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $root 'publish.ps1')

$isccCandidates = @(
  (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
  'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
  'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
  $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
  if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) {
  throw 'Inno Setup 6 was not found. Install it with: winget install --id JRSoftware.InnoSetup -e'
}

$installerDir = Join-Path $root 'artifacts\installer'
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

Push-Location $root
try {
  & $iscc (Join-Path $root 'installer.iss')
  if ($LASTEXITCODE -ne 0) { throw 'Inno Setup build failed.' }
}
finally {
  Pop-Location
}

Write-Host "Installer: $(Join-Path $installerDir 'Slice-Setup-x64.exe')"
