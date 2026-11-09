# Modelo de dados

## Banco

SQLite local para o P0 e V1, em modo WAL. PostgreSQL remoto só será considerado quando sincronização comercial for aprovada.

## Entidades principais

- `Meeting`: título, datas, idioma, estado e metadados.
- `MeetingSession`: sessões de captura, dispositivos usados, modo de isolamento e instante de início (UTC + QPC).
- `TranscriptSegment`: fluxo (`SystemAudio`/`Microphone`), `SequenceNumber` por fluxo e sessão, posição inicial/final em amostras, texto, estado (`Final`/`Edited`), flags e confiança quando disponível. Parciais não são persistidas.
- `TranslationSegment`: referência ao segmento original, idioma de destino, camada (`Fast`/`Refined`), texto traduzido e modelo/versão.
- `AudioGap`: fluxo, início, duração e causa do descarte.
- `Speaker`: identificador local provisório, rótulo e metadados de diarização.
- `AssistantMessage`: mensagens do usuário e respostas da Iris/modelo, com tipo, origem e metadados de modelo.
- `AssistantInsight`: alerta, pergunta candidata (`Likely`/`Possible`), explicação ou insight, com prioridade, confiança, motivo, estado e proveniência.
- `ContextReference`: vínculo entre insight/resumo/tarefa e segmentos de transcrição que o fundamentam.
- `IrisPreference`: preferências locais de proatividade, categorias, nome/apelidos e temas do usuário, idioma, voz e frequência.
- `MeetingSummary`: resumo e versão do modelo que o gerou.
- `ActionItem`: descrição, responsável inferido/confirmado, prazo e estado de confirmação.
- `GlossaryTerm`: termo, definição e tradução personalizada.
- `ModelInstallation`: modelo, versão, checksum, licença e caminho local.
- `UserSettings`: preferências locais.

## Regras

- IDs UUIDv7 (`Guid.CreateVersion7()`, ADR-012).
- Armazenar timestamps em UTC e apresentar em fuso local; posições de áudio em amostras.
- Segmentos finais são gravados antes de serem publicados (ADR-009).
- Preservar original e tradução separadamente; preservar as versões rápida e refinada.
- Marcar conteúdo final, editado e inferido.
- Insights devem distinguir observado, inferido, sugerido, confirmado pelo usuário e rejeitado.
- Armazenar evidências/referências para extrações de decisão e tarefa quando disponíveis.
- Preferências da Iris são locais por padrão e devem ser removíveis pelo usuário.
- Não armazenar áudio bruto permanentemente por padrão.
- Exclusão de reunião deve remover ou invalidar dados derivados associados conforme política de retenção.
- Não guardar conteúdo de reunião em logs de diagnóstico por padrão.

## Criptografia

No P0 o banco não é criptografado; a proteção depende do perfil do usuário no sistema operacional. Essa limitação é documentada. Criptografia do banco (ex.: SQLCipher ou chave protegida por DPAPI/Keychain) será avaliada na V1, considerando licença e impacto de desempenho.

## Pesquisa

P0: pesquisa textual simples em SQLite. Pesquisa semântica local e embeddings ficam para V1/V2 após avaliação de custo de memória e qualidade.
