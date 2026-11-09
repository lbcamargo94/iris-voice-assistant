# Iris — estratégia de testes e critérios de qualidade

## 1. Camadas

- Unitários: classificação, prioridades, cooldown, deduplicação e política.
- Contrato: eventos, payloads LLM, repositórios e notificações.
- Integração: pipeline com segmentos reais de teste, SQLite e workers locais.
- Avaliação linguística: qualidade de explicações e sugestões, revisão humana.
- Desempenho: latência, CPU, RAM, VRAM, filas e impacto sobre STT/tradução.
- Privacidade/segurança: minimização, retenção, exclusão, ausência de chamadas externas e limites de autonomia.

## 2. Casos obrigatórios

1. Distinguir pergunta dirigida ao usuário de pergunta entre outros participantes.
2. Tratar ambiguidade como possibilidade, não certeza.
3. Não gerar alerta para cada segmento parcial.
4. Deduplicar pergunta ou insight repetido.
5. Aplicar prioridades e cooldown sem perder eventos importantes.
6. Respeitar modo silencioso, pausa e categorias desabilitadas.
7. Não interromper indevidamente TTS ou tradução.
8. Não executar ações externas nem enviar mensagens.
9. Marcar inferências e sugestões separadamente de fatos confirmados.
10. Manter captura, STT, tradução e persistência funcionando quando a Iris/LLM falhar.
11. Cancelar tarefas obsoletas e limitar filas/contexto.
12. Não enviar dados a serviços remotos durante execução normal.
13. Medir cada camada da detecção de perguntas (ADR-013) isoladamente e combinada; o LLM só é acionado acima da pontuação mínima.
14. Excluir da análise segmentos do microfone marcados `DuringAssistantPlayback`.

## 3. Métricas

- Precisão, recall e F1 para detecção de perguntas/intenções em conjunto anotado.
- Taxa de falsos alertas por hora de reunião.
- Taxa de alertas úteis avaliados por usuários de teste.
- Latência p50/p95 entre evento final e insight exibido.
- Duplicações, eventos perdidos e tarefas canceladas corretamente.
- Incremento de CPU, RAM e VRAM com Iris habilitada.
- Impacto sobre latência e estabilidade de STT/tradução/TTS.

## 4. Conjunto de avaliação

Usar gravações próprias ou licenciadas, consentidas e anonimizadas quando possível. O AMI Meeting Corpus (CC-BY 4.0) é a base sugerida, por conter reuniões reais com falantes anotados. Anotar falante, pergunta dirigida/não dirigida/ambígua, evento, prioridade e evidências. Incluir sotaques, ruído, reuniões com várias pessoas, sobreposição de fala e vocabulário técnico. Não usar reuniões confidenciais de trabalho sem autorização.

## 5. Critério de liberação

Não liberar proatividade ativa por padrão sem avaliação de falsos positivos, latência e controles do usuário. Os limiares quantitativos devem ser definidos após a primeira linha de base e registrados por versão. A Iris não pode comprometer os critérios de aceite de áudio, transcrição e tradução do P0.
