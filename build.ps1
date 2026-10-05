param(
    [string]$OutputName = "BgmHotkey.exe"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDirectory = $projectRoot
if ([System.IO.Path]::GetFileName($OutputName) -ne $OutputName -or [System.IO.Path]::GetExtension($OutputName) -ne ".exe") {
    throw "OutputName 只能是 exe 文件名。"
}
$outputPath = Join-Path $projectRoot $OutputName
$iconPath = Join-Path $projectRoot "BgmHotkey.ico"
$compilerCandidates = @(
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw "未找到 .NET Framework C# 编译器。"
}

$sources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter "*.cs" | Where-Object Name -ne 'Installer.cs' | ForEach-Object { $_.FullName })
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
    "/win32manifest:$(Join-Path $projectRoot 'app.manifest')",
    "/out:$outputPath",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll",
    "/reference:System.Xml.dll"
)
if (Test-Path -LiteralPath $iconPath) {
    $arguments += "/win32icon:$iconPath"
}
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
Copy-Item -LiteralPath (Join-Path $projectRoot 'app.config') -Destination ($outputPath + '.config') -Force
Write-Host "BGM 文件目录：$(Join-Path $projectRoot 'bgm')"
