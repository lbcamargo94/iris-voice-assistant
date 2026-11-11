# Hosts de terceiros (processos separados)

Os programas desta pasta rodam em ambientes próprios (`.tools/<nome>`), **fora** dos workers MIT. Eles existem porque dependem de componentes copyleft. Cada arquivo declara a própria licença no cabeçalho SPDX. O texto da GPL-3.0 está em `COPYING`.

| Host | Licença | Motivo |
| --- | --- | --- |
| `kokoro/kokoro_host.py` | GPL-3.0-or-later | importa `kokoro-onnx` → `phonemizer` (GPL-3.0-or-later) e carrega o espeak-ng (GPL-3.0) |

Os workers (`workers/`, MIT) **não importam** esses hosts. Eles só abrem o processo e conversam pelo protocolo abaixo (`iris_workers/tts/framed_host.py`).

## Protocolo de stdio v1 (TTS)

**Entrada (stdin):** uma requisição JSON por linha, em UTF-8.

```json
{"id": "42", "text": "Texto a ler."}
{"op": "shutdown"}
```

**Saída (stdout):** frames. Cada frame é uma linha de cabeçalho JSON, seguida dos bytes indicados em `bytes`, quando houver. Nada mais é escrito no stdout; logs vão para o stderr.

| `type` | Campos | Payload |
| --- | --- | --- |
| `ready` | `sample_rate`, `load_seconds` | — |
| `audio` | `id`, `seq`, `bytes` | PCM 16 bits little-endian, mono |
| `end` | `id`, `chunks` | — |
| `error` | `id` (pode ser `null`), `message` | — |

- Todo cabeçalho traz `"v": 1`.
- O host envia um `audio` por frase assim que ela fica pronta. O cliente pode começar a tocar já no primeiro.
- Toda requisição termina com `end` ou `error`. Diferente da CLI do Piper, o fim da fala é explícito.
