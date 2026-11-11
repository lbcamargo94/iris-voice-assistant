# Workers Python

Workers locais de IA do Iris Voice Assistant (ADR-003, ADR-012). Executam isolados do app .NET e se comunicam por contratos versionados: stdio com o Iris Worker Protocol v1 (ADR-014, `iris_workers.protocol`).

## Ambiente

- Python **3.12**, gerenciado por [`uv`](https://docs.astral.sh/uv/).
- Ambiente virtual em `workers/.venv`; dependências fixadas em `uv.lock`.
- Cache do `uv` em `<repo>/.cache/uv` (mesma unidade do repositório, ignorado pelo Git), para não ocupar a unidade do sistema.

```powershell
cd workers
$env:UV_CACHE_DIR = "..\.cache\uv"
uv sync          # cria .venv com Python 3.12 e instala dependências
uv run pytest    # executa os testes
uv run python -m iris_workers.environment   # relatório de ambiente (sem conteúdo de reunião)
```

`scripts/check-env.ps1` verifica todos os pré-requisitos do repositório.

## Módulos

| Módulo | Estado |
| --- | --- |
| `iris_workers.environment` | Relatório de hardware/software para benchmarks |
| `iris_workers.protocol` | Iris Worker Protocol v1: codificação e `WorkerServer` (stdout reservado ao protocolo, logs no stderr) |
| `iris_workers.testing.contract_worker` | Worker de teste com modos de falha, usado pelos testes de contrato do WorkerHost |
| `speech`, `translation`, `llm`, `tts` | Adaptadores medidos no POC-001A; os workers de produção entram no POC-001D e E |

## Regras

- Nenhum worker faz upload de conteúdo nem chama APIs externas de IA.
- Downloads de modelos são explícitos, com checksum e licença registrados (`docs/MODEL-MANAGER.md`, `docs/LICENSES.md`).
