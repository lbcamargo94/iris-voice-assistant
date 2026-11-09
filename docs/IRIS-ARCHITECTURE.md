# Iris — arquitetura técnica

## 1. Papel arquitetural

`Iris Assistant Core` é um serviço da camada Application que coordena contexto, detecção de intenção, política de proatividade e geração de respostas. Não implementa captura, STT, tradução, TTS ou persistência diretamente; consome contratos dos módulos especializados.

## 2. Componentes propostos

| Componente | Responsabilidade |
| --- | --- |
| `IrisAssistantService` | Fachada/caso de uso para comandos e eventos da agente |
| `IrisEventProcessor` | Normaliza e encaminha eventos da reunião |
| `IrisContextManager` | Monta contexto recente e limitado por reunião |
| `IrisIntentDetector` | Detecta perguntas, pedidos, conceitos e eventos candidatos |
| `IrisProactivityManager` | Decide prioridade, oportunidade e frequência de intervenção |
| `IrisPolicyEngine` | Aplica permissões, privacidade e limites de autonomia |
| `IrisResponseGenerator` | Produz explicações e sugestões usando `ILanguageModelProvider` |
| `IrisMemoryService` | Consulta e grava memória autorizada |
| `IrisNotificationService` | Emite cartões/eventos para a UI sem bloquear o pipeline |
| `IrisEvaluationTelemetry` | Métricas locais opcionais, sem conteúdo sensível por padrão |

## 3. Contratos de aplicação sugeridos

- `IIrisAssistantService`
- `IIrisEventProcessor`
- `IIrisContextProvider`
- `IIrisIntentDetector`
- `IIrisProactivityPolicy`
- `IIrisResponseGenerator`
- `IIrisMemoryRepository`
- `IIrisNotificationPublisher`

Os nomes são propostas de contratos; validar convenções do código antes de implementá-los. Todos os contratos assíncronos devem aceitar `CancellationToken`, retornar resultados tipados e evitar acoplamento a um LLM específico.

## 4. Eventos e pipeline

1. Audio/Transcription/Translation publicam eventos tipados com IDs, timestamps e estados.
2. IrisEventProcessor filtra eventos relevantes e deduplica atualizações parciais.
3. IrisContextManager monta janela de contexto limitada, incluindo referências a segmentos e metadados permitidos.
4. IrisIntentDetector produz candidatos estruturados com evidências e confiança.
5. IrisPolicyEngine verifica permissões e políticas.
6. IrisProactivityManager classifica prioridade, aplica cooldown e decide se deve notificar.
7. IrisResponseGenerator cria explicação/sugestão quando necessário.
8. IrisNotificationService publica o resultado na UI.
9. Interações e confirmações do usuário são persistidas de acordo com preferências e retenção.

## 5. Modelos e workers

O LLM local é acessado por `ILanguageModelProvider`. A detecção de intenção pode combinar regras determinísticas com LLM, mas decisões críticas de privacidade e autorização devem ser determinísticas. Não enviar conteúdo para APIs remotas. Modelos são geridos pelo Model Manager e recursos são coordenados pelo Resource Manager.

## 6. Limites de recursos

- Não executar inferência LLM para cada token ou segmento parcial.
- Priorizar STT e tradução em tempo real; tarefas da Iris devem usar filas limitadas e prioridades.
- Permitir cancelar ou descartar tarefas obsoletas de contexto.
- Limitar tamanho e duração do contexto.
- Evitar manter múltiplos modelos pesados carregados sem medição de VRAM/RAM.
- Respeitar backpressure e não bloquear o thread da UI nem a captura de áudio.

## 7. Erros e degradação

Se o LLM estiver indisponível, transcrição e tradução devem continuar. A UI deve indicar que a assistência contextual está temporariamente indisponível. Falha da Iris não pode interromper captura, STT, tradução ou persistência dos segmentos finais.

## 8. Testabilidade

Usar interfaces substituíveis e modelos determinísticos/fakes nos testes. Avaliar detector e política separadamente: precisão da intenção, calibração de confiança, falsos alertas, deduplicação, latência, consumo e comportamento de cancelamento.
