param(
    [string]$OutputName = 'WanxiangBgmSetup-1.9.8.exe',
    [string]$PythonPath = '',
    [string]$PythonDependencyPath = '',
    [string]$CryptoHelperArchive = '',
    [string]$PlayerPath = '',
    [string]$GuideName = 'INSTALLATION-1.9.8.txt'
)

$ErrorActionPreference = 'Stop'
$Version = '1.9.8'
if ([System.IO.Path]::GetFileName($OutputName) -ne $OutputName -or [System.IO.Path]::GetExtension($OutputName) -ne '.exe') {
    throw 'OutputName must be a .exe file name without a path.'
}
if ([System.IO.Path]::GetFileName($GuideName) -ne $GuideName -or [System.IO.Path]::GetExtension($GuideName) -ne '.txt') {
    throw 'GuideName must be a .txt file name without a path.'
}
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskBuildRoot = Join-Path $taskRoot 'local-only\build-staging'
$taskBuild = Join-Path $taskBuildRoot ('.build-' + $Version + '-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$taskOutput = Join-Path $taskRoot 'release\1.9.8'
$taskCompiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Path $taskBuildRoot,$taskBuild,$taskOutput -Force | Out-Null
$taskPlayer = Join-Path $taskBuild ('BgmHotkey_' + $Version + '.exe')
$taskSetup = Join-Path $taskOutput $OutputName
$taskGuideOutput = Join-Path $taskOutput $GuideName
if (Test-Path -LiteralPath $taskSetup) { throw "Refusing to overwrite existing installer: $taskSetup" }
$taskHelperArchive = Join-Path $taskBuild 'encrypted-audio-helper.zip'
if (Test-Path -LiteralPath $taskGuideOutput) { throw "Refusing to overwrite existing installation guide: $taskGuideOutput" }
$taskHelperBuilder = Join-Path $taskRoot 'tools\build-encrypted-audio-helper.ps1'
if (-not [String]::IsNullOrWhiteSpace($CryptoHelperArchive)) {
    $helperSource = if ([System.IO.Path]::IsPathRooted($CryptoHelperArchive)) { [System.IO.Path]::GetFullPath($CryptoHelperArchive) } else { [System.IO.Path]::GetFullPath((Join-Path $taskRoot $CryptoHelperArchive)) }
    $helperRoot = [System.IO.Path]::GetFullPath((Join-Path $taskRoot 'local-only')) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $helperSource.StartsWith($helperRoot, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $helperSource -PathType Leaf)) {
        throw 'CryptoHelperArchive must be an existing archive under local-only/.'
    }
    Copy-Item -LiteralPath $helperSource -Destination $taskHelperArchive
}
else {
$taskHelperArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$taskHelperBuilder,'-OutputArchive',$taskHelperArchive)
if (-not [String]::IsNullOrWhiteSpace($PythonPath)) { $taskHelperArguments += @('-PythonPath',$PythonPath) }
if (-not [String]::IsNullOrWhiteSpace($PythonDependencyPath)) { $taskHelperArguments += @('-DependencyPath',$PythonDependencyPath) }
& powershell.exe @taskHelperArguments
if ($LASTEXITCODE -ne 0) { throw 'Encrypted-audio helper build failed.' }
}
$taskSource = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'app\src') -Filter *.cs -File -Recurse | Sort-Object FullName | ForEach-Object FullName)
$taskStandalonePlayer = ''
if (-not [String]::IsNullOrWhiteSpace($PlayerPath)) {
    $taskStandalonePlayer = if ([IO.Path]::IsPathRooted($PlayerPath)) { [IO.Path]::GetFullPath($PlayerPath) } else { [IO.Path]::GetFullPath((Join-Path $taskRoot $PlayerPath)) }
    if (-not (Test-Path -LiteralPath $taskStandalonePlayer -PathType Leaf)) { throw "PlayerPath does not exist: $taskStandalonePlayer" }
    $standaloneVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskStandalonePlayer).FileVersion
    $standaloneAssembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($taskStandalonePlayer))
    if ($standaloneVersion -ne ($Version + '.0') -or $standaloneAssembly.GetName().Version.ToString() -ne ($Version + '.0')) {
        throw "PlayerPath must be a $Version public build: $taskStandalonePlayer"
    }
}
$taskConverterNotice = Join-Path $taskRoot 'licenses\MusicConverter-NOTICES.txt'
if (-not (Test-Path -LiteralPath $taskConverterNotice -PathType Leaf)) { throw 'Missing converter third-party notices.' }
$taskReferences = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$taskCommon = @('/nologo','/langversion:5','/codepage:65001','/target:winexe','/platform:anycpu','/optimize+',('/win32manifest:' + (Join-Path $taskRoot 'app\app.manifest')),('/win32icon:' + (Join-Path $taskRoot 'app\assets\BgmHotkey.ico')))
$taskX64Common = @('/nologo','/langversion:5','/codepage:65001','/target:winexe','/platform:x64','/optimize+',('/win32manifest:' + (Join-Path $taskRoot 'app\app.manifest')),('/win32icon:' + (Join-Path $taskRoot 'app\assets\BgmHotkey.ico')))
$taskSdk = Join-Path $taskRoot 'vendor\webview2\sdk\net462'
$taskWebReferences = @('/r:System.Web.Extensions.dll', "/r:$(Join-Path $taskSdk 'Microsoft.Web.WebView2.Core.dll')", "/r:$(Join-Path $taskSdk 'Microsoft.Web.WebView2.WinForms.dll')", "/resource:$(Join-Path $taskRoot 'app\ui\index.html'),Interface")
$taskWebReferences += "/resource:$taskHelperArchive,CryptoHelperArchive"
$taskWebReferences += "/resource:$taskConverterNotice,MusicConverterNotice"
if ([String]::IsNullOrWhiteSpace($taskStandalonePlayer)) {
    & $taskCompiler @taskX64Common @taskReferences @taskWebReferences /define:PUBLIC_RELEASE "/out:$taskPlayer" @taskSource
    if ($LASTEXITCODE -ne 0) { throw 'Player compilation failed.' }
}
else {
    $taskPlayer = $taskStandalonePlayer
}
$taskConfig = Join-Path $taskRoot 'app\app.config'
$taskGuide = Join-Path $PSScriptRoot 'installation-guide.en.txt'
$taskInstallerSource = Join-Path $PSScriptRoot 'Installer.cs'
if (-not [Environment]::Is64BitOperatingSystem) { throw 'The share installer targets 64-bit Windows.' }
$taskDriver = Join-Path $taskBuild 'VB-CABLE.zip'
$taskDriverArchive = Join-Path $taskRoot 'vendor\vb-cable\VBCABLE_Driver_Pack45.zip'
if (Test-Path -LiteralPath $taskDriverArchive) { Copy-Item -LiteralPath $taskDriverArchive -Destination $taskDriver }
else {
    $taskDriverFolder = Join-Path $taskRoot 'local-only\vendor-cache\vb-cable-original'
    if (-not (Test-Path -LiteralPath $taskDriverFolder)) { throw 'Missing the official VB-CABLE driver package.' }
    Compress-Archive -LiteralPath $taskDriverFolder -DestinationPath $taskDriver -Force
}
$taskDriverCheck = Join-Path $taskBuild 'driver-check'
Expand-Archive -LiteralPath $taskDriver -DestinationPath $taskDriverCheck -Force
foreach ($taskDriverName in @('VBCABLE_Setup_x64.exe','VBCABLE_Setup.exe')) {
    $taskDriverSetup = Get-ChildItem -LiteralPath $taskDriverCheck -Filter $taskDriverName -Recurse | Select-Object -First 1
    if (-not $taskDriverSetup) { throw "The driver archive is missing $taskDriverName." }
    $taskDriverSignature = Get-AuthenticodeSignature -LiteralPath $taskDriverSetup.FullName
    if ($taskDriverSignature.Status -ne 'Valid' -or $taskDriverSignature.SignerCertificate.Subject -notmatch 'BUREL VINCENT') { throw "VB-CABLE signature validation failed: $taskDriverName." }
}
$taskBootstrapper = Join-Path $taskRoot 'vendor\webview2\bootstrapper\MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path -LiteralPath $taskBootstrapper)) { throw 'Download the official Microsoft WebView2 Evergreen Bootstrapper from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and place it at vendor/webview2/bootstrapper/MicrosoftEdgeWebview2Setup.exe.' }
$taskBootstrapperSignature = Get-AuthenticodeSignature -LiteralPath $taskBootstrapper
if ($taskBootstrapperSignature.Status -ne 'Valid' -or $taskBootstrapperSignature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'WebView2 Bootstrapper signature validation failed.' }
$taskUninstaller = Join-Path $taskBuild 'Uninstaller.exe'
& $taskCompiler @taskCommon @taskReferences /define:UNINSTALLER_ONLY /main:Installer "/out:$taskUninstaller" $taskInstallerSource
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller compilation failed.' }
$taskWebResources = @("/resource:$(Join-Path $taskRoot 'licenses\Tabler-LICENSE.txt'),IconLicense", "/resource:$(Join-Path $taskRoot 'licenses\WebView2-LICENSE.txt'),WebLicense", "/resource:$(Join-Path $taskRoot 'licenses\WebView2-NOTICE.txt'),WebNotice", "/resource:$(Join-Path $taskRoot 'licenses\MusicConverter-NOTICES.txt'),MusicConverterNotice", "/resource:$(Join-Path $taskSdk 'Microsoft.Web.WebView2.Core.dll'),WebCore", "/resource:$(Join-Path $taskSdk 'Microsoft.Web.WebView2.WinForms.dll'),WebForms", "/resource:$(Join-Path $taskRoot 'vendor\webview2\loader\win-x64\WebView2Loader.dll'),WebLoader64", "/resource:$(Join-Path $taskRoot 'vendor\webview2\loader\win-x86\WebView2Loader.dll'),WebLoader32", "/resource:$taskBootstrapper,WebBootstrapper", "/resource:$taskUninstaller,Uninstaller")
& $taskCompiler @taskCommon @taskReferences @taskWebResources /main:Installer "/out:$taskSetup" "/resource:$taskPlayer,Player" "/resource:$taskConfig,Config" "/resource:$taskGuide,Guide" "/resource:$taskDriver,Driver" $taskInstallerSource
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Copy-Item -LiteralPath $taskGuide -Destination $taskGuideOutput
Write-Host "Generated the empty-library installer: $taskSetup"
