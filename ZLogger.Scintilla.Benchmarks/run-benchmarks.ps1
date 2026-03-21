param(
    [ValidateSet("ingest", "colorized", "search", "filter", "paint", "sustained", "all")]
    [string]$Suite = "all",
    [string]$Configuration = "Release",
    [switch]$RunRegressionGuard,
    [double]$RegressionTolerancePercent = 5.0
)

$project = "ZLogger.Scintilla.Benchmarks/ZLogger.Scintilla.Benchmarks.csproj"

switch ($Suite) {
    "ingest" {
        dotnet run --project $project -c $Configuration -- --filter "*IngestThroughputBenchmark*"
    }
    "colorized" {
        dotnet run --project $project -c $Configuration -- --filter "*ColorizedRenderBenchmark*"
    }
    "search" {
        dotnet run --project $project -c $Configuration -- --filter "*LargeDocumentSearchBenchmark*"
    }
    "filter" {
        dotnet run --project $project -c $Configuration -- --filter "*FilterReapplyBenchmark*"
    }
    "paint" {
        dotnet run --project $project -c $Configuration -- --filter "*PaintBenchmark*"
    }
    "sustained" {
        dotnet run --project $project -c $Configuration -- --filter "*SustainedLoadBenchmark*"
    }
    default {
        dotnet run --project $project -c $Configuration
    }
}

if ($RunRegressionGuard) {
    .\ZLogger.Scintilla.Benchmarks\check-regression.ps1 -TolerancePercent $RegressionTolerancePercent
}
