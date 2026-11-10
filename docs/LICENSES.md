# Licenças e distribuição de modelos

## Licença do projeto

O código do Iris Voice Assistant é MIT (`LICENSE`, decidido em 2026-11-09). A licença cobre apenas o código deste repositório; componentes de terceiros mantêm suas licenças. Componentes copyleft (ex.: sucessor do Piper, GPL-3.0) devem ser usados como processo separado e não incorporados ao código MIT.

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

Revisado em 2026-11-09 a partir dos metadados publicados (PyPI, Hugging Face, página do corpus). Revisão técnica, não jurídica. Os SHA-256 dos modelos ficam registrados em cada relatório em `benchmarks/results/`.

### Dependências Python

| Pacote | Versão (uv.lock) | Grupo | Função | Licença | Observação |
| --- | --- | --- | --- | --- | --- |
| numpy | 2.x | runtime | Buffers de áudio | BSD-3-Clause | Mantido ativamente |
| faster-whisper | 1.2.1 | bench | STT | MIT | Mantido pela SYSTRAN |
| ctranslate2 | 4.8.5 | bench (dep.) | Inferência Whisper/Opus-MT | MIT | Wheel Windows inclui libiomp5 (Intel, redistribuível) |
| nvidia-cublas-cu12 / nvidia-cudnn-cu12 / nvidia-cuda-nvrtc-cu12 | 12.9 / 9.28 / 12.9 | bench | DLLs CUDA para CTranslate2 sem CUDA Toolkit global | NVIDIA EULA (redistribuição permitida sob os termos) | ~1,3 GB; revisar termos antes de empacotar |
| av, onnxruntime, tokenizers, huggingface-hub | ver uv.lock | bench (dep. de faster-whisper) | Decodificação, VAD Silero, tokenização, download | BSD/LGPL (av usa FFmpeg LGPL), MIT, Apache-2.0, Apache-2.0 | `av` não é usado: o áudio entra como array |
| psutil | 7.x | bench | Amostragem de CPU/RAM | BSD-3-Clause | |
| nvidia-ml-py | 13.x | bench | Amostragem de VRAM (NVML) | BSD-3-Clause | |
| sentencepiece | 0.2.3 | bench | Tokenização Opus-MT/Argos | Apache-2.0 | Mantido pelo Google; wheel de ~1,6 MB. O pacote `argostranslate` **não** é usado, porque puxaria stanza (PyTorch) e spaCy |

`huggingface-hub` é usado somente para downloads explícitos (`--allow-download`), com `HF_HUB_DISABLE_TELEMETRY=1`.

### Modelos e dados

| Artefato | Origem | Revisão | Tamanho | Licença |
| --- | --- | --- | --- | --- |
| faster-whisper small.en | huggingface.co/Systran/faster-whisper-small.en | d1d751a5f8 | 464 MiB | MIT (pesos Whisper, OpenAI) |
| faster-whisper medium.en | huggingface.co/Systran/faster-whisper-medium.en | a29b04bd15 | 1460 MiB | MIT |
| faster-distil-whisper large-v3 | huggingface.co/Systran/faster-distil-whisper-large-v3 | c3058b4752 | 1446 MiB | MIT (distil-whisper, Hugging Face) |
| faster-whisper large-v3-turbo | huggingface.co/mobiuslabsgmbh/faster-whisper-large-v3-turbo (redireciona para dropbox-dash/…) | 0a363e9161 | 1547 MiB | MIT |
| AMI Meeting Corpus: IS1009a, ES2004a (Mix-Headset) + anotações manuais 1.6.2 | groups.inf.ed.ac.uk/ami | ver `benchmarks/corpus/ami.manifest.json` | ~83 MB | CC BY 4.0 — atribuição obrigatória |

| Opus-MT opus-mt-tc-big-en-pt (release Marian 2022-03-13), convertido para CTranslate2 float16 | object.pouta.csc.fi/Tatoeba-MT-models/eng-por (Helsinki-NLP) | ver `benchmarks/models/translation.manifest.json` | 823 MiB zip → 448 MiB convertido | CC BY 4.0 — atribuição obrigatória |
| Argos Translate translate-en_pt 1.9 (somente baseline) | argos-net.com (índice argospm-index) | idem | 63 MiB | CC BY 4.0 (derivado de OPUS-MT, conforme README do pacote) |

Atribuição AMI: *Carletta, J. et al. (2005). The AMI Meeting Corpus: A Pre-Announcement. MLMI 2005.* Dados usados somente como material de teste; não redistribuídos neste repositório.

Atribuição Opus-MT: *Tiedemann, J. and Thottingal, S. (2020). OPUS-MT — Building open translation services for the World. EAMT 2020*, e *Tiedemann, J. (2020). The Tatoeba Translation Challenge. WMT 2020*. Helsinki-NLP, Universidade de Helsinki. Se o modelo for distribuído com o produto, a atribuição precisa aparecer na tela "Sobre" ou nos avisos de terceiros.

## Distribuição

- Preferir baixar modelos durante instalação/configuração, não embutir todos no instalador.
- Exibir licença e tamanho antes do download.
- Verificar checksum.
- Preservar avisos e atribuições exigidos.
- Reavaliar licenças em cada atualização.

Este documento é um checklist técnico e não substitui revisão jurídica.
