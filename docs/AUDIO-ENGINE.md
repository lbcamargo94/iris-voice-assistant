# Audio Engine

## Objetivo

Capturar o áudio recebido do sistema e o microfone em fluxos independentes, com timestamps, controle explícito do usuário e isolamento do áudio produzido pelo próprio aplicativo.

## Abstrações

- `IAudioDeviceEnumerator`
- `IAudioCaptureService`
- `IAudioPlaybackService`
- `IAudioFormatConverter`
- `IAudioLoopbackIsolation`

## Fluxos

- `SystemAudio`: áudio reproduzido pela plataforma de reunião, rotulado na UI como **Participantes**.
- `Microphone`: áudio do usuário local, rotulado como **Você**.
- `AssistantPlayback`: áudio TTS produzido pelo aplicativo (sob demanda, ADR-011).

`AssistantPlayback` não pode voltar ao fluxo de reconhecimento. Ver isolamento abaixo.

## Formato interno (ADR-007)

- PCM float32, 16 000 Hz, mono, frames de 20 ms (320 amostras).
- Downmix e resample uma única vez, na borda do adaptador.
- Cada frame carrega `StreamPosition` (amostras desde o início da sessão), número de sequência do frame e flags (ex.: `DuringAssistantPlayback`).
- Alinhamento entre fluxos pelo instante de início registrado em UTC + contador de alta resolução (QPC).

## Isolamento da voz da Iris (ADR-007)

| Camada | Mecanismo | Protege contra |
| --- | --- | --- |
| A — principal | *Process loopback* excluindo a árvore de processos do app (Windows 10 20348+ / Windows 11). Toda reprodução TTS ocorre no app ou em processo filho. | Realimentação digital |
| A' — opcional | *Process loopback* incluindo apenas o app da reunião escolhido | Realimentação + notificações/música |
| B — rede de segurança | Durante a reprodução, segmentos com alta similaridade ao texto lido são descartados e contados (`SelfEchoSuppressed`) | Configurações inesperadas |
| C — acústico | Aviso quando a saída é caixa de som (`PKEY_AudioEndpoint_FormFactor`); segmentos do microfone durante a reprodução são marcados e excluídos da análise da Iris | Eco caixa de som → microfone |

Se o process loopback estiver indisponível, cair para loopback de endpoint com aviso explícito ao usuário. Não confiar somente em cancelamento de eco.

## Bufferização e backpressure (ADR-009)

- Buffers limitados por fluxo.
- Contadores de frames recebidos, processados, descartados e atrasados.
- Descarte de frames somente sob sobrecarga, sempre gerando `AudioGap` (início, duração, causa) exibido na linha do tempo.
- Durante reinício de worker, retenção limitada (~30 s); o excedente vira `AudioGap`.
- Segmentos finais já confirmados nunca são descartados.
- Conciliação: amostras capturadas = processadas + descartadas.

## VAD e ruído

VAD (Silero VAD candidato) é usado para evitar inferência em silêncio e delimitar segmentos (ADR-008). Redução de ruído é opcional e só será habilitada por padrão após medir o impacto na precisão.

## Estados

`Stopped`, `Starting`, `Capturing`, `Paused`, `Recovering`, `Faulted`.

## Segurança e UX

- Indicador visível de captura.
- Ação de parar acessível.
- Seleção explícita dos dispositivos.
- Aviso de consentimento ao iniciar a sessão.
- Permissões nativas respeitadas.
- Não implementar captura oculta nem contornar controles corporativos.

## Testes

Testar dispositivo padrão, troca de dispositivo, suspensão/retomada, desconexão, áudio silencioso, clipping, canais, diferentes taxas de amostragem, isolamento do TTS nas camadas A e B, fallback quando o process loopback estiver indisponível e aviso de caixa de som.

## Implementação P0 (POC-001B, 2026-11-11)

| Peça | Onde | Observação |
| --- | --- | --- |
| Contratos | `IrisVoiceAssistant.Core.Audio` | `AudioFrame` (sequência e posição), `AudioGap` (causa; `MissingSamples` nos episódios de sobrecarga), `AudioStreamCounters` (conciliação), `AudioDeviceInfo`, `IAudioCaptureSource`, `IRawAudioSink` |
| Pipeline portável | `IrisVoiceAssistant.Audio` | `PcmDecoder` (downmix), `StreamingResampler` (sinc com janela de Kaiser e passo racional exato, sem deriva), `CaptureStreamPipeline` (blocos de 20 ms, linha do tempo e fila `DropOldest` de 30 s), `CaptureStreamRunner` (estados e recuperação) |
| Windows | `IrisVoiceAssistant.Platform.Windows` | Interop COM própria, sem pacotes: microfone, loopback do dispositivo e process loopback com fallback avisado |
| Diagnóstico | `tools/IrisVoiceAssistant.AudioDiagnostics` (`iris-audio-diag`) | `devices` e `capture` (opções `--pause-at`/`--resume-at`, `--slow-consumer-ms`, `--self-tone`, `--wav` só em `tmp/`) |

Regras de lacuna:
- Silêncio do loopback (nada tocando) é preenchido e **não** é lacuna.
- Falha do dispositivo (discontinuity): o trecho é preenchido e registrado como `DeviceDiscontinuity`.
- Pausa e perda do dispositivo avançam a posição sem gerar quadros e viram `Paused` ou `DeviceLost`. Parar durante a recuperação também fecha a lacuna.
- Sobrecarga descarta os quadros mais antigos. Descartes a menos de 1 s um do outro formam **um episódio** `Overflow`, com a duração do trecho e a perda exata.

Validação na máquina de desenvolvimento:
- **Isolamento:** ver ADR-007, refinamento de 2026-11-11.
- **Pausa e retomada:** lacuna `Paused` de ~5,04 s; o dispositivo é liberado durante a pausa.
- **Sobrecarga forçada:** um episódio de 6,1 s, com 3,12 s perdidos e contagem exata.
- **Desconexão:** detectada na hora (`AUDCLNT_E_DEVICE_INVALIDATED`), com novas tentativas a cada 1 s.
- **Conciliação e sequência:** fecharam em todas as rodadas.

Pendências:
- Aviso de **"microfone sem sinal"**: um headset sem fio com o microfone desconectado continua presente no Windows e entrega silêncio quase digital (cerca de −75 dBFS de pico). Hoje isso não se distingue de "ninguém falando".
- Seguir a troca do dispositivo padrão do **microfone** (`IMMNotificationClient`). Hoje o fluxo reabre o padrão só depois de uma perda.
- Camada B do isolamento, reconexão real do headset e a sessão de 60 min (POC-001E e F).
