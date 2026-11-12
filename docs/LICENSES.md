# Licenças e distribuição de modelos

## Licença do projeto

O código do Iris Voice Assistant é MIT (`LICENSE`, decidido em 2026-11-09). **Exceção:** os hosts em `hosts/` são GPL-3.0-or-later, porque importam componentes GPL. Cada arquivo declara isso no cabeçalho SPDX, e os hosts rodam como processos separados (`hosts/README.md`). A licença MIT cobre o restante do código deste repositório; componentes de terceiros mantêm suas licenças. Componentes copyleft (ex.: sucessor do Piper, GPL-3.0) devem ser usados como processo separado e não incorporados ao código MIT.

## Regra

Nenhum modelo, voz, pacote ou runtime será incluído em distribuição comercial antes de revisão da licença específica da versão e do artefato utilizado.

## Inventário obrigatório

Manter uma tabela atualizada com:

- Componente e versão.
- URL oficial e origem do artefato.
- Licença do código.
- Licença dos pesos/modelos.
- Licença da voz/dataset quando aplicável.
- Obrigações de atribuição.
- Restrições comerciais ou de redistribuição.
- SHA-256 do artefato.
- Data e responsável pela revisão.

## Candidatos e pontos de atenção

Licenças abaixo são a informação conhecida no planejamento e **devem ser confirmadas** no artefato e versão efetivamente usados.

| Componente | Ponto de atenção |
| --- | --- |
| faster-whisper / CTranslate2 | Código MIT; verificar licença dos pesos de cada modelo (Whisper, distil-whisper) |
| NVIDIA Parakeet / sherpa-onnx | Pesos e runtime com licenças próprias (CC-BY / Apache esperadas); confirmar |
| Silero VAD | Verificar licença da versão usada |
| Opus-MT (Helsinki-NLP) | Pesos CC-BY 4.0 esperados: exige atribuição |
| Argos Translate e pacote en→pt | Código e pacote com licenças distintas |
| NLLB-200 | **Excluído**: licença não comercial (ADR-010) |
| Ollama e modelo LLM escolhido | Runtime e pesos com licenças distintas; alguns modelos têm restrições de uso |
| Piper e voz pt-BR | O projeto original foi arquivado e o sucessor é distribuído sob GPL-3.0; cada voz tem licença própria. Avaliar impacto comercial (execução como processo separado) com revisão jurídica |
| NVIDIA CUDA/cuDNN (via pip) | Termos de redistribuição da NVIDIA |
| Avalonia UI e bibliotecas .NET | Verificar cada pacote |
| NAudio ou alternativa para captura Windows | Verificar licença |
| AMI Meeting Corpus (testes) | CC-BY 4.0: atribuição; uso apenas como dado de teste |

A licença do runtime não determina automaticamente a licença dos pesos ou dos pacotes de idioma. Verificar cada artefato separadamente.

## Inventário em uso (POC-001A)

Revisado em 2026-11-11 (passo 7), a partir dos metadados publicados: PyPI, Hugging Face, GitHub, registro do Ollama e página do corpus. É uma revisão técnica, não jurídica.

**Checksums e revisões ficam nos manifestos** (`benchmarks/models/*.manifest.json` e `benchmarks/corpus/ami.manifest.json`), e os scripts e benchmarks recusam artefatos que não batem:

| Família | Manifesto | Como é verificado |
| --- | --- | --- |
| STT (faster-whisper) | `whisper.manifest.json` | Download só da revisão fixada; SHA-256 do `model.bin` conferido antes de carregar (`bench/stt.py`). Modelo fora do manifesto não é baixado |
| Tradução (Opus-MT, Argos) | `translation.manifest.json` | SHA-256 do arquivo baixado e do `model.bin` convertido (`fetch-translation-models.ps1`) |
| LLM (Ollama) | `llm.manifest.json` | Digest de cada modelo conferido depois do pull (`scripts/verify-ollama-models.ps1`) e antes de cada benchmark |
| Runtime Ollama | `scripts/fetch-ollama.ps1` | SHA-256 do release e assinatura Authenticode da Ollama Inc. |
| Voz da Iris (Kokoro) | `kokoro.manifest.json` | SHA-256 do modelo e das vozes. O valor foi registrado no primeiro download, porque o release não publica checksum |
| Piper e vozes (comparação) | `tts.manifest.json` | Revisão do Hugging Face fixada e SHA-256 de cada arquivo |
| Ambientes Python separados (Piper, Kokoro) | `benchmarks/tts/*-requirements.txt` | `uv pip sync --require-hashes` |
| Workers | `workers/uv.lock` | `uv sync --locked` (também no CI) |

O teste `workers/tests/test_manifests.py` confere o formato de todos os checksums, revisões e URLs (HTTPS).

### Dependências Python (workers, `uv.lock`)

| Pacote | Versão | Grupo | Função | Licença | Observação |
| --- | --- | --- | --- | --- | --- |
| numpy | 2.5.4 | runtime | Buffers de áudio | BSD-3-Clause | Mantido ativamente |
| faster-whisper | 1.2.1 | bench | STT | MIT | Mantido pela SYSTRAN |
| ctranslate2 | 4.8.5 | bench (dep.) | Inferência Whisper/Opus-MT | MIT | Wheel Windows inclui libiomp5 (Intel, redistribuível) |
| nvidia-cublas-cu12 / nvidia-cudnn-cu12 / nvidia-cuda-nvrtc-cu12 | 12.9.2.10 / 9.28.1.4 / 12.9.86 | bench | DLLs CUDA para CTranslate2 sem CUDA Toolkit global | NVIDIA EULA (redistribuição permitida sob os termos) | ~1,3 GB; revisar termos antes de empacotar |
| av / onnxruntime / tokenizers / huggingface-hub | 19.0.2 / 1.31.1 / 0.23.3 / 2.4.0 | bench (dep. de faster-whisper) | Decodificação, VAD Silero, tokenização, download | BSD/LGPL (av usa FFmpeg LGPL), MIT, Apache-2.0, Apache-2.0 | `av` não é usado: o áudio entra como array |
| psutil | 7.2.2 | bench | Amostragem de CPU/RAM | BSD-3-Clause | |
| nvidia-ml-py | 13.615.71 | bench | Amostragem de VRAM (NVML) | BSD-3-Clause | |
| sentencepiece | 0.2.3 | bench | Tokenização Opus-MT/Argos | Apache-2.0 | Mantido pelo Google; wheel de ~1,6 MB. O pacote `argostranslate` **não** é usado, porque puxaria stanza (PyTorch) e spaCy |

### Pacotes .NET (`Directory.Packages.props`)

| Pacote | Versão | Projeto | Função | Licença | Observação |
| --- | --- | --- | --- | --- | --- |
| Microsoft.Data.Sqlite (+ .Core) | 10.0.15 | Infrastructure | Banco local SQLite (DATABASE.md) | MIT | Mantido pela Microsoft (EF Core). Aprovado pelo usuário em 2026-11-12 |
| SQLitePCLRaw (bundle_e_sqlite3, core, provider, lib.e_sqlite3) | 2.1.13 | Infrastructure (transitivo) | Motor SQLite nativo empacotado | Apache-2.0; o SQLite em si é de domínio público | Inclui a DLL `e_sqlite3` |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, coverlet.collector | ver props | testes | Testes | Apache-2.0 / MIT | Só nos testes, não distribuídos |

`huggingface-hub` é usado somente para downloads explícitos (`--allow-download`), com `HF_HUB_DISABLE_TELEMETRY=1`.

### Modelos, runtimes e dados

| Artefato | Uso | Origem | Revisão / verificação | Tamanho | Licença |
| --- | --- | --- | --- | --- | --- |
| faster-whisper **large-v3-turbo** | **escolhido** (STT) | huggingface.co/mobiuslabsgmbh/faster-whisper-large-v3-turbo (redireciona para dropbox-dash/…) | 0a363e9161, `whisper.manifest.json` | 1547 MiB | MIT (pesos Whisper, OpenAI) |
| faster-distil-whisper large-v3 | comparação | huggingface.co/Systran/faster-distil-whisper-large-v3 | c3058b4752 | 1446 MiB | MIT (distil-whisper, Hugging Face) |
| faster-whisper medium.en | comparação | huggingface.co/Systran/faster-whisper-medium.en | a29b04bd15 | 1460 MiB | MIT |
| faster-whisper small.en | comparação | huggingface.co/Systran/faster-whisper-small.en | d1d751a5f8 | 464 MiB | MIT |
| **Opus-MT opus-mt-tc-big-en-pt** (release Marian 2022-03-13), convertido para CTranslate2 float16 | **escolhido** (tradução rápida) | object.pouta.csc.fi/Tatoeba-MT-models/eng-por (Helsinki-NLP) | `translation.manifest.json` | 823 MiB zip → 448 MiB convertido | CC BY 4.0, atribuição obrigatória |
| Argos Translate translate-en_pt 1.9 | baseline | argos-net.com (índice argospm-index) | idem | 63 MiB | CC BY 4.0 (derivado de OPUS-MT, conforme README do pacote) |
| Ollama v0.40.16, versão portátil para Windows x64 | **runtime do LLM** | github.com/ollama/ollama/releases | SHA-256 do release e Authenticode (`scripts/fetch-ollama.ps1`) | 1,5 GB zip (inclui as bibliotecas CUDA) | MIT. Runtime separado, não embutido no app. Tem recursos de nuvem, desligados com `OLLAMA_NO_CLOUD=1` |
| **qwen3:4b-instruct** (Q4_K_M) | **escolhido** (refinamento e classificação) | registry.ollama.ai/library/qwen3 (Alibaba Qwen) | digest 0edcdef345, `llm.manifest.json` | 2,5 GB | Apache-2.0 |
| phi4-mini:3.8b (Q4_K_M) | comparação | registry.ollama.ai/library/phi4-mini (Microsoft) | digest 78fad5d182 | 2,5 GB | MIT |
| **Kokoro-82M v1.0** (ONNX fp32) + voices-v1.0.bin | **voz da Iris** (75% if_sara + 25% pf_dora) | github.com/thewh1teagle/kokoro-onnx, release model-files-v1.0 (export dos pesos de huggingface.co/hexgrad/Kokoro-82M) | `kokoro.manifest.json` (SHA-256 do primeiro download) | 310 MiB + 27 MiB | Pesos Apache-2.0. Segundo o model card, o treino usou áudio permissivo ou de domínio público e áudio sintético de TTS comerciais fechados: **revisar juridicamente antes de distribuir** |
| kokoro-onnx 0.6.1 | runtime da voz | pypi.org/project/kokoro-onnx | `benchmarks/tts/kokoro-requirements.txt` (hashes) | <1 MiB | MIT |
| phonemizer 3.6.0 / espeakng-loader 0.2.4 (carrega o espeak-ng) | runtime da voz | PyPI | idem | ~10 MiB | **GPL-3.0-or-later** / MIT (a biblioteca espeak-ng carregada é GPL-3.0). Por isso o Kokoro roda pelo host `hosts/kokoro/kokoro_host.py` (GPL-3.0-or-later), em ambiente e processo separados |
| piper-tts 1.8.0 (+ espeak-ng embutido no wheel) | comparação (vozes só masculinas) | pypi.org/project/piper-tts, github.com/OHF-Voice/piper1-gpl | `benchmarks/tts/piper-requirements.txt` (hashes) | 33 MiB | **GPL-3.0-or-later**. Ambiente próprio em `.tools/piper`, executado como processo separado por stdin/stdout. O código MIT nunca importa o pacote |
| Vozes pt_BR-cadu-medium e pt_BR-jeff-medium | comparação (apagadas do disco, recuperáveis por `fetch-tts.ps1`) | huggingface.co/rhasspy/piper-voices, revisão cf910fb59f | `tts.manifest.json` | 60 MiB cada | CC0 na cadeia toda (dataset próprio CC0, base nl_BE-rdh CC0). Manter o MODEL_CARD junto com a voz |
| AMI Meeting Corpus: IS1009a, ES2004a (Mix-Headset) + anotações manuais 1.6.2 | dados de teste | groups.inf.ed.ac.uk/ami | `benchmarks/corpus/ami.manifest.json` | ~83 MB | CC BY 4.0, atribuição obrigatória |

Vozes pt-BR excluídas por licença: `pt_BR-faber-medium`, derivada da en_US-lessac (licença Blizzard 2013, só pesquisa e sem uso comercial), e `pt_BR-edresson-low`, derivada da en_US-ryan (CC BY-NC-SA 4.0). O Piper não concede licença sobre as vozes, e a questão de os pesos serem obra derivada dos dados está em aberto (`docs/LICENSING.md` do Piper). Por isso escolhemos vozes cuja cadeia inteira é CC0.

Fora da lista por licença: `gemma3` (Gemma Terms of Use, com restrições de uso) e `llama3.2` (Llama 3.2 Community License, com restrições e atribuição obrigatória).

Atribuição AMI: *Carletta, J. et al. (2005). The AMI Meeting Corpus: A Pre-Announcement. MLMI 2005.* Dados usados somente como material de teste; não redistribuídos neste repositório.

Atribuição Opus-MT: *Tiedemann, J. and Thottingal, S. (2020). OPUS-MT — Building open translation services for the World. EAMT 2020*, e *Tiedemann, J. (2020). The Tatoeba Translation Challenge. WMT 2020*. Helsinki-NLP, Universidade de Helsinki. Se o modelo for distribuído com o produto, a atribuição precisa aparecer na tela "Sobre" ou nos avisos de terceiros.

## Distribuição

- Preferir baixar modelos durante instalação/configuração, não embutir todos no instalador.
- Exibir licença e tamanho antes do download.
- Verificar checksum.
- Preservar avisos e atribuições exigidos.
- Reavaliar licenças em cada atualização.

Este documento é um checklist técnico e não substitui revisão jurídica.
