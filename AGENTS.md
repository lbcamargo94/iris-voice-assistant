# AGENTS.md — Regras para agentes de programação

Estas instruções são obrigatórias para Claude Code, Copilot e outros agentes que contribuam com o **Iris Voice Assistant**. A assistente inteligente integrada chama-se **Iris**.

## 1. Fonte de verdade

- `README.md` apresenta o projeto.
- `docs/` contém requisitos e decisões técnicas.
- `docs/IRIS-*.md` é a fonte de verdade para identidade, comportamento, arquitetura, memória, integração, UX e testes da agente Iris.
- **Iris Voice Assistant** é o nome aprovado do projeto/produto nesta documentação. Isso não representa liberação jurídica: domínio, marcas e disponibilidade nos mercados-alvo ainda precisam ser verificados antes do lançamento comercial.
- Use `IrisVoiceAssistant` como namespace raiz .NET sugerido e `iris-voice-assistant` como slug sugerido do repositório.
- Não introduza novamente os nomes antigos `InteliVoice`, `MeetingAI` ou `Veya` em novos artefatos.
- Em caso de conflito, não invente uma decisão: sinalize o conflito e proponha um ADR.
- O escopo aprovado do MVP é o P0 descrito em `docs/POC-001.md`.

## 2. Princípios obrigatórios

- Priorizar execução local e privacidade.
- Não adicionar chamadas a APIs externas de IA, telemetria externa, analytics ou serviços remotos sem aprovação explícita.
- Não enviar áudio, transcrições, prompts ou dados de reunião para a internet.
- Downloads iniciais de pacotes/modelos são permitidos durante instalação/configuração, mas devem ser explícitos, verificáveis e documentados.
- Não gravar áudio permanentemente por padrão.
- A captura deve ser visível e oferecer controles claros de iniciar, pausar e parar.
- Não implementar captura oculta, bypass de permissões do sistema ou evasão de controles corporativos.
- Nunca executar respostas, mensagens ou ações em nome do usuário sem confirmação explícita.
- A Iris é ativa, mas não invasiva: priorizar relevância, agrupar notificações e respeitar limites de frequência.
- A Iris deve distinguir fatos observados, inferências, sugestões e conteúdo confirmado pelo usuário.
- Não tratar toda pergunta capturada como pergunta dirigida ao usuário; avaliar falante, contexto e evidências, indicando incerteza.
- Não criar uma agente/modelo separado para cada capacidade: Iris Listen, Translate, Assist, Learn, Memory e Insights são modos/capacidades da mesma agente, orquestrados por contratos.
- A identidade/persona da Iris não pode ficar acoplada a um fornecedor ou modelo específico de LLM.

## 3. Arquitetura

- Usar C# e .NET 10 LTS para o núcleo e a aplicação desktop.
- Usar Avalonia UI para a interface multiplataforma.
- Aplicar MVVM na camada de apresentação.
- Manter regras de negócio independentes do sistema operacional.
- Implementar captura e serviços nativos por adaptadores em projetos específicos de plataforma.
- Manter os workers Python isolados (Python 3.12 via `uv`, em `workers/`) e comunicando-se por contratos versionados.
- Não acoplar o domínio diretamente a Ollama, faster-whisper, Opus-MT, Argos, sherpa-onnx ou Piper. Criar interfaces e adaptadores.
- Seguir as convenções do ADR-012 (Generic Host, Central Package Management, UUIDv7).
- Persistir primeiro localmente; sincronização em nuvem não faz parte do P0.

## 4. Qualidade de código

- Preferir código simples, tipado, assíncrono e cancelável.
- Não bloquear a thread da UI.
- Propagar `CancellationToken` em operações longas.
- Tratar erros de dispositivo, worker, modelo e armazenamento explicitamente.
- Não usar `async void`, exceto em handlers de UI quando necessário.
- Não adicionar dependências sem justificar função, licença, manutenção e impacto de distribuição.
- Fixar versões de dependências quando a implementação começar.
- Não incluir segredos, chaves ou credenciais no repositório.

## 5. Áudio e processamento

- Manter áudio do sistema e microfone em fluxos separados.
- Definir formato, sample rate, canais, timestamps e tamanho dos blocos explicitamente.
- Usar buffers limitados e política documentada de backpressure.
- Nunca descartar silenciosamente segmentos confirmados.
- Prevenir que a voz sintetizada pelo aplicativo volte ao pipeline de entrada.
- Não afirmar identificação nominal de participantes sem evidência; diarização é uma funcionalidade evolutiva.

## 6. Inteligência artificial

- Distinguir resultados parciais de finais.
- Preservar texto original e tradução como dados separados.
- Não tratar inferências do LLM como fatos confirmados.
- Registrar proveniência e estado de confiança quando forem extraídas decisões ou tarefas.
- Limitar contexto enviado ao LLM e manter memória por reunião.
- Não carregar simultaneamente modelos grandes sem medir VRAM/RAM e latência.
- Não prometer tradução perfeita ou latência fixa antes de testes.

## 7. Testes obrigatórios

- Adicionar testes unitários para lógica de domínio.
- Adicionar testes de contrato para workers e adaptadores.
- Testar cancelamento, falhas e reconexão.
- Medir latência ponta a ponta, uso de CPU, RAM e VRAM.
- Testar falsos positivos/negativos na detecção de perguntas dirigidas ao usuário e nos gatilhos de proatividade.
- Testar supressão de notificações repetidas, prioridades, cancelamento e modo silencioso.
- Validar que a Iris nunca apresenta inferências como fatos nem executa ações externas sem autorização.
- Usar apenas gravações de teste autorizadas e conjuntos de referência documentados.
- Não declarar compatibilidade macOS/Linux antes de testar em máquinas/ambientes reais dessas plataformas.

## 8. Segurança e privacidade

- Minimizar logs e nunca registrar conteúdo confidencial por padrão.
- Validar caminhos de arquivos e integridade de artefatos baixados.
- Armazenar configurações sensíveis usando mecanismos adequados ao sistema operacional.
- Implementar exclusão de dados e retenção explícita.
- Tratar transcrições como dados potencialmente pessoais e confidenciais.

## 9. Fluxo de trabalho

Antes de codificar:

1. Ler os documentos relevantes.
2. Identificar critérios de aceite.
3. Descrever arquivos que serão alterados.
4. Identificar riscos e dependências.

Ao concluir:

1. Executar testes pertinentes.
2. Informar comandos executados e resultados reais.
3. Atualizar documentação se necessário.
4. Não declarar algo implementado se apenas criou stubs ou interfaces.
5. Sinalizar pendências e limitações.

## 10. Escopo

Não implementar no P0: conta de usuário, billing, nuvem, sincronização remota, integração com calendários, gravação permanente por padrão, diarização avançada, pesquisa semântica completa, licenciamento comercial ou versões macOS/Linux de produção.
