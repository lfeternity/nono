#!/usr/bin/env python3
"""Local microphone, wake phrase, ASR, and Ollama service for NoNo."""

from __future__ import annotations

import argparse
import collections
import json
import os
import queue
import re
import sys
import threading
import time
import traceback
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any


# Runtime inference is local-only. setup.ps1 is responsible for downloading models.
os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
os.environ.setdefault("TRANSFORMERS_VERBOSITY", "error")
os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")


SAMPLE_RATE = 16000
FRAME_SAMPLES = 512
DEFAULT_TTS_VOICE = 3
KOKORO_SPEAKER_COUNT = 103
DEFAULT_WAKE_PHRASES = ["nono", "诺诺", "你好 nono"]
EXIT_PHRASES = {"结束", "退出对话", "不用了", "停止对话", "再见"}
CONTROL_PHRASES = {
    "停下", "停止", "停止操作", "取消操作", "别动了", "立即停止", "stop", "cancel",
    "确认", "确定", "继续", "执行", "可以", "是", "yes", "confirm",
    "取消", "不要", "不执行", "否", "算了", "no", "reject",
}
DIAGNOSTIC_LOG = Path(__file__).resolve().parent / "cache" / "voice-service.log"
_LOG_LOCK = threading.Lock()


def normalize_tts_voice(value: Any) -> int:
    try:
        voice = int(value)
    except (TypeError, ValueError):
        return DEFAULT_TTS_VOICE
    return voice if 0 <= voice < KOKORO_SPEAKER_COUNT else DEFAULT_TTS_VOICE


def diagnostic_log(message: str) -> None:
    try:
        DIAGNOSTIC_LOG.parent.mkdir(parents=True, exist_ok=True)
        line = time.strftime("%Y-%m-%d %H:%M:%S") + " " + str(message).replace("\r", " ").replace("\n", " | ")
        with _LOG_LOCK, DIAGNOSTIC_LOG.open("a", encoding="utf-8") as stream:
            stream.write(line + "\n")
    except OSError:
        pass


def process_is_alive(process_id: int) -> bool:
    if process_id <= 0:
        return True
    if os.name != "nt":
        try:
            os.kill(process_id, 0)
            return True
        except OSError:
            return False

    try:
        import ctypes

        synchronize = 0x00100000
        wait_timeout = 0x00000102
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(synchronize, False, process_id)
        if not handle:
            return False
        try:
            return kernel32.WaitForSingleObject(handle, 0) == wait_timeout
        finally:
            kernel32.CloseHandle(handle)
    except Exception:
        return True


def emit(event_type: str, **payload: Any) -> None:
    message = {"type": event_type, **payload}
    print(json.dumps(message, ensure_ascii=False, separators=(",", ":")), flush=True)


def log(message: str) -> None:
    diagnostic_log(message)
    print(message, file=sys.stderr, flush=True)


def normalize_text(value: str) -> str:
    value = (value or "").casefold()
    value = value.replace("no no", "nono").replace("no-no", "nono")
    return "".join(ch for ch in value if ch.isalnum() or "\u4e00" <= ch <= "\u9fff")


def repair_surrogateescaped_text(value: str) -> str:
    value = value or ""
    if not any("\udc80" <= character <= "\udcff" for character in value):
        return value

    raw = value.encode("utf-8", errors="surrogateescape")
    try:
        return raw.decode("gb18030")
    except UnicodeDecodeError:
        return raw.decode("utf-8", errors="replace")


def find_wake_phrase(text: str, phrases: list[str]) -> tuple[str | None, str]:
    normalized = normalize_text(text)
    ordered = sorted(phrases, key=lambda item: len(normalize_text(item)), reverse=True)
    for phrase in ordered:
        candidate = normalize_text(phrase)
        index = normalized.find(candidate)
        if index < 0:
            continue

        remainder = normalized[index + len(candidate) :]
        return phrase, remainder
    return None, ""


def remove_wake_phrase(text: str, phrase: str) -> str:
    normalized_phrase = normalize_text(phrase)
    if not normalized_phrase:
        return (text or "").strip()

    # ASR may insert or remove spaces and punctuation inside mixed Chinese/English phrases.
    separator = r"[^\w\u4e00-\u9fff]*"
    pattern = separator.join(re.escape(character) for character in normalized_phrase)
    return re.sub(pattern, "", text or "", count=1, flags=re.IGNORECASE).strip(" ，,。.!！?？")


def strip_thinking(text: str) -> str:
    text = re.sub(r"<think>[\s\S]*?</think>", "", text or "", flags=re.IGNORECASE)
    text = re.sub(r"^\s*<think>[\s\S]*$", "", text, flags=re.IGNORECASE)
    return text.strip()


def default_config() -> dict[str, Any]:
    return {
        "asr_model": "Qwen/Qwen3-ASR-0.6B",
        "device": "cuda:0",
        "wake_phrases": list(DEFAULT_WAKE_PHRASES),
        "vad_threshold": 0.55,
        "vad_release_threshold": 0.35,
        "end_silence_ms": 600,
        "wake_end_silence_ms": 620,
        "command_end_silence_ms": 480,
        "long_end_silence_ms": 680,
        "max_utterance_seconds": 15,
        "follow_up_seconds": 30,
        "tts_model_dir": "models/tts/kokoro-multi-lang-v1_1",
        "tts_voice": DEFAULT_TTS_VOICE,
        "tts_threads": 4,
        "ollama_url": "http://127.0.0.1:11434/api/chat",
        "ollama_model": "qwen3:4b-instruct-2507-q4_K_M",
        "external_conversation": False,
        "system_prompt": (
            "你是诺诺，一只安静、准确、简洁的桌面 AI 宠物。"
            "默认使用中文回答，除非用户要求其他语言。回答适合直接朗读，避免 Markdown 表格和冗长列表。"
        ),
    }


def load_config(path: Path | None) -> dict[str, Any]:
    result = default_config()
    if path is None or not path.exists():
        return result

    with path.open("r", encoding="utf-8-sig") as stream:
        loaded = json.load(stream)
    if isinstance(loaded, dict):
        result.update(loaded)
    phrases = result.get("wake_phrases")
    if not isinstance(phrases, list) or not any(str(item).strip() for item in phrases):
        result["wake_phrases"] = list(DEFAULT_WAKE_PHRASES)
    else:
        result["wake_phrases"] = [str(item).strip() for item in phrases if str(item).strip()]
    return result


@dataclass
class SpeechSegmenter:
    threshold: float
    release_threshold: float
    end_silence_ms: int
    max_seconds: int
    long_end_silence_ms: int | None = None

    def __post_init__(self) -> None:
        self.pre_roll: collections.deque[Any] = collections.deque(maxlen=10)
        self.frames: list[Any] = []
        self.in_speech = False
        self.start_count = 0
        self.quiet_count = 0
        self.frame_ms = FRAME_SAMPLES * 1000 / SAMPLE_RATE
        self.long_speech_frames = max(1, int(4.0 * SAMPLE_RATE / FRAME_SAMPLES))
        self.set_end_silence_ms(self.end_silence_ms)
        configured_long = self.end_silence_ms if self.long_end_silence_ms is None else self.long_end_silence_ms
        self.long_end_frames = max(self.end_frames, int(configured_long / self.frame_ms))
        self.max_frames = max(1, int(self.max_seconds * SAMPLE_RATE / FRAME_SAMPLES))

    def set_end_silence_ms(self, milliseconds: int) -> None:
        self.end_silence_ms = max(32, min(1200, int(milliseconds)))
        self.end_frames = max(4, int(self.end_silence_ms / self.frame_ms))

    def reset(self) -> None:
        self.pre_roll.clear()
        self.frames.clear()
        self.in_speech = False
        self.start_count = 0
        self.quiet_count = 0

    def accept(self, frame: Any, probability: float) -> Any | None:
        if not self.in_speech:
            self.pre_roll.append(frame.copy())
            self.start_count = self.start_count + 1 if probability >= self.threshold else 0
            if self.start_count >= 2:
                self.in_speech = True
                self.frames = list(self.pre_roll)
                self.quiet_count = 0
            return None

        self.frames.append(frame.copy())
        if probability < self.release_threshold:
            self.quiet_count += 1
        else:
            self.quiet_count = 0

        required_quiet_frames = (
            self.long_end_frames if len(self.frames) >= self.long_speech_frames else self.end_frames
        )
        if self.quiet_count >= required_quiet_frames or len(self.frames) >= self.max_frames:
            result = list(self.frames)
            self.reset()
            return result
        return None


class OllamaConversation:
    def __init__(self, config: dict[str, Any]) -> None:
        self.url = str(config["ollama_url"])
        self.model = str(config["ollama_model"])
        self.system_prompt = str(config["system_prompt"])
        self.history: list[dict[str, str]] = []

    def ask(self, question: str) -> str:
        messages = [{"role": "system", "content": self.system_prompt}]
        messages.extend(self.history[-10:])
        messages.append({"role": "user", "content": question})
        body = json.dumps(
            {
                "model": self.model,
                "messages": messages,
                "stream": False,
                "think": False,
                "options": {"temperature": 0.35, "num_predict": 256},
            },
            ensure_ascii=False,
        ).encode("utf-8")
        request = urllib.request.Request(
            self.url,
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                payload = json.loads(response.read().decode("utf-8"))
        except urllib.error.URLError as exc:
            raise RuntimeError(
                "无法连接本地 Ollama。请启动 Ollama，并确认已安装模型 " + self.model
            ) from exc

        answer = strip_thinking(str(payload.get("message", {}).get("content", "")))
        if not answer:
            answer = strip_thinking(str(payload.get("response", "")))
        if not answer:
            raise RuntimeError("Ollama 没有返回可朗读的回答。")

        self.history.extend(
            [
                {"role": "user", "content": question},
                {"role": "assistant", "content": answer},
            ]
        )
        self.history = self.history[-12:]
        return answer

    def clear(self) -> None:
        self.history.clear()


class LocalNeuralTts:
    def __init__(self, config: dict[str, Any], sounddevice: Any) -> None:
        configured_root = Path(str(config.get("tts_model_dir", "")))
        if not configured_root.is_absolute():
            configured_root = Path(__file__).resolve().parent / configured_root
        self.model_root = configured_root.resolve()
        self.voice = normalize_tts_voice(config.get("tts_voice", DEFAULT_TTS_VOICE))
        self.threads = max(1, min(8, int(config.get("tts_threads", 4))))
        self.sd = sounddevice
        self.engine: Any = None
        self.sherpa: Any = None

    def load(self) -> None:
        required = [
            self.model_root / "model.onnx",
            self.model_root / "voices.bin",
            self.model_root / "tokens.txt",
            self.model_root / "espeak-ng-data",
            self.model_root / "lexicon-us-en.txt",
            self.model_root / "lexicon-zh.txt",
            self.model_root / "phone-zh.fst",
            self.model_root / "date-zh.fst",
            self.model_root / "number-zh.fst",
        ]
        missing = [str(path) for path in required if not path.exists()]
        if missing:
            raise RuntimeError("Kokoro 本地人声模型不完整，缺少: " + ", ".join(missing))

        try:
            import sherpa_onnx
        except ImportError as exc:
            raise RuntimeError("Kokoro 运行库尚未安装，请重新运行 voice\\setup.ps1。") from exc

        lexicons = ",".join(
            str(self.model_root / name) for name in ("lexicon-us-en.txt", "lexicon-zh.txt")
        )
        tts_config = sherpa_onnx.OfflineTtsConfig(
            model=sherpa_onnx.OfflineTtsModelConfig(
                kokoro=sherpa_onnx.OfflineTtsKokoroModelConfig(
                    model=str(self.model_root / "model.onnx"),
                    voices=str(self.model_root / "voices.bin"),
                    tokens=str(self.model_root / "tokens.txt"),
                    data_dir=str(self.model_root / "espeak-ng-data"),
                    lexicon=lexicons,
                ),
                provider="cpu",
                debug=False,
                num_threads=self.threads,
            ),
            rule_fsts=",".join(
                str(self.model_root / name)
                for name in ("phone-zh.fst", "date-zh.fst", "number-zh.fst")
            ),
            max_num_sentences=1,
        )
        if not tts_config.validate():
            raise RuntimeError("Kokoro 本地人声模型配置无效。")

        self.sherpa = sherpa_onnx
        self.engine = sherpa_onnx.OfflineTts(tts_config)
        if not 0 <= self.voice < self.engine.num_speakers:
            diagnostic_log(
                "invalid Kokoro voice " + str(self.voice) + "; using default " + str(DEFAULT_TTS_VOICE)
            )
            self.voice = DEFAULT_TTS_VOICE
        diagnostic_log(
            "Kokoro TTS ready; voice=" + str(self.voice) + "; threads=" + str(self.threads)
        )

    def speak(self, text: str, speed: float) -> None:
        if self.engine is None or self.sherpa is None:
            raise RuntimeError("Kokoro 本地人声尚未就绪。")

        text = repair_surrogateescaped_text(text)
        generation = self.sherpa.GenerationConfig()
        generation.sid = self.voice
        generation.speed = max(0.7, min(1.4, float(speed)))
        generation.silence_scale = 0.2
        started = time.monotonic()
        audio = self.engine.generate(text, generation)
        if len(audio.samples) == 0:
            raise RuntimeError("Kokoro 没有生成可播放的语音。")

        duration = len(audio.samples) / float(audio.sample_rate)
        diagnostic_log(
            "Kokoro generated; characters="
            + str(len(text))
            + "; seconds="
            + f"{duration:.2f}"
            + "; generation_seconds="
            + f"{time.monotonic() - started:.2f}"
        )
        self.sd.play(audio.samples, samplerate=audio.sample_rate, blocking=True)


class LocalVoiceService:
    def __init__(self, config: dict[str, Any], parent_pid: int = 0) -> None:
        self.config = config
        self.parent_pid = parent_pid
        self.commands: queue.Queue[dict[str, Any]] = queue.Queue()
        self.audio_queue: queue.Queue[Any] = queue.Queue(maxsize=256)
        self.shutdown = threading.Event()
        self.playback = threading.Event()
        self.manual_capture = threading.Event()
        self.mode = "wake"
        self.follow_up_deadline = 0.0
        self.question_sequence = 0
        self.model: Any = None
        self.vad_model: Any = None
        self.torch: Any = None
        self.np: Any = None
        self.sd: Any = None
        self.tts: LocalNeuralTts | None = None
        self.tts_error = ""
        self.conversation = OllamaConversation(config)

    def _start_parent_monitor(self) -> None:
        if self.parent_pid <= 0:
            return

        def monitor_parent() -> None:
            while not self.shutdown.wait(1.0):
                if not process_is_alive(self.parent_pid):
                    diagnostic_log("parent process exited; stopping voice service")
                    self.shutdown.set()
                    return

        threading.Thread(target=monitor_parent, name="voice-parent-monitor", daemon=True).start()

    def _load_dependencies(self) -> None:
        diagnostic_log("loading dependencies")
        emit("state", state="loading", message="正在加载本地语音组件")
        try:
            import numpy as np
            import sounddevice as sd
            import torch
            from qwen_asr import Qwen3ASRModel
            from silero_vad import load_silero_vad
        except ImportError as exc:
            raise RuntimeError(
                "本地语音运行库尚未安装，请先运行 voice\\setup.ps1。缺少: " + str(exc)
            ) from exc

        self.np = np
        self.sd = sd
        self.torch = torch

        requested_device = str(self.config.get("device", "cuda:0"))
        use_cuda = requested_device.startswith("cuda") and torch.cuda.is_available()
        if requested_device.startswith("cuda") and not use_cuda:
            emit("warning", message="CUDA 不可用，Qwen3-ASR 将回退到 CPU，响应会明显变慢。")
        device = requested_device if use_cuda else "cpu"
        dtype = torch.bfloat16 if use_cuda else torch.float32

        emit(
            "state",
            state="loading",
            message="正在从项目缓存加载 Qwen3-ASR-0.6B",
        )
        self.model = Qwen3ASRModel.from_pretrained(
            str(self.config["asr_model"]),
            dtype=dtype,
            device_map=device,
            max_inference_batch_size=1,
            max_new_tokens=256,
        )
        self.vad_model = load_silero_vad(onnx=True)
        try:
            self.tts = LocalNeuralTts(self.config, self.sd)
            self.tts.load()
            emit("tts_ready", voice=self.tts.voice)
        except Exception as exc:
            self.tts = None
            self.tts_error = str(exc)
            diagnostic_log("Kokoro TTS unavailable: " + traceback.format_exc())
            emit("warning", message="本地自然人声不可用，将使用系统语音。" + str(exc))
        diagnostic_log("dependencies ready; device=" + device)

    def _start_command_reader(self) -> None:
        def read_commands() -> None:
            for line in sys.stdin:
                if self.shutdown.is_set():
                    break
                try:
                    # .NET Framework may prefix the first redirected stdin write
                    # with a UTF-8 BOM. Accept it so control commands are not lost.
                    command = json.loads(line.lstrip("\ufeff"))
                    if isinstance(command, dict):
                        self.commands.put(command)
                        diagnostic_log("command received: " + str(command.get("type", "")))
                except json.JSONDecodeError:
                    log("Ignored invalid command: " + line.rstrip())
            diagnostic_log("command channel reached EOF; keeping voice service alive")

        threading.Thread(target=read_commands, name="voice-command-reader", daemon=True).start()

    def _audio_callback(self, indata: Any, frames: int, time_info: Any, status: Any) -> None:
        del frames, time_info
        if status:
            log("Audio status: " + str(status))
        if self.shutdown.is_set() or self.playback.is_set():
            return
        try:
            self.audio_queue.put_nowait(indata[:, 0].copy())
        except queue.Full:
            try:
                self.audio_queue.get_nowait()
                self.audio_queue.put_nowait(indata[:, 0].copy())
            except queue.Empty:
                pass

    def _process_commands(self) -> None:
        while True:
            try:
                command = self.commands.get_nowait()
            except queue.Empty:
                break
            command_type = str(command.get("type", ""))
            if command_type == "shutdown":
                self.shutdown.set()
            elif command_type == "start_capture":
                self.mode = "command"
                self.follow_up_deadline = time.monotonic() + 30
                self.manual_capture.set()
                emit("state", state="listening_command", message="请说，我在听")
            elif command_type == "speech_started":
                self.playback.set()
            elif command_type == "speech_done":
                self.playback.clear()
                self._flush_audio()
                self.mode = "command"
                self.follow_up_deadline = time.monotonic() + float(self.config["follow_up_seconds"])
                emit("state", state="listening_followup", message="可以继续说")
            elif command_type == "speak":
                self._speak(str(command.get("text", "")), command.get("speed", 1.0))
            elif command_type == "ask_local":
                question = str(command.get("text", "")).strip()
                if question:
                    emit("state", state="thinking", message="正在思考")
                    self._answer_local(question, str(command.get("request_id", "")))
            elif command_type == "clear_history":
                self.conversation.clear()
                emit("history_cleared")

    def _flush_audio(self) -> None:
        while True:
            try:
                self.audio_queue.get_nowait()
            except queue.Empty:
                return

    def _transcribe(self, audio: Any) -> str:
        result = self.model.transcribe(audio=(audio, SAMPLE_RATE), language=None)
        if not result:
            return ""
        return str(result[0].text).strip()

    def _speak(self, text: str, requested_speed: Any) -> None:
        repaired_text = repair_surrogateescaped_text(text)
        if repaired_text != text:
            diagnostic_log("repaired legacy-encoded TTS command text")
        text = repaired_text.strip()
        if not text:
            self._finish_speech()
            return
        try:
            speed = max(0.7, min(1.4, float(requested_speed)))
        except (TypeError, ValueError):
            speed = 1.0

        self.playback.set()
        try:
            if self.tts is None:
                raise RuntimeError(self.tts_error or "Kokoro 本地人声尚未就绪。")
            self.tts.speak(text, speed)
        except Exception as exc:
            try:
                self.sd.stop()
            except Exception:
                pass
            diagnostic_log("Kokoro speech failure: " + traceback.format_exc())
            emit("tts_error", message=str(exc), text=text)
            return

        emit("speech_finished")
        self._finish_speech()

    def _finish_speech(self) -> None:
        self.playback.clear()
        self._flush_audio()
        self.mode = "command"
        self.follow_up_deadline = time.monotonic() + float(self.config["follow_up_seconds"])
        emit("state", state="listening_followup", message="可以继续说")

    def _handle_question(self, question: str) -> None:
        if normalize_text(question) in {normalize_text(item) for item in EXIT_PHRASES}:
            self.mode = "wake"
            self.follow_up_deadline = 0
            emit("state", state="listening_wake", message="等待唤醒")
            return

        self.question_sequence += 1
        request_id = str(self.question_sequence)
        emit("question", text=question, request_id=request_id)
        emit("state", state="thinking", message="正在思考")
        is_control_phrase = normalize_text(question) in {normalize_text(item) for item in CONTROL_PHRASES}
        if bool(self.config.get("external_conversation", False)) or is_control_phrase:
            # C# owns cloud routing, approvals, desktop actions, and the final
            # speak command. Keep capture active so a local "stop" utterance
            # can cancel a cloud request before the answer arrives.
            emit("external_question", text=question, request_id=request_id)
            return
        self._answer_local(question, request_id)

    def _answer_local(self, question: str, request_id: str = "") -> None:
        try:
            answer = self.conversation.ask(question)
        except Exception as exc:
            emit("error", message=str(exc), request_id=request_id, recoverable=True)
            self.mode = "wake"
            emit("state", state="listening_wake", message="等待唤醒")
            return

        self.playback.set()
        emit("answer", text=answer, request_id=request_id)
        emit("state", state="speaking", message="正在回答")

    def _handle_segment(self, audio: Any) -> None:
        diagnostic_log("processing speech segment; samples=" + str(len(audio)))
        emit("state", state="transcribing", message="正在识别")
        text = self._transcribe(audio)
        if not text:
            diagnostic_log("ASR returned no text")
            emit("state", state="listening_command" if self.mode == "command" else "listening_wake")
            return

        diagnostic_log("ASR completed; characters=" + str(len(text)))
        emit("transcript", text=text)
        if self.mode == "wake":
            phrase, remainder = find_wake_phrase(text, list(self.config["wake_phrases"]))
            if phrase is None:
                emit("state", state="listening_wake", message="等待唤醒")
                return

            emit("wake", phrase=phrase, transcript=text)
            if remainder:
                question = remove_wake_phrase(text, phrase)
                self._handle_question(question or remainder)
            else:
                self.mode = "command"
                self.follow_up_deadline = time.monotonic() + 20
                emit("state", state="listening_command", message="请说，我在听")
            return

        self._handle_question(text)

    def run(self) -> int:
        diagnostic_log("voice service starting; pid=" + str(os.getpid()))
        self._start_parent_monitor()
        try:
            self._load_dependencies()
        except Exception as exc:
            diagnostic_log("dependency failure: " + traceback.format_exc())
            emit("error", message=str(exc), fatal=True)
            return 2

        # Importing NumPy/PortAudio while another thread blocks on a redirected
        # stdin pipe can deadlock on Windows. Start command I/O after model setup.
        self._start_command_reader()

        segmenter = SpeechSegmenter(
            threshold=float(self.config["vad_threshold"]),
            release_threshold=float(self.config["vad_release_threshold"]),
            end_silence_ms=int(self.config["end_silence_ms"]),
            max_seconds=int(self.config["max_utterance_seconds"]),
            long_end_silence_ms=int(self.config.get("long_end_silence_ms", 680)),
        )
        try:
            device_info = self.sd.query_devices(kind="input")
            emit("microphone", name=str(device_info.get("name", "默认麦克风")))
            stream = self.sd.InputStream(
                samplerate=SAMPLE_RATE,
                channels=1,
                dtype="float32",
                blocksize=FRAME_SAMPLES,
                callback=self._audio_callback,
            )
            stream.start()
            diagnostic_log("microphone ready: " + str(device_info.get("name", "default")))
        except Exception as exc:
            diagnostic_log("microphone failure: " + traceback.format_exc())
            emit(
                "error",
                message="无法打开麦克风，请检查 Windows 麦克风隐私权限和默认输入设备。" + str(exc),
                fatal=True,
            )
            return 3

        emit("ready", model=str(self.config["asr_model"]))
        emit("state", state="listening_wake", message="等待唤醒")
        try:
            while not self.shutdown.is_set():
                self._process_commands()
                if self.playback.is_set():
                    segmenter.reset()
                    self._flush_audio()
                    time.sleep(0.05)
                    continue

                if self.mode == "command" and self.follow_up_deadline:
                    if time.monotonic() > self.follow_up_deadline:
                        self.mode = "wake"
                        self.follow_up_deadline = 0
                        emit("state", state="listening_wake", message="等待唤醒")

                try:
                    frame = self.audio_queue.get(timeout=0.1)
                except queue.Empty:
                    continue
                try:
                    tensor = self.torch.from_numpy(frame)
                    probability = float(self.vad_model(tensor, SAMPLE_RATE).item())
                    endpoint_ms = (
                        self.config.get("command_end_silence_ms", 480)
                        if self.mode == "command"
                        else self.config.get("wake_end_silence_ms", 620)
                    )
                    segmenter.set_end_silence_ms(int(endpoint_ms))
                    segment_frames = segmenter.accept(frame, probability)
                    if segment_frames is None:
                        continue

                    audio = self.np.concatenate(segment_frames).astype(self.np.float32, copy=False)
                    if len(audio) < int(SAMPLE_RATE * 0.3):
                        continue
                    self._handle_segment(audio)
                    segmenter.reset()
                    self.vad_model.reset_states()
                    self._flush_audio()
                except Exception as exc:
                    diagnostic_log("recoverable speech pipeline failure: " + traceback.format_exc())
                    emit("error", message="语音识别失败，已恢复监听：" + str(exc), recoverable=True)
                    segmenter.reset()
                    try:
                        self.vad_model.reset_states()
                    except Exception:
                        pass
                    self._flush_audio()
                    if self.mode == "command":
                        self.follow_up_deadline = time.monotonic() + float(self.config["follow_up_seconds"])
                        emit("state", state="listening_command", message="请再说一次")
                    else:
                        emit("state", state="listening_wake", message="等待唤醒")
        except KeyboardInterrupt:
            pass
        except Exception as exc:
            diagnostic_log("fatal service loop failure: " + traceback.format_exc())
            emit("error", message="本地语音服务异常: " + str(exc), fatal=True)
            return 4
        finally:
            try:
                stream.stop()
                stream.close()
            except Exception:
                diagnostic_log("microphone close failure: " + traceback.format_exc())
            diagnostic_log("voice service stopped")
        return 0


def run_self_test() -> int:
    cases = [
        ("Nono", "nono", ""),
        ("NO NO，帮我看看天气", "nono", "帮我看看天气"),
        ("诺诺。", "诺诺", ""),
        ("你好 Nono，解释这段代码", "你好 nono", "解释这段代码"),
        ("普通的一句话", None, ""),
    ]
    failures: list[str] = []
    for text, expected_phrase, expected_remainder in cases:
        phrase, remainder = find_wake_phrase(text, DEFAULT_WAKE_PHRASES)
        if phrase != expected_phrase or remainder != normalize_text(expected_remainder):
            failures.append(
                f"{text!r}: got {(phrase, remainder)!r}, expected {(expected_phrase, normalize_text(expected_remainder))!r}"
            )
    if normalize_text("No-No!") != "nono":
        failures.append("No-No normalization failed")
    removal_cases = [
        ("你好Nono，解释这段代码", "你好 nono", "解释这段代码"),
        ("你好，Nono 帮我看 C sharp", "你好 nono", "帮我看 C sharp"),
        ("NO NO，今天天气", "nono", "今天天气"),
    ]
    for text, phrase, expected in removal_cases:
        actual = remove_wake_phrase(text, phrase)
        if actual != expected:
            failures.append(f"wake removal failed: {text!r} -> {actual!r}")
    if strip_thinking("<think>过程</think>答案") != "答案":
        failures.append("thinking removal failed")
    legacy_text = b"\xc4\xe3\xba\xc3".decode("utf-8", errors="surrogateescape")
    if repair_surrogateescaped_text(legacy_text) != "你好":
        failures.append("legacy TTS text encoding repair failed")
    if normalize_tts_voice("invalid") != DEFAULT_TTS_VOICE:
        failures.append("invalid TTS voice fallback failed")
    if normalize_tts_voice(KOKORO_SPEAKER_COUNT) != DEFAULT_TTS_VOICE:
        failures.append("out-of-range TTS voice fallback failed")
    normalized_controls = {normalize_text(item) for item in CONTROL_PHRASES}
    for control_phrase in ("停下", "确认", "取消"):
        if normalize_text(control_phrase) not in normalized_controls:
            failures.append("voice control phrase is not externally routable: " + control_phrase)
    try:
        command = json.loads('\ufeff{"type":"start_capture"}'.lstrip("\ufeff"))
        if command.get("type") != "start_capture":
            failures.append("BOM command parsing failed")
    except json.JSONDecodeError:
        failures.append("BOM command parsing failed")
    segmenter = SpeechSegmenter(0.5, 0.3, 32, 2)
    segment = None
    for probability in [0.6, 0.6, 0.7, 0.1, 0.1, 0.1, 0.1]:
        segment = segmenter.accept([probability], probability) or segment
    if not segment:
        failures.append("speech segment was cleared before processing")
    adaptive_segmenter = SpeechSegmenter(0.5, 0.3, 480, 10, 680)
    if adaptive_segmenter.end_frames >= adaptive_segmenter.long_end_frames:
        failures.append("adaptive endpointing did not preserve a longer window for long speech")
    if not process_is_alive(os.getpid()):
        failures.append("parent process monitor cannot see current process")

    if failures:
        for failure in failures:
            log(failure)
        emit("self_test", ok=False, failures=len(failures))
        return 1
    emit("self_test", ok=True, cases=len(cases) + len(removal_cases) + 9)
    return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path)
    parser.add_argument("--parent-pid", type=int, default=0)
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.self_test:
        return run_self_test()
    try:
        config = load_config(args.config)
    except Exception as exc:
        emit("error", message="读取语音配置失败: " + str(exc), fatal=True)
        return 1
    return LocalVoiceService(config, parent_pid=args.parent_pid).run()


if __name__ == "__main__":
    raise SystemExit(main())
