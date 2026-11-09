# ADR-008 — STT em streaming por concordância e ciclo de vida dos segmentos

- **Estado:** Aceita, sujeita ao benchmark POC-001A
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

## Consequências

- Custo de GPU maior que VAD puro por causa das retranscrições; limitado pelo VAD e pelo tamanho do buffer.
- Latências-alvo (RNF-015) são hipóteses até o benchmark.
- Fala em outro idioma será transcrita incorretamente no P0; limitação documentada.
