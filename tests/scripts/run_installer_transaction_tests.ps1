$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$testRoot = Join-Path $projectRoot ('local-only\test-build\installer-transaction-' + [Guid]::NewGuid().ToString('N'))
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler was not found.' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$harness = Join-Path $testRoot 'InstallerTransactionHarness.exe'
$references = @('/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Xml.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll')
& $compiler /nologo /langversion:5 /codepage:65001 /target:exe /platform:anycpu @references "/main:InstallerTransactionHarness" "/out:$harness" (Join-Path $projectRoot 'packaging\Installer.cs') (Join-Path $projectRoot 'tests\integration\installer\InstallerTransactionHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installer transaction harness compilation failed.' }
& $harness
if ($LASTEXITCODE -ne 0) { throw "Installer transaction tests failed with exit code $LASTEXITCODE." }
Write-Host "Installer transaction checks passed. Disposable fixture root: $testRoot"
