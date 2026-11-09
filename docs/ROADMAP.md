# Roadmap do Iris Voice Assistant

## P0 — Protótipo técnico local (primeiro marco)

- Ambiente .NET/Python.
- Benchmark de STT, tradução, LLM e TTS.
- Captura Windows: sistema (excluindo o próprio app) e microfone separados.
- Transporte e WorkerHost para os workers locais.
- Pipeline em streaming por segmentos, com tradução PT-BR por oração.
- TTS sob demanda e prevenção de realimentação.
- Persistência SQLite experimental.
- Janela mínima de anotações e diagnóstico.
- Métricas de latência e hardware.

**Aceite:** sessão de teste de 60 minutos sem perda silenciosa de segmentos finais; pipeline local ponta a ponta; métricas registradas.

## V1 — Primeiro produto funcional Windows

- Interface Avalonia completa.
- Dashboard e central de reunião.
- Legendas e áudio traduzido.
- Iris v1: agente contextual com personalidade Balanced e comportamento Active controlado.
- Detecção de perguntas possivelmente dirigidas ao usuário, explicações de termos e sugestões de resposta.
- Proatividade com prioridades, deduplicação, cooldown, modo silencioso e preferências.
- Identificação explícita de fatos, inferências e sugestões.
- Histórico e pesquisa textual.
- Glossário.
- Relatórios e tarefas.
- Model Manager e Resource Manager básicos.
- Segurança e diagnóstico.

## V1.5 — Expansão multiplataforma

- Adaptador macOS.
- Adaptador Linux.
- Instaladores específicos.
- Testes reais de compatibilidade e permissões.
- Atualização por plataforma.

## V2 — Inteligência avançada

- Diarização evolutiva.
- Memória entre reuniões da Iris, sempre opcional e com escopo/consentimento explícitos.
- Pesquisa semântica local.
- Modo de aprendizado de inglês.
- Documentos locais como contexto opcional.
- Gravação de áudio opcional e consentida.
- Backup criptografado opcional.

## V3 — Comercialização

- Licenciamento e ativação.
- Planos e limites.
- Distribuição e atualização assinadas.
- Inventário e verificação de licenças.
- Suporte e documentação de usuário.

## Regra de escopo

Não iniciar V1/V1.5/V2/V3 antes de cumprir os critérios de aceite da etapa anterior, salvo decisão explícita registrada em ADR.
