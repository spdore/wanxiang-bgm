param([string]$OutputName = 'WanxiangBgmSetup-1.9.6.exe')

$ErrorActionPreference = 'Stop'
$GuideName = 'INSTALLATION-1.9.6.txt'
$Version = '1.9.6'
if ([System.IO.Path]::GetFileName($OutputName) -ne $OutputName -or [System.IO.Path]::GetExtension($OutputName) -ne '.exe') {
    throw 'OutputName must be a .exe file name without a path.'
}
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskBuildRoot = Join-Path $taskRoot 'local-only\build-staging'
$taskBuild = Join-Path $taskBuildRoot ('.build-' + $Version + '-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
$taskOutput = Join-Path $taskRoot 'release'
$taskCompiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Path $taskBuildRoot,$taskBuild,$taskOutput -Force | Out-Null
$taskPlayer = Join-Path $taskBuild 'BgmHotkey.exe'
$taskSetup = Join-Path $taskOutput $OutputName
if (Test-Path -LiteralPath $taskSetup) { throw "Refusing to overwrite existing installer: $taskSetup" }
$taskSource = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'app\src') -Filter *.cs | ForEach-Object FullName)
$taskReferences = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$taskCommon = @('/nologo','/langversion:5','/codepage:65001','/target:winexe','/platform:anycpu','/optimize+',('/win32manifest:' + (Join-Path $taskRoot 'app\app.manifest')),('/win32icon:' + (Join-Path $taskRoot 'app\assets\BgmHotkey.ico')))
$taskX64Common = @('/nologo','/langversion:5','/codepage:65001','/target:winexe','/platform:x64','/optimize+',('/win32manifest:' + (Join-Path $taskRoot 'app\app.manifest')),('/win32icon:' + (Join-Path $taskRoot 'app\assets\BgmHotkey.ico')))
$taskSdk = Join-Path $taskRoot 'vendor\webview2\sdk\net462'
$taskWebReferences = @('/r:System.Web.Extensions.dll', "/r:$(Join-Path $taskSdk 'Microsoft.Web.WebView2.Core.dll')", "/r:$(Join-Path $taskSdk 'Microsoft.Web.WebView2.WinForms.dll')", "/resource:$(Join-Path $taskRoot 'app\ui\index.html'),Interface")
& $taskCompiler @taskX64Common @taskReferences @taskWebReferences /define:PUBLIC_RELEASE "/out:$taskPlayer" @taskSource
if ($LASTEXITCODE -ne 0) { throw 'Player compilation failed.' }
$taskConfig = Join-Path $taskRoot 'app\app.config'
$taskGuide = Join-Path $PSScriptRoot 'installation-guide.zh-CN.txt'
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
$taskWebResources = @("/resource:$(Join-Path $taskRoot 'licenses\Tabler-LICENSE.txt'),IconLicense", "/resource:$(Join-Path $taskRoot 'licenses\WebView2-LICENSE.txt'),WebLicense", "/resource:$(Join-Path $taskRoot 'licenses\WebView2-NOTICE.txt'),WebNotice", "/resource:$(Join-Path $taskSdk 'Microsoft.Web.WebView2.Core.dll'),WebCore", "/resource:$(Join-Path $taskSdk 'Microsoft.Web.WebView2.WinForms.dll'),WebForms", "/resource:$(Join-Path $taskRoot 'vendor\webview2\loader\win-x64\WebView2Loader.dll'),WebLoader64", "/resource:$(Join-Path $taskRoot 'vendor\webview2\loader\win-x86\WebView2Loader.dll'),WebLoader32", "/resource:$taskBootstrapper,WebBootstrapper", "/resource:$taskUninstaller,Uninstaller")
& $taskCompiler @taskCommon @taskReferences @taskWebResources /main:Installer "/out:$taskSetup" "/resource:$taskPlayer,Player" "/resource:$taskConfig,Config" "/resource:$taskGuide,Guide" "/resource:$taskDriver,Driver" $taskInstallerSource
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Copy-Item -LiteralPath $taskGuide -Destination (Join-Path $taskOutput $GuideName)
Write-Host "Generated the empty-library installer: $taskSetup"
