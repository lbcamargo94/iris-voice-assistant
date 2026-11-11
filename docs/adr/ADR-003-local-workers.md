# ADR-003 — Workers locais para componentes de IA

- **Estado:** Aceita para o P0. O transporte foi decidido no [ADR-014](ADR-014-worker-transport-stdio.md): stdio com o Iris Worker Protocol v1.
- **Data:** 2026-11-09

## Contexto

Bibliotecas de STT, tradução e TTS possuem ecossistemas e dependências diferentes do aplicativo C#.

## Decisão

Manter C# como aplicação e orquestrador. Executar componentes Python/LLM como workers locais isolados, comunicando-se por contratos versionados. O transporte final será escolhido após benchmark entre IPC, pipes e HTTP loopback. Benchmark feito em 2026-11-11, com a decisão no ADR-014.

## Consequências

- Isolamento de dependências e falhas.
- Necessidade de gerenciar processos e versões.
- Protocolo deve suportar cancelamento, health checks e mensagens binárias eficientes.
