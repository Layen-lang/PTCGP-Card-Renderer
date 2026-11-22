$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root = Join-Path $repo 'data'
# Unity's package manager requires this Windows variable. The execution
# environment may omit it; restore its normal OS-defined value for this process.
if (-not $env:ALLUSERSPROFILE) {
    $env:ALLUSERSPROFILE = [Environment]::GetFolderPath('CommonApplicationData')
}
$editor = if ($env:UNITY_EDITOR) { $env:UNITY_EDITOR } else { 'C:\Program Files\Unity\Hub\Editor\6000.0.78f1\Editor\Unity.exe' }
New-Item -ItemType Directory -Force (Join-Path $root 'logs') | Out-Null
$result = Join-Path $root 'logs/player-build-result.txt'
# A compilation failure can occur before the editor build method is called.
# Invalidate its previous success marker rather than exposing a stale result.
Set-Content -LiteralPath $result -Value 'Pending: current build has not completed'
$arguments = @('-batchmode','-force-vulkan','-projectPath',(Join-Path $repo 'unity/project'),'-executeMethod','BuildNativePreview.Run','-logFile',(Join-Path $root 'logs/player-build.log'))
$process = Start-Process -FilePath $editor -ArgumentList $arguments -WindowStyle Hidden -PassThru
$process.WaitForExit()
Get-Content -LiteralPath $result
if ($process.ExitCode -ne 0) { throw "The player build failed; see data/logs/player-build.log" }
