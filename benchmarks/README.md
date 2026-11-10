# Benchmarks (POC-001A)

Relatórios reproduzíveis de STT, tradução, LLM e TTS. Os JSON em `results/` contêm **somente métricas**, ambiente, versões e SHA-256 dos modelos — nunca transcrições.

## Preparação

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks\fetch-corpus.ps1   # AMI (~83 MB, CC BY 4.0, SHA-256 verificado)
cd workers; $env:UV_CACHE_DIR = "..\.cache\uv"; uv sync --group bench             # ~1,5 GB (CUDA/cuDNN via pip)
```

## STT (passo 2) — faster-whisper + LocalAgreement-2

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks\run-stt.ps1 -AllowDownload   # modelos ~4,8 GB em models/whisper
cd workers; uv run python -m iris_workers.bench.summarize ..\benchmarks\results\stt
```

**Método.** Primeiros 300 s de duas reuniões AMI de teste (IS1009a, ES2004a; mix de headsets, 16 kHz mono). Streaming: blocos de 1 s, buffer reprocessado a cada bloco com timestamps por palavra e VAD Silero, confirmação pelo prefixo comum de duas hipóteses (`iris_workers/speech/local_agreement.py`), corte do buffer após fim de frase confirmado (15 s; máx. 30 s). A latência usa relógio simulado em tempo real: um passo só começa quando o áudio existe *e* o passo anterior terminou.

- **1ª exibição:** tempo entre o fim da palavra falada e sua primeira aparição na tela (parcial).
- **Confirmação:** tempo até a palavra se tornar final (é o que dispara a tradução, ADR-010).
- **Atraso máx.:** maior atraso acumulado do processamento em relação ao áudio.
- **WER:** normalização simples e simétrica (minúsculas, sem pontuação, sem hesitações); números **não** normalizados. Use para comparar configurações, não como qualidade absoluta.

### Resultados — 2026-11-09, RTX 4060 Ti 8 GB, driver 616.92, Ryzen 7 5800X3D, faster-whisper 1.2.1, CTranslate2 4.8.5

| Modelo | Compute | Beam | Reunião | WER offline | WER streaming | RTF streaming | 1ª exibição p50/p95 (s) | Confirmação p50/p95 (s) | Atraso máx. (s) | Cortes forçados | VRAM Δ (MiB) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| small.en | float16 | 5 | IS1009a | 0.152 | 0.152 | 0.258 | 0.83 / 2.29 | 1.91 / 3.85 | 3.05 | 1 | 1074 |
| small.en | float16 | 5 | ES2004a | 0.215 | 0.194 | 0.175 | 0.78 / 2.67 | 1.84 / 3.99 | 1.39 | 4 | 1074 |
| medium.en | float16 | 5 | IS1009a | 0.176 | 0.189 | 0.404 | 1.01 / 2.23 | 2.21 / 6.32 | 2.87 | 0 | 2707 |
| medium.en | float16 | 5 | ES2004a | 0.221 | 0.210 | 0.343 | 1.06 / 2.90 | 2.17 / 4.29 | 2.53 | 4 | 2707 |
| distil-large-v3 | float16 | 5 | IS1009a | 0.176 | 0.152 | 0.226 | 0.73 / 1.64 | 1.82 / 2.96 | 0.48 | 1 | 2362 |
| distil-large-v3 | float16 | 5 | ES2004a | 0.226 | 0.189 | 0.189 | 0.82 / 2.07 | 1.88 / 4.36 | 0.38 | 4 | 2362 |
| large-v3-turbo | float16 | 5 | IS1009a | 0.154 | 0.163 | 0.255 | 0.78 / 1.69 | 1.84 / 2.92 | 1.33 | 1 | 2541 |
| large-v3-turbo | float16 | 5 | ES2004a | 0.210 | 0.181 | 0.198 | 0.83 / 2.30 | 1.87 / 3.65 | 0.41 | 4 | 2541 |
| **large-v3-turbo** | **int8_float16** | 5 | IS1009a | 0.150 | 0.172 | 0.246 | 0.74 / 1.73 | 1.87 / 3.35 | 1.15 | 0 | **1489** |
| **large-v3-turbo** | **int8_float16** | 5 | ES2004a | 0.205 | 0.178 | 0.197 | 0.83 / 2.27 | 1.85 / 3.76 | 0.41 | 4 | **1489** |
| large-v3-turbo | float16 | 1 | IS1009a | 0.185 | 0.159 | 0.203 | 0.69 / 1.56 | 1.76 / 2.92 | 0.51 | 1 | 2583 |
| large-v3-turbo | float16 | 1 | ES2004a | 0.215 | 0.176 | 0.175 | 0.81 / 2.44 | 1.86 / 4.02 | 0.32 | 4 | 2583 |

VRAM Δ = pico de VRAM do dispositivo menos a linha de base antes de carregar o modelo (~2,3–2,5 GB já ocupados pelo desktop do Windows). CPU do processo ~6% em todas as execuções; RSS pico 1,1–1,8 GB.

### Conclusões provisórias

1. **Tempo real é viável em todas as configurações** (RTF streaming ≤ 0,40; um fluxo). Com dois fluxos (sistema + microfone) o custo aproximadamente dobra: ainda abaixo de 1, mas precisa ser medido no POC-001D.
2. **Latência é dominada pela política, não pelo modelo.** 1ª exibição p50 ≈ 0,7–0,8 s atende à meta do RNF-015 (≤ 1,5 s), mas o **p95 (1,6–2,9 s) não atende**. Confirmação p50 ≈ 1,8 s / p95 ≈ 3–4 s. Próximas alavancas: blocos de 0,5 s, buffer menor, publicar parciais antes da confirmação.
3. **Candidato provisório: large-v3-turbo int8_float16** — melhor WER offline, streaming equivalente aos demais, e a menor VRAM entre os modelos grandes (~1,5 GB), deixando espaço para tradução e LLM em 8 GB. Alternativa: distil-large-v3 float16. As diferenças de WER (±0,03) são pequenas para 2×300 s e **não são estatisticamente conclusivas**.
4. **Cortes forçados (ES2004a: 4 em 300 s em todos os modelos)** indicavam trechos de até 30 s sem nenhuma palavra confirmada. Investigado e corrigido na política v2 (seção abaixo); a tabela acima é da política v1.
5. A amostra de IS1009a inclui conversa antes do início anotado pelo AMI, o que penaliza o WER de todos igualmente.

### Investigação dos cortes forçados → política v2 (2026-11-09)

Rastreamento passo a passo em ES2004a (large-v3-turbo int8) mostrou duas falhas da política v1, não do modelo:

1. **Silêncio acumulado esconde fala.** Após uma frase confirmada, o buffer seguiu crescendo com ~18 s de silêncio; com a janela dominada por silêncio, o Whisper classificou tudo como "sem fala" e omitiu "Is this going to fall off?" (50 s) até o corte forçado de 63 s — 14 s de atraso.
2. **O corte forçado descartava palavras provisórias** sem registro, violando a ADR-009. Na v1: 4 cortes, 10 palavras da referência dentro de áudio descartado e nunca confirmadas (ex.: "What's missing").

Correções em `local_agreement.py` (com testes):

- **Corte de silêncio:** quando uma passada não ouve nenhuma palavra nova e já se passaram ≥ 3 s desde a última palavra confirmada, o áudio antigo é descartado (mantendo os últimos 3 s). Pausas comuns mantêm o contexto acústico. Contabilizado em `silence_trimmed_seconds` (não é perda: o modelo não ouviu palavras ali).
- **Corte forçado sem perda:** palavras provisórias dentro do trecho descartado são confirmadas em vez de perdidas; o trecho fica em `forced_trim_ranges`.

Resultados (large-v3-turbo int8_float16, beam 5; `results/stt-policy-v2/`):

| Política | Bloco | WER IS1009a / ES2004a | 1ª exibição p50 / p95 (s) | Confirmação p50 / p95 (s) | RTF | Cortes forçados | Palavras perdidas |
| --- | --- | --- | --- | --- | --- | --- | --- |
| v1 | 1 s | 0.172 / 0.178 | 0.74–0.83 / 1.73–2.27 | 1.85–1.87 / 3.35–3.76 | 0.20–0.25 | 0 / 4 | 10 |
| **v2** | 1 s | 0.185 / 0.176 | 0.80–0.82 / 1.70–1.81 | 1.89 / 3.11–4.04 | 0.18–0.24 | 0 / 0 | 0 |
| v2 | 0,5 s | 0.216 / 0.207 | **0.54 / 1.16–1.47** | **1.08 / 2.06–2.74** | 0.35–0.44 | 0 / 0 | 0 |

Leitura:

- A v2 elimina a perda silenciosa sem custo relevante de WER (+0,013 em IS1009a, −0,002 em ES2004a).
- **Blocos de 0,5 s atingem a meta de 1ª exibição do RNF-015 também no p95** (≤ 1,5 s) e reduzem a confirmação pela metade, ao custo de ~+0,03 de WER e do dobro de GPU (RTF ~0,4 por fluxo; ~0,8 estimado com dois fluxos — apertado). Decisão de produto: ver pendências.

### Pendências do passo 2

- Escolher o bloco padrão (1 s × 0,5 s) e medir **dois fluxos simultâneos** antes de fixar.
- Alternativa a avaliar: parciais a cada 0,5 s, mas confirmação só com hipóteses de ≥ 1 s de áudio novo (latência de exibição do 0,5 s com a precisão do 1 s).
- Parakeet via sherpa-onnx (concorrente, ADR-008) ainda não medido.
- Reuniões completas e mais reuniões AMI; normalização de números.
