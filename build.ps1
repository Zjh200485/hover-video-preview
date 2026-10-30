$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '没有找到 Windows .NET Framework 编译器。' }
$sourceDir = Join-Path $PSScriptRoot 'src'
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' | ForEach-Object { $_.FullName })
$outputPath = Join-Path $PSScriptRoot '悬停视频预览.exe'
$manifestPath = Join-Path $sourceDir 'app.manifest'
& $compilerPath /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 /utf8output "/out:$outputPath" "/win32manifest:$manifestPath" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll $sourceFiles
if ($LASTEXITCODE -ne 0) { throw "编译失败：$LASTEXITCODE" }
Get-Item -LiteralPath $outputPath | Select-Object FullName, Length
