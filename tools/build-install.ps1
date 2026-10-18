# Builds the plugin, packs it like the release workflow does, and installs it into ACT for a local
# test (ACT must be closed).  The version is the installed one's, given as -Version (4 parts, e.g.
# 0.19.110.4): every assembly must match the main OverlayPlugin.dll or ACT refuses to load it.
#
# powershell -File tools\build-install.ps1 -Version 0.19.110.4 [-ActDir "C:\Program Files (x86)\Advanced Combat Tracker"] [-NoInstall]
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$ActDir = 'C:\Program Files (x86)\Advanced Combat Tracker',
  [switch]$NoInstall
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

dotnet publish -c release "-p:AssemblyVersion=$Version" "-p:FileVersion=$Version" 2>&1 |
  Select-String -Pattern 'error CS|error MSB' | Select-Object -First 5
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
"built $(git log --oneline -1)"

Set-Location out\Release
if (Test-Path OverlayPlugin) { Remove-Item -Recurse -Force OverlayPlugin }
New-Item -ItemType Directory -Force OverlayPlugin\libs | Out-Null
Copy-Item @('OverlayPlugin.dll', 'OverlayPlugin.dll.config', 'README.md', 'LICENSE.txt') OverlayPlugin
Copy-Item -Recurse libs\resources OverlayPlugin
Copy-Item libs\*.dll OverlayPlugin\libs
Remove-Item OverlayPlugin\libs\CefSharp.*
Remove-Item OverlayPlugin\libs\Microsoft.Web.WebView2.WinForms.dll, OverlayPlugin\libs\Microsoft.Web.WebView2.Wpf.dll
New-Item -ItemType Directory -Force OverlayPlugin\libs\runtimes\win-x64\native | Out-Null
Copy-Item libs\runtimes\win-x64\native\WebView2Loader.dll OverlayPlugin\libs\runtimes\win-x64\native
Copy-Item -Recurse @('de-DE', 'fr-FR', 'ja-JP', 'ko-KR', 'zh-CN') OverlayPlugin
Copy-Item -Recurse @('libs\de-DE', 'libs\fr-FR', 'libs\ja-JP', 'libs\ko-KR', 'libs\zh-CN') OverlayPlugin\libs
"packed out\Release\OverlayPlugin"
if ($NoInstall) { return }

if (Get-Process 'Advanced Combat Tracker' -ErrorAction SilentlyContinue) {
  'ACT is running: not installed (close it and run again)'
  return
}
$inst = Join-Path $ActDir 'Plugins\OverlayPlugin'
$installed = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $inst 'OverlayPlugin.dll')).Version.ToString()
if ($installed -ne $Version) { throw "installed OverlayPlugin is $installed, built as $Version" }
Copy-Item -Recurse -Force OverlayPlugin\* $inst
foreach ($f in @('OverlayPlugin.dll', 'libs\HtmlRenderer.dll', 'libs\OverlayPlugin.Core.dll', 'libs\OverlayPlugin.Common.dll')) {
  if (-not (Test-Path OverlayPlugin\$f)) { continue }
  "{0}: {1}" -f $f, $(if ((Get-FileHash (Join-Path $inst $f)).Hash -eq (Get-FileHash OverlayPlugin\$f).Hash) { 'installed' } else { 'MISMATCH' })
}
