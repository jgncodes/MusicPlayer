param(
    [Parameter(Mandatory)] [string] $ResultsDirectory,
    [ValidateRange(0, 100)] [int] $MinimumLinePercent = 0,
    [ValidateRange(0, 100)] [int] $MinimumBranchPercent = 0,
    [string] $ReportGeneratorPath = 'reportgenerator',
    [string] $HtmlReportDirectory
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

# Generate the gate inputs with the same parser and aggregation as the HTML report.
$reportDir = Join-Path $ResultsDirectory 'report'
$reportTypes = 'JsonSummary'
if ($HtmlReportDirectory) {
    $reportDir = $HtmlReportDirectory
    $reportTypes = 'JsonSummary;Html;TextSummary'
}
& $ReportGeneratorPath "-reports:$($files[0].FullName)" "-targetdir:$reportDir" "-reporttypes:$reportTypes"
if ($LASTEXITCODE -ne 0) { throw "ReportGenerator failed with exit code $LASTEXITCODE." }
$result = Get-Content -LiteralPath (Join-Path $reportDir 'Summary.json') -Raw | ConvertFrom-Json
if ($result.summary.assemblies -ne 1 -or $result.coverage.assemblies[0].name -cne 'MusicPlayer') {
    throw 'Expected a ReportGenerator summary for the MusicPlayer production assembly.'
}

function Test-CoverageMinimum($metric, $covered, $total, $minimum) {
    if ($null -eq $covered -or $total -le 0 -or $covered -lt 0 -or $covered -gt $total) {
        throw "Invalid $metric counters in the ReportGenerator summary."
    }
    Write-Host ('Production {0}: {1}/{2} ({3:P2}), minimum {4}%' -f $metric, $covered, $total, ($covered / $total), $minimum)
    return (100L * $covered -ge [long]$minimum * $total)
}
$summary = $result.summary
$linesPass = Test-CoverageMinimum 'lines' $summary.coveredlines $summary.coverablelines $MinimumLinePercent
$branchesPass = Test-CoverageMinimum 'branches' $summary.coveredbranches $summary.totalbranches $MinimumBranchPercent
if (-not ($linesPass -and $branchesPass)) { throw 'Production coverage is below the required minimum. See counts above.' }
