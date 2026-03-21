param(
    [string]$ResultsDirectory = "",
    [double]$TolerancePercent = 5.0
)

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path (Get-Location) "BenchmarkDotNet.Artifacts\results"
}

function Convert-BdnMeanToMilliseconds([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    $parts = $value.Trim() -split "\s+"
    if ($parts.Length -lt 2) { return $null }
    $num = [double]$parts[0]
    $unit = $parts[1].ToLowerInvariant()
    switch ($unit) {
        "ns" { return $num / 1000000.0 }
        "us" { return $num / 1000.0 }
        "ms" { return $num }
        "s"  { return $num * 1000.0 }
        default { return $null }
    }
}

function Assert-FileExists([string]$path) {
    if (-not (Test-Path $path)) {
        throw "Required benchmark result file not found: $path"
    }
}

function Is-SlowerThanWithTolerance([double]$leftMs, [double]$rightMs, [double]$tolerancePercent) {
    $threshold = $rightMs * (1.0 + ($tolerancePercent / 100.0))
    return $leftMs -gt $threshold
}

$ingestCsv = Join-Path $ResultsDirectory "ZLogger.Scintilla.Benchmarks.Benchmarks.IngestThroughputBenchmark-report.csv"
$colorizedCsv = Join-Path $ResultsDirectory "ZLogger.Scintilla.Benchmarks.Benchmarks.ColorizedRenderBenchmark-report.csv"
$searchCsv = Join-Path $ResultsDirectory "ZLogger.Scintilla.Benchmarks.Benchmarks.LargeDocumentSearchBenchmark-report.csv"

$failures = New-Object System.Collections.Generic.List[string]

# Guard 1: Ingest enqueue — Scintilla should not be slower than Serilog
if (Test-Path $ingestCsv) {
    $ingestRows = Import-Csv $ingestCsv
    $serilogEnqueue = $ingestRows | Where-Object { $_.Method -like "*Serilog*enqueue*" } | Select-Object -First 1
    $scintillaEnqueue = $ingestRows | Where-Object { $_.Method -like "*Scintilla*enqueue*" } | Select-Object -First 1
    if ($null -ne $serilogEnqueue -and $null -ne $scintillaEnqueue) {
        $serilogMs = Convert-BdnMeanToMilliseconds $serilogEnqueue.Mean
        $scintillaMs = Convert-BdnMeanToMilliseconds $scintillaEnqueue.Mean
        if ($null -ne $serilogMs -and $null -ne $scintillaMs -and (Is-SlowerThanWithTolerance $scintillaMs $serilogMs $TolerancePercent)) {
            $failures.Add("Ingest regression: Scintilla=$($scintillaEnqueue.Mean) slower than Serilog=$($serilogEnqueue.Mean) beyond tolerance $TolerancePercent%.")
        }
    }
}

# Guard 2: Colorized enqueue — Scintilla should not be slower than Serilog
if (Test-Path $colorizedCsv) {
    $colorRows = Import-Csv $colorizedCsv
    $serilogColor = $colorRows | Where-Object { $_.Method -like "*Serilog*colorized*enqueue*" } | Select-Object -First 1
    $scintillaColor = $colorRows | Where-Object { $_.Method -like "*Scintilla*colorized*enqueue*" } | Select-Object -First 1
    if ($null -ne $serilogColor -and $null -ne $scintillaColor) {
        $serilogMs = Convert-BdnMeanToMilliseconds $serilogColor.Mean
        $scintillaMs = Convert-BdnMeanToMilliseconds $scintillaColor.Mean
        if ($null -ne $serilogMs -and $null -ne $scintillaMs -and (Is-SlowerThanWithTolerance $scintillaMs $serilogMs $TolerancePercent)) {
            $failures.Add("Colorized regression: Scintilla=$($scintillaColor.Mean) slower than Serilog=$($serilogColor.Mean) beyond tolerance $TolerancePercent%.")
        }
    }
}

# Guard 3: Search — Scintilla should not be slower than RichTextBox
if (Test-Path $searchCsv) {
    $searchRows = Import-Csv $searchCsv
    $rtbSearch = $searchRows | Where-Object { $_.Method -like "*RTB*Find*common*" } | Select-Object -First 1
    $sciSearch = $searchRows | Where-Object { $_.Method -like "*Scintilla*Highlight*FindNext*common*" } | Select-Object -First 1
    if ($null -ne $rtbSearch -and $null -ne $sciSearch) {
        $rtbMs = Convert-BdnMeanToMilliseconds $rtbSearch.Mean
        $sciMs = Convert-BdnMeanToMilliseconds $sciSearch.Mean
        if ($null -ne $rtbMs -and $null -ne $sciMs -and (Is-SlowerThanWithTolerance $sciMs $rtbMs $TolerancePercent)) {
            $failures.Add("Search regression: Scintilla=$($sciSearch.Mean) slower than RichTextBox=$($rtbSearch.Mean) beyond tolerance $TolerancePercent%.")
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "Benchmark regression guards failed:" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }
    exit 1
}

Write-Host "Benchmark regression guards passed." -ForegroundColor Green
