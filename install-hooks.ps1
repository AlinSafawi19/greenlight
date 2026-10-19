# Points every repo next to feature-ledger at the shared hooks folder (local git config only, nothing is committed).
#   .\install-hooks.ps1              install
#   .\install-hooks.ps1 -Uninstall   remove
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$cfg = Get-Content (Join-Path $here 'ledger.json') -Raw | ConvertFrom-Json
$root = [IO.Path]::GetFullPath((Join-Path $here $cfg.reposRoot))
$hooks = (Join-Path $here 'hooks') -replace '\\', '/'

foreach ($dir in Get-ChildItem $root -Directory) {
    if (-not (Test-Path (Join-Path $dir.FullName '.git'))) { continue }
    if ($cfg.excludeRepos -contains $dir.Name -or $dir.FullName -eq $here) { continue }

    $current = git -C $dir.FullName config --local --get core.hooksPath
    if ($Uninstall) {
        if ($current -eq $hooks) {
            git -C $dir.FullName config --local --unset core.hooksPath
            git -C $dir.FullName config --local --unset ledger.keyPattern
            "removed    $($dir.Name)"
        }
        continue
    }
    if ($current -and $current -ne $hooks) { "SKIPPED    $($dir.Name) (already uses hooks at $current)"; continue }
    git -C $dir.FullName config --local core.hooksPath $hooks
    git -C $dir.FullName config --local ledger.keyPattern $cfg.keyPattern
    "installed  $($dir.Name)"
}
