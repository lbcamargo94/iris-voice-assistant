# ADR-001 — Avalonia UI para desktop multiplataforma

- **Estado:** Aceita
- **Data:** 2026-11-09

## Contexto

O produto deve compartilhar interface entre Windows, macOS e Linux. WPF não atende esse requisito.

## Decisão

Adotar Avalonia UI com MVVM e C#/.NET para a aplicação desktop.

## Consequências

- Compartilhamento amplo de UI e lógica de apresentação.
- Recursos nativos e captura continuam exigindo adaptadores específicos.
- Validar compatibilidade de bibliotecas e empacotamento por sistema.
