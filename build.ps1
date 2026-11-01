# Builds bin\LenoMode.exe with the C# compiler that ships with .NET Framework 4.8 (no SDK needed).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
New-Item -ItemType Directory -Force "$root\bin" | Out-Null
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu /out:"$root\bin\LenoMode.exe" `
  /win32manifest:"$root\app.manifest" /win32icon:"$root\assets\LenoMode.ico" `
  /resource:"$root\src\MainWindow.xaml,LenoMode.MainWindow.xaml" `
  /r:"$fw\WPF\PresentationFramework.dll" /r:"$fw\WPF\PresentationCore.dll" /r:"$fw\WPF\WindowsBase.dll" `
  /r:System.Xaml.dll /r:System.Management.dll /r:System.ServiceProcess.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.dll /r:System.Core.dll `
  "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Write-Host "Built $root\bin\LenoMode.exe"
