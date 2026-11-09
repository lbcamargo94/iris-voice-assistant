# Scripts

| Script | Função |
| --- | --- |
| `check-env.ps1` | Verifica os pré-requisitos (Windows build, .NET SDK, uv/Python 3.12, GPU, Ollama, espaço em disco). Somente leitura; não instala nada. |

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-env.ps1
```

Scripts de setup, benchmark e build serão adicionados conforme o POC-001 fixar versões das dependências. Não executar scripts de instalação sem revisar origem, checksum e licença dos artefatos.
