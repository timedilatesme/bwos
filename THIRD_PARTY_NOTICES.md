# Optional offline voice assets

BWOS remains MIT licensed. Offline voice recognition uses separately downloaded components.

| Component | License | Source |
|---|---|---|
| whisper.cpp 1.9.2 | MIT | https://github.com/ggml-org/whisper.cpp/tree/v1.9.2 |
| Whisper multilingual large-v3-turbo and base quantized models | MIT | https://huggingface.co/ggerganov/whisper.cpp |
| NVIDIA cuBLAS 11.11.3.6 (downloaded only on NVIDIA PCs) | NVIDIA CUDA Toolkit license; retained as `data/tools/whisper/NVIDIA-LICENSE.txt` | https://developer.download.nvidia.com/compute/cuda/redist/redistrib_11.8.0.json |
| Vosk API 0.3.45 | Apache License 2.0 | https://github.com/alphacep/vosk-api |
| `vosk-model-small-en-us-0.15`, `vosk-model-small-en-in-0.4`, `vosk-model-small-hi-0.22` | Apache License 2.0 (as listed by the model publisher) | https://alphacephei.com/vosk/models |
| Kaldi (included in libvosk) | Apache License 2.0 | https://github.com/kaldi-asr/kaldi |
| OpenFst (included in libvosk) | Apache License 2.0 | https://www.openfst.org |
| GCC runtime libraries (`libgcc_s_seh-1.dll`, `libstdc++-6.dll`) | GNU GPL with GCC Runtime Library Exception | https://gcc.gnu.org/onlinedocs/libstdc++/manual/license.html |
| MinGW-w64 winpthreads (`libwinpthread-1.dll`) | MIT and BSD-style notices; see upstream | https://github.com/mingw-w64/mingw-w64/tree/master/mingw-w64-libraries/winpthreads |

Assets are obtained from Vosk's official release and model servers. They are stored in the local BWOS data directory and are excluded from ordinary BWOS release ZIPs. The download does not include microphone recordings, and recognition runs locally after installation.

Whisper assets are obtained from the engine maintainer's GitHub release, its model repository on Hugging Face, and NVIDIA's official redistribution server. The NVIDIA archive is verified against the publisher's SHA-256. Vosk remains an optional legacy engine.

See the upstream sources for full license text and dependency notices. Keep upstream notices with any manually redistributed voice runtime or model files.
