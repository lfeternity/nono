param(
    [long]$ContentLength = 364816464,
    [string]$Sha256 = "a3f4c73d043860e3fd2e5b06f36795eb81de0fc8e8de6df703245edddd87dbad",
    [int]$Connections = 8,
    [int]$SegmentMB = 2
)

$ErrorActionPreference = "Stop"
$voiceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $voiceRoot "cache"))
$partsRoot = [IO.Path]::GetFullPath((Join-Path $cacheRoot "kokoro-parts"))
$archive = [IO.Path]::GetFullPath((Join-Path $cacheRoot "kokoro-multi-lang-v1_1.tar.bz2"))
$ttsRoot = [IO.Path]::GetFullPath((Join-Path $voiceRoot "models\tts"))
$modelRoot = [IO.Path]::GetFullPath((Join-Path $ttsRoot "kokoro-multi-lang-v1_1"))

foreach ($path in @($cacheRoot, $partsRoot, $archive, $ttsRoot, $modelRoot)) {
    if (-not $path.StartsWith($voiceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Kokoro files must stay inside the voice directory."
    }
}
if ($ContentLength -le 0 -or $Connections -lt 1 -or $Connections -gt 16 -or $SegmentMB -lt 1 -or $SegmentMB -gt 8) {
    throw "Invalid Kokoro download parameters."
}

New-Item -ItemType Directory -Force -Path $cacheRoot,$partsRoot,$ttsRoot | Out-Null
$url = "https://ghproxy.net/https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_1.tar.bz2"
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
            throw "Failed to download Kokoro segment $index."
        }
        if ((Get-Item -LiteralPath $part).Length -ne $expectedLength) {
            throw "Kokoro segment $index has an unexpected size."
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
    Write-Host ("Kokoro {0:N1}% ({1:N0}/{2:N0} MB), active={3}" -f `
        (($downloaded / $ContentLength) * 100),
        ($downloaded / 1MB),
        ($ContentLength / 1MB),
        $active.Count)
    if ($active.Count -gt 0) {
        Start-Sleep -Seconds 5
    }
} while ($active.Count -gt 0)

$downloads | Wait-Job | Out-Null
$failed = @($downloads | Where-Object { $_.State -ne "Completed" })
if ($failed.Count -gt 0) {
    $failed | Receive-Job
    throw "One or more Kokoro download chunks failed. Run this script again to retry."
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
    throw "Kokoro archive hash mismatch. Expected $Sha256, got $actualHash."
}

if (Test-Path -LiteralPath $modelRoot) {
    Remove-Item -LiteralPath $modelRoot -Recurse -Force
}
& tar.exe -xjf $archive -C $ttsRoot
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $modelRoot)) {
    throw "Failed to extract the Kokoro model."
}

Write-Host "Verified and extracted Kokoro model: $modelRoot"
