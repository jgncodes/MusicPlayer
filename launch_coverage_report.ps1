# MusicPlayer - Run automated tests, generate code coverage, and open the HTML report.
# Place this file alongside MusicPlayer.csproj and run it from PowerShell.

param([switch] $NoOpen)

$ErrorActionPreference = 'Stop'

# Resolve paths relative to this script, regardless of the terminal's current folder.
$root = $PSScriptRoot
$testProject = Join-Path $root 'tests/MusicPlayer.Tests/MusicPlayer.Tests.csproj'
$resultsDir = Join-Path $root ('TestResults/local/' + [guid]::NewGuid().ToString('N'))
$reportDir = Join-Path $root 'coverage-report'

if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
    Write-Error "Test project not found: $testProject"
    exit 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'The .NET SDK (dotnet) was not found in PATH.'
    exit 1
}

Write-Host '[1/4] Running MusicPlayer tests and collecting coverage...' -ForegroundColor Cyan
$settings = Join-Path $root 'tests/coverage.runsettings'
& dotnet test $testProject --configuration Release '--collect:XPlat Code Coverage' --settings $settings `
    --logger 'trx;LogFileName=MusicPlayer.Tests.trx' --results-directory $resultsDir
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Tests failed. Fix the failures before generating a coverage report.' -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host '[2/4] Locating ReportGenerator...' -ForegroundColor Cyan
$reportGenerator = Get-Command reportgenerator -ErrorAction SilentlyContinue
if (-not $reportGenerator) {
    Write-Host 'ReportGenerator is not installed. Installing it as a global .NET tool...' -ForegroundColor Yellow
    & dotnet tool install --global dotnet-reportgenerator-globaltool --version 5.6.1
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'ReportGenerator installation failed. Install manually: dotnet tool install --global dotnet-reportgenerator-globaltool'
        exit 1
    }
    # A newly installed global tool might not yet be on PATH in this terminal.
    $toolDirectory = Join-Path $HOME '.dotnet/tools'
    $reportGeneratorPath = Join-Path $toolDirectory 'reportgenerator.exe'
    if (-not (Test-Path -LiteralPath $reportGeneratorPath)) {
        Write-Error "ReportGenerator was installed but its executable was not found at $reportGeneratorPath. Open a new PowerShell terminal and retry."
        exit 1
    }
} else {
    $reportGeneratorPath = $reportGenerator.Source
}

Write-Host '[3/4] Measuring coverage and generating the HTML report...' -ForegroundColor Cyan
& (Join-Path $root 'tests/Measure-Coverage.ps1') -ResultsDirectory $resultsDir `
    -ReportGeneratorPath $reportGeneratorPath -HtmlReportDirectory $reportDir

$index = Join-Path $reportDir 'index.html'
if (-not (Test-Path -LiteralPath $index -PathType Leaf)) {
    Write-Error "HTML report was not created: $index"
    exit 1
}

if (-not $NoOpen) {
    Write-Host '[4/4] Opening the coverage report...' -ForegroundColor Cyan
    Start-Process -FilePath $index
}
Write-Host "Success! Coverage report: $index" -ForegroundColor Green
