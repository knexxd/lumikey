# Builds bin\Lumikey.exe with the C# compiler that ships with .NET Framework 4.x (no SDK needed).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$csc = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw ".NET Framework 4.x C# compiler (csc.exe) not found" }

if (-not (Test-Path src\app.ico)) { & powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-icon.ps1 }
New-Item -ItemType Directory -Force bin | Out-Null

& $csc /nologo /target:winexe /optimize+ /platform:anycpu /out:bin\Lumikey.exe /win32icon:src\app.ico `
    /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/resource:src\ui\index.html,index.html" "/resource:src\keymatrix.txt,keymatrix.txt" `
    src\Lumikey.cs
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Get-Item bin\Lumikey.exe | Select-Object Name, Length, LastWriteTime
