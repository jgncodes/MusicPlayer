# MusicPlayer - Run automated tests, generate code coverage, and open the HTML report.
# Place this file alongside MusicPlayer.csproj and run it from PowerShell.

$ErrorActionPreference = 'Stop'

# Resolve paths relative to this script, regardless of the terminal's current folder.
$root = $PSScriptRoot
$testProject = Join-Path $root 'tests/MusicPlayer.Tests/MusicPlayer.Tests.csproj'
$resultsDir = Join-Path $root 'TestResults'
$reportDir = Join-Path $root 'coverage-report'

if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
    Write-Error "Test project not found: $testProject`nPlace launch_coverage.ps1 in the MusicPlayer repository root."
    exit 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'The .NET SDK (dotnet) was not found in PATH.'
    exit 1
}

Write-Host '[1/4] Running MusicPlayer tests and collecting coverage...' -ForegroundColor Cyan
# Old results can cause stale reports and confusing duplicate coverage runs.
if (Test-Path -LiteralPath $resultsDir) {
    Remove-Item -LiteralPath $resultsDir -Recurse -Force
}

& dotnet test $testProject --configuration Release '--collect:XPlat Code Coverage' --results-directory $resultsDir
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Tests failed. Fix the failures before generating a coverage report.' -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host '[2/4] Finding the Cobertura coverage output...' -ForegroundColor Cyan
$coverageFiles = @(Get-ChildItem -LiteralPath $resultsDir -Filter 'coverage.cobertura.xml' -File -Recurse -ErrorAction SilentlyContinue)
if ($coverageFiles.Count -eq 0) {
    Write-Error 'No coverage.cobertura.xml was produced. Verify coverlet.collector is referenced by the test project.'
    exit 1
}

Write-Host '[3/4] Generating the HTML report...' -ForegroundColor Cyan
$reportGenerator = Get-Command reportgenerator -ErrorAction SilentlyContinue
if (-not $reportGenerator) {
    Write-Host 'ReportGenerator is not installed. Installing it as a global .NET tool...' -ForegroundColor Yellow
    & dotnet tool install --global dotnet-reportgenerator-globaltool
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

# ReportGenerator accepts multiple Cobertura files separated by semicolons.
$reportInputs = ($coverageFiles | ForEach-Object { $_.FullName }) -join ';'
& $reportGeneratorPath "-reports:$reportInputs" "-targetdir:$reportDir" '-reporttypes:Html'
if ($LASTEXITCODE -ne 0) {
    Write-Error 'ReportGenerator failed.'
    exit $LASTEXITCODE
}

$index = Join-Path $reportDir 'index.html'
if (-not (Test-Path -LiteralPath $index -PathType Leaf)) {
    Write-Error "HTML report was not created: $index"
    exit 1
}

Write-Host '[4/4] Opening the coverage report...' -ForegroundColor Cyan
Start-Process -FilePath $index
Write-Host "Success! Coverage report: $index" -ForegroundColor Green
