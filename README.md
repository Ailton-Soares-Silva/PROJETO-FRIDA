# PROJETO FRIDA - Agente Autônoma de Bancada & Assistente Executiva Windows 11

A **FRIDA** é uma agente autônoma de elite residente no Windows 11, projetada para operar como parceira técnica de bancada, engenharia, desenvolvimento e secretária executiva. Equipada com interface holográfica flutuante (Spectro Azul 3D), ativação por comando de voz (*Wake-Word* via Vosk, com alternativa WinRT), síntese de voz neural natural sem dependência de cotas (Edge Neural) e painel executivo 32-bits estilo retro/Zelda para gestão e auditoria de modelos LLM em tempo real.

---

## 🌟 Funcionalidades Principais

- **Orbe Holográfico 3D Flutuante (Spectro Azul)**: Gadget animado que reage visualmente aos estados da agente (Ociosa, Ouvindo, Pensando, Executando Ferramentas e Falando).
- **Ativação por Voz Contínua (*Wake-Word*)**: Vosk local em português, com alternativa WinRT, para detecção em tempo real de chamadas como *"Frida"*, *"Oi Frida"*, *"Ok Frida"*.
- **Biometria Vocal & Speaker Lock**: Verificação neural do operador com WeSpeaker/sherpa-onnx e detecção de fala Silero. Vozes sobrepostas podem exigir repetição.
- **Voz Neural Natural com Zero Cota**: Síntese de fala de alta fidelidade em português brasileiro (*Francisca Neural*), com cache local inteligente e independência total de cotas de APIs comerciais.
- **Painel Executivo Retro 32-Bit (Zelda)**:
  - **Escritório**: Modelos LLM atualmente operacionais.
  - **Sala de Jogos**: Modelos ociosos em standby.
  - **Dormitório**: Modelos com cota diária ou limite por minuto atingido.
  - **Sala de Reuniões**: Conclave multi-modelo e deliberação coletiva.
- **Roteamento Inteligente & Motor de Laboratório**: Capacidade irrestrita para engenharia reversa, análise de executáveis, automação PowerShell, scripts CAD (SolidWorks) e arquitetura de segurança sem bloqueios indevidos ou recusas em contexto fechado.
- **Ferramentas de Automação de Sistema**: Execução de scripts PowerShell, abertura de programas instalados no Windows, manipulação de arquivos e pesquisa web.

---

## 📁 Estrutura do Projeto

```text
PROJETO FRIDA/
├── .gitignore                      # Proteção de chaves e dados pessoais
├── README.md                       # Documentação principal
└── Agente Frida/                   # Código-fonte da aplicação C#
    ├── AgentBrainAndTray.cs        # Raciocínio, roteamento e system tray
    ├── AudioVoiceEngine.cs         # Captura, ativação local, gravação e síntese TTS
    ├── FridaSpeechRecognition.cs   # STT, qualidade, tentativas e métricas
    ├── FridaConfig.cs              # Estrutura e leitura de configurações
    ├── HologramOrbForm.cs          # Renderização do Orbe Holográfico 3D
    ├── LlmUsageLedger.cs           # Controle e auditoria de cotas e telemetria
    ├── SpeakerVoiceProfile.cs      # Identificação neural do operador
    ├── ZeldaConsumptionViews.cs    # Gráficos e visualizações do painel
    ├── ZeldaExecutiveLlmDashboard.cs # Gestão e interface 32-bit Zelda
    ├── compilar_frida.bat          # Script de compilação nativa C# (.NET 4.8)
    ├── config.example.json         # Modelo de configuração (sem chaves)
    ├── frida.ico                   # Ícone do aplicativo
    └── GUIA_AGENTE_FRIDA.md        # Guia operacional detalhado
```

---

## 🚀 Como Configurar e Compilar

### 1. Pré-requisitos
- **Sistema Operacional**: Windows 10 ou Windows 11 (64 bits).
- **Framework**: .NET Framework 4.8 (já incluso nativamente no Windows 10/11).
- **Compilador**: `csc.exe` (localizado em `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`).

### 2. Configuração de Chaves de API
Copie o arquivo de exemplo para criar a sua configuração local:
```powershell
cd "Agente Frida"
copy config.example.json config.json
```
Abra o `config.json` e adicione sua chave de API da Google Gemini (e opcionalmente Groq, Cerebras, OpenRouter, etc.).
> ⚠️ **Atenção**: O arquivo `config.json` está explicitamente no `.gitignore` e **nunca** deve ser commitado no repositório.

### 3. Compilação
Execute o script em lote na pasta `Agente Frida`:
```powershell
.\compilar_frida.bat
```
O executável nativo `FridaAgent.exe` será gerado com sucesso.

### 4. Execução
Inicie o aplicativo:
```powershell
.\FridaAgent.exe
```
O ícone da Frida aparecerá na bandeja do sistema (System Tray) e o Orbe Holográfico surgirá flutuando na área de trabalho.

---

## 🔒 Segurança & Privacidade

- **Zero Chaves no Repositório**: Nenhuma chave de API ou credencial sensível é incluída no versionamento.
- **Sem Dados Biométricos no Git**: O arquivo `frida_voice_profile.json` é mantido estritamente na máquina local do operador.
- **Logs e Áudios Protegidos**: Caches de voz (`tts_cache/`), arquivos temporários e logs de depuração são completamente ignorados pelo controle de versão.

---

## 📜 Licença
Projeto de uso pessoal e laboratório de engenharia autônoma.


## Reconhecimento de voz 1.3.0.6

Correções, configuração reversível, coleta e comparação com gravações reais: [guia de implementação](Agente%20Frida/IMPLEMENTACAO_VOZ_2026-11-06.md). O ganho de precisão ainda depende da homologação com a voz do operador.
