# Copilot instructions

## Project shape

This repository is a Python prototype for an AI driving companion. The runtime is in `ai-service/`; `voice-corpus/` is the dialogue source data, and `tools/voicelib_gen.py` builds its TTS manifest. The original implementation plan is in `IT方案1.md`.

Read `开发进度.md` first: it records the current stage of `IT方案1.md` §10 (S1–S12), the environment snapshot, the verification baseline commands, known debt, and the next steps. Update it (including its changelog) whenever a stage lands, a bug is fixed, or a baseline number changes, so the next session does not have to re-analyze the project.

The main flow is `cli.py` or `server.py` → `pipeline.py` → components in `core/`. `DialoguePipeline` turns text or audio into a stream of `TurnEvent`s. For a text turn, it checks input safety first, then streams the validated `drive_action` before dialogue text, routes speech through TTS, and emits the trust/emotion `state_delta`. Audio goes through VAD and ASR before entering the same text-turn flow. Keep event names and payloads compatible across the pipeline, JSON-TCP transport, and the protobuf adapter in `server.py`; the contract is defined in `ai-service/proto/copilot.proto`.

`core/types.py` defines shared game state, intents, actions, and event structures. `core/action_parser.py` is the final allow-list, range-clamping, and telemetry-based rule check for model-proposed driving actions. `core/safety.py` protects both input and generated dialogue. `core/state.py` owns trust and emotion transitions. `core/dialogue.py` builds dialogue context and persists turn memory in SQLite.

LLM, ASR, VAD, and TTS have replaceable or optional backends. Backend selection and thresholds live in `ai-service/config.yaml`, with defaults and relative-path resolution in `ai-service/config.py`. Preserve the `auto`/fallback behavior when changing integrations. The dialogue lines in `voice-corpus/lines.csv` are the source of truth for corpus metadata; regenerate `voice-corpus/manifest.json` with the generator after changing those lines.

## Build, run, and test

There is no separate build command or configured lint command. The project uses Python's standard-library `unittest`; optional integrations and coverage tooling are documented separately below.

From the repository root, regenerate the voice-library manifest:

```sh
python tools/voicelib_gen.py
```

Run the service or CLI from `ai-service/`:

```sh
python cli.py --text "前面路口左转"
python server.py
python cli.py --server 127.0.0.1:50051 --text "靠边停车"
```

From `ai-service/`, run tests:

```sh
python -m unittest discover -s tests
python -m unittest tests.test_action_parser
python -m unittest tests.test_action_parser.ActionParserTest.test_clamps_out_of_range_values
python tests/run_dialogue_regression.py
```

Coverage is optional and requires the `coverage` package:

```sh
python -m coverage run --source=core,pipeline,server,cli,config -m unittest discover -s tests
python -m coverage run --append --source=core,pipeline,server,cli,config tests/run_dialogue_regression.py
python -m coverage report -m
```

## Unity side

The Unity project lives in `unity/` (2022.3 skeleton, no Editor installed on the dev box yet). It splits into:

- `unity/Assets/Scripts/Core/` — plain C#, no `UnityEngine` namespace: the JSON-TCP session, `TurnEvent`/`DriveAction` parsing, `ActionExecutorCore` clamping/smoothing, `StateView` trust/emotion, PCM helpers. Keep it engine-free so it stays testable.
- `unity/Assets/Scripts/{AI,Vehicle,State,Audio,UI}/` — thin MonoBehaviours that only wire references and marshal events to the main thread.
- `unity/Tests~/` — Unity ignores `~` folders. `CoreTests` is a zero-NuGet .NET console runner (it spawns the real `server.py` for end-to-end checks); `UnityStubs` compiles all of `Assets/Scripts` against a hand-written UnityEngine stub to catch API misuse.

```sh
# from the repository root
dotnet run --project "unity/Tests~/CoreTests/CoreTests.csproj" -- --require-server
dotnet build "unity/Tests~/UnityStubs/UnityCompileCheck.csproj"
```

Mirror server-side constants instead of inventing new ones: intent whitelist (`core/types.py`), `LOCAL_EVENTS`/`next_emotion` (`core/state.py`), and the JSON-TCP frame shapes in `server.py` — there are tests asserting they match.

## Repository conventions

- Keep `DriveAction` intents within `ALLOWED_INTENTS`; dangerous or unknown intents must be refused by `ActionParser`, not passed through to the game.
- Preserve the turn ordering: execute/emit the action as soon as it is available, then stream dialogue, speak the final reply, and emit state changes. Safety checks apply before LLM generation and again to streamed/final output.
- Components are intentionally injectable and backend-specific dependencies are generally imported only when that backend is selected. Keep the dependency-free rule/text path operational and avoid making optional audio/model packages mandatory.
- Resolve configured data paths relative to the `ai-service/` directory (or use the existing `resolve_path` helper), not the caller's current working directory.
- Tests use `unittest` and live in `ai-service/tests/`. Run commands from `ai-service/`; test modules add the service directory to `sys.path` and use `unittest.TestCase`.
- Keep corpus metadata in the CSV (`id`, `text`, `emotion`, `intent_tags`, `priority`) and regenerate the manifest rather than editing duplicate metadata by hand.
