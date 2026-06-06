# Regenerates layout.json for an MSFS package folder.
# Usage (PowerShell, from this folder):
#   .\build-layout.ps1 -PackageDir .\ufo-light
#
# MSFS requires a layout.json listing every file (except layout.json itself)
# with its size and last-write time as a Windows FILETIME. The SDK normally
# generates this; this script does the same so you don't need the SDK tool.

param(
    [Parameter(Mandatory = $true)] [string] $PackageDir
)

$root = (Resolve-Path $PackageDir).Path
$entries = @()

Get-ChildItem -Path $root -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
    if ($rel -eq 'layout.json') { return }
    $entries += [ordered]@{
        path = $rel
        size = $_.Length
        date = $_.LastWriteTimeUtc.ToFileTimeUtc()
    }
}

$layout = [ordered]@{ content = $entries }
$json = $layout | ConvertTo-Json -Depth 5
$outPath = Join-Path $root 'layout.json'
Set-Content -Path $outPath -Value $json -Encoding UTF8

Write-Host "Wrote $outPath with $($entries.Count) entries."
