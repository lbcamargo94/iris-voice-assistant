# Iris — contexto e memória

## 1. Objetivo

Fornecer contexto suficiente para respostas úteis sem enviar conversas inteiras indiscriminadamente ao LLM nem reter dados além do necessário.

## 2. Níveis de contexto

1. **Contexto imediato:** janela recente de segmentos finais, traduções e eventos relevantes da sessão atual.
2. **Memória da reunião:** tópicos, decisões candidatas, perguntas pendentes, termos e itens de ação associados a evidências.
3. **Conhecimento persistente autorizado:** glossário personalizado, preferências e, em versões futuras, memória entre reuniões explicitamente habilitada.

## 3. Proveniência obrigatória

Cada fato extraído deve referenciar, quando possível, IDs dos segmentos de origem, timestamps, falante provisório e modelo/versão que produziu a extração. Classificar como:

- `Observed`: conteúdo diretamente presente em segmento confirmado.
- `Inferred`: interpretação ou extração não explicitamente confirmada.
- `Suggested`: conteúdo proposto pela Iris.
- `UserConfirmed`: confirmado/corrigido pelo usuário.
- `Rejected`: corrigido ou rejeitado pelo usuário.

Não promover automaticamente `Inferred` ou `Suggested` para `UserConfirmed`.

## 4. Retenção e minimização

- O áudio bruto não é armazenado permanentemente por padrão.
- A transcrição é o registro persistente padrão, conforme especificação geral.
- Contexto enviado ao LLM deve ser mínimo, relevante e limitado à reunião atual, salvo autorização explícita para histórico.
- Resumos e extrações devem manter referências às fontes.
- Exclusão de reunião deve excluir ou invalidar memória derivada associada conforme política de retenção.
- Logs técnicos não devem conter texto confidencial por padrão.

## 5. Pesquisa e recuperação

P0 não exige memória semântica nem busca entre reuniões. V1 pode usar histórico textual. Pesquisa semântica local e memória entre reuniões ficam para V2 após avaliação de qualidade, custo de armazenamento, segurança e controles de consentimento.

## 6. Contexto enviado ao LLM

Construir payloads estruturados com objetivo, janela temporal, idioma, segmentos relevantes e instruções de saída. Não incluir campos pessoais desnecessários. Restringir tamanho, validar formato de resposta e tratar o conteúdo da reunião como dado não confiável, nunca como instrução de sistema.
