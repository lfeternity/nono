param(
    [switch]$InstallOllama,
    [switch]$PullChatModel,
    [string]$ChatModel = "qwen3:4b-instruct-2507-q4_K_M",
    [string]$OllamaVersion = "v0.32.4",
    [string]$OllamaSha256 = "4ce7e765dc2bf1bb424a76b96d6631cc0462f5c7507e85f0dc2abf30c564953b"
)

$ErrorActionPreference = "Stop"
$voiceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$venvRoot = Join-Path $voiceRoot ".venv"
$python = Join-Path $venvRoot "Scripts\python.exe"
$modelRoot = Join-Path $voiceRoot "models"
$cacheRoot = Join-Path $voiceRoot "cache"
$ollamaRoot = Join-Path $voiceRoot "ollama"
$ollamaModels = Join-Path $modelRoot "ollama"
$huggingFaceModels = Join-Path $modelRoot "huggingface"
$torchModels = Join-Path $modelRoot "torch"

New-Item -ItemType Directory -Force -Path $modelRoot,$cacheRoot,$ollamaModels,$huggingFaceModels,$torchModels | Out-Null
$env:HF_HOME = $huggingFaceModels
$env:TORCH_HOME = $torchModels
$env:OLLAMA_MODELS = $ollamaModels
$env:PIP_CACHE_DIR = Join-Path $cacheRoot "pip"

if (-not (Test-Path -LiteralPath $python)) {
    $launcher = Get-Command py.exe -ErrorAction Stop
    & $launcher.Source -3.13 -m venv $venvRoot
}

& $python -m pip install --upgrade pip "setuptools<82" wheel
& $python -m pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu128
& $python -m pip install -r (Join-Path $voiceRoot "requirements.txt")
$ttsModel = Join-Path $modelRoot "tts\kokoro-multi-lang-v1_1\model.onnx"
if (-not (Test-Path -LiteralPath $ttsModel)) {
    & (Join-Path $voiceRoot "download_tts.ps1")
}
$env:HF_HUB_OFFLINE = "0"
$env:TRANSFORMERS_OFFLINE = "0"
& $python -c "from huggingface_hub import snapshot_download; snapshot_download('Qwen/Qwen3-ASR-0.6B')"
Remove-Item Env:HF_HUB_OFFLINE -ErrorAction SilentlyContinue
Remove-Item Env:TRANSFORMERS_OFFLINE -ErrorAction SilentlyContinue
& $python (Join-Path $voiceRoot "voice_service.py") --self-test

$ollamaPath = Join-Path $ollamaRoot "ollama.exe"
if ($InstallOllama -and -not (Test-Path -LiteralPath $ollamaPath)) {
    New-Item -ItemType Directory -Force -Path $ollamaRoot | Out-Null
    $ollamaArchive = Join-Path $cacheRoot "ollama-windows-amd64.zip"
    $archiveVerified = $false
    if (Test-Path -LiteralPath $ollamaArchive) {
        $cachedHash = (Get-FileHash -LiteralPath $ollamaArchive -Algorithm SHA256).Hash.ToLowerInvariant()
        $archiveVerified = [String]::Equals($cachedHash, $OllamaSha256.ToLowerInvariant(), [StringComparison]::Ordinal)
    }
    if (-not $archiveVerified) {
        & curl.exe -L --fail --retry 5 --retry-delay 3 -C - `
            -o $ollamaArchive `
            "https://github.com/ollama/ollama/releases/download/$OllamaVersion/ollama-windows-amd64.zip"
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to download bundled Ollama."
        }
    }
    $actualHash = (Get-FileHash -LiteralPath $ollamaArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [String]::Equals($actualHash, $OllamaSha256.ToLowerInvariant(), [StringComparison]::Ordinal)) {
        throw "Ollama archive hash mismatch. Expected $OllamaSha256, got $actualHash."
    }
    Expand-Archive -LiteralPath $ollamaArchive -DestinationPath $ollamaRoot -Force
}

if ($PullChatModel) {
    if (-not (Test-Path -LiteralPath $ollamaPath)) {
        throw "Bundled Ollama was not found. Run setup.ps1 with -InstallOllama."
    }

    $env:OLLAMA_HOST = "127.0.0.1:11434"
    $ollamaServer = Start-Process -FilePath $ollamaPath -ArgumentList "serve" -WindowStyle Hidden -PassThru
    try {
        $ready = $false
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            try {
                Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/tags" -TimeoutSec 2 | Out-Null
                $ready = $true
                break
            }
            catch {
                Start-Sleep -Milliseconds 500
            }
        }

        if (-not $ready) {
            throw "Bundled Ollama did not become ready."
        }
        & $ollamaPath pull $ChatModel
    }
    finally {
        if ($ollamaServer -and -not $ollamaServer.HasExited) {
            Stop-Process -Id $ollamaServer.Id -Force
        }
    }
}

Write-Host "NoNo local voice runtime is ready: $python"
