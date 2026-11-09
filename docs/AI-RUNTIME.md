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
| TTS | Piper (ou sucessor) com voz pt-BR licenciável | — | ADR-011 |

Essas são escolhas candidatas, não certificação de qualidade ou licença. Registrar versão, checksum, licença, origem e requisitos de cada artefato antes de distribuir.

## Regras de processamento

- STT com idioma fixo em inglês no P0 e streaming por concordância (ADR-008): parciais revisáveis, finais imutáveis.
- Tradução rápida por oração/frase confirmada; nunca traduzir parcial.
- Refinamento por LLM opcional e cancelável; preserva a versão rápida.
- Normalizador PT-PT→PT-BR e glossário aplicados às traduções.
- TTS somente sob demanda (ADR-011).
- LLM recebe contexto resumido e apenas quando necessário.
- A interface permanece responsiva.
- Cada operação aceita cancelamento e timeout.

## GPU e CUDA

faster-whisper/CTranslate2 usam CUDA 12 e cuDNN 9. As bibliotecas necessárias são instaladas por pip no ambiente isolado (`nvidia-cublas-cu12`, `nvidia-cudnn-cu12`), sem instalação global do CUDA Toolkit. Exige driver NVIDIA compatível. Fixar versões após o benchmark.

## Orçamento de recursos (hipótese a validar)

| Componente | VRAM estimada |
| --- | --- |
| STT (uma instância compartilhada pelos dois fluxos) | ~1,5 GB |
| Tradução rápida | ~0,5 GB (ou CPU) |
| LLM 3–4B Q4 | ~3 GB |

Total esperado abaixo de 8 GB; medir isolado e combinado na RTX 4060 Ti 8 GB de desenvolvimento.

## Privacidade

Nenhum worker pode fazer upload de conteúdo. Verificar e documentar o tráfego de rede durante os testes. Downloads de instalação/modelos devem ser explícitos e separados do processamento de reunião. Ollama é um serviço local instalado separadamente: o app deve verificar que ele escuta apenas em loopback (`OLLAMA_HOST` não exposto) e alertar caso contrário.

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
