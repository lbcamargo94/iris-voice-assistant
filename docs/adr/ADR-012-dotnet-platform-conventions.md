# ADR-012 — Convenções da plataforma .NET e do ambiente Python

- **Estado:** Aceita
- **Data:** 2026-11-09

## Decisão

- **SDK:** .NET 10, fixado por `global.json` (`rollForward: latestFeature`).
- **Versões de pacotes:** Central Package Management (`Directory.Packages.props`); propriedades comuns em `Directory.Build.props` (nullable, warnings como erro, analisadores).
- **Composição:** `Microsoft.Extensions.Hosting` (Generic Host) com injeção de dependência, configuração e `Microsoft.Extensions.Logging`. Logs não registram conteúdo de reunião por padrão.
- **IDs:** UUIDv7 via `Guid.CreateVersion7()` (ordenáveis por tempo, sem dependência externa).
- **Testes .NET:** xUnit.
- **Workers Python:** Python 3.12 gerenciado por `uv`, ambiente isolado em `workers/.venv`, dependências fixadas em `uv.lock`. Bibliotecas CUDA 12/cuDNN 9 instaladas via pip quando necessárias, sem instalação global do CUDA Toolkit.
- **Artefatos locais:** modelos em `/models` e dados em `/data` na raiz do repositório (ignorados pelo Git), configuráveis.

## Consequências

- Atualizações de SDK e pacotes são explícitas e revisáveis.
- Pacotes adicionais seguem a regra de justificativa do `AGENTS.md`.
