from __future__ import annotations

import argparse
import base64
import concurrent.futures
import json
import os
import socketserver
import sys
import threading

from config import load_config
from core.types import TurnEvent, to_dict
from pipeline import DialoguePipeline

SERVICE_DIR = os.path.dirname(os.path.abspath(__file__))


def build_pipeline(cfg: dict | None = None) -> DialoguePipeline:
    cfg = cfg or load_config()
    try:
        from core.asr import create_asr
        from core.vad import create_vad
    except ImportError:
        create_asr = None
        create_vad = None
    vad = create_vad(cfg) if create_vad else None
    asr = create_asr(cfg, service_dir=SERVICE_DIR) if create_asr else None
    pipeline = DialoguePipeline(cfg, SERVICE_DIR, asr=asr, vad=vad)
    return pipeline


class JsonRequestHandler(socketserver.StreamRequestHandler):
    def handle(self) -> None:
        pipeline: DialoguePipeline = self.server.pipeline  # type: ignore[attr-defined]
        for raw in self.rfile:
            line = raw.decode("utf-8", errors="replace").strip()
            if not line:
                continue
            try:
                request = json.loads(line)
            except json.JSONDecodeError:
                self._send({"kind": "error", "data": {"code": "bad_json", "message": "invalid json"}})
                continue
            try:
                self._dispatch(pipeline, request)
            except Exception as exc:
                self._send({"kind": "error", "data": {"code": "internal", "message": str(exc)}})
            self._send({"kind": "end", "data": {}})

    def _dispatch(self, pipeline: DialoguePipeline, request: dict) -> None:
        rtype = request.get("type")
        if rtype == "ping":
            self._send({"kind": "health", "data": pipeline.health})
        elif rtype == "sync_state":
            state = pipeline.sync_state(request.get("state") or {})
            self._send({"kind": "ack", "data": {"ok": True, "trust": state.trust, "emotion": state.emotion_name()}})
        elif rtype == "state_query":
            self._send({"kind": "state", "data": to_dict(pipeline.state)})
        elif rtype == "text_turn":
            for event in pipeline.handle_text(request.get("text", "")):
                self._send(event.to_dict())
        elif rtype == "audio_turn":
            chunks = request.get("chunks") or []
            decoded = [base64.b64decode(c) for c in chunks]
            for event in pipeline.handle_audio(decoded):
                self._send(event.to_dict())
        elif rtype == "local_event":
            delta = pipeline.state_manager.apply_local_event(str(request.get("event", "")))
            if delta:
                self._send({"kind": "state_delta", "data": {"trust": delta.trust, "emotion": delta.emotion, "trust_delta": delta.trust_delta}})
            else:
                self._send({"kind": "error", "data": {"code": "unknown_event", "message": str(request.get("event"))}})
        else:
            self._send({"kind": "error", "data": {"code": "unknown_type", "message": str(rtype)}})

    def _send(self, payload: dict) -> None:
        data = json.dumps(_json_safe(payload), ensure_ascii=False).encode("utf-8")
        self.wfile.write(data + b"\n")
        self.wfile.flush()


def _json_safe(obj):
    if isinstance(obj, bytes):
        return {"__b64__": base64.b64encode(obj).decode("ascii")}
    if isinstance(obj, dict):
        return {k: _json_safe(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [_json_safe(v) for v in obj]
    return obj


class JsonServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, host: str, port: int, pipeline: DialoguePipeline):
        self.pipeline = pipeline
        super().__init__((host, port), JsonRequestHandler)

    def handle_error(self, request, client_address):
        pass


def _try_start_grpc(pipeline: DialoguePipeline, host: str, port: int):
    try:
        import grpc
        from proto import copilot_pb2, copilot_pb2_grpc
    except Exception:
        return None

    class Servicer(copilot_pb2_grpc.AIServiceServicer):
        def SyncGameState(self, request, context):
            state = pipeline.sync_state(_proto_state_to_dict(request))
            return copilot_pb2.Ack(ok=True)

        def Ping(self, request, context):
            health = pipeline.health
            return copilot_pb2.Health(
                ok=True,
                llm_backend=health["llm_backend"],
                asr_backend=health["asr_backend"],
                tts_backend=health["tts_backend"],
                version=health["version"],
            )

        def VoiceTurn(self, request_iterator, context):
            chunks = []
            for chunk in request_iterator:
                chunks.append(chunk.pcm16)
                if chunk.end_of_stream:
                    break
            for event in pipeline.handle_audio(chunks):
                yield _event_to_proto(copilot_pb2, event)

        def TextTurn(self, request, context):
            for event in pipeline.handle_text(request.text):
                yield _event_to_proto(copilot_pb2, event)

    server = grpc.server(concurrent.futures.ThreadPoolExecutor(max_workers=4))
    copilot_pb2_grpc.add_AIServiceServicer_to_server(Servicer(), server)
    server.add_insecure_port(f"{host}:{port}")
    server.start()
    return server


def _proto_state_to_dict(request) -> dict:
    return {
        "trust": request.trust,
        "emotion": copilot_emotion_name(request.emotion),
        "scene": {
            "scene_id": request.scene.scene_id,
            "weather": request.scene.weather,
            "time_of_day": request.scene.time_of_day,
        },
        "telemetry": {
            "speed_kmh": request.telemetry.speed_kmh,
            "gear": request.telemetry.gear,
            "at_intersection": request.telemetry.at_intersection,
            "obstacle_summary": request.telemetry.obstacle_summary,
        },
        "level_id": request.level_id,
    }


def copilot_emotion_name(value: int) -> str:
    return {0: "CALM", 1: "ANNOYED", 2: "NERVOUS", 3: "HAPPY"}.get(value, "CALM")


def _event_to_proto(copilot_pb2, event: TurnEvent):
    te = copilot_pb2.TurnEvent()
    kind, data = event.kind, event.data
    if kind == "asr_partial":
        te.asr_partial.text = data.get("text", "")
        te.asr_partial.confidence = float(data.get("confidence", 0))
    elif kind == "asr_final":
        te.asr_final.text = data.get("text", "")
        te.asr_final.confidence = float(data.get("confidence", 0))
        te.asr_final.low_confidence = bool(data.get("low_confidence", False))
    elif kind == "llm_delta":
        te.llm_delta.text = data.get("text", "")
    elif kind == "drive_action":
        te.drive_action.steering = float(data.get("steering", 0))
        te.drive_action.throttle = float(data.get("throttle", 0))
        te.drive_action.brake = float(data.get("brake", 0))
        te.drive_action.hold_ms = int(data.get("hold_ms", 0))
        te.drive_action.intent = data.get("intent", "")
        te.drive_action.confidence = float(data.get("confidence", 0))
    elif kind == "tts_audio":
        te.tts_audio.pcm16 = data.get("pcm16", b"")
        te.tts_audio.from_voicelib = bool(data.get("from_voicelib", False))
        te.tts_audio.text = data.get("text", "")
    elif kind == "state_delta":
        te.state_delta.trust = float(data.get("trust", 0))
        te.state_delta.trust_delta = float(data.get("trust_delta", 0))
        te.state_delta.emotion = copilot_emotion_reverse(data.get("emotion", "CALM"))
    elif kind == "error":
        te.error.code = data.get("code", "")
        te.error.message = data.get("message", "")
    return te


def copilot_emotion_reverse(name: str) -> int:
    return {"CALM": 0, "ANNOYED": 1, "NERVOUS": 2, "HAPPY": 3}.get(name, 0)


def serve(cfg: dict, transport: str | None = None, block: bool = True):
    host = cfg.get("server", {}).get("host", "127.0.0.1")
    port = int(cfg.get("server", {}).get("port", 50051))
    choice = (transport or cfg.get("server", {}).get("transport", "auto")).lower()
    pipeline = build_pipeline(cfg)

    grpc_server = None
    if choice in ("auto", "grpc"):
        grpc_server = _try_start_grpc(pipeline, host, port)
        if grpc_server is not None:
            print(f"[server] gRPC listening on {host}:{port} | {json.dumps(pipeline.health, ensure_ascii=False)}")
            if not block:
                return grpc_server
            try:
                grpc_server.wait_for_termination()
            except KeyboardInterrupt:
                grpc_server.stop(0)
            return grpc_server
        if choice == "grpc":
            raise SystemExit("gRPC unavailable: install grpcio/grpcio-tools and generate proto/copilot_pb2*.py")

    json_server = JsonServer(host, port, pipeline)
    print(f"[server] JSON-TCP listening on {host}:{json_server.server_address[1]} | {json.dumps(pipeline.health, ensure_ascii=False)}")
    thread = threading.Thread(target=json_server.serve_forever, daemon=True)
    thread.start()
    if not block:
        return json_server
    try:
        while True:
            thread.join(0.5)
    except KeyboardInterrupt:
        print("[server] shutdown")
        json_server.shutdown()
    return json_server


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description="副驾指令 AI 服务")
    parser.add_argument("--config", default=None)
    parser.add_argument("--transport", default=None, choices=["auto", "grpc", "json"])
    parser.add_argument("--port", type=int, default=None)
    args = parser.parse_args(argv)
    cfg = load_config(args.config)
    if args.port:
        cfg.setdefault("server", {})["port"] = args.port
    serve(cfg, args.transport)


if __name__ == "__main__":
    sys.exit(main())
