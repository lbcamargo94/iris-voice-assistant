# Benchmarks (POC-001A)

Relatórios reproduzíveis de STT, tradução, LLM e TTS. Os JSON em `results/` contêm **somente métricas**, ambiente, versões e SHA-256 dos modelos — nunca transcrições. Exceção: os relatórios de tradução incluem as saídas, porque as frases de referência são texto original do projeto (MIT), não conteúdo de reuniões.

## Preparação

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks\fetch-corpus.ps1   # AMI (~83 MB, CC BY 4.0, SHA-256 verificado)
cd workers; $env:UV_CACHE_DIR = "..\.cache\uv"; uv sync --group bench             # ~1,5 GB (CUDA/cuDNN via pip)
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks\fetch-translation-models.ps1  # Opus-MT + Argos (~930 MB, CC BY 4.0)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\fetch-ollama.ps1                  # Ollama portátil (~1,5 GB, MIT)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\start-ollama.ps1 -Pull qwen3:4b-instruct,phi4-mini:3.8b  # loopback, sem nuvem
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks\fetch-kokoro.ps1               # voz da Iris: Kokoro + kokoro-onnx (~355 MB, processo separado)
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks\fetch-tts.ps1                  # Piper + vozes CC0 (~155 MB), só comparação
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

## LLM local (passo 4) — Ollama: refinamento e classificação

```powershell
cd workers
uv run --group bench python -m iris_workers.bench.llm --model qwen3:4b-instruct   # refine + classify
```

**Configuração do runtime:**

- Ollama **v0.40.16**, versão portátil (`scripts/fetch-ollama.ps1`). O script confere o SHA-256 do release e a assinatura Authenticode da Ollama Inc.
- O instalador ficou de fora porque grava no C:, roda na bandeja e se atualiza sozinho.
- O servidor sobe com `OLLAMA_HOST=127.0.0.1:11434` e `OLLAMA_NO_CLOUD=1`. O log confirma `Ollama cloud disabled: true` e `Listening on 127.0.0.1:11434`, e conferi que só existe socket em loopback.
- O cliente (`iris_workers/llm/ollama.py`) usa apenas a biblioteca padrão. Ele recusa URLs fora de loopback e ignora proxies do sistema, para que nenhum prompt seja desviado.

**Tarefas:**

1. **Refinamento (ADR-010, camada 2).** O modelo recebe o original em inglês, a saída do Opus-MT com glossário (chrF 72,8) e os termos que ficam em inglês.
2. **Classificação (ADR-013, camada 6).** Ele decide se uma fala de participante é dirigida ao usuário (`directed`), a outra pessoa (`not_directed`) ou ao grupo (`ambiguous`). São 32 falas originais em `llm/directed-questions.md`, ainda em rascunho, e o modelo responde em JSON estruturado.

O modelo roda com temperatura 0, seed 7 e contexto de 2048 tokens.

### Resultados do LLM — 2026-11-11, RTX 4060 Ti 8 GB, modelos Q4_K_M

| Modelo | Prompt | Refino: chrF (Opus-MT: 72,8) | Refino: frases alteradas | Refino: total p50 / p95 | Classificação: acerto | Recall directed / not_directed / ambiguous | Classificação: total p50 / p95 | VRAM do modelo |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| qwen3:4b-instruct | v1 | 70,4 | 44/50 | 247 / 346 ms | 0,53 | 1,00 / 0,20 / 0,22 | 494 / 641 ms | 2,7 GB |
| **qwen3:4b-instruct** | **v2** | 73,4 | 37/50 | 255 / 366 ms | **0,75** | 0,85 / 0,60 / 0,78 | 591 / 939 ms | 2,7 GB |
| phi4-mini:3.8b | v2 | 72,0 | 18/50 | 209 / 271 ms | 0,56 | 0,23 / 0,90 / 0,67 | 378 / 496 ms | 2,7 GB |

- O primeiro token sai em 27 a 33 ms. Todos os 13 termos do glossário foram mantidos, nenhuma saída veio suja e não apareceu PT-PT.
- A VRAM do dispositivo foi de ~2,2 para ~5,2 GB com o modelo carregado.
- **Mudança do prompt v1 para v2** (`llm/prompts.py`, `PROMPT_VERSION`):
  - no v1, o modelo deixava em inglês palavras fora da lista ("twenty itens", "library") e reescrevia frases que já estavam corretas;
  - o classificador entendia "directed" como "dirigida a alguém";
  - o v2 restringe o inglês à lista e classifica por "quem deve responder", pedindo o motivo antes do rótulo.

  Fiz uma única revisão, para não ajustar o prompt demais aos 32 exemplos.

### Conclusões

1. **O refinamento por LLM não pode substituir a tradução automaticamente.** O chrF subiu só 0,6, dentro do ruído. Entre as melhorias reais estão "teste instável", "área jurídica", "fornecedor", "crash" e "tempo limite". Mas o modelo também **introduziu erros de fato**:
   - "next Tuesday" virou "segunda-feira" (035);
   - "a couple of weeks" virou "duas semanas", em vez de "algumas semanas" (018);
   - erro de concordância: "Nós reverteram" (007);
   - sentido trocado: "Could you share" virou "Podemos compartilhar" (021);
   - "Vamos não reinventar a roda" (042).

   **Decisão do usuário (2026-11-11):** o refinamento vira **sugestão**, protegida por uma verificação determinística de fatos (`translation/refine_guard.py`). A versão rápida continua sendo a exibida por padrão (ADR-010, refinamento, item 6).

   A verificação compara a versão rápida com a refinada e rejeita a sugestão quando muda:
   - números (dígitos e por extenso; "um"/"uma" contam como artigo);
   - dias da semana ("sexta" e "sexta-feira" contam como iguais), meses e datas relativas (ontem, próxima, passada…);
   - negações;
   - nomes no meio da frase;
   - termos do glossário;
   - quantidade de perguntas.

   Também rejeita saída vazia ou suja, mudança grande de tamanho e PT-PT novo.

   Nova rodada do qwen3 (`20261111T030412Z_…_refine_p2.json`, mesmas saídas e mesma latência da anterior): **2 das 37 mudanças foram rejeitadas**, exatamente os erros de fato 035 e 018. O chrF com a verificação ficou em 73,43. Nenhuma das melhorias boas foi bloqueada. No phi4-mini, nenhuma mudança foi rejeitada.

   **Limite:** a verificação não checa gramática nem sentido. Os erros 007, 021 e 042 passam por ela, e é por isso que o refinamento é sugestão e não substituição.
2. **Na classificação, o qwen3:4b-instruct foi o melhor** como camada 6 do ADR-013:
   - acerto de 0,75 e precisão de 0,85 em `directed`;
   - de 10 falas para outras pessoas, só 1 foi tratada como dirigida ao usuário;
   - de 13 perguntas dirigidas ao usuário, perdeu 2: uma em que o modelo leu "legal" como pessoa e uma do tipo "which of you".

   O phi4-mini dá justificativas que contradizem o próprio rótulo ("Directly asking Ana" com resposta `not_directed`).
3. **Latência:**
   - refinamento, uma frase: ~0,25 s (p95 ~0,37 s);
   - classificação: 0,6 s (p95 0,9 s).

   Cabe na meta de 4 s, mas soma ~3 GB de VRAM. A conta prevista fica no limite dos 8 GB:

   | Componente | VRAM |
   | --- | --- |
   | Desktop | 2,2 GB |
   | STT | ~2 GB |
   | Tradução na GPU | 0,55 GB |
   | LLM | 2,7 GB |
   | **Total** | **~7,5 GB** |

   O passo 6 vai medir isso de verdade. As saídas são a tradução na CPU e o LLM carregado só sob demanda (`keep_alive`).
4. **Os modelos estão num HD mecânico** (D:, Seagate ST1000DM010). A primeira carga do qwen3 levou **68 s**. Com o cache do sistema operacional, cai para 2 s. O phi4-mini, lido do disco, levou 10 s. Para o produto, vale recomendar SSD para os modelos ou pré-carregar o LLM antes da reunião. Isso vale também para o Whisper.
5. **Escolha provisória: qwen3:4b-instruct** (Apache-2.0). O qwen3.5:4b e o granite4:3b ainda não foram medidos.

### Pendências do passo 4

- Revisar os rótulos de `directed-questions.md` e as referências de tradução na prática.
- Definir na UX (passo de interface) como a sugestão aparece e é aceita. Medir a taxa de falsos positivos da verificação com orações reais do STT.
- Medir o qwen3.5:4b e o granite4:3b, se houver espaço de VRAM.
- Medir a carga do LLM a partir de SSD.
- Traduzir orações confirmadas vindas do STT real (AMI), não só frases escritas.

## TTS (passo 5) — voz da Iris (Kokoro) e Piper

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File benchmarks\fetch-tts.ps1   # 2 vozes CC0 + piper-tts 1.8.0 (~155 MB)
cd workers; uv run --group bench python -m iris_workers.bench.tts --voice pt_BR-cadu-medium
```

**Runtime e licenças do Piper.**

- O `piper-tts` 1.8.0 (OHF-Voice/piper1-gpl, sucessor oficial do Piper) é **GPL-3.0-or-later**. Por isso ele roda num ambiente próprio, `.tools/piper`, instalado a partir de `tts/piper-requirements.txt` com hashes fixados, e sempre em **processo separado**.
- O código MIT nunca importa o Piper. O adaptador `iris_workers/tts/piper_cli.py` conversa com ele por stdin/stdout:
  - o processo fica aberto com a voz carregada;
  - recebe uma fala por linha;
  - devolve PCM 16 bits mono a 22,05 kHz.
- As vozes vêm do `rhasspy/piper-voices`, numa revisão fixada e com SHA-256 conferido (`models/tts.manifest.json`).
- Ficaram só as vozes cuja **cadeia de treino inteira** é CC0, conforme os MODEL_CARD e o `docs/COMMERCIAL_USE.md` do Piper: **cadu** e **jeff**. As duas foram treinadas a partir da voz flamenga rdh, também CC0.
- Vozes excluídas:
  - `faber`: derivada da lessac, cujo dataset (Blizzard 2013) só permite pesquisa;
  - `edresson`: derivada da ryan, CC BY-NC-SA.
- Não há voz pt-BR feminina com licença liberada.

**Método.**

- Os textos são as 50 traduções rápidas (Opus-MT com glossário), justamente o que o botão "Ouvir" leria, com os termos técnicos em inglês.
- Cada voz passou por 2 rodadas de 50 frases avulsas e por 5 entradas de 3 frases.
- A medição é feita com a voz já carregada:
  - tempo até o primeiro áudio, que é quando a reprodução pode começar;
  - tempo até o último áudio;
  - RTF (tempo de síntese ÷ duração do áudio).
- A partida a frio inclui iniciar o processo, carregar o modelo do HD e sintetizar a primeira fala.
- RAM e CPU são do processo do Piper (a árvore de processos, porque o `python.exe` do venv é um lançador).
- Tudo roda na CPU, com o `onnxruntime` padrão. A GPU fica livre para o STT e o LLM.

#### Resultados do Piper — 2026-11-11, Ryzen 7 5800X3D (8 núcleos, 16 threads), CPU

| Voz | 1º áudio, frase: p50 / p95 / máx | Fim, frase: p50 / p95 | 1º áudio, 3 frases: p50 / p95 | Fim, 3 frases: p50 / p95 | RTF | Partida a frio | RAM do processo |
| --- | --- | --- | --- | --- | --- | --- | --- |
| pt_BR-cadu-medium | 91 / 130 / 157 ms | 91 / 130 ms | 79 / 108 ms | 244 / 326 ms | 0,023 | 3,3 s | 364 MiB |
| pt_BR-jeff-medium | 94 / 135 / 251 ms | 95 / 135 ms | 84 / 125 ms | 252 / 360 ms | 0,026 | 5,0 s | 417 MiB |

As entradas de 3 frases geraram a mesma duração de áudio que a soma das frases avulsas (diferença de no máximo 0,1 s). Nenhum áudio foi cortado pela detecção de fim de fala.

**O Piper é rápido (~0,1 s até o primeiro áudio), mas todas as vozes pt-BR dele são masculinas.** O usuário quer uma voz feminina, mais humana e jovem, coerente com a persona da Iris. A voz "Maria" do Windows (SAPI) foi testada e recusada por soar robótica. As vozes do Piper foram apagadas do disco e podem ser baixadas de novo pelo `fetch-tts.ps1`. O adaptador `piper_cli.py` e os relatórios ficam como alternativa medida.

### Voz da Iris: Kokoro-82M com mistura de estilos (escolhida em 2026-11-11)

- **Modelo:** Kokoro-82M v1.0 em ONNX fp32 (`models/kokoro.manifest.json`). Os pesos são Apache-2.0. O release não publica checksum; o SHA-256 foi registrado no primeiro download.
- **Voz:** **75% `if_sara` + 25% `pf_dora`**, na velocidade 1,0, com fonemas do espeak-ng pt-BR. O Kokoro guarda cada voz como um vetor de estilo, e a voz da Iris é a média ponderada de dois deles, sem pesos novos.
  - A `pf_dora`, única voz feminina pt-BR do Kokoro, soou madura demais.
  - A `if_sara` (italiana) deu o timbre jovem.
  - Os 25% de `pf_dora` puxam o sotaque para o brasileiro.
  - A proporção foi escolhida pelo usuário, de ouvido, entre misturas de 15% a 60% de `pf_dora`.
- **Processo separado:** o `kokoro-onnx` depende do `phonemizer` (GPL) e do espeak-ng (GPL). Por isso ele roda em `.tools/kokoro`, pelo host `hosts/kokoro/kokoro_host.py`, que tem licença GPL-3.0-or-later declarada no arquivo.
  - O host fala o **protocolo de stdio v1** (`hosts/README.md`): JSON por linha na entrada e frames de áudio por frase na saída, com fim de fala explícito.
  - O cliente MIT é o `iris_workers/tts/framed_host.py`, com testes de contrato feitos com um host falso.
- **Início por oração:** quando a primeira frase tem mais de 60 caracteres, o host gera primeiro a parte até a primeira vírgula ou ponto-e-vírgula. "1,5" não é cortado. O tempo até o primeiro som cresce com o tamanho do primeiro trecho, e isso o reduz. O resto do texto continua frase a frase.

#### Resultados da voz da Iris — 2026-11-11, Ryzen 7 5800X3D, CPU, 100 frases + 10 entradas de 3 frases

| Threads | Início | 1º áudio, frase: p50 / p95 / máx | Frases > 1,5 s | Fim, frase: p50 / p95 | 1º áudio, 3 frases: p50 / p95 | RTF | Núcleos usados |
| --- | --- | --- | --- | --- | --- | --- | --- |
| padrão (8) | frase inteira | 910 / 1187 / 1496 ms | 0 | 929 / 1187 ms | 875 / 983 ms | 0,24 | 7,3 |
| **padrão (8)** | **1ª oração** | **789 / 1030 / 1193 ms** | **0** | 962 / 1365 ms | 858 / 938 ms | 0,25 | 7,4 |
| 4 | frase inteira | 1282 / 1679 / 2194 ms | 12 | 1311 / 1679 ms | 1240 / 1373 ms | 0,34 | 3,8 |
| 4 | 1ª oração | 1112 / 1469 / 1691 ms | 3 | 1355 / 1903 ms | 1196 / 1328 ms | 0,35 | 3,8 |

- A partida a frio, com o modelo no cache do sistema operacional, leva ~2,2 s: abrir o processo, carregar o modelo e aquecer. O processo ocupa ~650 MiB de RAM.
- A carga de fundo da CPU durante as rodadas ficou entre 3% e 28%, por causa dos apps do desktop.

### Conclusões

1. **A voz da Iris atende a meta de começar a falar em até 1,5 s (RNF-015) na CPU com 8 threads**: p95 de 1,03 s e máximo de 1,19 s, começando pela primeira oração. Com 4 threads, que é o cenário de CPU disputada, o p95 fica em 1,47 s, no limite, e 3 de 100 frases passam de 1,5 s.
2. **O Kokoro é ~10 vezes mais lento que o Piper** (RTF 0,25 contra 0,02), mas ainda gera o áudio 4 vezes mais rápido que o tempo de fala. A reprodução nunca fica esperando a síntese depois que começa.
3. **O processo do Kokoro precisa ser aberto antes do primeiro "Ouvir"** (~2,2 s), no início da reunião ou em segundo plano.
4. **Contenção:** o Kokoro ocupa ~7 núcleos enquanto sintetiza, e a síntese só acontece quando o usuário pede. O passo 6 decide se o TTS roda com menos threads, na GPU (exige `onnxruntime-gpu` e VRAM) ou se pausa a tradução na CPU durante a leitura.

### Pendências do passo 5

- Avaliar a pronúncia de termos em inglês (build, deploy, dead letter queue) e a emenda entre orações (`tmp/tts-samples/iris-oracao_*` comparadas com `iris-frase_*`).
- Medir o Kokoro na GPU, se o passo 6 indicar que vale a pena.
- Teste de realimentação: a voz da Iris não pode voltar ao STT. Isso fica para a captura (POC-001B, ADR-007).
- Medir a carga a partir de SSD e a contenção de CPU com o pipeline completo (passo 6).
- V1: voz própria da Iris gerada a partir de uma gravação de referência com direitos de uso, por exemplo com o Chatterbox Multilingual (MIT). Exige VRAM.

## Medição combinada (passo 6) — tudo rodando junto

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\start-ollama.ps1   # em outro terminal
cd workers; uv run --group bench python -m iris_workers.bench.combined     # padrão: tradução CPU 4 threads, LLM, voz da Iris
```

**Método.**

- O áudio entra **em tempo real**: cada bloco só é processado depois que o áudio dele "chegou". Assim, todos os componentes disputam GPU e CPU como numa reunião de verdade (o benchmark do passo 2 usava um relógio simulado).
- **STT:** duas reuniões AMI ao mesmo tempo (IS1009a como áudio do sistema e ES2004a como microfone, 300 s cada), num só large-v3-turbo int8_float16, com a cadência híbrida padrão do ADR-008.
- **Tradução:** as palavras confirmadas do **sistema** viram frases (`speech/sentences.py`), que são traduzidas pelo Opus-MT com glossário. A tradução do microfone fica desligada por padrão (ADR-011).
- **LLM:** cada tradução é refinada e passa pela verificação de fatos; as perguntas também são classificadas. Usa qwen3:4b-instruct no Ollama, com fila limitada a 4; se a fila enche, o refinamento é pulado e contado.
- **Voz:** a cada 30 s, a última tradução é lida pela voz da Iris, simulando alguém clicando em "Ouvir".
- As latências são medidas a partir do fim da última palavra falada. Os modelos são carregados e aquecidos antes do relógio começar, como o app faria no início da reunião.
- A VRAM é a do dispositivo inteiro, e a linha de base (desktop) é medida com o LLM descarregado.
- O relatório tem só métricas (`results/combined/`).

### Resultados — 2026-11-11, RTX 4060 Ti 8 GB, Ryzen 7 5800X3D, GPU e CPU livres

| Configuração | Exibição sistema p50 / p95 | Exibição mic p50 / p95 | Confirmação sistema p50 / p95 | Tradução desde a fala p50 / p95 / máx | Refino desde a fala p50 / p95 | Voz: 1º áudio p50 / máx | VRAM pico | CPU da máquina p95 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Base: STT + tradução CPU | 0,58 / 1,43 s | 0,98 / 2,33 s | 1,90 / 3,13 s | 1,85 / 4,17 / 4,92 s | — | — | 3,9 GB | 42% |
| + voz | 0,63 / 1,51 s | 1,12 / 2,58 s | 1,94 / 3,16 s | 1,92 / 4,35 / 4,94 s | — | 0,67 / 1,32 s | 3,8 GB | 71% |
| **+ voz + LLM (padrão)**, rodada 1 | 0,68 / 1,60 s | 1,15 / 2,71 s | 1,98 / 3,13 s | 1,96 / 4,28 / 4,98 s | 2,26 / 4,63 s | 0,64 / 1,39 s | 6,8 GB | 85% |
| **+ voz + LLM (padrão)**, rodada 2 | 0,65 / 1,58 s | 1,10 / 2,77 s | 1,94 / 3,17 s | 1,94 / 4,20 / 4,93 s | 2,19 / 4,30 s | 0,66 / 1,25 s | 6,7 GB | 41% |
| Tradução na GPU | 0,71 / 1,65 s | 1,20 / 2,70 s | 1,98 / 3,44 s | 1,92 / 4,14 / 4,93 s | 2,17 / 4,36 s | 0,59 / 1,42 s | 7,1 GB | 88% |
| Voz com 4 threads | 0,71 / 1,73 s | 1,20 / 2,23 s | 2,01 / 3,25 s | 2,01 / 4,22 / 4,94 s | 2,30 / 4,34 s | 0,83 / 1,77 s | 6,8 GB | 74% |

Em todas as rodadas:
- 78 de 78 frases foram traduzidas, nenhuma por corte forçado;
- zero cortes forçados no STT e zero erros;
- nenhum refinamento foi pulado, e 7 de 78 foram rejeitados pela verificação de fatos;
- 5 perguntas foram classificadas;
- a desktop sozinha usa 2,2 a 2,4 GB de VRAM.

A tradução em si leva 0,1 s no p50 e 0,2 a 0,3 s no p95, e nunca esperou na fila (espera máxima de 0,12 s). A diferença entre as duas rodadas iguais dá o ruído: ~0,1 s nas latências. O pico de CPU da máquina oscila com a desktop.

### Conclusões

1. **Tudo cabe nos 8 GB:** o pico de VRAM é de **6,7 a 6,8 GB** com STT, tradução na CPU, LLM e voz, deixando ~1,2 GB de folga. A estimativa anterior (~7,5 GB) era pessimista. Tradução na GPU levaria o pico a 7,1 GB sem ganho.
2. **Configuração padrão escolhida:**
   - tradução na **CPU** com 4 threads: na GPU não houve ganho (ADR-010, item 4);
   - voz da Iris na **CPU com as threads padrão**: limitar a 4 threads leva o primeiro áudio a 1,77 s, acima da meta, sem melhorar o STT (ADR-011, item 5);
   - LLM na GPU.
3. **Voz:** o primeiro áudio fica abaixo de 1,5 s em todas as rodadas com as threads padrão (máximo de 1,25 a 1,42 s), mesmo com o pipeline inteiro rodando.
4. **Duas metas do RNF-015 não são cumpridas, já na linha de base, e por causa do STT:**
   - **Tradução ≤ 4 s no p95:** medimos 4,2 a 4,35 s. Quase todo esse tempo é a confirmação do STT (p95 de ~3,1 s), somada à espera pela última palavra da frase. A tradução custa ~0,1 s.
   - **Exibição ≤ 1,5 s no p95:** o sistema fica em 1,43 s na base e 1,58 a 1,60 s com tudo ligado. O microfone fica em 2,3 a 2,8 s, porque ele é processado depois do sistema em cada ciclo e tem mais passadas de exibição puladas (140 a 198 de ~600).
   - Voz e LLM somam só ~0,1 a 0,2 s nessas latências. O caminho para cumprir as metas está na **política do STT**, que é assunto do POC-001D (pendências abaixo).

### Pendências do passo 6

Todas são da política de STT e devem ser tratadas no POC-001D:
- Confirmar mais cedo o fim de frase: quando o VAD detectar silêncio depois de pontuação final, confirmar a cauda provisória sem esperar a próxima concordância.
- Dar prioridade ao sistema em relação ao microfone (ADR-009, RNF-017): por exemplo, exibição do microfone a cada 1 s.
- Repetir a medição em reuniões mais longas e no teste de 60 minutos (POC-001F).
