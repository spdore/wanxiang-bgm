param(
    [string]$HelperArchive = 'local-only\build-staging\encrypted-audio-helper-1.9.8.zip',
    [string]$NodePath = '',
    [string]$PlaywrightPath = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$testRoot = Join-Path $projectRoot ('local-only\test-build\project-regression-' + [Guid]::NewGuid().ToString('N'))
$archivePath = if ([IO.Path]::IsPathRooted($HelperArchive)) { $HelperArchive } else { Join-Path $projectRoot $HelperArchive }
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw "Missing pinned helper archive: $archivePath" }
$compilerCandidates = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'The .NET Framework C# compiler was not found.' }
New-Item -ItemType Directory -Path $testRoot,(Join-Path $testRoot 'bgm') -Force | Out-Null
$player = Join-Path $testRoot 'BgmHotkey.TestBuild.exe'
$harness = Join-Path $testRoot 'ProjectRegressionHarness.exe'
$sdk = Join-Path $projectRoot 'vendor\webview2\sdk\net462'
$loader = Join-Path $projectRoot 'vendor\webview2\loader\win-x64\WebView2Loader.dll'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'app\src') -Filter '*.cs' -File -Recurse | Sort-Object FullName | ForEach-Object FullName)
$references = @(
    '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',
    '/reference:System.Xml.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll',
    '/reference:System.Web.Extensions.dll',("/reference:" + (Join-Path $sdk 'Microsoft.Web.WebView2.Core.dll')),
    ("/reference:" + (Join-Path $sdk 'Microsoft.Web.WebView2.WinForms.dll'))
)
$resources = @(
    ("/resource:" + (Join-Path $projectRoot 'app\ui\index.html') + ',Interface'),
    ("/resource:" + $archivePath + ',CryptoHelperArchive'),
    ("/resource:" + (Join-Path $projectRoot 'licenses\MusicConverter-NOTICES.txt') + ',MusicConverterNotice')
)
& $compiler /nologo /langversion:5 /codepage:65001 /target:winexe /platform:x64 /optimize+ /define:TEST_BUILD @references @resources "/out:$player" @sources
if ($LASTEXITCODE -ne 0) { throw 'TEST_BUILD player compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'app\app.config') -Destination ($player + '.config')
Copy-Item -LiteralPath (Join-Path $sdk 'Microsoft.Web.WebView2.Core.dll') -Destination $testRoot
Copy-Item -LiteralPath (Join-Path $sdk 'Microsoft.Web.WebView2.WinForms.dll') -Destination $testRoot
Copy-Item -LiteralPath $loader -Destination $testRoot

& $compiler /nologo /langversion:5 /codepage:65001 /target:exe /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll "/reference:$player" "/out:$harness" (Join-Path $projectRoot 'tests\unit\ProjectRegressionHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Project regression harness compilation failed.' }
& $harness $player
if ($LASTEXITCODE -ne 0) { throw "Core regression tests failed with exit code $LASTEXITCODE. Isolated build remains at $testRoot" }

$node = $NodePath
if ([String]::IsNullOrWhiteSpace($node)) {
    $nodeCommand = Get-Command node -ErrorAction SilentlyContinue
    if ($nodeCommand) { $node = $nodeCommand.Source }
}
if ([String]::IsNullOrWhiteSpace($node)) { throw 'Node.js is required to run the browser regression suite.' }
if ([String]::IsNullOrWhiteSpace($PlaywrightPath)) {
    $playwrightCandidates = @(
        (Join-Path $projectRoot 'node_modules\playwright'),
        (Join-Path $projectRoot 'tests\node_modules\playwright')
    )
    $npmCommand = Get-Command npm -ErrorAction SilentlyContinue
    if ($npmCommand) {
        $globalNodeModules = & $npmCommand.Source root -g 2>$null
        if (-not [String]::IsNullOrWhiteSpace($globalNodeModules)) {
            $playwrightCandidates += Join-Path $globalNodeModules 'playwright'
        }
    }
    foreach ($candidate in $playwrightCandidates) {
        if (Test-Path -LiteralPath $candidate -PathType Container) { $PlaywrightPath = $candidate; break }
    }
}
if ([String]::IsNullOrWhiteSpace($PlaywrightPath)) { throw 'Playwright was not found. Install it locally or pass -PlaywrightPath.' }
if (-not [String]::IsNullOrWhiteSpace($PlaywrightPath)) { $env:BGM_PLAYWRIGHT_MODULE = $PlaywrightPath }
$env:BGM_UI_EXECUTABLE_PATH = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
& $node (Join-Path $projectRoot 'tests\ui\ui_regression.cjs') (Join-Path $projectRoot 'app\ui\index.html')
if ($LASTEXITCODE -ne 0) { throw "UI regression tests failed with exit code $LASTEXITCODE." }

& (Join-Path $PSScriptRoot 'run_import_timeout_tests.ps1') -PlayerPath $player
if ($LASTEXITCODE -ne 0) { throw 'Import timeout regression tests failed.' }
& (Join-Path $PSScriptRoot 'run_encrypted_import_isolated_smoke.ps1') -HelperArchive $archivePath
if ($LASTEXITCODE -ne 0) { throw 'Encrypted import integration smoke failed.' }
& (Join-Path $PSScriptRoot 'run_installer_transaction_tests.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Installer transaction regression tests failed.' }
Write-Host "Project regression suite passed. Isolated TEST_BUILD: $testRoot"
