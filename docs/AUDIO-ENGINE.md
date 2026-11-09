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
