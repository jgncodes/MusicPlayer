param(
    [Parameter(Mandatory)] [string] $ResultsDirectory,
    [ValidateRange(0, 100)] [int] $MinimumLinePercent = 0
)

$ErrorActionPreference = 'Stop'
# VSTest also copies attachments into the TRX host directory. Use the original only.
$files = @(Get-ChildItem -Path (Join-Path $ResultsDirectory '*/coverage.cobertura.xml') -File)
if ($files.Count -ne 1) { throw "Expected one collector report in $ResultsDirectory, found $($files.Count)." }
[xml] $xml = Get-Content -LiteralPath $files[0].FullName -Raw
$coverage = $xml.coverage
$packages = @($coverage.packages.package)
if ($packages.Count -ne 1 -or $packages[0].name -cne 'MusicPlayer') {
    throw 'Expected coverage for the MusicPlayer production assembly.'
}
$covered = [long] $coverage.'lines-covered'
$total = [long] $coverage.'lines-valid'
if ($total -le 0) { throw 'The report contains no measurable production lines.' }

$report = foreach ($file in ($packages[0].classes.class | Group-Object filename)) {
    $lines = @($file.Group.lines.line | Group-Object number)
    $hits = @($lines | Where-Object { @($_.Group | Where-Object { [long] $_.hits -gt 0 }).Count -gt 0 })
    [pscustomobject]@{ File = $file.Name; Covered = $hits.Count; Lines = $lines.Count }
}
$report | Format-Table -AutoSize
$core = $report | Where-Object { $_.File -match '(^|[\\/])Services[\\/]' }
$coreHits = ($core | Measure-Object Covered -Sum).Sum
$coreLines = ($core | Measure-Object Lines -Sum).Sum
if ($coreLines -le 0) { throw 'No production service files found.' }
Write-Host ('Services: {0}/{1} lines ({2:P2})' -f $coreHits, $coreLines, ($coreHits / $coreLines))
Write-Host ('Production: {0}/{1} lines ({2:P2}), {3}/{4} branches' -f $covered, $total,
    ($covered / $total), $coverage.'branches-covered', $coverage.'branches-valid')
if (100 * $covered -lt $MinimumLinePercent * $total) {
    throw "Production line coverage is below $MinimumLinePercent%."
}
