# ADR-008 — STT em streaming por concordância e ciclo de vida dos segmentos

- **Estado:** Aceita. A cadência foi refinada no POC-001A ("Refinamento 2026-11-10"), e a política v3 e o worker de STT entraram no POC-001D ("Refinamento 2026-11-12").
- **Data:** 2026-11-09

## Contexto

O uso pretendido exige texto o mais próximo possível do tempo real. faster-whisper não é um modelo de streaming nativo; esperar pausas (VAD puro) gera 3–10 s de atraso. Blocos com sobreposição ingênua duplicam palavras nas emendas.

## Decisão

1. **Idioma fixo em inglês** no P0 para ambos os fluxos, permitindo modelos somente-inglês mais rápidos (ex.: distil-large-v3, Parakeet). Detecção automática de idioma fica como opção futura.
2. **Gating por VAD** (Silero VAD candidato): não executar STT em silêncio.
3. **Política de streaming por concordância (LocalAgreement-2):** a cada ~1 s de áudio novo, retranscrever o buffer recente com timestamps por palavra; confirmar o maior prefixo comum entre as duas últimas hipóteses. Palavras confirmadas nunca são reemitidas; o buffer é aparado após fins de frase confirmados (limite ~15–30 s).
4. **Ciclo de vida do segmento:** `Open` (início de fala detectado; ID estável criado) → `Partial` (revisões numeradas, texto mutável) → `Final` (pausa detectada ou corte forçado em ~15 s). Um segmento `Final` é imutável; correções do usuário geram estado `Edited`, preservando o original.
5. **Concorrente no benchmark:** NVIDIA Parakeet via sherpa-onnx (que possui binding .NET) será medido contra faster-whisper + LocalAgreement. A política de streaming fica atrás de `ISpeechToTextProvider`, independente do modelo.
6. **Compartilhamento de GPU:** os dois fluxos usam a mesma instância de modelo; o fluxo do sistema tem prioridade sobre o microfone.

## Refinamento 2026-11-10 (benchmark POC-001A, `benchmarks/README.md`)

Medido com large-v3-turbo int8_float16 em RTX 4060 Ti, AMI IS1009a + ES2004a (300 s cada):

1. **Cadência híbrida (padrão):** passadas a cada **0,5 s** atualizam só o texto provisório (beam 1); a confirmação compara hipóteses separadas por **≥ 1 s** de áudio novo (beam 5). Mantém o WER do bloco de 1 s (blocos de 0,5 s puros pioram ~+0,03) e atinge a 1ª exibição ≤ 1,5 s no p95 com um fluxo.
2. **Passadas de exibição são "melhor esforço":** o agendador as pula quando o worker está > 0,25 s atrasado em relação ao áudio. Com dois fluxos simultâneos com fala contínua (pior caso), 1ª exibição p95 1,25–1,76 s e confirmação p95 3,1–4,0 s, melhor que o bloco de 1 s em todas as métricas; a saída confirmada não muda.
3. **Silêncio não se acumula no buffer:** após ≥ 3 s sem palavras novas desde a última confirmada, o áudio antigo é descartado (evita que o Whisper classifique a janela como "sem fala"). Cortes forçados confirmam as palavras provisórias do trecho descartado e são registrados (ADR-009).
4. **Concorrência com outros usos da GPU** (ex.: jogos) elevou a latência a dezenas de segundos no benchmark: o Resource Manager deve detectar atraso crescente e avisar o usuário.

## Consequências

- Custo de GPU maior que VAD puro por causa das retranscrições; limitado pelo VAD, pelo tamanho do buffer e pelo descarte de silêncio (RTF combinado ~0,6 com dois fluxos no pior caso).
- RNF-015: a meta de 1ª exibição é atendida; a confirmação (p95 3–4 s) mais a tradução ainda precisa caber nos 4 s do RNF-015 — medir no POC-001D.
- Fala em outro idioma será transcrita incorretamente no P0; limitação documentada.

## Refinamento 2026-11-12 (POC-001D: política v3 e worker de STT)

1. **Confirmação na pausa (política v3, padrão).** Na passada de confirmação, a cauda provisória é confirmada na hora quando termina uma frase (`.`, `?`, `!`) e vem seguida de **≥ 0,6 s** sem palavras, sem esperar uma segunda hipótese igual (`pause_commit_seconds`). Medido com large-v3-turbo int8_float16, dois fluxos ao mesmo tempo e AMI IS1009a/ES2004a (300 s cada), duas rodadas de cada (`benchmarks/results/stt-policy-v3/`):

   | Política | Confirmação p95 (IS / ES) | Exibição p95 (IS / ES) | WER (IS / ES) |
   | --- | --- | --- | --- |
   | v2 | 3,12 / 4,07 s | 1,37 / 2,04 s | 0,185 / 0,176 |
   | **v3** | **2,63–2,66 / 2,89–3,01 s** | 1,21 / 1,92–2,16 s | 0,187–0,189 / 0,181 |

   A confirmação no p95 cai de 0,5 a 1,1 s, ao custo de +0,002 a +0,005 de WER.
2. **Prioridade do sistema sobre o microfone (decisão 6).** O microfone só recebe passadas de confirmação, a cada 1 s; as passadas de exibição ficam para o áudio do sistema. Passadas de exibição são puladas quando o fluxo está atrasado mais de 0,25 s.
3. **Fechamento sem perda.**
   - Um salto de posição (pausa, perda do dispositivo, reenvio depois de um reinício) fecha o trecho contínuo, e a pausa da captura também o fecha.
   - Ao fechar, o buffer é transcrito mais uma vez com beam completo e tudo o que essa passada ouviu é confirmado (`finish(transcribe_tail=True)`). Antes, palavras vistas só em passadas de exibição podiam se perder. O defeito foi encontrado pelos testes do POC-001D.
4. **Frases e IDs.** As palavras confirmadas viram frases (`speech/sentences.py`, no máximo 30 palavras sem pontuação). Cada frase final é gravada com UUIDv7 e um `SequenceNumber` por fluxo e sessão, sem buracos, antes de ser publicada (ADR-009).
5. **Implementação.** `iris_workers/workers/speech.py` e `speech_engine.py`, pelo WorkerHost (ADR-014). O worker emite `stt.partial`, `stt.final` e `stt.ack` (posição de reenvio), e o app reenvia o áudio não confirmado depois de um reinício.
6. **Demo ao vivo (`tools/iris-live`)**, com áudio AMI tocado por outro processo, process loopback e as frases saindo até a tradução:
   - ES2004a, 90 s: tradução com p50 1,45 s e p95 2,80 s desde o fim da fala;
   - IS1009a, 70 s: p50 1,78 s e p95 6,75 s; o pior caso veio no início da sessão e não se repetiu.

   A GPU estava parcialmente ocupada por outro programa (24–47%). O teste de 60 minutos (POC-001F) vai dar a medida longa.
