# Referência de tradução EN → PT-BR (v1, rascunho)

Conjunto de referência para o passo 3 do POC-001A (Opus-MT × Argos; ADR-010). Frases **originais**, escritas para este projeto no estilo de reuniões de trabalho em inglês (sem conteúdo real de reuniões). Licença: MIT, como o restante do repositório.

## Como revisar

- Corrija a coluna **PT-BR** para como um brasileiro diria naturalmente numa reunião. Ela é o gabarito: a tradução automática será comparada com ela.
- Termos técnicos: mantenha em inglês os que normalmente são falados em inglês no Brasil (ex.: "deploy", "sprint", "pull request") e traduza os demais. Se aceitar as duas formas, escreva a alternativa em **Notas** (ex.: `alt: implantação`).
- Mude **Estado** de `rascunho` para `revisado` (ou `remover`, se a frase não servir).
- Não use o caractere `|` dentro das células.
- As frases da categoria `stt` imitam a saída do reconhecimento de voz (sem pontuação, hesitações, reformulações), porque é isso que o tradutor receberá.

## Marcadores de PT-PT

Contados na saída do tradutor (não devem aparecer): ecrã, utilizador, ficheiro, telemóvel, equipa, registo, partilhar, guardar (= salvar), "estar a + infinitivo", "vós", "fixe", "autocarro", "pequeno-almoço", "contacto", "facto", "projecto", "objectivo", "actual".

## Frases

| ID | Categoria | EN | PT-BR | Estado | Notas |
| --- | --- | --- | --- | --- | --- |
| 001 | status | I finished the integration tests yesterday, and they're all passing now. | Terminei os testes de integração ontem e agora todos estão passando. | rascunho | |
| 002 | status | We're still blocked by the API changes from the platform team. | Ainda estamos travados pelas mudanças na API do time de plataforma. | rascunho | alt: bloqueados; equipe |
| 003 | status | The migration is about eighty percent done, but the last part is the hardest. | A migração está uns oitenta por cento pronta, mas a última parte é a mais difícil. | rascunho | |
| 004 | status | I didn't have time to look at the bug you mentioned last week. | Não tive tempo de olhar o bug que você mencionou semana passada. | rascunho | |
| 005 | status | The build has been failing since this morning because of a flaky test. | O build está falhando desde hoje de manhã por causa de um teste instável. | rascunho | alt: teste flaky |
| 006 | status | Most of the work this sprint went into fixing performance issues. | A maior parte do trabalho desta sprint foi para corrigir problemas de desempenho. | rascunho | alt: performance |
| 007 | status | We rolled back the release because the error rate went up after the deploy. | Fizemos rollback da versão porque a taxa de erros subiu depois do deploy. | rascunho | alt: revertemos a release |
| 008 | status | Staging is up again, so you can test your changes there. | O ambiente de staging voltou, então você pode testar suas mudanças lá. | rascunho | |
| 009 | tecnico | We need to add an index to that table, otherwise the query will time out. | Precisamos adicionar um índice naquela tabela, senão a consulta vai dar timeout. | rascunho | alt: query |
| 010 | tecnico | The memory leak only happens when the cache is full. | O vazamento de memória só acontece quando o cache está cheio. | rascunho | alt: memory leak |
| 011 | tecnico | Can you open a pull request so we can review it before the merge? | Você pode abrir um pull request para revisarmos antes do merge? | rascunho | alt: PR |
| 012 | tecnico | The service retries three times and then sends the message to a dead letter queue. | O serviço tenta de novo três vezes e depois manda a mensagem para uma dead letter queue. | rascunho | |
| 013 | tecnico | Latency spikes every hour when the batch job starts. | A latência dá picos a cada hora, quando o job em lote começa. | rascunho | alt: job batch |
| 014 | tecnico | We should move the secrets out of the config file and into the vault. | Devemos tirar os segredos do arquivo de configuração e colocar no cofre. | rascunho | alt: no vault |
| 015 | tecnico | The new endpoint returns a paginated list, twenty items per page. | O novo endpoint retorna uma lista paginada, vinte itens por página. | rascunho | |
| 016 | tecnico | If we upgrade the library, we'll have to rewrite part of the authentication flow. | Se atualizarmos a biblioteca, vamos ter que reescrever parte do fluxo de autenticação. | rascunho | |
| 017 | tecnico | The dashboard shows the CPU usage, but not the memory. | O dashboard mostra o uso de CPU, mas não o de memória. | rascunho | alt: painel |
| 018 | tecnico | Let's keep the old version behind a feature flag for a couple of weeks. | Vamos manter a versão antiga atrás de uma feature flag por umas duas semanas. | rascunho | |
| 019 | pergunta | Do you have any concerns about the timeline? | Você tem alguma preocupação com o cronograma? | rascunho | alt: prazo |
| 020 | pergunta | What do you think, should we ship it on Friday or wait until Monday? | O que você acha, a gente lança na sexta ou espera até segunda? | rascunho | |
| 021 | pergunta | Could you share your screen so we can all see the error? | Você pode compartilhar a tela para todo mundo ver o erro? | rascunho | PT-PT: partilhar/ecrã |
| 022 | pergunta | Who's going to own the follow-up with the client? | Quem vai ficar responsável pelo retorno ao cliente? | rascunho | |
| 023 | pergunta | Is anyone else seeing this issue, or is it just me? | Mais alguém está vendo esse problema ou sou só eu? | rascunho | PT-PT: a ver |
| 024 | pergunta | How long do you think it'll take to fix? | Quanto tempo você acha que vai levar para corrigir? | rascunho | |
| 025 | pergunta | Sorry, can you repeat the last part? You broke up a little. | Desculpa, pode repetir a última parte? Você cortou um pouco. | rascunho | |
| 026 | pergunta | Did we ever get an answer from legal about the contract? | A gente chegou a ter uma resposta do jurídico sobre o contrato? | rascunho | |
| 027 | decisao | Okay, so we agreed to postpone the launch by one week. | Certo, então combinamos adiar o lançamento em uma semana. | rascunho | |
| 028 | decisao | Let's go with the second option, it's cheaper and easier to maintain. | Vamos com a segunda opção, é mais barata e mais fácil de manter. | rascunho | |
| 029 | decisao | We decided not to support the old browsers anymore. | Decidimos não dar mais suporte aos navegadores antigos. | rascunho | |
| 030 | decisao | The final call is Maria's, but the team is leaning towards option A. | A decisão final é da Maria, mas o time está tendendo para a opção A. | rascunho | alt: equipe |
| 031 | tarefa | I'll send the meeting notes to everyone after the call. | Vou mandar a ata da reunião para todo mundo depois da call. | rascunho | alt: as anotações; chamada |
| 032 | tarefa | John, can you update the ticket and assign it to me? | John, você pode atualizar o ticket e atribuir para mim? | rascunho | alt: chamado |
| 033 | tarefa | Please review the document by Thursday so we can sign off on it. | Por favor, revisem o documento até quinta para podermos aprovar. | rascunho | |
| 034 | tarefa | Someone needs to talk to the vendor about the license renewal. | Alguém precisa falar com o fornecedor sobre a renovação da licença. | rascunho | |
| 035 | tarefa | I'll set up a follow-up meeting for next Tuesday at ten. | Vou marcar uma reunião de acompanhamento para terça que vem às dez. | rascunho | |
| 036 | numeros | The budget for the next quarter is two hundred and fifty thousand dollars. | O orçamento do próximo trimestre é de duzentos e cinquenta mil dólares. | rascunho | |
| 037 | numeros | Response time went from 300 milliseconds down to 120. | O tempo de resposta caiu de 300 milissegundos para 120. | rascunho | |
| 038 | numeros | We have about 1.5 million active users, up 12% from last year. | Temos cerca de 1,5 milhão de usuários ativos, 12% a mais que no ano passado. | rascunho | separador decimal |
| 039 | numeros | The deadline is March 3rd, which gives us less than three weeks. | O prazo é 3 de março, o que nos dá menos de três semanas. | rascunho | |
| 040 | numeros | Version 2.4 fixes the crash on startup. | A versão 2.4 corrige o travamento na inicialização. | rascunho | alt: crash |
| 041 | informal | That's a good point, I hadn't thought about that. | Bom ponto, eu não tinha pensado nisso. | rascunho | |
| 042 | informal | Let's not reinvent the wheel here. | Não vamos reinventar a roda aqui. | rascunho | |
| 043 | informal | We're running out of time, so let's wrap up. | Estamos ficando sem tempo, então vamos encerrar. | rascunho | |
| 044 | informal | I'm on the same page as Alex on this one. | Concordo com o Alex nessa. | rascunho | expressão idiomática |
| 045 | informal | Can we take this offline? It's getting too detailed. | Podemos conversar sobre isso depois, à parte? Está ficando detalhado demais. | rascunho | expressão idiomática |
| 046 | stt | so um basically what we need is uh a way to track the the requests | então basicamente o que a gente precisa é um jeito de rastrear as requisições | rascunho | hesitações e repetição |
| 047 | stt | yeah no I think that makes sense but we should double check with the security team first | é, não, acho que faz sentido, mas devemos confirmar com o time de segurança antes | rascunho | "yeah no" |
| 048 | stt | the thing is we can't really scale it without changing the database | o problema é que a gente não consegue escalar isso sem mudar o banco de dados | rascunho | |
| 049 | stt | I mean it works but it's not it's not ideal you know | quer dizer, funciona, mas não é ideal, sabe | rascunho | reformulação |
| 050 | stt | okay cool so next item on the agenda is the hiring plan | beleza, então o próximo item da pauta é o plano de contratação | rascunho | |
