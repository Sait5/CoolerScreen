# Build CoolerScreen with the C# compiler that ships with Windows (.NET Framework 4.8).
# Nothing to install: run   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force "$root\bin" | Out-Null
& $csc /nologo /codepage:65001 /optimize+ /platform:x64 /target:winexe /win32icon:"$root\assets\app.ico" `
  /out:"$root\bin\CoolerScreen.exe" `
  /r:System.Management.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
  "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
Write-Host "OK: $root\bin\CoolerScreen.exe"
