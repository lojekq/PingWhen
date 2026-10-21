# Scene GIF prop (spec §10): a fake long job with a progress bar. pwsh docs\fake-progress.ps1 [-Seconds 60]
param([int]$Seconds = 60)
$width = 40
for ($i = 0; $i -le $Seconds; $i++) {
  $p = [int](100 * $i / $Seconds)
  $done = [int]($width * $i / $Seconds)
  Write-Host -NoNewline ("`rRendering trailer_4k.mp4  [{0}{1}] {2,3}%" -f ('#' * $done), ('.' * ($width - $done)), $p)
  Start-Sleep 1
}
Write-Host "`nDone."
exit 0
