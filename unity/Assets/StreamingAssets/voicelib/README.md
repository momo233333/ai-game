# StreamingAssets/voicelib

预生成语音库（方案 §4.5 路由①）的落位目录。

- 语料单一数据源：`voice-corpus/lines.csv`
- 索引：`voice-corpus/manifest.json`（`python tools/voicelib_gen.py` 生成）
- 音频资产：`voice-corpus/*.wav` 尚未批量合成（阶段 S9，依赖 GPT-SoVITS 或本地 TTS）

Unity 打包阶段（S11）把 `manifest.json` + wav 拷贝到这里随包分发；
开发期 AI 服务直接读仓库内的 `voice-corpus/`，无需拷贝。
