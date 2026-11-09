# ADR-005 — Iris como agente transversal de inteligência

- **Estado:** Aceita
- **Data:** 2026-11-09

## Contexto

O Iris Voice Assistant precisa de uma experiência inteligente e proativa que conecte transcrição, tradução, contexto, glossário, memória e relatórios. Essa experiência não deve duplicar os módulos técnicos nem ficar presa a um fornecedor/modelo de IA.

## Decisão

Adotar a Iris como agente inteligente única do produto, implementada como `Iris Assistant Core` na camada Application. Sua personalidade é Balanced (profissional e amigável) e seu comportamento é Active, com proatividade mediada por relevância, confiança, prioridades, deduplicação, cooldown e preferências do usuário.

As denominações Iris Listen, Translate, Assist, Learn, Memory e Insights representam capacidades da mesma agente, não agentes ou modelos independentes obrigatórios. A Iris consome contratos dos módulos especializados e não os substitui.

A Iris não executa ações externas em nome do usuário. Falhas da agente não podem interromper captura, transcrição, tradução, reprodução das traduções ou persistência.

## Consequências

- Uma identidade e experiência conversacional consistentes em todos os módulos.
- Necessidade de contratos de eventos/contexto e testes de proatividade.
- Necessidade de rotular proveniência e incerteza para insights, decisões e tarefas.
- Maior consumo de recursos deve ser limitado pelo Resource Manager.
- Proatividade ativa precisa de controles explícitos e avaliação de falsos positivos.
