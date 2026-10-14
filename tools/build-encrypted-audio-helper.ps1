param(
    [Parameter(Mandatory = $true)]
    [string]$OutputArchive,
    [string]$PythonPath = '',
    [string]$DependencyPath = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$archivePath = [System.IO.Path]::GetFullPath($OutputArchive)
$localOnlyRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'local-only'))
if (-not $archivePath.StartsWith($localOnlyRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The helper build archive must stay under local-only/.'
}
if (Test-Path -LiteralPath $archivePath) { throw "Refusing to overwrite helper archive: $archivePath" }

$pythonExe = $PythonPath
if ([String]::IsNullOrWhiteSpace($pythonExe)) {
    $pythonCommand = Get-Command python -ErrorAction SilentlyContinue
    if (-not $pythonCommand) { throw 'CPython 3.14.3 x64 is required only to build the bundled helper.' }
    $pythonExe = $pythonCommand.Source
}
if (-not (Test-Path -LiteralPath $pythonExe)) { throw "Build Python was not found: $pythonExe" }
$previousPythonPath = $env:PYTHONPATH
if (-not [String]::IsNullOrWhiteSpace($DependencyPath)) { $env:PYTHONPATH = $DependencyPath }
try {
$versions = & $pythonExe -c "import importlib.metadata as m,platform,sys; names=['pycryptodome','pyinstaller','pyinstaller-hooks-contrib','altgraph','packaging','pefile','pywin32-ctypes']; print('|'.join([f'{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}',platform.architecture()[0]]+[m.version(name) for name in names]))"
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the helper build Python environment.' }
$parts = $versions.Trim().Split('|')
$expected = @('3.14.3','64bit','3.20.0','6.22.3','2026.8','0.17.5','23.1','2023.2.7','0.2.3')
if ($parts.Count -ne $expected.Count) { throw "Unexpected helper build environment: $versions" }
for ($versionIndex = 0; $versionIndex -lt $expected.Count; $versionIndex++) {
    if ($parts[$versionIndex] -ne $expected[$versionIndex]) {
        throw "Expected the pinned CPython/PyInstaller/PyCryptodome build environment $($expected -join '|'); found: $versions"
    }
}

$stageRoot = Split-Path -Parent $archivePath
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null
$stage = Join-Path $stageRoot ('helper-build-' + [Guid]::NewGuid().ToString('N'))
$dist = Join-Path $stage 'dist'
$work = Join-Path $stage 'work'
$spec = Join-Path $stage 'spec'
$vendor = Join-Path $projectRoot 'vendor\music-geshizhuanhuan'
$script = Join-Path $projectRoot 'tools\encrypted_audio_helper.py'
$asset = Join-Path $vendor 'assets\kugou_key.xz'
$arguments = @(
    '-m', 'PyInstaller', '--noconfirm', '--clean', '--onedir', '--console',
    '--name', 'BgmCryptoHelper', '--distpath', $dist, '--workpath', $work,
    '--specpath', $spec, '--paths', $vendor, '--add-data', ($asset + ';assets'), $script
)
Push-Location $projectRoot
try {
    & $pythonExe @arguments
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }

$helperFolder = Join-Path $dist 'BgmCryptoHelper'
$helperExe = Join-Path $helperFolder 'BgmCryptoHelper.exe'
if (-not (Test-Path -LiteralPath $helperExe)) { throw 'The helper executable was not produced.' }
Compress-Archive -Path (Join-Path $helperFolder '*') -DestinationPath $archivePath -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $archivePath)) { throw 'The helper archive was not created.' }
Write-Host "Built helper archive: $archivePath"
}
finally { $env:PYTHONPATH = $previousPythonPath }
