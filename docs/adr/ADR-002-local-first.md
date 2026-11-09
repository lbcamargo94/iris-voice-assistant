# ADR-002 — Local First e IA local

- **Estado:** Aceita
- **Data:** 2026-11-09

## Contexto

Privacidade estrita e previsibilidade de custos são requisitos centrais.

## Decisão

Áudio, transcrição, tradução, assistência e persistência operam localmente. Nenhuma API externa de IA é obrigatória. Downloads de dependências/modelos são ações explícitas de instalação, separadas do uso normal.

## Consequências

- Menor dependência de rede e provedores.
- O desempenho depende do hardware local.
- Modelos, pesos e vozes exigem inventário de licença e integridade.
- Backup/sincronização em nuvem ficam fora do P0.
