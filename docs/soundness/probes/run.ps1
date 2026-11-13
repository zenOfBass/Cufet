param([string[]]$Names, [string]$Cufet = 'cufet')
# Runs each probe three ways: check, interpreted, compiled. A probe SHOWS its hole when `check`
# passes and the compiled run crashes or prints something the interpreted run did not.
#   pwsh -File docs/soundness/probes/run.ps1 [-Cufet <path to cufet>] [-Names a,b]
# Run it from PowerShell: gcc is not on Git Bash's PATH.
$dir = $PSScriptRoot
$out = Join-Path ([IO.Path]::GetTempPath()) "cufet-soundness-probes"
New-Item -ItemType Directory -Force $out | Out-Null
if (-not $Names) { $Names = Get-ChildItem $dir -Filter *.cufe | Sort-Object Name | ForEach-Object BaseName }
foreach ($n in $Names) {
    $f = Join-Path $out "$n.cufe"
    Copy-Item (Join-Path $dir "$n.cufe") $f -Force
    $null = & $Cufet check $f 2>&1
    if ($LASTEXITCODE -ne 0) { "{0,-46} REFUSED by check" -f $n; continue }
    $int = (& $Cufet $f 2>&1 | Out-String).Trim()
    $exe = Join-Path $out "$n.exe"
    Remove-Item $exe -ErrorAction SilentlyContinue
    Push-Location $out; $null = & $Cufet build $f 2>&1; Pop-Location
    if (-not (Test-Path $exe)) { "{0,-46} check passes, build REFUSED" -f $n; continue }
    $cmp = (& $exe 2>&1 | Out-String).Trim()
    $code = $LASTEXITCODE
    $verdict = if ($code -ne 0) { "compiled CRASHED (exit 0x{0:X8})" -f $code }
               elseif ($int -ne $cmp) { "compiled printed WRONG output" }
               else { "same on both" }
    "{0,-46} check passes, {1}" -f $n, $verdict
}
