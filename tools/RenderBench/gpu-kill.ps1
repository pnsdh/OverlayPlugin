# Kills a CEF GPU process 8 times, as Hancom Office's updater did to "CefSharp.BrowserSubprocess.exe"
# (Chromium gives up after 6 GPU crashes, taking ACT down with it), and reports whether RenderBench
# (cef-gpu, its subprocess renamed like OverlayPlugin's) survived.  Re-run after CEF upgrades or
# subprocess changes.  A window shows while it runs.
#
# powershell -File tools\RenderBench\gpu-kill.ps1 <name> ["cef;args"]   (after dotnet build -c Release)
# Logs go to tools\RenderBench\out\<name>\.
param([Parameter(Mandatory = $true)][string]$Name, [string]$CefArgs)
$out = Join-Path $PSScriptRoot "out\$Name"
New-Item -ItemType Directory -Force $out | Out-Null
$env:RB_SUBPROCESS = 'OverlayPlugin.BrowserSubprocess.exe'
$env:RB_CEFARGS = $CefArgs
$exe = Join-Path $PSScriptRoot 'bin\Release\RenderBench.exe'
$bench = Start-Process $exe -ArgumentList 'cef-gpu', 'about:blank', '60', '50', (Join-Path $out 'bench.log') -PassThru
Start-Sleep 6
$seen = Get-CimInstance Win32_Process -Filter "Name like '%BrowserSubprocess%'" |
  Where-Object { $_.ParentProcessId -eq $bench.Id }
'subprocesses: ' + (($seen | ForEach-Object { $_.Name + ' ' + ([regex]::Match($_.CommandLine, '--type=\S+').Value) }) -join '; ')
for ($i = 1; $i -le 8 -and -not $bench.HasExited; $i++) {
  $gpu = Get-CimInstance Win32_Process -Filter "Name = 'OverlayPlugin.BrowserSubprocess.exe'" |
    Where-Object { $_.ParentProcessId -eq $bench.Id -and $_.CommandLine -match '--type=gpu-process' }
  if ($gpu) { Stop-Process -Id $gpu.ProcessId -Force; "kill $i gpu pid $($gpu.ProcessId)" } else { "kill ${i}: no gpu process" }
  Start-Sleep 4
}
Start-Sleep 3
if ($bench.HasExited) { "bench EXITED code $($bench.ExitCode)" } else { 'bench ALIVE'; Stop-Process -Id $bench.Id -Force }
Get-ChildItem $out -Filter 'cef-*.log' | ForEach-Object { Select-String -Path $_.FullName -Pattern 'FATAL|GPU process exited' | ForEach-Object Line }
