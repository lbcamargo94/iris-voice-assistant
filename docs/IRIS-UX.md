# Iris — experiência conversacional e UX

## 1. Princípios

- A Iris deve ser reconhecível, mas visualmente discreta.
- Tradução e legendas são o fluxo principal; alertas da Iris são secundários.
- Mostrar o motivo da intervenção e, quando aplicável, confiança/ambiguidade em linguagem simples.
- Permitir abrir detalhes, dispensar, adiar ou silenciar categorias.
- Não usar animações ou sons que distraiam durante reuniões.

## 1.1 Janela de anotações (text-first, ADR-011)

Na configuração padrão, a Iris trabalha somente com texto. Uma janela permanece aberta durante a reunião, com:

- **Linha do tempo de falas:** cada segmento mostra o rótulo do fluxo (**Você** / **Participantes**), o original em inglês e a tradução PT-BR. Texto parcial aparece atenuado e é substituído quando confirmado; tradução refinada recebe marca discreta de revisão.
- **Assuntos:** tópicos da reunião anotados em tempo real.
- **Cartões da Iris:** perguntas, termos e pontos importantes, em área separada da linha do tempo.
- **Botão "Ouvir"** em cada mensagem/anotação: a Iris lê o texto em voz (sob demanda); um controle global permite interromper a leitura.
- **Lacunas de áudio** exibidas na linha do tempo (ex.: "⚠ lacuna de áudio de 1,2 s").
- **Avisos persistentes:** captura ativa, saída em caixa de som (risco de eco) e indisponibilidade de workers.

## 2. Estados visuais

| Estado | Significado | Indicação sugerida |
| --- | --- | --- |
| Idle | Aguardando sessão ou interação | Ícone estático |
| Listening | Sessão capturando áudio | Onda discreta |
| Processing | Contexto sendo analisado | Indicador sutil de processamento |
| Translating | Tradução em andamento | Estado associado ao fluxo de tradução |
| Speaking | TTS da Iris em reprodução | Indicador de voz/onda |
| Attention | Insight relevante disponível | Destaque visual moderado com prioridade |
| Paused | Análise/captura pausada conforme controle | Estado explícito de pausa |
| Error | Recurso indisponível ou falha | Mensagem clara e ação de recuperação |

Os estados da Iris não devem mascarar o estado real da captura ou dos workers.

## 3. Padrões de cartão

- **Pergunta:** trecho original, tradução, indicação de que pode ser dirigida ao usuário e sugestão opcional em inglês com tradução.
- **Termo:** termo, significado contextual e opção de adicionar ao glossário.
- **Decisão/tarefa:** texto extraído, evidência de origem e estado `inferido` ou `confirmado`.
- **Resumo:** síntese curta com expansão e links internos para segmentos de origem.

## 4. Exemplos de linguagem

**Início:** “Estou pronta para acompanhar sua reunião. Vou destacar traduções, perguntas e pontos importantes sem interromper desnecessariamente.”

**Pergunta provável:** “Parece que esta pergunta pode ser direcionada a você. Quer ver uma sugestão de resposta?”

**Baixa confiança:** “Não consegui determinar com segurança quem está sendo questionado. Esta é apenas uma possibilidade.”

**Termo técnico:** “Neste contexto, ‘rollback’ significa reverter uma alteração ou implantação para uma versão anterior.”

**Encerramento:** “Organizei os principais assuntos, decisões e possíveis tarefas. Itens inferidos estão identificados separadamente.”

## 5. Acessibilidade e controle

A interface deve oferecer controles acessíveis por teclado, foco visível, contraste adequado, escala de texto e alternativas visuais para alertas sonoros. O usuário deve poder desativar voz da Iris sem desativar a transcrição ou tradução.
