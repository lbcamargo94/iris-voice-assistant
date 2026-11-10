# Benchmarks (POC-001A)

Relatórios reproduzíveis de STT, tradução, LLM e TTS. Os JSON em `results/` contêm **somente métricas**, ambiente, versões e SHA-256 dos modelos — nunca transcrições. Exceção: os relatórios de tradução incluem as saídas, porque as frases de referência são texto original do projeto (MIT), não conteúdo de reuniões.

## Preparação

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks\fetch-corpus.ps1   # AMI (~83 MB, CC BY 4.0, SHA-256 verificado)
cd workers; $env:UV_CACHE_DIR = "..\.cache\uv"; uv sync --group bench             # ~1,5 GB (CUDA/cuDNN via pip)
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks\fetch-translation-models.ps1  # Opus-MT + Argos (~930 MB, CC BY 4.0)
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

### Cadência híbrida e dois fluxos simultâneos → padrão escolhido (2026-11-10)

Modo híbrido (`agreement_interval_seconds`): passadas a cada 0,5 s só atualizam o texto provisório; a confirmação compara hipóteses separadas por ≥ 1 s de áudio novo. `--concurrent` processa as duas reuniões ao mesmo tempo no mesmo modelo, simulando sistema + microfone num único worker (pior caso: fala contínua nos dois). GPU livre; uma primeira rodada com um jogo aberto foi **descartada** (latências de dezenas de segundos). Resultados em `results/stt-policy-v3/`; large-v3-turbo int8_float16, beam 5 na confirmação.

| Fluxos | Configuração | WER IS / ES | 1ª exibição p50 / p95 (s) | Confirmação p50 / p95 (s) | RTF somado |
| --- | --- | --- | --- | --- | --- |
| 1 | bloco 1 s | 0.185 / 0.176 | 0.81–0.82 / 1.67–1.81 | 1.89–1.90 / 3.11–4.02 | 0.18–0.24 |
| 1 | bloco 0,5 s | 0.211 / 0.207 | 0.53 / 1.18–1.47 | 1.08 / 2.07–2.61 | 0.35–0.45 |
| 1 | híbrido (exibição beam 5) | 0.185 / 0.176 | 0.52–0.54 / 1.27–1.37 | 1.88 / 3.10–4.03 | 0.34–0.44 |
| 1 | híbrido (exibição beam 1) | 0.185 / 0.176 | 0.49–0.51 / 1.28–1.35 | 1.88–1.90 / 3.10–4.01 | 0.32–0.41 |
| 1 | **híbrido beam 1 + pular se atrasado** | 0.185 / 0.176 | **0.48–0.50 / 1.26–1.32** | 1.86 / 3.09–4.01 | 0.30–0.39 |
| 2 | bloco 1 s | 0.185 / 0.176 | 0.79–1.07 / 1.67–2.47 | 1.86–2.13 / 3.11–4.40 | 0.40 |
| 2 | bloco 0,5 s | 0.216 / 0.207 | 0.78–1.45 / 5.06–5.42 | 1.43–2.15 / 5.64–5.97 | 0.73 |
| 2 | híbrido beam 1 | 0.185 / 0.176 | 0.72–1.25 / 3.19–3.59 | 2.09–2.65 / 4.65–5.25 | 0.70 |
| 2 | **híbrido beam 1 + pular se atrasado** | 0.185 / 0.176 | **0.51–0.82 / 1.25–1.76** | **1.86–2.11 / 3.09–4.01** | 0.64 |

Zero cortes forçados e zero palavras perdidas em todas as configurações. "Pular se atrasado" = passadas de exibição puladas quando o worker está > 0,25 s atrás do áudio (1 fluxo: 2 de 600; 2 fluxos: 87 de 1200); a saída confirmada é idêntica.

**Padrão escolhido (ADR-008, refinamento):** híbrido 0,5 s / 1 s, exibição com beam 1 e pulada quando atrasada, confirmação beam 5. Atende a meta de 1ª exibição do RNF-015 também no p95 com dois fluxos no pior caso.

### Pendências do passo 2

- Parakeet via sherpa-onnx (concorrente, ADR-008) ainda não medido.
- Reuniões completas e mais reuniões AMI; normalização de números.
- Resource Manager: detectar atraso crescente quando outro programa usa a GPU e avisar o usuário.
- Confirmação p95 de 3–4 s + tradução precisa caber na meta de 4 s do RNF-015: medir no POC-001D.

## Tradução (passo 3) — Opus-MT tc-big × Argos

```powershell
cd workers
uv run --group bench python -m iris_workers.bench.translation --engine opus                      # >>pob<<, GPU int8_float16, beam 4
uv run --group bench python -m iris_workers.bench.translation --engine argos --target-token ">>pob<<"
uv run --group bench python -m iris_workers.bench.translation --engine opus --device cpu --compute-type int8 --intra-threads 4 --rounds 1
```

- **Referência:** 50 frases originais em `translation/reference-en-ptbr.md`, de 8 categorias, incluindo saída de STT sem pontuação. Ainda são **rascunho**, sem revisão humana, e vão ser corrigidas na prática.
- **chrF:** n-gramas de caracteres de 1 a 6, beta 2, uma só referência. As alternativas da coluna Notas não entram na pontuação. Com 50 frases, diferenças de 1 a 2 pontos não são significativas.
- **Latência:** medida por frase com batch 1, como no pipeline, que traduz cada oração confirmada. São 3 passadas na GPU e 1 na CPU.
- **PT-PT:** contagem de marcadores inequívocos (`translation_quality.PT_PT_MARKERS`).
- **Execução:** os dois motores rodam no CTranslate2 4.8.5 com SentencePiece 0.2.3, sem PyTorch e sem o pacote `argostranslate`.

### Resultados — 2026-11-10, RTX 4060 Ti 8 GB, Ryzen 7 5800X3D

Durante a medição, o desktop (Chrome, Teams, Discord) gerava picos de GPU de 5 a 60%, como num uso normal.

| Motor | Token | Dispositivo | Beam | chrF | Latência p50 / p95 (ms) | Frases com PT-PT | VRAM Δ (MiB) | RSS (MiB) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Opus-MT tc-big** | `>>pob<<` | GPU int8_float16 | 4 | **68,6** | 56 / 76 | **0** | ~550 | 829 |
| Opus-MT tc-big | `>>pob<<` | GPU float16 | 4 | 68,8 | 45 / 68 | 0 | ~810 | 784 |
| Opus-MT tc-big | `>>pob<<` | GPU int8_float16 | 1 | 67,5 | 48 / 66 | 0 | ~530 | 796 |
| Opus-MT tc-big | `>>pob<<` | CPU int8, 4 threads | 4 | 68,7 | 151 / 207 | 0 | 0 | 443 |
| Opus-MT tc-big | `>>por<<` | GPU int8_float16 | 4 | 65,8 | 60 / 86 | 3 (ficheiro, "estar a") | ~480 | 816 |
| Argos 1.9 | `>>pob<<` (config alterado) | GPU int8_float16 | 4 | 66,2 | 51 / 72 | 0 | ~310 | 798 |
| Argos 1.9 | padrão (`>>por<<`) | GPU int8_float16 | 4 | 62,7 | 54 / 74 | 8 (equipa, "estar a", ficheiro, utilizador) | ~280 | 799 |
| Argos 1.9 | padrão (`>>por<<`) | CPU int8, 4 threads | 4 | 62,2 | 33 / 46 | 8 | 0 | 169 |

Os modelos carregam em 0,2 a 1 s. A VRAM é medida no dispositivo inteiro: é o pico menos a linha de base.

### Conclusões

1. **O `>>pob<<` é necessário.** O token `>>por<<` produz PT-PT nos dois motores. O pacote `translate-en_pb` do Argos é **o mesmo modelo** do `translate-en_pt`, com o mesmo SHA-256 e só os metadados diferentes, e o token `>>por<<` vem fixo no `config.json`.
2. **Escolha: Opus-MT tc-big com `>>pob<<` e beam 4.** Ele tem o maior chrF. Na leitura das saídas, o Argos com `>>pob<<` **omite trechos**: "batch job" (013), "Okay, so" (027), "follow-up" (035) e "the thing is" (048). O Argos também erra mais termos: "inseto" para bug, "corrida" para sprint, "acidente" para crash.
3. **A latência não é o gargalo.** Na GPU fica entre 45 e 76 ms por frase, e na CPU entre 151 e 207 ms. A tradução ocupa menos de 5% da meta de 4 s do RNF-015. O orçamento continua dominado pela confirmação do STT.
4. **A escolha do dispositivo fica para o passo 6.** A CPU int8 não usa VRAM e não disputa a GPU com o STT, ao custo de cerca de 100 ms a mais. A GPU int8_float16 ocupa cerca de 0,55 GB. A decisão final sai da medição combinada (STT, tradução e LLM).
5. **O Marian descarta tudo depois da primeira frase** de uma entrada com várias. O adaptador separa as frases antes de traduzir (`split_sentences`). Medido nos casos 025 e 045 antes da correção.
6. **Erros que se repetem nos dois motores.** Os termos técnicos que se falam em inglês no Brasil saem traduzidos ao pé da letra:
   - build → construção, flaky → escamoso, pull request → pedido, merge → fusão;
   - feature flag → bandeira, endpoint → ponto final, dead letter queue → fila de cartas morta, ticket → bilhete.

   As expressões idiomáticas também falham: "take this offline" → tirar do ar, "you broke up" → você terminou, "final call" → última chamada. Isso justifica o glossário de termos que não devem ser traduzidos e o refinamento opcional por LLM do ADR-010. O LLM é o passo 4.

### Termos técnicos mantidos em inglês (glossário, 2026-11-10)

O usuário decidiu que os termos técnicos ficam em inglês. Para escolher como protegê-los, testei 8 formas de placeholder em 7 termos com o Opus-MT `>>pob<<`:

| Forma | Termos preservados |
| --- | --- |
| Termo original, sem placeholder | 0/7 |
| Entre aspas | 2/7 (traduziu "pedido de retirada" e "bandeira de recurso") |
| `__0__` | 2/7 |
| `[0]` | 6/7 |
| `X0`, `TERM0`, `ZQX0` ou um nome inventado | 7/7 |

O escolhido foi `ZQX0`, por ser improvável num texto real. O modelo trata o placeholder como substantivo masculino, por isso o glossário guarda o gênero do termo e corrige o artigo ("uma feature flag").

```powershell
uv run --group bench python -m iris_workers.bench.translation --engine opus --glossary default
```

**Resultado:**

- O chrF subiu de **68,6 para 72,8**.
- 13 termos foram preservados e nenhuma frase precisou de fallback.
- 10 das 50 frases mudaram, e todas melhoraram. Exemplos: bug (antes "inseto"), build ("construção"), pull request ("pedido"), merge ("fusão"), dead letter queue ("fila de cartas morta"), endpoint ("ponto final"), feature flag ("bandeira"), ticket ("bilhete"), "desta sprint".
- O custo de proteger e restaurar é de **33 µs por frase**.
- **Latência:** remedida com a máquina ociosa e uma rodada de controle sem glossário logo antes:

  | Configuração | Sem glossário (p50 / p95) | Com glossário (p50 / p95) |
  | --- | --- | --- |
  | GPU int8_float16 | 60 / 87 ms | 63 / 84 ms |
  | CPU int8 | — | 164 / 209 ms |

  A diferença fica dentro do ruído. A primeira tentativa foi descartada porque um jogo usava 67% da GPU. Naquela condição, a tradução sem glossário também subiu para cerca de 234 ms por frase: outro uso da GPU afeta a tradução, assim como afeta o STT.
- **Glossário padrão ativo** no motor rápido (ADR-010, refinamento, item 5).

### Pendências do passo 3

- Revisar as referências na prática e aceitar alternativas na pontuação, com múltiplas referências.
- Normalizador PT-PT→PT-BR (ADR-010, item 4). Nesta medição, com `>>pob<<`, nenhum marcador apareceu.
- "flaky test" ainda sai como "teste escamoso". Avaliar se o termo entra na lista padrão ou fica para o refinamento por LLM.
- Traduzir orações confirmadas vindas do STT real (AMI), não só frases escritas.
