param(
    [string]$HelperArchive = 'local-only\build-staging\encrypted-audio-helper-1.9.8.zip'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$testRoot = Join-Path $projectRoot ('local-only\test-build\import-pipeline-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$helperArchivePath = if ([System.IO.Path]::IsPathRooted($HelperArchive)) { $HelperArchive } else { Join-Path $projectRoot $HelperArchive }
$helperRoot = Join-Path $testRoot 'helper'
Expand-Archive -LiteralPath $helperArchivePath -DestinationPath $helperRoot
$fixtureRoot = Join-Path $testRoot 'fixtures'
python (Join-Path $projectRoot 'tests\integration\import\create_import_fault_fixtures.py') $fixtureRoot
if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture generation failed.' }

$player = Join-Path $testRoot 'BgmHotkey.ImportTest.exe'
$smoke = Join-Path $testRoot 'EncryptedAudioImportSmoke.exe'
$taskId = [Guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path $testRoot ('test-data\import-work\job_' + $taskId)
New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler was not found.' }
$sdk = Join-Path $projectRoot 'vendor\webview2\sdk\net462'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'app\src') -Filter '*.cs' -File -Recurse | Sort-Object FullName | ForEach-Object FullName)
$references = @('/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Xml.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll','/reference:System.Web.Extensions.dll',("/reference:" + (Join-Path $sdk 'Microsoft.Web.WebView2.Core.dll')),("/reference:" + (Join-Path $sdk 'Microsoft.Web.WebView2.WinForms.dll')))
$resources = @(("/resource:" + (Join-Path $projectRoot 'app\ui\index.html') + ',Interface'),("/resource:" + $helperArchivePath + ',CryptoHelperArchive'))
$notice = Join-Path $projectRoot 'licenses\MusicConverter-NOTICES.txt'
$resources += "/resource:$notice,MusicConverterNotice"
& $compiler /nologo /langversion:5 /codepage:65001 /target:exe /platform:x64 /optimize+ /define:TEST_BUILD @references @resources "/out:$player" @sources
if ($LASTEXITCODE -ne 0) { throw 'TEST_BUILD player compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'app\app.config') -Destination ($player + '.config')
Copy-Item -LiteralPath (Join-Path $sdk 'Microsoft.Web.WebView2.Core.dll') -Destination $testRoot
Copy-Item -LiteralPath (Join-Path $sdk 'Microsoft.Web.WebView2.WinForms.dll') -Destination $testRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'vendor\webview2\loader\win-x64\WebView2Loader.dll') -Destination $testRoot

& $compiler /nologo /langversion:5 /codepage:65001 /target:exe /reference:System.dll /reference:System.Core.dll "/out:$smoke" (Join-Path $projectRoot 'tests\integration\import\encrypted_audio_import_smoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Encrypted import smoke compilation failed.' }
$valid = Join-Path $fixtureRoot '有效中文 QMCv2.mflac'
$keyless = Join-Path $fixtureRoot '缺密钥 MusicEx.mflac'
& $smoke $player $valid $keyless $taskDirectory
if ($LASTEXITCODE -ne 0) { throw "Encrypted import integration smoke failed with exit code $LASTEXITCODE." }
Write-Host "Encrypted import isolated smoke passed. TEST_BUILD output (disposable): $testRoot"
