param(
    [ValidateSet('Solid', 'Regular', 'Brands')]
    [string[]]$Weights = @('Solid', 'Regular', 'Brands'),

    [ValidateRange(1, 50)]
    [int]$Repeats = 5,

    [ValidateRange(1, 10000)]
    [int]$WarmBatches = 100
)

$ErrorActionPreference = 'Stop'
$benchmark = Join-Path $PSScriptRoot '..\osu.Framework.Benchmarks\bin\Release\net11.0\osu.Framework.Benchmarks.dll'

if (-not (Test-Path -LiteralPath $benchmark)) {
    throw "Release benchmark assembly not found: $benchmark. Build osu.Framework.Benchmarks first."
}

$scenarioPairs = @(
    @{ BmFont = 'bmfont-cold'; Outline = 'outline-cold' },
    @{ BmFont = 'bmfont-warm'; Outline = 'outline-warm' }
)

foreach ($weight in $Weights) {
    for ($repeat = 0; $repeat -lt $Repeats; $repeat++) {
        foreach ($pair in $scenarioPairs) {
            $modes = if (($repeat % 2) -eq 0) {
                @($pair.BmFont, $pair.Outline)
            } else {
                @($pair.Outline, $pair.BmFont)
            }

            foreach ($mode in $modes) {
                $batches = if ($mode.EndsWith('-warm')) { $WarmBatches } else { 1 }
                Write-Host "[$($repeat + 1)/$Repeats] FontAwesome $weight $mode ($batches batch(es))"

                & dotnet $benchmark --fontawesome-migration $weight $mode $batches

                if ($LASTEXITCODE -ne 0) {
                    throw "Benchmark process failed with exit code ${LASTEXITCODE}: $weight $mode"
                }
            }
        }
    }
}
