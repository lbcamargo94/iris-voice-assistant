# Requisitos do produto — Iris Voice Assistant

## 1. Escopo

O nome do produto/projeto é **Iris Voice Assistant** e a assistente integrada chama-se **Iris**. A denominação está aprovada para uso no projeto; a disponibilidade jurídica e de domínio permanece pendente de validação antes da comercialização.

Iris Voice Assistant é uma aplicação desktop multiplataforma que transcreve toda a reunião em inglês, traduz para português brasileiro e oferece assistência contextual. Na configuração padrão a experiência é **text-first** (ADR-011): uma janela permanece aberta com as anotações em tempo real (inglês original + tradução PT-BR) e a Iris lê qualquer mensagem em voz sob demanda. O processamento de IA ocorre localmente.

## 2. Perfis de usuário

- Usuário individual que participa de reuniões em inglês.
- Futuro usuário profissional/comercial, sujeito a licenciamento e políticas da organização.

## 3. Requisitos funcionais

### Captura de áudio

- RF-AUD-001: listar dispositivos de entrada e saída disponíveis.
- RF-AUD-002: capturar áudio do sistema em adaptador suportado.
- RF-AUD-003: capturar microfone separadamente.
- RF-AUD-004: iniciar, pausar, retomar e parar captura por ação explícita.
- RF-AUD-005: exibir indicador persistente de captura.
- RF-AUD-006: detectar perda ou desconexão de dispositivo.
- RF-AUD-007: manter timestamps e metadados de formato.
- RF-AUD-008: excluir da captura do sistema o áudio produzido pelo próprio aplicativo (ADR-007).
- RF-AUD-009: oferecer, opcionalmente, captura restrita ao aplicativo da reunião escolhido pelo usuário.
- RF-AUD-010: identificar o tipo do dispositivo de saída e avisar quando for caixa de som (risco de eco acústico).
- RF-AUD-011: exibir aviso de consentimento/responsabilidade ao iniciar cada sessão de captura.

### Transcrição

- RF-STT-001: transcrever inglês localmente; idioma fixo em inglês no P0 (ADR-008).
- RF-STT-002: produzir resultados parciais (revisáveis) e finais (imutáveis) com ID de segmento estável.
- RF-STT-003: associar timestamps aos segmentos.
- RF-STT-004: preservar texto original.
- RF-STT-005: permitir recuperação controlada após falha do worker.
- RF-STT-006: transcrever os dois fluxos, rotulando o microfone como **Você** e o áudio do sistema como **Participantes**.
- RF-STT-007: exibir lacunas de áudio (`AudioGap`) na linha do tempo quando houver descarte ou falha (ADR-009).

### Tradução e voz

- RF-TR-001: traduzir inglês para português **brasileiro** localmente (ADR-010).
- RF-TR-002: preservar relação entre segmento original e tradução.
- RF-TR-003: apresentar original e tradução simultaneamente.
- RF-TR-004: traduzir incrementalmente cada oração/frase confirmada, sem traduzir texto parcial.
- RF-TR-005: permitir refinamento opcional por LLM local, preservando a versão rápida e a refinada.
- RF-TR-006: tornar opcional (desabilitada por padrão) a tradução da fala do próprio usuário.
- RF-TTS-001: sintetizar português brasileiro localmente.
- RF-TTS-002: reproduzir em voz sob demanda (botão "Ouvir") qualquer mensagem/anotação; leitura contínua automática é opcional e desabilitada por padrão.
- RF-TTS-003: permitir controlar volume e interromper a reprodução.
- RF-TTS-004: prevenir realimentação do áudio sintetizado.

### Interface

- RF-UI-001: janela de anotações em tempo real com original EN, tradução PT-BR e rótulo do fluxo.
- RF-UI-002: distinguir visualmente texto parcial, final, refinado e editado.
- RF-UI-003: exibir estado da captura, dos workers e da Iris sem que um mascare o outro.

### Agente inteligente Iris

- RF-AI-001: responder perguntas sobre o contexto da reunião.
- RF-AI-002: explicar termos e expressões.
- RF-AI-003: sugerir respostas em inglês e apresentar tradução.
- RF-AI-004: identificar perguntas possivelmente dirigidas ao usuário, classificadas como provável ou possível, com motivo explicável e indicação de incerteza (ADR-013).
- RF-AI-005: distinguir fatos observados, inferências, sugestões e confirmações do usuário.
- RF-AI-006: não executar ações externas em nome do usuário.
- RF-AI-007: operar com personalidade equilibrada (profissional e amigável) e comportamento ativo controlado por relevância.
- RF-AI-008: aplicar prioridades, deduplicação, cooldown e preferências de notificação.
- RF-AI-009: continuar degradando de forma independente, sem interromper captura, STT, tradução ou persistência, quando a Iris/LLM falhar.
- RF-AI-010: permitir modo silencioso, pausa e desativação por categoria.

### Histórico e dados

- RF-DATA-001: salvar transcrições e traduções localmente.
- RF-DATA-002: organizar reuniões por data e título.
- RF-DATA-003: pesquisar texto no histórico.
- RF-DATA-004: permitir exclusão de reunião e seus dados associados.
- RF-DATA-005: não armazenar áudio permanentemente por padrão.
- RF-DATA-006: persistir cada segmento final antes de publicá-lo para a UI e a Iris.

### Identidade e capacidades da Iris

- A agente do produto chama-se Iris.
- Iris Listen, Translate, Assist, Learn, Memory e Insights são capacidades da mesma agente, não agentes independentes.
- A Iris usa contratos para consumir os 16 módulos e não substitui seus serviços especializados.
- A proatividade ativa deve ser validada com métricas de falsos alertas e controles do usuário.

### Evoluções posteriores

- Glossário técnico personalizado.
- Diarização e identificação evolutiva de falantes entre participantes.
- Detecção automática de idioma.
- Relatórios, decisões e tarefas.
- Pesquisa semântica local.
- Modo de aprendizado de inglês.
- Backup criptografado opcional.
- Instaladores e atualizações multiplataforma.
- Licenciamento comercial e auditoria de licenças de modelos.

## 4. Requisitos não funcionais

- RNF-001: nenhuma API externa de IA obrigatória.
- RNF-002: áudio e transcrições não podem ser enviados automaticamente para a nuvem.
- RNF-003: interface não pode bloquear durante captura ou inferência.
- RNF-004: operações longas devem aceitar cancelamento.
- RNF-005: o pipeline deve usar buffers limitados e backpressure explícito (ADR-009).
- RNF-006: persistência de segmentos finais deve ser resiliente a falhas.
- RNF-007: uso de CPU, RAM, VRAM e latência deve ser mensurável.
- RNF-008: arquitetura deve permitir adaptadores de Windows, macOS e Linux.
- RNF-009: compatibilidade só será declarada após testes em cada plataforma.
- RNF-010: captura deve ser visível e controlável.
- RNF-011: falha da Iris não pode interromper o pipeline principal de áudio, transcrição, tradução ou persistência.
- RNF-012: contexto enviado ao LLM deve ser limitado, relevante e local.
- RNF-013: eventos proativos devem ser deduplicados e sujeitos a limites de frequência configuráveis.
- RNF-014: decisões e tarefas inferidas devem preservar proveniência e estado de confirmação.
- RNF-015: metas provisórias de latência (hipóteses a validar no benchmark, medidas em p50/p95): texto parcial em inglês ≤ 1,5 s após a fala; tradução PT-BR de uma oração confirmada ≤ 4 s (p95) após o fim da oração; início da leitura sob demanda ≤ 1,5 s após o clique.
- RNF-016: toda perda de áudio deve ser contabilizada e visível; segmentos finais nunca são descartados (ADR-009).
- RNF-017: o STT do áudio do sistema tem prioridade sobre o microfone, a tradução de refinamento e a Iris.

## 5. Critérios de aceite do P0

- Capturar áudio do sistema e microfone separadamente no Windows, excluindo o áudio do próprio app.
- Transcrever localmente os dois fluxos em inglês, com parciais e finais.
- Traduzir os segmentos do sistema para português brasileiro sem API externa.
- Sintetizar e reproduzir sob demanda um texto em português.
- Impedir que o áudio sintetizado seja reprocessado como entrada.
- Salvar transcrição e tradução em SQLite antes de exibi-las.
- Registrar latência, CPU, RAM e VRAM.
- Demonstrar uma sessão contínua de teste de 60 minutos com zero buracos de sequência em segmentos finais, conciliação de amostras fechada e todas as lacunas exibidas.
- Documentar limitações e resultados reais.
