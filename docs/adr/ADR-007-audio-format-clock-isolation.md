# ADR-007 — Formato canônico de áudio, relógio e isolamento da captura

- **Estado:** Aceita (isolamento por processo sujeito a validação no POC-001B)
- **Data:** 2026-11-09

## Contexto

Os modelos de STT candidatos (Whisper, Parakeet) operam em 16 kHz mono. A captura WASAPI entrega formatos variados (tipicamente 48 kHz estéreo float). Dois fluxos independentes (sistema e microfone) precisam ser alinhados no tempo. A voz sintetizada pela Iris não pode reentrar no reconhecimento (realimentação digital), e o eco acústico (caixas de som → microfone) é um risco separado.

## Decisão

1. **Formato canônico interno:** PCM float32, 16 000 Hz, mono, frames de 20 ms (320 amostras). A conversão (downmix + resample) ocorre uma única vez, na borda do adaptador de plataforma.
2. **Relógio:** cada fluxo tem um relógio monotônico baseado em contagem de amostras (`StreamPosition`, em amostras desde o início da sessão). O início da sessão é registrado em UTC e via contador de alta resolução (QPC) para alinhar os fluxos. O horário de parede (UTC) é derivado apenas para persistência e exibição.
3. **Isolamento digital (camada A):** no Windows, a captura do sistema usa *process loopback* (`ActivateAudioInterfaceAsync` com `VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK` e `PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE`) tendo como alvo o processo do aplicativo. Toda reprodução de TTS deve ocorrer no processo do app ou em processo filho. Modo opcional: `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE` para capturar somente o aplicativo da reunião escolhido pelo usuário.
4. **Rede de segurança (camada B):** durante a reprodução de TTS, segmentos reconhecidos com alta similaridade ao texto em reprodução são descartados e contabilizados (`SelfEchoSuppressed`), nunca de forma silenciosa.
5. **Eco acústico (camada C):** o app lê o form factor do endpoint de saída (`PKEY_AudioEndpoint_FormFactor`) e avisa quando a saída é caixa de som. Enquanto a Iris fala, segmentos do microfone são marcados como `DuringAssistantPlayback` e excluídos da análise da Iris. Fone de ouvido é a configuração recomendada.

## Consequências

- Process loopback requer Windows 10 build 20348+ / Windows 11; o adaptador deve detectar indisponibilidade e cair para loopback de endpoint com aviso explícito.
- NAudio não oferece process loopback pronto; será necessária interop COM própria em `IrisVoiceAssistant.Platform.Windows`.
- macOS/Linux precisarão de mecanismos equivalentes, avaliados em suas fases.
- O formato canônico pode ser revisto se o benchmark indicar modelo com outro requisito.
