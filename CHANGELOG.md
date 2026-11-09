# Changelog

## v3.3 — 2026-11-09

### Decisões registradas (ADR-007 a ADR-013)

- Formato canônico 16 kHz mono float32, relógio por contagem de amostras e isolamento da voz da Iris por process loopback, com rede de segurança e aviso de caixa de som (ADR-007).
- STT com idioma fixo em inglês, VAD e streaming por concordância (LocalAgreement); ciclo de vida de segmento com ID estável; Parakeet/sherpa-onnx como concorrente no benchmark (ADR-008).
- Perdas visíveis: `AudioGap`, `SequenceNumber`, persistência antes da publicação e conciliação de amostras (ADR-009).
- Tradução PT-BR em camadas: Opus-MT rápido, refinamento opcional por LLM, normalizador PT-PT→PT-BR; NLLB excluído por licença (ADR-010).
- Experiência text-first com TTS sob demanda (botão "Ouvir") (ADR-011).
- Convenções .NET (Generic Host, CPM, UUIDv7) e Python 3.12 via `uv` (ADR-012).
- Detecção em camadas de perguntas dirigidas ao usuário, usando o microfone separado como sinal de turno (ADR-013).

### Documentação

- Requisitos: microfone transcrito como **Você**; RF-AI consolidados (antigos RF-AI-004/009 e 005/008 unificados e renumerados); novos RF-AUD-008..011, RF-STT-006/007, RF-TR-004..006, RF-UI-001..003, RF-DATA-006 e RNF-015..017; critérios de aceite do P0 mensuráveis.
- Prioridades da Iris renomeadas para `Immediate` / `Important` / `Informational` / `Archive`, evitando confusão com a fase P0.
- POC-001 reorganizado em seis etapas (A–F), incluindo transporte/WorkerHost e janela mínima.
- Arquitetura: tabela módulo → projeto → fase e fluxo de dados atualizado.
- Audio Engine, AI Runtime, Database, Security, Licenses, Test Strategy, IRIS-BEHAVIOR, IRIS-UX e IRIS-TESTS atualizados (AMI Meeting Corpus, verificação do Ollama em loopback, banco não criptografado no P0, CUDA via pip, licença GPL do sucessor do Piper).
- ADR-006 padronizado no formato dos demais ADRs.
- Projeto promovido para a raiz do repositório.

## v3.2 — 2026-11-09

### Identidade e nomenclatura

- Nome do projeto/produto padronizado como **Iris Voice Assistant**.
- Nome da assistente padronizado como **Iris**.
- Convenções propostas: repositório `iris-voice-assistant` e namespace .NET `IrisVoiceAssistant`.
- Adicionado ADR-006 para registrar a decisão e suas consequências.

### Documentação

- Atualizados README, AGENTS, requisitos, arquitetura, identidade da Iris, roadmap, integração e índice de ADRs.
- Preservadas as decisões técnicas e de produto aprovadas na versão anterior.
- Mantido explícito que o nome aprovado para o projeto não equivale a liberação jurídica de marca ou confirmação de domínio.

### Escopo preservado

- Local First; Windows como primeira plataforma; macOS e Linux como plataformas planejadas.
- C#/.NET 10 LTS, Avalonia UI + MVVM, SQLite/EF Core e workers locais Python.
- P0 continua sendo planejamento e prova de conceito; esta documentação não declara software funcional.
