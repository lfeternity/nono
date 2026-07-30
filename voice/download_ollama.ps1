param(
    [string]$Version = "v0.32.4",
    [long]$ContentLength = 1457827520,
    [string]$Sha256 = "4ce7e765dc2bf1bb424a76b96d6631cc0462f5c7507e85f0dc2abf30c564953b",
    [int]$Connections = 8,
    [int]$SegmentMB = 1
)

$ErrorActionPreference = "Stop"
$voiceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $voiceRoot "cache"))
$partsRoot = [IO.Path]::GetFullPath((Join-Path $cacheRoot "ollama-parts"))
$archive = [IO.Path]::GetFullPath((Join-Path $cacheRoot "ollama-windows-amd64.zip"))

if (-not $cacheRoot.StartsWith($voiceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The Ollama cache must stay inside the voice directory."
}
if ($ContentLength -le 0 -or $Connections -lt 1 -or $Connections -gt 16 -or $SegmentMB -lt 1 -or $SegmentMB -gt 8) {
    throw "Invalid Ollama download parameters."
}

New-Item -ItemType Directory -Force -Path $cacheRoot,$partsRoot | Out-Null
$url = "https://ghproxy.net/https://github.com/ollama/ollama/releases/download/$Version/ollama-windows-amd64.zip"
$segmentSize = [long]$SegmentMB * 1MB
$segmentCount = [int][Math]::Ceiling($ContentLength / [double]$segmentSize)
$workerScript = {
    param($WorkerIndex, $WorkerCount, $SegmentCount, $SegmentSize, $TotalLength, $PartsDirectory, $DownloadUrl)

    for ($index = $WorkerIndex; $index -lt $SegmentCount; $index += $WorkerCount) {
        $start = [long]($index * $SegmentSize)
        $end = [long][Math]::Min($TotalLength - 1, $start + $SegmentSize - 1)
        $expectedLength = $end - $start + 1
        $part = Join-Path $PartsDirectory ("segment-{0:D4}.bin" -f $index)
        if ((Test-Path -LiteralPath $part) -and (Get-Item -LiteralPath $part).Length -eq $expectedLength) {
            continue
        }

        & curl.exe -L --fail --retry 8 --retry-delay 2 `
            -r ($start.ToString() + "-" + $end.ToString()) `
            -o $part `
            $DownloadUrl 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to download segment $index."
        }
    }
}

$downloads = @()
for ($workerIndex = 0; $workerIndex -lt $Connections; $workerIndex++) {
    $downloads += Start-Job -ScriptBlock $workerScript -ArgumentList `
        $workerIndex,$Connections,$segmentCount,$segmentSize,$ContentLength,$partsRoot,$url
}

do {
    $active = @($downloads | Where-Object { $_.State -in "NotStarted","Running" })
    $downloaded = (Get-ChildItem -LiteralPath $partsRoot -Filter "segment-*.bin" -File | Measure-Object Length -Sum).Sum
    Write-Host ("Ollama {0:N1}% ({1:N0}/{2:N0} MB), active={3}" -f `
        (($downloaded / $ContentLength) * 100),
        ($downloaded / 1MB),
        ($ContentLength / 1MB),
        $active.Count)
    if ($active.Count -gt 0) {
        Start-Sleep -Seconds 10
    }
} while ($active.Count -gt 0)

$downloads | Wait-Job | Out-Null
$failed = @($downloads | Where-Object { $_.State -ne "Completed" })
if ($failed.Count -gt 0) {
    $failed | Receive-Job
    throw "One or more Ollama download chunks failed. Run this script again to retry."
}
$downloads | Receive-Job | Out-Null
$downloads | Remove-Job

$output = [IO.File]::Open($archive, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    for ($index = 0; $index -lt $segmentCount; $index++) {
        $part = Join-Path $partsRoot ("segment-{0:D4}.bin" -f $index)
        $input = [IO.File]::OpenRead($part)
        try {
            $input.CopyTo($output)
        }
        finally {
            $input.Dispose()
        }
    }
}
finally {
    $output.Dispose()
}

$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not [String]::Equals($actualHash, $Sha256.ToLowerInvariant(), [StringComparison]::Ordinal)) {
    throw "Ollama archive hash mismatch. Expected $Sha256, got $actualHash."
}

Write-Host "Verified Ollama archive: $archive"
