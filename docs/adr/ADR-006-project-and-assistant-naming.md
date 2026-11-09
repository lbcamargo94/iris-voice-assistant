# ADR-006 — Nome do projeto e identidade da assistente

- **Estado:** Aceita
- **Data:** 2026-11-09
- **Decisores:** responsável pelo produto

## Contexto

A documentação anterior utilizava InteliVoice como nome provisório do produto e Iris como nome da agente. O responsável pelo projeto definiu explicitamente a nomenclatura a ser utilizada daqui em diante.

## Decisão

- O nome do projeto/produto será **Iris Voice Assistant**.
- O nome da assistente inteligente será **Iris**.
- Iris é a assistente integrada ao produto, não o nome da empresa ou de uma marca corporativa separada.
- O nome apresentado em documentação, interface, materiais internos e comunicação do projeto deve ser “Iris Voice Assistant”.
- A assistente deve ser referida simplesmente como “Iris”.
- Slug sugerido para repositório e pacote: `iris-voice-assistant`.
- Namespace raiz sugerido para projetos .NET: `IrisVoiceAssistant`.
- Serviços da assistente podem usar o prefixo `Iris`, por exemplo `IrisAssistantService`.

## Consequências

- Documentos, exemplos, namespaces, identificadores e novos artefatos devem seguir a nomenclatura definida.
- Nomes legados InteliVoice, MeetingAI e Veya não devem ser reutilizados em novos artefatos.
- Esta decisão é de nomenclatura do projeto e não constitui parecer jurídico, registro de marca ou confirmação de disponibilidade de domínio.
- Antes de lançamento comercial, executar busca de anterioridade de marca nos territórios e classes relevantes, além de verificar os domínios pretendidos.
- A decisão não altera stack, arquitetura Local First, requisitos de privacidade, escopo P0 ou roadmap.

## Alternativas consideradas

1. Manter InteliVoice — rejeitada por decisão expressa de nomenclatura do responsável pelo projeto.
2. Usar Iris como nome do produto e da assistente — rejeitada para evitar confusão entre o nome do produto e a identidade da assistente.
3. Usar Iris Voice Assistant para o produto e Iris para a assistente — **aceita**.

## Revisão

Reabrir este ADR somente se houver conflito jurídico relevante, indisponibilidade comercial impeditiva ou decisão explícita de rebranding.
