$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskBuild = Join-Path $PSScriptRoot '.build'
$taskOutput = Join-Path $taskRoot '分享安装包'
$taskCompiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Path $taskBuild,$taskOutput -Force | Out-Null
$taskPlayer = Join-Path $taskBuild 'BgmHotkey.exe'
$taskSetup = Join-Path $taskOutput '万象BGM_安装.exe'
$taskSource = @(Get-ChildItem -LiteralPath $taskRoot -Filter *.cs | Where-Object Name -ne 'Installer.cs' | ForEach-Object FullName)
$taskReferences = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$taskCommon = @('/nologo','/langversion:5','/codepage:65001','/target:winexe','/platform:anycpu','/optimize+',('/win32manifest:' + (Join-Path $taskRoot 'app.manifest')),('/win32icon:' + (Join-Path $taskRoot 'BgmHotkey.ico')))
& $taskCompiler @taskCommon @taskReferences /define:PUBLIC_RELEASE "/out:$taskPlayer" @taskSource
if ($LASTEXITCODE -ne 0) { throw '分享版编译失败' }
$taskConfig = Join-Path $taskRoot 'app.config'
$taskGuide = Join-Path $PSScriptRoot '使用说明.txt'
$taskInstallerSource = Join-Path $PSScriptRoot 'Installer.cs'
$taskDriver = Join-Path $taskBuild 'VB-CABLE.zip'
Copy-Item -LiteralPath (Join-Path $taskRoot 'VBCABLE_Driver_Pack45.zip') -Destination $taskDriver -Force
& $taskCompiler @taskCommon @taskReferences /main:Installer "/out:$taskSetup" "/resource:$taskPlayer,Player" "/resource:$taskConfig,Config" "/resource:$taskGuide,Guide" "/resource:$taskDriver,Driver" $taskInstallerSource
if ($LASTEXITCODE -ne 0) { throw '安装包编译失败' }
Copy-Item -LiteralPath $taskGuide -Destination (Join-Path $taskOutput '使用说明.txt') -Force
Write-Host "已生成空曲库分享安装包：$taskSetup"
