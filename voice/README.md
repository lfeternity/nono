# NoNo Local Voice Runtime

This directory contains the private, local voice sidecar used by the standalone pet.

## Setup

Run from PowerShell:

```powershell
.\voice\setup.ps1 -InstallOllama -PullChatModel
```

If GitHub's single-connection download is unusually slow, use the resumable,
hash-verified downloader first, then run setup again:

```powershell
.\voice\download_ollama.ps1
.\voice\setup.ps1 -InstallOllama -PullChatModel
```

If PyPI is unusually slow while installing `sherpa-onnx`, use its verified
multi-connection installer:

```powershell
.\voice\download_sherpa.ps1
```

The setup creates `voice/.venv`, installs the CUDA build of PyTorch, installs the
voice dependencies, downloads Qwen3-ASR and Kokoro v1.1 Chinese TTS into the
project cache, and optionally installs Ollama with
`qwen3:4b-instruct-2507-q4_K_M`. Runtime inference uses the project cache in
offline mode. Kokoro runs on CPU and Windows TTS is retained only as a fallback.
The desktop voice settings expose all 103 Kokoro speaker voices and store the
selected speaker locally.

Everything is kept inside this directory:

- Python and packages: `voice/.venv`
- Bundled Ollama: `voice/ollama`
- Qwen3-ASR, Silero, and Ollama models: `voice/models`
- Download caches: `voice/cache`

## Privacy

- Microphone audio remains in memory.
- Audio is transcribed locally with `Qwen3-ASR-0.6B`.
- Only the local Ollama endpoint is contacted for answers.
- Raw audio is not written to disk.

## Protocol

The service writes newline-delimited JSON events to stdout and reads commands
from stdin. Run the dependency-free protocol self-test with:

```powershell
python .\voice\voice_service.py --self-test
```
