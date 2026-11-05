# Chamada local da Frida

O reconhecedor recebe PCM 16 kHz mono do mesmo microfone selecionado no aplicativo. Não usa uma API em nuvem para identificar a chamada durante a espera. Os arquivos desta pasta são necessários junto ao executável x64.

Componentes obtidos das distribuições oficiais:

- Vosk API Windows x64 0.3.45: https://github.com/alphacep/vosk-api/releases/tag/v0.3.45
- Modelo português pequeno 0.3: https://alphacephei.com/vosk/models (Apache 2.0, conforme catálogo do fornecedor).
- Licença da API: LICENSE-vosk.txt, obtida de https://github.com/alphacep/vosk-api/blob/v0.3.45/COPYING
- As DLLs libstdc++, libgcc e libwinpthread são dependências da distribuição oficial Windows. Licenças e fontes: https://gcc.gnu.org/onlinedocs/libstdc++/manual/license.html e https://www.mingw-w64.org/license/ .

## Verificação "só a minha voz" (pasta `sherpa/`)

Detector de fala e identificação de locutor 100% locais, de k2-fsa/sherpa-onnx (Apache-2.0):

- `sherpa-onnx-c-api.dll`, `onnxruntime.dll`, `onnxruntime_providers_shared.dll`: de `sherpa-onnx-v2.0.1-win-x64-shared-MT-Release-no-tts-lib.tar.bz2` (SHA-256 21da6a8b92d63ca02ad41dbfc120012343aaaec2e5e02b14819c6b4f38004509), https://github.com/k2-fsa/sherpa-onnx/releases/tag/v2.0.1
- `wespeaker_en_voxceleb_resnet34_LM.onnx` (WeSpeaker ResNet34-LM, VoxCeleb): SHA-256 e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012 (confere com o checksum.txt oficial), https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-recongition-models
- `silero_vad_v5.onnx` (Silero VAD v5): SHA-256 6b99cbfd39246b6706f98ec13c7c50c6b299181f2474fa05cbc8046acc274396, https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models

A pasta `vosk-model-spk-0.4` não é mais usada (substituída pelo modelo acima) e pode ser apagada.

Arquivos originais e SHA-256:

- vosk-model-small-pt-0.3.zip: 6e1ce909032e1afa7a88e68a3d628ecafff302bdf195befab308826c395e93b7
- vosk-win64-0.3.45.zip: f1dcc9cca460630f81ea8f71794f69c80bed6556d2a4e6237b5785e1d2dff34b
