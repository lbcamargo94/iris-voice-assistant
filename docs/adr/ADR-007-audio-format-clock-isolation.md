# ADR-007 — Formato canônico de áudio, relógio e isolamento da captura

- **Estado:** Aceita. O isolamento por processo (camada A) foi validado no POC-001B: ver o refinamento de 2026-11-11.
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

## Refinamento 2026-11-11 (POC-001B, validado na máquina de desenvolvimento)

Ambiente: Windows 11 build 26300, headset sem fio H510-PRO (fone e microfone), `tools/IrisVoiceAssistant.AudioDiagnostics`.

1. **A camada A está validada.** O process loopback que exclui a árvore de processos do app ativa sem erro. Teste com o comando `capture --self-tone`, em que o próprio processo toca um tom de 1234 Hz a −12 dBFS:
   - **com process loopback, o tom não aparece** na captura do sistema: −104,6 dBFS, abaixo do ruído de −97 dBFS;
   - **com loopback do dispositivo, ele aparece a −12,0 dBFS**, exatamente o nível tocado. Isso prova que o teste é capaz de detectá-lo e que a conversão até 16 kHz não altera o nível.
2. **Formato.** O process loopback não tem formato padrão (mix format). O adaptador pede float32 com a taxa e os canais da saída padrão, aqui 48 kHz estéreo, com conversão do próprio Windows (`AUTOCONVERTPCM`). A conversão para o formato canônico acontece uma vez, no pipeline portável (`IrisVoiceAssistant.Audio`).
3. **Relógio.**
   - O primeiro pacote de cada fluxo é posicionado no instante real da captura. Antes, os fluxos começavam desalinhados em ~90 ms.
   - Depois disso, a posição é a contagem de amostras. O timestamp de captura (QPC) só serve para detectar buracos maiores que 100 ms e preencher silêncio quando o loopback não entrega nada.
   - A deriva medida ficou entre 1 e 3 ms em 45 s. Amostras reais nunca são descartadas para corrigir deriva.
4. **O process loopback segue a saída padrão.** Com o headset desligado no meio da captura, a captura do sistema continuou sem perda, e o Windows passou a usar a outra saída.
5. **O fallback para loopback do dispositivo** só acontece com aviso explícito, porque nessa situação a voz da Iris seria capturada (`WasapiCaptureSource.OpenSystemAudio`).
6. **Camada C.** O tipo do dispositivo de saída (`PKEY_AudioEndpoint_FormFactor`) é lido corretamente: o headset aparece como `Headphones`. Saídas digitais ou de tipo desconhecido geram o aviso de eco por precaução.
7. **Pendente:**
   - camada B (`SelfEchoSuppressed`), que depende do TTS reproduzido no app (POC-001E);
   - modo A' (incluir só o app da reunião);
   - reconexão real do headset, vista hoje só em teste automatizado. A detecção da perda foi validada no hardware.
