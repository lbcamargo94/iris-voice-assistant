# Arquitetura do Iris Voice Assistant

> Convenção de nomes .NET: usar `IrisVoiceAssistant` como namespace raiz (por exemplo, `IrisVoiceAssistant.Desktop`, `IrisVoiceAssistant.Core` e `IrisVoiceAssistant.Iris`). Essa convenção substitui os identificadores legados de produto.

## 1. Visão

Arquitetura Local First, modular e multiplataforma. O desktop coordena captura, interface, persistência e workers locais de IA. Não há backend remoto obrigatório no P0.

## 2. Camadas

1. **Presentation:** Avalonia UI + MVVM.
2. **Application:** casos de uso, orquestração de sessão e comandos.
3. **Domain/Core:** entidades, contratos e regras independentes de plataforma.
4. **Infrastructure:** SQLite, logging, filesystem e segurança local.
5. **Platform adapters:** captura/reprodução e recursos nativos de cada OS.
6. **Local AI workers:** STT, tradução, LLM e TTS.

## 3. Projetos propostos

- `IrisVoiceAssistant.Desktop`: aplicação Avalonia.
- `IrisVoiceAssistant.Core`: domínio e contratos.
- `IrisVoiceAssistant.Application`: casos de uso e orquestração.
- `IrisVoiceAssistant.Audio`: pipeline e abstrações de áudio.
- `IrisVoiceAssistant.Platform.Windows`: WASAPI e serviços Windows.
- `IrisVoiceAssistant.Platform.MacOS`: adaptadores macOS.
- `IrisVoiceAssistant.Platform.Linux`: PipeWire/PulseAudio.
- `IrisVoiceAssistant.Infrastructure`: persistência, arquivos e logging.
- `IrisVoiceAssistant.AI.Contracts`: contratos versionados dos workers.
- `IrisVoiceAssistant.WorkerHost`: ciclo de vida e comunicação com workers locais.
- `IrisVoiceAssistant.Iris`: serviços de aplicação para contexto, intenção, proatividade, política, memória e notificações da agente Iris.
- `workers/speech`: worker Python de transcrição.
- `workers/translation`: worker de tradução.
- `workers/llm`: adaptador para runtime LLM local.
- `workers/tts`: worker de síntese de voz.

A separação acima é uma direção arquitetural; projetos podem ser criados gradualmente conforme o P0 exigir.

### 3.1 Módulos funcionais → projetos → fase

| Nº | Módulo (IRIS-INTEGRATION) | Projeto(s) | Fase |
| ---: | --- | --- | --- |
| 1 | Audio Engine | `Audio`, `Platform.*` | P0 |
| 2 | Transcription Engine | `AI.Contracts`, `WorkerHost`, `workers/speech` | P0 |
| 3 | Translation Engine | `AI.Contracts`, `WorkerHost`, `workers/translation` | P0 |
| 4 | Voice Engine | `AI.Contracts`, `WorkerHost`, `workers/tts`, `Audio` | P0 |
| 5 | Meeting Intelligence | `Iris` | V1 |
| 6 | Speaker Intelligence | `Iris` (rótulos Você/Participantes); diarização futura | V1/V2 |
| 7 | Technical Glossary | `Core`, `Application`, `Infrastructure` | V1 |
| 8 | Meeting Memory | `Iris`, `Infrastructure` | V1/V2 |
| 9 | Reports & Tasks | `Iris`, `Application` | V1 |
| 10 | English Learning | `Iris` | V2 |
| 11 | Model Manager | `Application`, `Infrastructure` | P0 (manifests) / V1 |
| 12 | Resource Manager | `Application` | P0 (medição) / V1 |
| 13 | Local Storage | `Infrastructure` | P0 |
| 14 | Security & Privacy | transversal (`Application`, `Infrastructure`) | P0 |
| 15 | Update & Distribution Manager | a definir | V1.5 |
| 16 | Commercial License Manager | a definir | V3 |

## 4. Fluxo de dados

1. Adaptadores de áudio emitem frames separados no formato canônico (ADR-007): `SystemAudio` (Participantes) e `Microphone` (Você), excluindo o áudio do próprio app.
2. VAD delimita atividade de fala e evita inferência em silêncio.
3. STT em streaming por concordância produz revisões parciais e segmentos finais com ID estável (ADR-008).
4. Segmentos finais são persistidos antes de serem publicados (ADR-009).
5. A tradução rápida PT-BR é produzida por oração confirmada e vinculada ao segmento; o refinamento opcional por LLM substitui a exibição preservando ambas as versões (ADR-010).
6. A UI apresenta a linha do tempo de anotações (text-first, ADR-011), lacunas e controles.
7. O TTS sintetiza somente sob demanda, no processo do app.
8. O Iris Assistant Core recebe eventos finais/relevantes e contexto limitado; aplica política, detecta intenções (ADR-013) e decide se apresenta um insight.
9. A Iris publica sugestões e explicações na UI; não executa ações externas nem substitui os módulos especializados.

## 5. Contratos principais

Criar contratos para `IIrisAssistantService`, `IIrisContextProvider`, `IIrisIntentDetector`, `IIrisProactivityPolicy`, `IIrisResponseGenerator` e `IIrisNotificationPublisher`, além de contratos para `IAudioCaptureService`, `IAudioPlaybackService`, `ISpeechToTextProvider`, `ITranslationProvider`, `ITextToSpeechProvider`, `ILanguageModelProvider`, `IModelCatalog` e `IMeetingRepository`.

Contratos devem incluir cancelamento, estados, erros tipados, metadados de modelo e versões de protocolo quando cruzarem fronteiras de processo.

## 6. Comunicação com workers

Para o P0, preferir processos locais isolados com protocolo JSON-RPC/HTTP local ou pipes. Escolher um mecanismo após protótipo de latência e robustez. Não expor endpoints em interfaces de rede; bind em loopback ou usar IPC. Validar origem, permissões e tamanho de payload.

Áudio binário não deve ser codificado em JSON base64 para streaming de alta frequência. Usar transporte binário/IPC adequado e mensagens de controle separadas.

## 7. Decisões de implantação

- .NET 10 LTS e Avalonia UI.
- Generic Host (`Microsoft.Extensions.Hosting`) para DI, configuração e logging; versões centralizadas (ADR-012).
- Python 3.12 em ambiente isolado gerenciado por `uv` para bibliotecas de IA (ADR-012).
- Ollama é um runtime local opcional para LLM, não um serviço remoto.
- Modelos são artefatos separados, baixados explicitamente.
- O aplicativo deve funcionar sem internet depois que dependências e modelos estiverem instalados, dentro das limitações de drivers e licenças.

## 8. Multiplataforma

O domínio e a aplicação não podem referenciar APIs específicas do sistema operacional. Adaptadores nativos ficam em assemblies separados. A captura de áudio no macOS pode depender de permissões e capacidades específicas da versão do sistema; Linux varia conforme PipeWire/PulseAudio e ambiente. Não prometer paridade até validar cada ambiente.

## 9. Iris Assistant Core

A Iris pertence à camada Application e consome eventos/contratos dos módulos de áudio, transcrição, tradução, glossário, memória e relatórios. A detecção pode combinar regras determinísticas e LLM local. Autorização, privacidade, prioridades e limites de autonomia devem ser aplicados por políticas determinísticas. A falha da Iris deve degradar apenas a assistência contextual, nunca o pipeline principal. Ver `IRIS-*.md`.

## 10. Diagramas e decisões

Ver `adr/` para decisões registradas. Alterações de stack, transporte, persistência ou privacidade devem ter ADR.
