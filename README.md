# Iris Voice Assistant

**Nome do projeto/produto:** Iris Voice Assistant  
**Assistente inteligente:** Iris  
**Versão da documentação:** 3.3  
**Status:** Planejamento técnico / P0  
**Arquitetura:** Local First, multiplataforma  
**Primeira plataforma:** Windows  
**Plataformas planejadas:** Windows, macOS e Linux

> O nome **Iris Voice Assistant** é a denominação aprovada para o projeto e para o produto nesta documentação. A disponibilidade de domínio e a liberação jurídica de marca ainda precisam ser verificadas antes de qualquer lançamento comercial. **Iris** é exclusivamente o nome da assistente inteligente.

Iris Voice Assistant é um aplicativo desktop para reuniões, cuja assistente inteligente integrada se chama Iris. Seu objetivo é transcrever toda a reunião em inglês (participantes e o próprio usuário, em fluxos separados), traduzir para português brasileiro em tempo quase real e oferecer assistência contextual usando modelos executados localmente. Na configuração padrão a experiência é text-first: uma janela de anotações mostra original e tradução, e a Iris lê qualquer mensagem em voz sob demanda. A agente Iris é a camada conversacional e de inteligência que coordena essas capacidades, com comportamento ativo, equilibrado e controlado pelo usuário.

## Convenções de nomenclatura

- Nome apresentado ao usuário: **Iris Voice Assistant**.
- Nome da assistente: **Iris**.
- Slug sugerido para repositório/pacote: `iris-voice-assistant`.
- Namespace raiz sugerido para projetos .NET: `IrisVoiceAssistant`.
- Nome da assistente em serviços, eventos e documentação: `Iris` (por exemplo, `IrisAssistantService`).
- Não usar `InteliVoice`, `MeetingAI` ou `Veya` em novos arquivos, namespaces, identificadores, interfaces ou textos de interface.
- A alteração do nome não altera decisões de arquitetura, escopo, privacidade ou roadmap.

## Princípios

- **Local First:** áudio, transcrições e inferência permanecem no dispositivo por padrão.
- **Sem APIs externas de IA obrigatórias:** o runtime normal não envia áudio ou texto para provedores externos.
- **Multiplataforma:** núcleo compartilhado em C#/.NET e Avalonia UI, com adaptadores específicos por sistema operacional.
- **Modularidade:** modelos e provedores podem ser substituídos por interfaces estáveis.
- **Transparência:** captura sempre visível, controlável e sujeita a consentimento e políticas aplicáveis.
- **Evolução incremental:** validar primeiro o P0 no Windows antes de ampliar o escopo.

## Estado atual

Esta entrega contém a documentação de engenharia e o plano da prova de conceito. Ainda não contém uma aplicação funcional. A implementação só deve ser considerada iniciada quando os artefatos de código, testes e resultados forem efetivamente adicionados ao repositório.

## Documentação

- [Conteúdo do pacote](PACKAGE-CONTENTS.md)
- [Changelog](CHANGELOG.md)

- [Identidade e escopo da Iris](docs/IRIS-IDENTITY.md)
- [Comportamento e proatividade da Iris](docs/IRIS-BEHAVIOR.md)
- [Arquitetura da Iris](docs/IRIS-ARCHITECTURE.md)
- [Contexto e memória da Iris](docs/IRIS-CONTEXT.md)
- [Integração da Iris com os módulos](docs/IRIS-INTEGRATION.md)
- [Experiência e UX da Iris](docs/IRIS-UX.md)
- [Testes da Iris](docs/IRIS-TESTS.md)
- [Requisitos](docs/REQUIREMENTS.md)
- [Arquitetura](docs/ARCHITECTURE.md)
- [Audio Engine](docs/AUDIO-ENGINE.md)
- [AI Runtime](docs/AI-RUNTIME.md)
- [Model Manager](docs/MODEL-MANAGER.md)
- [Banco de dados](docs/DATABASE.md)
- [Segurança e privacidade](docs/SECURITY.md)
- [Estratégia de testes](docs/TEST-STRATEGY.md)
- [Licenças e dependências](docs/LICENSES.md)
- [Roadmap](docs/ROADMAP.md)
- [Plano POC-001](docs/POC-001.md)
- [Decisões arquiteturais (ADR-001 a ADR-013)](docs/adr/README.md)
- [Instruções para agentes](AGENTS.md)

## Visão do P0

1. Preparar ambiente Windows, .NET e Python.
2. Medir execução local de transcrição, tradução, LLM e TTS, isolados e combinados.
3. Capturar áudio do sistema (excluindo o próprio app) e microfone em fluxos separados.
4. Definir o transporte com os workers locais.
5. Integrar uma pipeline de transcrição em streaming e tradução PT-BR por oração.
6. Reproduzir TTS sob demanda sem realimentar a captura.
7. Registrar métricas e resultados reproduzíveis numa sessão de 60 minutos.

## Requisitos iniciais de desenvolvimento

- Windows 11 x64 (ou Windows 10 build 20348+) para o primeiro protótipo, por causa do process loopback (ADR-007).
- .NET 10 SDK (versão fixada em `global.json`).
- Python 3.12 gerenciado por `uv`, em ambiente isolado para os workers de IA.
- Driver NVIDIA compatível, caso seja usada aceleração GPU. As bibliotecas CUDA 12/cuDNN 9 são instaladas por pip no ambiente dos workers; não é necessário instalar o CUDA Toolkit.
- Ollama instalado separadamente para testes do LLM local.
- Espaço em SSD para dependências e modelos (fora da unidade do sistema, se ela estiver com pouco espaço).
- Fone de ouvido recomendado durante reuniões (evita eco acústico da voz da Iris).

As versões exatas de pacotes e modelos deverão ser fixadas após a POC e registradas em manifests. Não assumir compatibilidade apenas pelo nome do modelo ou pela presença da GPU.

## Como contribuir

Leia `AGENTS.md`, `docs/ARCHITECTURE.md`, `docs/IRIS-*.md` quando trabalhar na agente e `docs/POC-001.md` antes de implementar. Toda alteração arquitetural relevante deve ser registrada em um ADR.

## Licença

O código deste repositório é distribuído sob a [licença MIT](LICENSE). Modelos, vozes, runtimes e conjuntos de dados de terceiros (Whisper, Opus-MT, Piper, CUDA/cuDNN, AMI etc.) **não** são cobertos por ela: cada um mantém a própria licença, listada em [docs/LICENSES.md](docs/LICENSES.md), e não é redistribuído neste repositório.
