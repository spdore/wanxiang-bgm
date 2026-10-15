param(
    [Parameter(Mandatory = $true)]
    [string]$PlayerPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$player = [System.IO.Path]::GetFullPath($PlayerPath)
if (-not (Test-Path -LiteralPath $player -PathType Leaf)) { throw "Player executable was not found: $player" }
$buildRoot = Join-Path $projectRoot 'local-only\test-build'
$testOutput = Join-Path $buildRoot ('import-timeout-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
$compilerCandidates = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'The .NET Framework C# compiler was not found.' }

$fixture = Join-Path $testOutput 'ImportProcessFixture.exe'
$smoke = Join-Path $testOutput 'ImportProcessIsolationSmoke.exe'
& $compiler /nologo /langversion:5 /target:exe "/out:$fixture" (Join-Path $projectRoot 'tests\integration\import\ImportProcessFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Import process fixture compilation failed.' }
& $compiler /nologo /langversion:5 /target:exe /reference:System.dll /reference:System.Core.dll "/out:$smoke" (Join-Path $projectRoot 'tests\integration\import\ImportProcessIsolationSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Import process smoke compilation failed.' }

& $smoke $player $fixture
if ($LASTEXITCODE -ne 0) { throw "Import process isolation tests failed with exit code $LASTEXITCODE." }
Write-Host "Import timeout tests passed. Test binaries: $testOutput"
