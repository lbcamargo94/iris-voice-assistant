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
uv sync --group ai   # runtime de IA dos workers de produção (faster-whisper, CUDA, sentencepiece; ~1,3 GB)
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
| `iris_workers.workers.speech` (+ `speech_engine`) | Worker de STT (POC-001D): faster-whisper large-v3-turbo, política v3, dois fluxos; eventos `stt.partial`, `stt.final` e `stt.ack` |
| `iris_workers.workers.translation` | Worker de tradução rápida (POC-001D): Opus-MT `>>pob<<` com glossário, CPU int8 com 4 threads |
| `iris_workers.testing.fake_pipeline_workers` | STT e tradução falsos, sem modelo, para os testes da sessão ao vivo no CI |
| `speech`, `translation`, `llm`, `tts` | Adaptadores e benchmarks do POC-001A; os workers de LLM e voz entram no POC-001E |

## Regras

- Nenhum worker faz upload de conteúdo nem chama APIs externas de IA.
- Downloads de modelos são explícitos, com checksum e licença registrados (`docs/MODEL-MANAGER.md`, `docs/LICENSES.md`).
