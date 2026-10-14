param(
    [string]$OutputName = "BgmHotkey_1.9.7.exe",
    [string]$CryptoHelperArchive = "local-only\build-staging\encrypted-audio-helper-1.9.7.zip"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDirectory = Join-Path $projectRoot "app\src"
if ([System.IO.Path]::GetFileName($OutputName) -ne $OutputName -or [System.IO.Path]::GetExtension($OutputName) -ne ".exe") {
    throw "OutputName 只能是 exe 文件名。"
}
$outputPath = Join-Path $projectRoot $OutputName
$configPath = $outputPath + '.config'
if (Test-Path -LiteralPath $outputPath) { throw "Refusing to overwrite existing player: $outputPath" }
if (Test-Path -LiteralPath $configPath) { throw "Refusing to overwrite existing player config: $configPath" }
$helperPath = if ([System.IO.Path]::IsPathRooted($CryptoHelperArchive)) { $CryptoHelperArchive } else { Join-Path $projectRoot $CryptoHelperArchive }
$helperPath = [System.IO.Path]::GetFullPath($helperPath)
if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) { throw "Missing bundled converter archive. Build it first: $helperPath" }
$converterNotice = Join-Path $projectRoot 'licenses\MusicConverter-NOTICES.txt'
if (-not (Test-Path -LiteralPath $converterNotice -PathType Leaf)) { throw "Missing converter third-party notices: $converterNotice" }
$processName = [System.IO.Path]::GetFileNameWithoutExtension($OutputName)
$runningPlayer = Get-Process -Name $processName -ErrorAction SilentlyContinue
if ($runningPlayer) { throw "A process with the target player name is running; refusing to build over it: $outputPath" }
$iconPath = Join-Path $projectRoot "app\assets\BgmHotkey.ico"
$compilerCandidates = @(
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw "未找到 .NET Framework C# 编译器。"
}

$sources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter "*.cs" | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) {
    throw "src 目录中没有 C# 源文件。"
}

$arguments = @(
    "/nologo",
    "/langversion:5",
    "/codepage:65001",
    "/target:winexe",
    "/platform:anycpu",
    "/optimize+",
    "/win32manifest:$(Join-Path $projectRoot 'app\app.manifest')",
    "/out:$outputPath",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll",
    "/reference:System.Xml.dll",
    "/reference:System.IO.Compression.dll",
    "/reference:System.IO.Compression.FileSystem.dll",
    "/reference:System.Web.Extensions.dll"
)
if (Test-Path -LiteralPath $iconPath) {
    $arguments += "/win32icon:$iconPath"
}
$webSdk = Join-Path $projectRoot 'vendor\webview2\sdk\net462'
$arguments += @("/reference:$(Join-Path $webSdk 'Microsoft.Web.WebView2.Core.dll')", "/reference:$(Join-Path $webSdk 'Microsoft.Web.WebView2.WinForms.dll')", "/resource:$(Join-Path $projectRoot 'app\ui\index.html'),Interface", "/resource:$helperPath,CryptoHelperArchive", "/resource:$converterNotice,MusicConverterNotice")
function Copy-IfDifferent([string]$sourcePath, [string]$destinationPath) {
    if (Test-Path -LiteralPath $destinationPath) {
        $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
        if ($sourceHash -eq $destinationHash) { return }
    }
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
}
Copy-IfDifferent (Join-Path $webSdk 'Microsoft.Web.WebView2.Core.dll') (Join-Path $projectRoot 'Microsoft.Web.WebView2.Core.dll')
Copy-IfDifferent (Join-Path $webSdk 'Microsoft.Web.WebView2.WinForms.dll') (Join-Path $projectRoot 'Microsoft.Web.WebView2.WinForms.dll')
$loaderArchitecture = if ([Environment]::Is64BitOperatingSystem) { 'win-x64' } else { 'win-x86' }
Copy-IfDifferent (Join-Path $projectRoot "vendor\webview2\loader\$loaderArchitecture\WebView2Loader.dll") (Join-Path $projectRoot 'WebView2Loader.dll')
$arguments += $sources

Push-Location $projectRoot
try {
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "C# 编译失败，退出代码：$LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

Write-Host "已生成：$outputPath"
Copy-Item -LiteralPath (Join-Path $projectRoot 'app\app.config') -Destination $configPath
Write-Host "BGM 文件目录：$(Join-Path $projectRoot 'bgm')"
