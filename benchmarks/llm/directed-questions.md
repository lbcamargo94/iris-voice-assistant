# Perguntas dirigidas ao usuário (v1, rascunho)

Conjunto para o passo 4 do POC-001A (classificação por LLM, camada 6 do ADR-013). As falas são **originais**, escritas para este projeto (sem conteúdo real de reuniões). Licença: MIT.

- O usuário configurado se chama **Ana**. O microfone é um fluxo separado, então as falas dela aparecem sempre como **Você** e as dos outros como **Participante**.
- **Rótulos:**
  - `directed`: a pergunta ou o pedido é para a Ana (cartão `Likely`).
  - `not_directed`: a fala é para outra pessoa, é retórica ou não é pergunta.
  - `ambiguous`: pergunta para o grupo, ou que depende de um papel não configurado (painel `Possible`).
- **Contexto:** as falas anteriores, separadas por ` / `.
- Corrija os rótulos na coluna **Rótulo** e marque **Estado** como `revisado`. Não use `|` dentro das células.

| ID | Contexto | Fala | Rótulo | Estado | Notas |
| --- | --- | --- | --- | --- | --- |
| 001 | Você: I pushed the fix to staging this morning. | Ana, did you also update the migration script? | directed | rascunho | nome |
| 002 | Participante: Mark, you were on call last night. | Mark, did the alerts fire again? | not_directed | rascunho | outro nome |
| 003 | Você: I think we can ship on Friday. | Are you sure the tests are stable enough? | directed | rascunho | turno após o usuário |
| 004 | Participante: Let's go around the room. | Does anyone have blockers? | ambiguous | rascunho | grupo |
| 005 | Participante: The client asked about the export feature. | What do you think, Ana, can we fit it in this sprint? | directed | rascunho | nome no meio |
| 006 | Participante: I'll talk to finance tomorrow. | John, can you send me the numbers before that? | not_directed | rascunho | outro nome |
| 007 | Você: The dashboard is ready for review. | Can you share the link in the chat? | directed | rascunho | turno após o usuário |
| 008 | Participante: We lost two days because of the outage. | How do we avoid that next time? | ambiguous | rascunho | grupo, aberta |
| 009 | Participante: Sarah built the new pipeline. | Sarah, how long does a full run take now? | not_directed | rascunho | outro nome |
| 010 | Você: I can take the login bug. | Great, when do you think it'll be done? | directed | rascunho | turno após o usuário |
| 011 | Participante: Moving on to the budget. | Is the budget approved already? | ambiguous | rascunho | grupo |
| 012 | Participante: Ana mentioned the API limits last week. | Do the limits apply to the internal services too? | ambiguous | rascunho | nome em terceira pessoa |
| 013 | Participante: Ana, you're on mute. | Can you hear us now? | directed | rascunho | continuação |
| 014 | Participante: I'm not sure about the design. | Why did we pick this database in the first place? | ambiguous | rascunho | grupo |
| 015 | Participante: We keep breaking the build. | Why does this always happen on Fridays? | not_directed | rascunho | retórica |
| 016 | Você: I reviewed the contract. | And what did legal say about the penalty clause? | directed | rascunho | turno após o usuário |
| 017 | Participante: Tom and Priya are handling the migration. | Priya, are you blocked on anything? | not_directed | rascunho | outro nome |
| 018 | Participante: Let me share my screen. | Can everyone see it? | ambiguous | rascunho | grupo |
| 019 | Você: I'd prefer option B. | Why B and not A? | directed | rascunho | turno após o usuário |
| 020 | Participante: Thanks for joining. | So today we'll cover the roadmap and the hiring plan. | not_directed | rascunho | não é pergunta |
| 021 | Participante: Ana owns the release notes. | Ana, can you post them today? | directed | rascunho | nome |
| 022 | Participante: Mike, you said the vendor called. | What did they want? | not_directed | rascunho | continuação com outro |
| 023 | Você: I'll check the logs. | Could you also look at the metrics while you're there? | directed | rascunho | turno após o usuário |
| 024 | Participante: The customer asked me, can you deliver by May? | I told them we would check. | not_directed | rascunho | discurso relatado |
| 025 | Participante: Quick question for the backend team. | Is the new endpoint deployed? | ambiguous | rascunho | papel não configurado |
| 026 | Participante: Ana, Mark, one question. | Which of you is taking the on-call shift this weekend? | directed | rascunho | inclui o usuário |
| 027 | Participante: I sent the proposal yesterday. | Did you get a chance to read it, Ana? | directed | rascunho | nome no fim |
| 028 | Participante: Lisa, your mic is noisy. | Could you switch to headphones? | not_directed | rascunho | continuação com outro |
| 029 | Você: The tests are passing now. | Nice. Lisa, can you deploy it then? | not_directed | rascunho | após o usuário, mas para outro |
| 030 | Participante: We need someone to own the follow-up. | Any volunteers? | ambiguous | rascunho | grupo |
| 031 | Você: I'm not sure that estimate is realistic. | Okay, so what would be a realistic estimate? | directed | rascunho | turno após o usuário |
| 032 | Participante: The numbers look off. | Does that make sense to you guys? | ambiguous | rascunho | plural |
