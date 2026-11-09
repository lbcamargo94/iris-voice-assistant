# Iris — integração com os 16 módulos do Iris Voice Assistant

A Iris é uma camada transversal de inteligência e interação. Os módulos especializados continuam sendo responsáveis por suas operações técnicas.

| Nº | Módulo | Integração da Iris | Limite de responsabilidade |
| ---: | --- | --- | --- |
| 1 | Audio Engine | Recebe eventos de estado/fluxo e timestamps | Não captura nem controla dispositivos diretamente |
| 2 | Transcription Engine | Consome segmentos parciais/finais e metadados | Não substitui STT |
| 3 | Translation Engine | Solicita ou apresenta traduções contextualizadas | Não substitui o tradutor |
| 4 | Voice Engine | Solicita síntese de explicações/respostas se o usuário habilitar | Não controla dispositivo de áudio diretamente |
| 5 | Meeting Intelligence | Principal integração para perguntas, tópicos e sugestões | Não apresenta inferências como fatos |
| 6 | Speaker Intelligence | Usa IDs/rótulos provisórios e confiança da diarização | Não promete identificação nominal |
| 7 | Technical Glossary | Consulta e propõe termos/definições | Alterações persistentes devem ser confirmáveis/configuráveis |
| 8 | Meeting Memory | Consulta contexto da sessão e histórico autorizado | Respeita retenção e escopo de consentimento |
| 9 | Reports & Tasks | Propõe resumos, decisões e tarefas com proveniência | Responsáveis/prazos inferidos devem ser rotulados |
| 10 | English Learning | Explica vocabulário, gramática e pronúncia | Não interrompe o fluxo principal por padrão |
| 11 | Model Manager | Solicita modelos necessários e verifica disponibilidade | Não baixa modelos silenciosamente |
| 12 | Resource Manager | Informa prioridade/custo e respeita limites de recursos | Não pode comprometer captura/STT/tradução |
| 13 | Local Storage | Persiste mensagens e preferências autorizadas via repositórios | Não acessa banco por SQL ad hoc na UI |
| 14 | Security & Privacy | Aplica permissões, retenção, consentimento e minimização | Não contorna políticas ou permissões |
| 15 | Update & Distribution Manager | Pode exibir avisos de atualização relevantes | Não instala/atualiza sem fluxo autorizado |
| 16 | Commercial License Manager | Verifica capacidades habilitadas | Não contorna limites da licença |

## 1. Eventos sugeridos

- `TranscriptSegmentFinalized`
- `TranslationSegmentReady`
- `SpeakerTurnUpdated`
- `MeetingSessionStarted` / `MeetingSessionPaused` / `MeetingSessionEnded`
- `GlossaryTermMatched`
- `ModelAvailabilityChanged`
- `ResourcePressureChanged`

Os nomes são conceituais; definir schemas versionados e compatíveis com o padrão de eventos do projeto antes de implementação.

## 2. Fluxos prioritários V1

1. Pergunta provável: segmento final → contexto → detector → política → cartão com pergunta e sugestão opcional.
2. Termo técnico: segmento final → glossário → explicação contextual → painel não intrusivo.
3. Sugestão em inglês: pergunta/contexto → resposta sugerida → tradução para português → usuário decide se usa.
4. Encerramento: segmentos finais → resumo e extração de tarefas → classificação de proveniência → revisão do usuário.

## 3. Degradação independente

A indisponibilidade da Iris não deve interromper captura, transcrição, tradução, reprodução de traduções ou persistência. Cada integração deve ter timeout, cancelamento, tratamento de falhas e fila limitada.
