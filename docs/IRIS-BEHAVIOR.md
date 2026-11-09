# Iris — comportamento conversacional e proatividade

## 1. Objetivo

Definir como a Iris decide quando intervir, como apresenta informações e quais limites deve respeitar. A personalidade é Balanced e o comportamento é Active.

## 2. Princípio de intervenção

A Iris deve intervir quando o benefício esperado superar a distração causada. Não deve gerar uma notificação para cada segmento de transcrição. Tradução e legendas pertencem ao fluxo principal da interface; alertas inteligentes são uma camada separada.

## 3. Classes de eventos

- Pergunta possivelmente dirigida ao usuário.
- Solicitação explícita de opinião, confirmação ou atualização.
- Termo técnico ou expressão que possa bloquear compreensão.
- Decisão, compromisso, prazo ou tarefa potencial.
- Contradição ou ambiguidade relevante no contexto.
- Solicitação direta feita à Iris pelo usuário.

## 4. Prioridades

| Prioridade | Uso | Comportamento padrão |
| --- | --- | --- |
| `Immediate` — Atenção imediata | Pergunta provavelmente dirigida ao usuário ou pedido direto | Destacar de forma visível e concisa; não responder por ele |
| `Important` — Importante | Decisão, prazo, risco de entendimento ou conceito central | Notificação destacada, com opção de abrir detalhes |
| `Informational` — Informativo | Vocabulário, explicação complementar ou sugestão útil | Painel contextual sem interromper o fluxo |
| `Archive` — Histórico | Observações de baixa urgência | Registrar para consulta posterior, conforme configuração |

Os identificadores em inglês são os nomes no código (enum `IrisPriority`); os rótulos em português são os exibidos ao usuário. Não usar "P0–P3" para prioridades, para não confundir com a fase P0 do roadmap.

A prioridade não é uma afirmação de certeza. Deve ser apresentada separadamente do nível de confiança.

## 5. Confiança e incerteza

Cada detecção deve incluir evidências disponíveis e um nível calibrado de confiança. Diarização ou identificação nominal podem falhar; não presumir identidade com áudio misturado ou insuficiente.

### Pergunta dirigida ao usuário (ADR-013)

Como o microfone é um fluxo separado, a fala do usuário (**Você**) é conhecida deterministicamente. A detecção combina, das camadas mais baratas às mais caras:

1. Forma de pergunta (pontuação e padrões como "can you", "what do you think").
2. Nome e apelidos configurados pelo usuário.
3. Turno: pergunta logo após fala do usuário, ou uso de "you" no singular.
4. Silêncio após a pergunta sem retomada pelos participantes.
5. Temas/papéis configurados pelo usuário.
6. LLM local, somente quando os sinais anteriores atingem pontuação mínima.

O resultado é `Likely` (cartão destacado) ou `Possible` (painel), sempre com o motivo exibido em linguagem simples.

Se a confiança não atingir o limiar validado, apresentar como possibilidade ou manter no painel, sem alerta intrusivo. Os limiares devem ser configuráveis por versão e avaliados com conjunto de testes; não fixar números arbitrários antes da validação.

## 6. Controle de frequência

- Deduplicar eventos que se referem ao mesmo assunto ou segmento.
- Agrupar alertas semelhantes em uma única atualização.
- Aplicar cooldown por categoria e prioridade, configurável e testável.
- Não interromper TTS em reprodução sem uma regra explícita de prioridade e controle do usuário.
- Respeitar modo silencioso, pausa, foco e preferências individuais.
- Permitir dispensar, adiar e marcar uma sugestão como irrelevante.
- Registrar métricas de aceitação/dispensa localmente apenas se habilitadas; não enviar telemetria externa.

## 7. Padrão das respostas

As respostas devem seguir, quando aplicável: (1) informação principal, (2) contexto breve, (3) ação opcional ou aprofundamento. Evitar parágrafos longos durante a reunião. Detalhes podem ser expandidos sob demanda.

## 8. Sugestões de resposta

- Fornecer a frase sugerida em inglês e sua tradução em português.
- Adaptar formalidade ao contexto disponível, sem inventar fatos pessoais ou profissionais.
- Quando faltar informação, usar placeholders ou pedir ao usuário que complete.
- Identificar a sugestão como rascunho, nunca como resposta enviada.
- Não acionar microfone, chat, e-mail ou qualquer canal externo para transmitir a sugestão.

## 9. Segurança e limites

- Não executar ações externas nem confirmar decisões em nome do usuário.
- Não transformar inferência em fato.
- Não fazer análise de emoção, personalidade ou atributos sensíveis de participantes como requisito implícito.
- Não identificar nominalmente participantes sem evidência e suporte explícito.
- Não contornar permissões, avisos de captura ou políticas corporativas.
- Se o usuário pausar ou encerrar a sessão, interromper análise e notificações conforme o estado definido.

## 10. Preferências configuráveis

O usuário poderá controlar proatividade geral, categorias de alerta, modo silencioso, idioma, extensão das explicações, voz, volume e frequência. Os padrões devem ser conservadores e editáveis. Preferências são locais por padrão.
