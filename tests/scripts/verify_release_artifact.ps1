param(
    [Parameter(Mandatory = $true)][string]$PlayerPath,
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [Parameter(Mandatory = $true)][string]$HelperArchive,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$Version = '1.9.8'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$expectedFileVersion = $Version + '.0'
function Get-ByteHash([byte[]]$bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}
function Get-Embedded([Reflection.Assembly]$assembly, [string]$name) {
    $stream = $assembly.GetManifestResourceStream($name)
    if ($null -eq $stream) { throw "Missing embedded resource: $name" }
    $memory = New-Object IO.MemoryStream
    try { $stream.CopyTo($memory); return ,$memory.ToArray() }
    finally { $stream.Dispose(); $memory.Dispose() }
}
function Get-FileRecord([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Required build input is missing: $full" }
    $item = Get-Item -LiteralPath $full
    return [ordered]@{ path=$full; bytes=$item.Length; sha256=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash }
}

$playerFull = [IO.Path]::GetFullPath($PlayerPath)
$setupFull = [IO.Path]::GetFullPath($SetupPath)
$helperFull = [IO.Path]::GetFullPath($HelperArchive)
$playerBytes = [IO.File]::ReadAllBytes($playerFull)
$setupBytes = [IO.File]::ReadAllBytes($setupFull)
$playerAssembly = [Reflection.Assembly]::Load($playerBytes)
$setupAssembly = [Reflection.Assembly]::Load($setupBytes)
$embeddedPlayerBytes = Get-Embedded $setupAssembly 'Player'
$embeddedPlayer = [Reflection.Assembly]::Load($embeddedPlayerBytes)
$sourceConfig = [IO.File]::ReadAllBytes((Join-Path $projectRoot 'app\app.config'))
$embeddedConfig = Get-Embedded $setupAssembly 'Config'
$sourceGuide = [IO.File]::ReadAllBytes((Join-Path $projectRoot 'packaging\installation-guide.en.txt'))
$embeddedGuide = Get-Embedded $setupAssembly 'Guide'
$sourceDriver = [IO.File]::ReadAllBytes((Join-Path $projectRoot 'vendor\vb-cable\VBCABLE_Driver_Pack45.zip'))
$embeddedDriver = Get-Embedded $setupAssembly 'Driver'
$sourceBootstrapper = [IO.File]::ReadAllBytes((Join-Path $projectRoot 'vendor\webview2\bootstrapper\MicrosoftEdgeWebview2Setup.exe'))
$embeddedBootstrapper = Get-Embedded $setupAssembly 'WebBootstrapper'
$sourceHtml = [IO.File]::ReadAllBytes((Join-Path $projectRoot 'app\ui\index.html'))
$embeddedHtml = Get-Embedded $embeddedPlayer 'Interface'
$separateHtml = Get-Embedded $playerAssembly 'Interface'
$embeddedHelper = Get-Embedded $embeddedPlayer 'CryptoHelperArchive'
$helperFileBytes = [IO.File]::ReadAllBytes($helperFull)
$embeddedSetupResources = @($setupAssembly.GetManifestResourceNames() | Sort-Object)
$requiredSetupResources = @('Player','Config','Guide','Driver','WebBootstrapper','Uninstaller','WebCore','WebForms','WebLoader64')
foreach ($resourceName in $requiredSetupResources) {
    if ($embeddedSetupResources -notcontains $resourceName) { throw "Setup candidate is missing resource $resourceName" }
}

$sourceInputs = @(
    (Join-Path $projectRoot 'build.ps1'),
    (Join-Path $projectRoot 'packaging\build-installer.ps1'),
    (Join-Path $projectRoot 'packaging\Installer.cs'),
    (Join-Path $projectRoot 'packaging\installation-guide.en.txt'),
    (Join-Path $projectRoot 'app\ui\index.html'),
    (Join-Path $projectRoot 'app\app.config'),
    (Join-Path $projectRoot 'app\app.manifest'),
    (Join-Path $projectRoot 'app\assets\BgmHotkey.ico'),
    (Join-Path $projectRoot 'licenses\MusicConverter-NOTICES.txt'),
    (Join-Path $projectRoot 'vendor\webview2\sdk\net462\Microsoft.Web.WebView2.Core.dll'),
    (Join-Path $projectRoot 'vendor\webview2\sdk\net462\Microsoft.Web.WebView2.WinForms.dll'),
    (Join-Path $projectRoot 'vendor\webview2\loader\win-x64\WebView2Loader.dll'),
    (Join-Path $projectRoot 'vendor\webview2\bootstrapper\MicrosoftEdgeWebview2Setup.exe'),
    (Join-Path $projectRoot 'vendor\vb-cable\VBCABLE_Driver_Pack45.zip'),
    $helperFull
)
$sourceInputs += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'app\src') -Filter '*.cs' -File -Recurse | Sort-Object FullName | ForEach-Object FullName)
$sourceRecords = @($sourceInputs | Sort-Object -Unique | ForEach-Object {
    $fullInput = [IO.Path]::GetFullPath($_)
    $relative = $fullInput.Substring($projectRoot.Length).TrimStart('\','/')
    $record = Get-FileRecord $fullInput
    [ordered]@{ path=$relative; bytes=$record.bytes; sha256=$record.sha256 }
})

$safeProjectRoot = $projectRoot.Replace('\','/')
$head = (& git -c "safe.directory=$safeProjectRoot" -C $projectRoot rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or [String]::IsNullOrWhiteSpace($head)) { throw 'Unable to record the source Git revision.' }
$head = $head.Trim()
$status = @(& git -c "safe.directory=$safeProjectRoot" -C $projectRoot status --short)
if ($LASTEXITCODE -ne 0) { throw 'Unable to record the source Git status.' }
$playerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($playerFull).FileVersion
$setupVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($setupFull).FileVersion
$playerSignature = (Get-AuthenticodeSignature -LiteralPath $playerFull).Status.ToString()
$setupSignature = (Get-AuthenticodeSignature -LiteralPath $setupFull).Status.ToString()
$candidate = [ordered]@{
    product='Wanxiang BGM Player'
    sourceVersion=$Version
    setupFileVersion=$setupVersion
    playerFileVersion=$playerVersion
    player=$(@{ path=$playerFull; bytes=$playerBytes.Length; sha256=(Get-FileHash -LiteralPath $playerFull -Algorithm SHA256).Hash; signature=$playerSignature })
    setup=$(@{ path=$setupFull; bytes=$setupBytes.Length; sha256=(Get-FileHash -LiteralPath $setupFull -Algorithm SHA256).Hash; signature=$setupSignature })
    helperArchive=$(@{ path=$helperFull; bytes=$helperFileBytes.Length; sha256=(Get-FileHash -LiteralPath $helperFull -Algorithm SHA256).Hash })
    embedded=$(@{
        playerBytes=$embeddedPlayerBytes.Length
        playerSha256=(Get-ByteHash $embeddedPlayerBytes)
        embeddedPlayerMatchesSeparatePlayer=((Get-ByteHash $embeddedPlayerBytes) -eq (Get-ByteHash $playerBytes))
        embeddedPlayerAssemblyVersion=$embeddedPlayer.GetName().Version.ToString()
        separatePlayerAssemblyVersion=$playerAssembly.GetName().Version.ToString()
        embeddedConfigMatchesSource=((Get-ByteHash $embeddedConfig) -eq (Get-ByteHash $sourceConfig))
        embeddedGuideMatchesSource=((Get-ByteHash $embeddedGuide) -eq (Get-ByteHash $sourceGuide))
        embeddedDriverMatchesSource=((Get-ByteHash $embeddedDriver) -eq (Get-ByteHash $sourceDriver))
        embeddedBootstrapperMatchesSource=((Get-ByteHash $embeddedBootstrapper) -eq (Get-ByteHash $sourceBootstrapper))
        htmlSha256=(Get-ByteHash $embeddedHtml)
        embeddedHtmlMatchesSource=((Get-ByteHash $embeddedHtml) -eq (Get-ByteHash $sourceHtml))
        separatePlayerHtmlMatchesSource=((Get-ByteHash $separateHtml) -eq (Get-ByteHash $sourceHtml))
        helperSha256=(Get-ByteHash $embeddedHelper)
        embeddedHelperMatchesArchive=((Get-ByteHash $embeddedHelper) -eq (Get-FileHash -LiteralPath $helperFull -Algorithm SHA256).Hash)
        setupResources=$embeddedSetupResources
    })
    sourceState=@{ gitHead=$head; workingTreeDirty=($status.Count -gt 0); status=$status; inputFiles=$sourceRecords }
    remote=@{ tag="v$Version"; releaseStatus='not checked by this local build'; releaseApiCheck='not checked' }
    verification=@{ builtFromWorkingTree=$true; candidateLaunched=$false; installerRun=$false; signaturePrerequisite='No application signing certificate was available; artifacts are unsigned.' }
}
if ($playerVersion -ne $expectedFileVersion -or $setupVersion -ne $expectedFileVersion) { throw "Candidate version metadata does not match $expectedFileVersion." }
if ($candidate.embedded.embeddedPlayerAssemblyVersion -ne $expectedFileVersion -or $candidate.embedded.separatePlayerAssemblyVersion -ne $expectedFileVersion -or
    -not $candidate.embedded.embeddedPlayerMatchesSeparatePlayer -or
    -not $candidate.embedded.embeddedConfigMatchesSource -or
    -not $candidate.embedded.embeddedGuideMatchesSource -or
    -not $candidate.embedded.embeddedDriverMatchesSource -or
    -not $candidate.embedded.embeddedBootstrapperMatchesSource -or
    -not $candidate.embedded.embeddedHtmlMatchesSource -or
    -not $candidate.embedded.separatePlayerHtmlMatchesSource -or -not $candidate.embedded.embeddedHelperMatchesArchive) {
    throw 'Embedded candidate resources do not match the verified source inputs.'
}
$outputFull = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputFull
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$candidate | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputFull -Encoding UTF8
Write-Host "Candidate artifact verification passed: $outputFull"
