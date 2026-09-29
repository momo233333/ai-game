from __future__ import annotations

import argparse
import json
import os
import sys

SERVICE_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, SERVICE_DIR)

from config import load_config
from pipeline import DialoguePipeline

INTENT_GLYPH = {
    "TURN_LEFT": "↰左转",
    "TURN_RIGHT": "↱右转",
    "ACCELERATE": "↑加速",
    "BRAKE": "↓刹车",
    "LANE_CHANGE": "⇄变道",
    "PARK": "⏻靠边停车",
    "IGNORE": "—观望",
    "QUESTION_BACK": "?反问",
}


def render_event(event, out) -> None:
    kind, data = event.kind, event.data
    if kind == "asr_final":
        out.write(f"  [玩家] {data.get('text', '')}\n")
    elif kind == "drive_action":
        glyph = INTENT_GLYPH.get(data.get("intent", ""), data.get("intent"))
        refused = " (已拦截)" if data.get("refused") else ""
        out.write(
            f"  [动作] {glyph} steering={data['steering']:+.2f} throttle={data['throttle']:.2f} "
            f"brake={data['brake']:.2f} hold={data['hold_ms']}ms{refused}\n"
        )
    elif kind == "llm_delta":
        out.write(data.get("text", ""))
        out.flush()
    elif kind == "tts_audio":
        route = data.get("route")
        voice = "语音库" if data.get("from_voicelib") else f"实时TTS/{route}"
        out.write(f"\n  [TTS ] ({voice}) {data.get('text', '')}\n")
    elif kind == "state_delta":
        out.write(f"  [状态] 信任 {data['trust']:.0f}/100 ({data['trust_delta']:+.0f}) 情绪={data['emotion']}\n")
    elif kind == "error":
        out.write(f"  [错误] {data.get('code')}: {data.get('message')}\n")
    elif kind == "asr_partial":
        out.write(f"\r  [字幕] {data.get('text', '')}   ")
        out.flush()
    elif kind in ("state", "health", "ack"):
        out.write(f"  [查询] {json.dumps(data, ensure_ascii=False)}\n")


def run_local(cfg: dict, once: str | None) -> int:
    pipeline = DialoguePipeline(cfg, SERVICE_DIR)
    print(json.dumps(pipeline.health, ensure_ascii=False, indent=2))
    print("输入中文驾驶指令开始（/state /event <key> /health /quit）\n")
    if once is not None:
        for event in pipeline.handle_text(once):
            render_event(event, sys.stdout)
        return 0
    while True:
        try:
            line = input("你> ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            return 0
        if not line:
            continue
        if line in ("/quit", "/exit", "/q"):
            return 0
        if line == "/health":
            print(json.dumps(pipeline.health, ensure_ascii=False, indent=2))
            continue
        if line == "/state":
            from core.types import to_dict

            print(json.dumps(to_dict(pipeline.state), ensure_ascii=False, indent=2))
            continue
        if line.startswith("/event "):
            delta = pipeline.state_manager.apply_local_event(line.split(maxsplit=1)[1].strip())
            if delta:
                print(f"  [状态] 信任 {delta.trust:.0f} ({delta.trust_delta:+.0f}) 情绪={delta.emotion}")
            else:
                print("  [错误] 未知事件，可选: collision red_light lane_cut safe_arrival smooth_drive honk")
            continue
        print()
        for event in pipeline.handle_text(line):
            render_event(event, sys.stdout)
        print()


class RemoteEvent:
    def __init__(self, kind: str, data: dict):
        self.kind = kind
        self.data = data


def _build_voice_pipeline(cfg: dict) -> DialoguePipeline | None:
    try:
        from core.asr import create_asr
        from core.vad import create_vad
    except ImportError as exc:
        print(f"  [错误] 语音依赖缺失: {exc}", file=sys.stderr)
        return None
    asr = create_asr(cfg, service_dir=SERVICE_DIR)
    vad = create_vad(cfg)
    if asr is None:
        print(
            "  [错误] ASR 未就绪：pip install funasr，或下载 "
            "vosk-model-small-cn-0.22 解压到 ai-service/models/",
            file=sys.stderr,
        )
        return None
    pipeline = DialoguePipeline(cfg, SERVICE_DIR, asr=asr, vad=vad)
    min_sil = int(cfg.get("vad", {}).get("min_silence_ms", 600))
    print(f"  [ASR ] backend={asr.name} 停顿约 {min_sil}ms 判定说完，TTS 当前为字幕输出\n")
    return pipeline


def run_mic(cfg: dict, free: bool = False) -> int:
    pipeline = _build_voice_pipeline(cfg)
    if pipeline is None:
        return 1
    import time

    from core.mic import MicCapture

    mic_cfg = cfg.get("mic", {})
    device = mic_cfg.get("device")
    block_ms = int(mic_cfg.get("block_ms", 100))
    try:
        with MicCapture(device=device, block_ms=block_ms) as mic:
            if free:
                return _free_talk_loop(pipeline, mic)
            return _ptt_loop(pipeline, mic, cfg)
    except KeyboardInterrupt:
        print()
        return 0
    except Exception as exc:
        print(f"  [错误] 麦克风启动失败: {exc}", file=sys.stderr)
        return 1


def _free_talk_loop(pipeline: DialoguePipeline, mic) -> int:
    print("[mic] 免提模式：说完停顿半秒自动结束，Ctrl+C 结束\n")
    try:
        while True:
            for event in pipeline.handle_audio(mic.frames()):
                render_event(event, sys.stdout)
            print()
    except KeyboardInterrupt:
        print()
        return 0


def _ptt_loop(pipeline: DialoguePipeline, mic, cfg: dict) -> int:
    import time

    from core.ptt import PttListener, ptt_label

    with PttListener() as ptt:
        print(f"[PTT] 按住 {ptt_label(ptt.key)} 说话，松开发送（Ctrl+C 退出）")
        while True:
            while not ptt.held():
                mic.drain()
                time.sleep(0.05)
            print("\n  [录音中] 松开 Ctrl 结束…")
            it = mic.frames()

            def chunks():
                while ptt.held():
                    try:
                        yield next(it)
                    except StopIteration:
                        return

            for event in pipeline.handle_audio(chunks(), stop_on_silence=False):
                render_event(event, sys.stdout)
            print()


def run_wav(cfg: dict, path: str) -> int:
    pipeline = _build_voice_pipeline(cfg)
    if pipeline is None:
        return 1
    from core.audio import wav_chunks

    rate = int(cfg.get("asr", {}).get("sample_rate", 16000))
    print(f"[wav ] {path}\n")
    try:
        for event in pipeline.handle_audio(wav_chunks(path, target_rate=rate)):
            render_event(event, sys.stdout)
    except FileNotFoundError:
        print(f"  [错误] 文件不存在: {path}", file=sys.stderr)
        return 1
    print()
    return 0


def run_remote(host: str, port: int, once: str | None) -> int:
    import socket

    def send(request: dict) -> None:
        with socket.create_connection((host, port), timeout=10) as sock:
            sock.sendall((json.dumps(request, ensure_ascii=False) + "\n").encode("utf-8"))
            buf = b""
            while True:
                chunk = sock.recv(4096)
                if not chunk:
                    break
                buf += chunk
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    if not line.strip():
                        continue
                    payload = json.loads(line.decode("utf-8"))
                    if payload.get("kind") == "end":
                        return
                    render_event(RemoteEvent(payload["kind"], payload.get("data", {})), sys.stdout)

    send({"type": "ping"})
    print()
    if once is not None:
        send({"type": "text_turn", "text": once})
        return 0
    while True:
        try:
            line = input("你> ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            return 0
        if line in ("/quit", "/exit", "/q"):
            return 0
        if not line:
            continue
        if line.startswith("/event "):
            send({"type": "local_event", "event": line.split(maxsplit=1)[1].strip()})
            print()
            continue
        if line == "/state":
            send({"type": "state_query"})
            print()
            continue
        send({"type": "text_turn", "text": line})
        print()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="副驾指令 CLI 原型")
    parser.add_argument("--config", default=None)
    parser.add_argument("--text", default=None, help="单次执行一条指令后退出")
    parser.add_argument("--server", default=None, help="连接已启动的服务，如 127.0.0.1:50051")
    parser.add_argument("--mic", action="store_true", help="语音对话：按住 Ctrl 说话（松开发送）")
    parser.add_argument("--free", action="store_true", help="语音对话：免提连续监听（配合 --mic）")
    parser.add_argument("--wav", default=None, help="播放一个 wav 文件走完语音链路")
    args = parser.parse_args(argv)
    cfg = load_config(args.config)
    if args.server:
        host, _, port = args.server.partition(":")
        return run_remote(host or "127.0.0.1", int(port or 50051), args.text)
    if args.mic:
        return run_mic(cfg, free=args.free)
    if args.wav:
        return run_wav(cfg, args.wav)
    return run_local(cfg, args.text)


if __name__ == "__main__":
    raise SystemExit(main())
