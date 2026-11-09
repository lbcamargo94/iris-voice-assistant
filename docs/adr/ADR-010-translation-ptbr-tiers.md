# ADR-010 — Tradução EN→PT-BR em camadas

- **Estado:** Aceita, sujeita ao benchmark POC-001A
- **Data:** 2026-11-09

## Contexto

O produto exige português **brasileiro**. O pacote Argos en→pt não garante a variante brasileira. NLLB-200 não distingue variantes e tem licença não comercial. LLMs locais produzem PT-BR mais natural, porém com latência e consumo de VRAM maiores.

## Decisão

1. **Tradução rápida (principal):** Opus-MT `opus-mt-tc-big-en-pt` convertido para CTranslate2, com token de destino brasileiro (`>>pob<<`, a confirmar no benchmark). Argos permanece como baseline comparativo.
2. **Tradução incremental:** traduzir cada oração/frase confirmada pelo STT, sem esperar o fim do segmento; nunca traduzir texto parcial.
3. **Refinamento opcional por LLM local:** com prompt PT-BR e glossário, quando habilitado e com recursos disponíveis; a versão refinada substitui a exibida com marca de revisão, preservando ambas as versões.
4. **Normalizador PT-PT→PT-BR:** substituições léxicas e de construção determinísticas (ex.: ecrã→tela, utilizador→usuário, "estar a + infinitivo"→gerúndio) aplicadas às duas camadas, além do glossário do usuário.
5. **Excluído:** NLLB-200 (licença não comercial).

## Consequências

- O benchmark deve medir qualidade com referência PT-BR revisada e contar marcadores de PT-PT na saída.
- O modelo de dados guarda duas versões de tradução por segmento (`Fast`, `Refined`).
