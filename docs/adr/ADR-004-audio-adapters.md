# ADR-004 — Adaptadores de áudio por sistema operacional

- **Estado:** Aceita
- **Data:** 2026-11-09

## Contexto

Captura e reprodução de áudio dependem de APIs, permissões e servidores de áudio específicos de cada sistema.

## Decisão

Definir contratos de áudio independentes da plataforma e implementá-los em projetos separados: WASAPI no Windows, APIs de áudio/captura suportadas no macOS e PipeWire/PulseAudio no Linux.

## Consequências

- Domínio e aplicação permanecem portáveis.
- Cada plataforma exige testes e permissões próprios.
- Não declarar paridade sem validação real.
