# Agente Frida — Manual de Arquitetura e Operação

## 1. Visão Geral do Sistema (`FridaAgent.exe`)

A **Frida** é uma agente residente nativa Windows x64 com ativação contínua por voz (`"Frida"`), holograma volumétrico 3D azul reativo ao espectro de fala, voz neural feminina humana e autonomia irrestrita sobre programas, arquivos, pesquisas web, código, engenharia e modelagem 3D.

| Subsistema | Implementação Técnica | Comportamento |
|---|---|---|
| **Inicialização com o Windows** | Registro automático em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AgenteFrida` ([`StartupRegistry`](file:///C:/Users/Ailton%20Soares/Documents/print%20window%20mbx/PROJETO%20FRIDA/Agente%20Frida/FridaConfig.cs#L162-L209)) | Inicia silenciosamente junto com o computador; pode ser ativada/desativada pelo menu do ícone na bandeja. |
| **Ícone de Aplicativos Ativos (Bandeja)** | `NotifyIcon` com ícone holográfico azul `frida.ico` ([`FridaTrayController`](file:///C:/Users/Ailton%20Soares/Documents/print%20window%20mbx/PROJETO%20FRIDA/Agente%20Frida/AgentBrainAndTray.cs#L951-L1063)) | Fica visível na área de aplicativos ativos do Windows 11 (`^`). Botão direito → **Sair** encerra completamente o processo. |
| **Escuta de Ativação `"Frida"`** | Motor híbrido `Windows.Media.SpeechRecognition` (`pt-BR` offline, 0 consumo de API em espera) + buffer circular `winmm.dll` `waveIn` 16 kHz com *pre-roll* de 1,5s ([`AudioVoiceEngine`](file:///C:/Users/Ailton%20Soares/Documents/print%20window%20mbx/PROJETO%20FRIDA/Agente%20Frida/AudioVoiceEngine.cs#L82-L340)) | Basta falar **"Frida"** (ou *"Oi Frida"*, *"Ei Frida"*, ou apertar **`F4`** / clicar no ícone da bandeja). Aceita tanto chamar *"Frida"* e esperar ela responder quanto falar a frase inteira direto (*"Frida, abre o SolidWorks"*). |
| **Gadget Holográfico 3D Azul Flutuante** | Projeção 3D volumétrica pura (sem caixa de texto) a 60 FPS em janela transparente `UpdateLayeredWindow` (`WS_EX_LAYERED \| WS_EX_TOPMOST \| WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW`) ([`HologramOrbForm`](file:///C:/Users/Ailton%20Soares/Documents/print%20window%20mbx/PROJETO%20FRIDA/Agente%20Frida/HologramOrbForm.cs#L101-L700)) | Permanece **100% oculto** em inatividade (0% CPU). Quando você chama a Frida, apenas a esfera holográfica azul materializa no desktop, pulsa com o espectro real de 16 bandas da sua voz e da voz dela, permite arrastar com o mouse para qualquer canto da tela e desaparece suavemente ao término da conversa. |
| **Voz Feminina Natural Humana (`Aoede`)** | Cascata `Aoede` (`gemini-3.8-flash-lite-tts` / `gemini-3.8-flash-tts` / `gemini-3.1-flash-tts-preview` / `gemini-2.5-flash-preview-tts`) sincronizada amostra por amostra com o holograma + cache local `tts_cache\` | Confirmação humana imediata antes de executar tarefas no sistema (informando que iniciou e já retornará com o resultado) e janela ativa de 7s após responder para continuar o diálogo naturalmente sem repetir *"Frida"*. |
| **Controle Total do PC & Especialidades** | 6 ferramentas autônomas encadeadas em [`FridaBrain`](file:///C:/Users/Ailton%20Soares/Documents/print%20window%20mbx/PROJETO%20FRIDA/Agente%20Frida/AgentBrainAndTray.cs#L68-L620): `run_powershell`, `open_program_or_url`, `manage_files`, `web_research`, `inspect_screen`, `secretary_memory` | Executa comandos PowerShell irrestritos, abre qualquer programa instalado (SolidWorks, Opera, Chrome, WhatsApp, Steam, etc.), analisa sua tela em tempo real, gera códigos completos, malhas/scripts 3D (`.stl`, `.obj`, `.scad`, macros SolidWorks), pesquisa na web via Google Search e gerencia agenda com lembretes falados. |

---

## 2. Como Iniciar e Usar

1. Copie o arquivo config.example.json para config.json e configure suas chaves de API. Execute o compilar_frida.bat ou abra o FridaAgent.exe.
2. Ao iniciar, o holograma azul aparece brevemente dando boas-vindas em voz feminina, confirma que está operante e **desaparece**, deixando apenas o ícone azul na bandeja de aplicativos ativos (ao lado do relógio).
3. Exemplos práticos de comandos de voz:
   - *"Frida, abre o SolidWorks."* / *"Frida, abre o Opera no YouTube."*
   - *"Frida, olha a minha tela agora e me explica esse código / esse projeto."*
   - *"Frida, cria na minha área de trabalho um arquivo STL 3D de uma flange cilíndrica com 6 furos de fixação."*
   - *"Frida, pesquisa na internet as propriedades mecânicas do aço SAE 4140 temperado e salva um resumo nos meus documentos."*
   - *"Frida, anota na minha agenda revisar o cronograma do projeto amanhã às 14 horas e me lembra daqui a 15 minutos de conferir o e-mail."*
   - *"Obrigado"* / *"Pode descansar"* → encerra o turno e oculta o holograma imediatamente.

---

## 3. Controles do Ícone na Área de Aplicativos Ativos (Botão Direito)

- **Chamar Frida Agora (`F4`):** abre a escuta imediatamente (também funciona clicando com o botão esquerdo no ícone ou apertando a tecla `F4`).
- **Enviar Comando por Texto...:** abre uma barra escura rápida caso queira enviar uma ordem digitada em ambiente silencioso.
- **Apresentar Holograma e Voz:** demonstração imediata do espectro 3D azul e da voz feminina.
- **Pausar Escuta por Voz ("Frida"):** silencia temporariamente o microfone.
- **Iniciar junto com o Computador:** marca/desmarca o registro de inicialização automática do Windows.
- **Configurar Chave API...:** permite trocar a chave Gemini (`config.json` também permite trocar a voz feminina entre `"Aoede"`, `"Kore"`, `"Leda"` e `"Zephyr"`).
- **Sair:** encerra completamente a Frida e remove o ícone da área de aplicativos ativos.

