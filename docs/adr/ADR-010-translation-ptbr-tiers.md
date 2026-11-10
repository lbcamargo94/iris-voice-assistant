# ADR-010 — Tradução EN→PT-BR em camadas

- **Estado:** Aceita; motor rápido confirmado pelo benchmark POC-001A (ver "Refinamento 2026-11-10")
- **Data:** 2026-11-09

## Contexto

O produto exige português **brasileiro**. O pacote Argos en→pt não garante a variante brasileira. NLLB-200 não distingue variantes e tem licença não comercial. LLMs locais produzem PT-BR mais natural, porém com latência e consumo de VRAM maiores.

## Decisão

1. **Tradução rápida (principal):** Opus-MT `opus-mt-tc-big-en-pt` convertido para CTranslate2, com token de destino brasileiro (`>>pob<<`, a confirmar no benchmark). Argos permanece como baseline comparativo.
2. **Tradução incremental:** traduzir cada oração/frase confirmada pelo STT, sem esperar o fim do segmento; nunca traduzir texto parcial.
3. **Refinamento opcional por LLM local:** com prompt PT-BR e glossário, quando habilitado e com recursos disponíveis; a versão refinada substitui a exibida com marca de revisão, preservando ambas as versões.
4. **Normalizador PT-PT→PT-BR:** substituições léxicas e de construção determinísticas (ex.: ecrã→tela, utilizador→usuário, "estar a + infinitivo"→gerúndio) aplicadas às duas camadas, além do glossário do usuário.
5. **Excluído:** NLLB-200 (licença não comercial).

## Refinamento 2026-11-10 (benchmark POC-001A, `benchmarks/README.md`)

1. **Token `>>pob<<` confirmado.** Com `>>por<<`, os dois motores produzem PT-PT (ficheiro, equipa, utilizador, "estar a + infinitivo"). Com `>>pob<<` não houve nenhum marcador nas 50 frases.
2. **Motor rápido: Opus-MT tc-big com `>>pob<<` e beam 4.** O chrF foi 68,6, contra 66,2 do Argos com `>>pob<<`, e o Argos omitiu trechos das frases. O Argos fica descartado como motor, mas pode servir de baseline.
3. **O adaptador separa as frases antes de traduzir.** Modelos Marian descartam o texto depois da primeira frase de uma entrada.
4. **Latência:** 45 a 76 ms por frase na GPU e 151 a 207 ms na CPU int8 (4 threads). A escolha entre CPU e GPU sai da medição combinada do passo 6. A CPU deixa a GPU livre para o STT.
5. **Termos técnicos mantidos em inglês já na camada rápida** (aprovado pelo usuário em 2026-11-10). Sem isso, termos comuns (build, merge, pull request, feature flag, endpoint, ticket) saíam traduzidos ao pé da letra. Como funciona (`translation/glossary.py`):
   - Cada termo do glossário vira um placeholder opaco (`ZQX0`), que o Opus-MT copia sem alterar. O termo original entra de volta depois da tradução.
   - O determinante antes do termo é corrigido conforme o gênero cadastrado ("uma feature flag", "da sprint").
   - Termos que também são verbos em inglês (build, deploy, merge, cache, branch) só são protegidos depois de um determinante ("the build"). "We need to build" continua sendo traduzido.
   - Se o modelo perde ou duplica um placeholder, a frase é traduzida de novo sem proteção. Nenhum termo some em silêncio.
   - Lista padrão em `DEFAULT_TECH_TERMS`. Palavras ambíguas (release, commit, job, query) ficam fora da lista. O glossário do usuário usa o mesmo mecanismo.

## Consequências

- O benchmark deve medir qualidade com referência PT-BR revisada e contar marcadores de PT-PT na saída.
- O modelo de dados guarda duas versões de tradução por segmento (`Fast`, `Refined`).
