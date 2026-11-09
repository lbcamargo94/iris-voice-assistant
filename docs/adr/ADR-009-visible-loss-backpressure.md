# ADR-009 — Perdas visíveis e política de backpressure

- **Estado:** Aceita
- **Data:** 2026-11-09

## Contexto

O critério de aceite do P0 exige "nenhuma perda silenciosa de segmentos finais" em 60 minutos, mas não definia como medir nem o que pode ser descartado sob sobrecarga.

## Decisão

1. **Frames de áudio** podem ser descartados sob sobrecarga, sempre com contador por fluxo e um `AudioGap` (início, duração, causa) inserido na linha do tempo e exibido na UI.
2. **Segmentos `Final` nunca são descartados.** Cada um recebe `SequenceNumber` monotônico por fluxo e sessão e é persistido em SQLite (modo WAL) **antes** de ser publicado para a UI e a Iris.
3. **Tarefas da Iris** e traduções de refinamento podem ser canceladas por obsolescência; cancelamentos são contabilizados.
4. **Conciliação:** ao fim da sessão (e periodicamente), `amostras capturadas = amostras processadas + amostras descartadas`; qualquer diferença é falha.
5. **Reinício de worker:** o áudio é retido em buffer limitado (~30 s) durante o reinício; excedido o limite, o excedente vira `AudioGap`.
6. **Prioridade das filas:** captura → STT do sistema → STT do microfone → tradução rápida → persistência de derivados → refinamento por LLM → Iris.

## Consequências

- "Perda silenciosa" passa a ser verificável: buracos de sequência, frames não contabilizados ou gaps não exibidos reprovam o teste.
- Exige harness de teste com áudio de referência (ver `TEST-STRATEGY.md`).
