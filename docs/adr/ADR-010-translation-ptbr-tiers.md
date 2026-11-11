# ADR-010 — Tradução EN→PT-BR em camadas

- **Estado:** Aceita. O motor rápido foi confirmado pelo benchmark POC-001A, e o refinamento por LLM passou a ser sugestão com verificação de fatos (ver "Refinamento 2026-11-10", itens 1 a 6).
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
4. **Latência:** 45 a 76 ms por frase na GPU e 151 a 207 ms na CPU int8 (4 threads). **Decisão do passo 6 (2026-11-11): CPU int8 com 4 threads.** Na medição combinada, a GPU não reduziu a latência e acrescentou ~0,3 GB de VRAM. A tradução custa ~0,1 s e nunca esperou na fila; o que domina a latência é a confirmação do STT.
5. **Termos técnicos mantidos em inglês já na camada rápida** (aprovado pelo usuário em 2026-11-10). Sem isso, termos comuns (build, merge, pull request, feature flag, endpoint, ticket) saíam traduzidos ao pé da letra. Como funciona (`translation/glossary.py`):
   - Cada termo do glossário vira um placeholder opaco (`ZQX0`), que o Opus-MT copia sem alterar. O termo original entra de volta depois da tradução.
   - O determinante antes do termo é corrigido conforme o gênero cadastrado ("uma feature flag", "da sprint").
   - Termos que também são verbos em inglês (build, deploy, merge, cache, branch) só são protegidos depois de um determinante ("the build"). "We need to build" continua sendo traduzido.
   - Se o modelo perde ou duplica um placeholder, a frase é traduzida de novo sem proteção. Nenhum termo some em silêncio.
   - Lista padrão em `DEFAULT_TECH_TERMS`. Palavras ambíguas (release, commit, job, query) ficam fora da lista. O glossário do usuário usa o mesmo mecanismo.

6. **Refinamento por LLM vira sugestão (passo 4; decisão do usuário em 2026-11-11). Isso substitui a troca automática do item 3 da Decisão.**
   - **Medição.** Com o qwen3:4b-instruct, o chrF passou de 72,8 para 73,4, dentro do ruído, com ~0,25 s por frase. O modelo também introduziu erros de fato ("next Tuesday" virou "segunda-feira"; "a couple of weeks" virou "duas semanas") e de concordância. Com modelos de 3 a 4B, a substituição automática não é segura.
   - **A versão rápida continua sendo a exibida.** A refinada só é oferecida como sugestão, e o usuário decide se aceita. As duas ficam guardadas (`Fast`, `Refined`).
   - **Verificação de fatos.** Antes de oferecer a sugestão, `translation/refine_guard.py` faz uma checagem determinística. Ela descarta a sugestão se mudarem números, dias da semana, meses, datas relativas, negações, nomes, termos do glossário ou a quantidade de perguntas. Também descarta saída suja, PT-PT novo e mudança grande de tamanho.
   - **Resultado no benchmark.** A verificação rejeitou os 2 erros de fato e nenhuma das melhorias.
   - **Limite.** Ela não checa gramática nem sentido; por isso o refinamento fica como sugestão.
   - **Pendente.** A forma de exibir e aceitar a sugestão fica para a UX (ADR-011).

## Consequências

- O benchmark deve medir qualidade com referência PT-BR revisada e contar marcadores de PT-PT na saída.
- O modelo de dados guarda duas versões de tradução por segmento (`Fast`, `Refined`).
