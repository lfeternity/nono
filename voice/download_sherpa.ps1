param(
    [int]$Connections = 6,
    [int]$SegmentMB = 1
)

$ErrorActionPreference = "Stop"
$voiceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $voiceRoot "cache\sherpa-onnx"))
$python = [IO.Path]::GetFullPath((Join-Path $voiceRoot ".venv\Scripts\python.exe"))
if (-not $cacheRoot.StartsWith($voiceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Sherpa files must stay inside the voice directory."
}
if (-not (Test-Path -LiteralPath $python)) {
    throw "The voice virtual environment was not found. Run voice\\setup.ps1 first."
}
if ($Connections -lt 1 -or $Connections -gt 16 -or $SegmentMB -lt 1 -or $SegmentMB -gt 8) {
    throw "Invalid sherpa-onnx download parameters."
}

New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null

function Get-VerifiedWheel {
    param(
        [string]$Name,
        [string]$Url,
        [long]$ContentLength,
        [string]$Sha256
    )

    $outputPath = [IO.Path]::GetFullPath((Join-Path $cacheRoot $Name))
    $partsRoot = [IO.Path]::GetFullPath((Join-Path $cacheRoot ($Name + ".parts")))
    New-Item -ItemType Directory -Force -Path $partsRoot | Out-Null
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
            if ($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath $part).Length -ne $expectedLength) {
                throw "Failed to download $DownloadUrl segment $index."
            }
        }
    }

    $downloads = @()
    for ($workerIndex = 0; $workerIndex -lt $Connections; $workerIndex++) {
        $downloads += Start-Job -ScriptBlock $workerScript -ArgumentList `
            $workerIndex,$Connections,$segmentCount,$segmentSize,$ContentLength,$partsRoot,$Url
    }
    $downloads | Wait-Job | Out-Null
    $failed = @($downloads | Where-Object { $_.State -ne "Completed" })
    if ($failed.Count -gt 0) {
        $failed | Receive-Job
        throw "One or more sherpa-onnx download chunks failed. Run this script again to retry."
    }
    $downloads | Receive-Job | Out-Null
    $downloads | Remove-Job

    $output = [IO.File]::Open($outputPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
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

    $actualHash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [String]::Equals($actualHash, $Sha256.ToLowerInvariant(), [StringComparison]::Ordinal)) {
        throw "$Name hash mismatch. Expected $Sha256, got $actualHash."
    }
    return $outputPath
}

$core = Get-VerifiedWheel `
    -Name "sherpa_onnx_core-1.13.4-py3-none-win_amd64.whl" `
    -Url "https://files.pythonhosted.org/packages/95/b0/c3d59ac76f3db873e41bd0cb4fc30b352a278da3289217985aaae3650211/sherpa_onnx_core-1.13.4-py3-none-win_amd64.whl" `
    -ContentLength 16450053 `
    -Sha256 "0a6949cf0fd83adb9fbcfdf5c27b8907a57f7b48626db703c7f6037be9b61764"
$api = Get-VerifiedWheel `
    -Name "sherpa_onnx-1.13.4-cp313-cp313-win_amd64.whl" `
    -Url "https://files.pythonhosted.org/packages/82/40/ee8a0a8c83fc6d7f5245a5a031e471d3b115e20cce867e7abb2f9d4185c9/sherpa_onnx-1.13.4-cp313-cp313-win_amd64.whl" `
    -ContentLength 2244504 `
    -Sha256 "17050fdfb48d37ae996364f697c554a1399740d18e5a56b143c011d00cfed3e0"

& $python -m pip install $core $api
if ($LASTEXITCODE -ne 0) {
    throw "Failed to install sherpa-onnx into the local voice environment."
}
Write-Host "Installed sherpa-onnx into: $python"
