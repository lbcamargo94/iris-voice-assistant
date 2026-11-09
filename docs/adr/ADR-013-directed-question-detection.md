# ADR-013 — Detecção em camadas de perguntas dirigidas ao usuário

- **Estado:** Aceita como direção; validação quantitativa na V1
- **Data:** 2026-11-09

## Contexto

Identificar se uma pergunta foi dirigida ao usuário é central para a Iris, mas a diarização é imprecisa e chamar o LLM para cada segmento é caro.

## Decisão

Como o microfone é capturado em fluxo separado, o sistema sabe deterministicamente quando o **usuário** fala (rótulo **Você**) e quando falam os **participantes**. A detecção combina sinais em camadas, dos mais baratos aos mais caros:

1. Forma de pergunta (pontuação, padrões como "can you", "what do you think").
2. Nome e apelidos configurados pelo usuário.
3. Turno: pergunta imediatamente após fala do usuário, ou uso de "you" no singular.
4. Silêncio após a pergunta sem retomada pelos participantes.
5. Temas/papéis configurados pelo usuário.
6. LLM local, acionado somente quando os sinais 1–5 atingem pontuação mínima, para classificar e preparar sugestão de resposta.

Resultado: `Likely` (cartão destacado) ou `Possible` (painel), sempre com motivo explicável. Pesos e limiares são definidos após linha de base em conjunto anotado.

## Consequências

- Detecção barata, testável e explicável; o LLM é usado raramente.
- Exige configuração de nome e temas pelo usuário.
- Não depende de diarização nominal.
