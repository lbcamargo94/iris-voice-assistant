# AI Runtime

## Objetivo

Executar STT, tradução, LLM e TTS localmente por meio de adaptadores substituíveis, com o texto o mais próximo possível do tempo real.

## Componentes candidatos

| Função | Candidato principal | Concorrente/baseline | Decisão |
| --- | --- | --- | --- |
| VAD | Silero VAD | — | ADR-008 |
| STT | faster-whisper (modelos somente-inglês, ex. distil-large-v3; e large-v3-turbo) + LocalAgreement | NVIDIA Parakeet via sherpa-onnx | ADR-008 |
| Tradução rápida | Opus-MT `opus-mt-tc-big-en-pt` (CTranslate2, destino PT-BR) | Argos Translate | ADR-010 |
| Refinamento / Iris | LLM local via Ollama (modelo 3–4B quantizado, escolhido por benchmark) | — | ADR-010, ADR-013 |
| TTS | Kokoro-82M (voz da Iris: 75% if_sara + 25% pf_dora), host GPL em processo separado | Piper (vozes pt-BR CC0, só masculinas) | ADR-011 |

Essas são escolhas candidatas, não certificação de qualidade ou licença. Registrar versão, checksum, licença, origem e requisitos de cada artefato antes de distribuir.

## Regras de processamento

- STT com idioma fixo em inglês no P0 e streaming por concordância (ADR-008): parciais revisáveis, finais imutáveis.
- Tradução rápida por oração/frase confirmada; nunca traduzir parcial.
- Refinamento por LLM opcional e cancelável. A versão rápida continua sendo a exibida, e a refinada só é oferecida como sugestão quando passa pela verificação de fatos (`translation/refine_guard.py`, ADR-010 item 6).
- Normalizador PT-PT→PT-BR e glossário aplicados às traduções.
- TTS somente sob demanda (ADR-011).
- LLM recebe contexto resumido e apenas quando necessário.
- A interface permanece responsiva.
- Cada operação aceita cancelamento e timeout.

## GPU e CUDA

faster-whisper/CTranslate2 usam CUDA 12 e cuDNN 9. As bibliotecas necessárias são instaladas por pip no ambiente isolado (`nvidia-cublas-cu12`, `nvidia-cudnn-cu12`), sem instalação global do CUDA Toolkit. Exige driver NVIDIA compatível. Fixar versões após o benchmark.

## Orçamento de recursos (medido no passo 6, 2026-11-11)

Medido na RTX 4060 Ti 8 GB com tudo rodando junto (`benchmarks/README.md`, "Medição combinada"):

| Componente | Onde | VRAM |
| --- | --- | --- |
| Desktop (Windows + apps abertos) | — | 2,2–2,4 GB |
| STT large-v3-turbo int8_float16 (uma instância para os dois fluxos) | GPU | ~1,1 GB carregado, ~1,5 GB no pico |
| Tradução rápida Opus-MT int8 (4 threads) | CPU | 0 (na GPU seriam ~0,3 GB) |
| LLM qwen3:4b-instruct Q4_K_M | GPU | 2,7 GB |
| Voz da Iris (Kokoro, threads padrão) | CPU | 0 (~650 MiB de RAM) |
| **Pico total do dispositivo** | | **6,7–6,8 GB** |

A folga é de ~1,2 GB. Com STT e tradução apenas, o pico é de 3,9 GB.

## Privacidade

Nenhum worker pode fazer upload de conteúdo. Verificar e documentar o tráfego de rede durante os testes. Downloads de instalação/modelos devem ser explícitos e separados do processamento de reunião. Ollama é um serviço local instalado separadamente: o app deve verificar que ele escuta apenas em loopback (`OLLAMA_HOST` não exposto) e alertar caso contrário.

O Ollama tem recursos de nuvem (modelos na nuvem e busca web). Eles devem ficar desligados com `OLLAMA_NO_CLOUD=1` ou `disable_ollama_cloud` em `~/.ollama/server.json`, e o app deve checar no log a linha `Ollama cloud disabled: true`. No POC usamos a versão portátil (`scripts/fetch-ollama.ps1`, `scripts/start-ollama.ps1`), sem a atualização automática do instalador. Os clientes HTTP do Ollama recusam URLs fora de loopback e ignoram proxies do sistema (`iris_workers/llm/ollama.py`).

## Orquestração

Prioridade (ADR-009): captura → STT do sistema → STT do microfone → tradução rápida → persistência de derivados → refinamento por LLM → Iris. Não carregar modelos simultaneamente sem medir VRAM/RAM.

## Métricas

- Latência da primeira hipótese parcial e do segmento final.
- Latência de tradução por oração confirmada.
- Tempo de processamento por segundo de áudio (RTF).
- CPU, RAM e VRAM, isolados e combinados.
- Taxa de segmentos incompletos/erro.
- WER de transcrição e qualidade de tradução em conjunto de referência; contagem de marcadores PT-PT.
- Tempo até início da leitura sob demanda.

## Limitações conhecidas

Whisper é primariamente ASR; sua tarefa de tradução é orientada para saída em inglês, por isso a tradução EN→PT-BR usa tradutor independente. Diarização e identificação nominal não são garantidas pelo STT básico. Com idioma fixo, fala em outro idioma será transcrita incorretamente no P0.
