# ADR-011 — Experiência text-first e TTS sob demanda

- **Estado:** Aceita
- **Data:** 2026-11-09

## Contexto

A proposta original previa reprodução contínua da tradução em voz. Isso sobrepõe a voz traduzida ao áudio original, chega com atraso e aumenta o risco de realimentação.

## Decisão

- Na configuração padrão, a Iris trabalha **somente com texto**: uma janela permanece aberta com as anotações em tempo real (inglês original + tradução PT-BR), rotuladas por fluxo (**Você** / **Participantes**), e com os assuntos da reunião.
- A voz é **sob demanda**: um botão "Ouvir" em cada mensagem/anotação faz a Iris ler o texto. Leitura contínua automática é uma opção desabilitada por padrão.
- A tradução da fala do próprio usuário (microfone) é opcional e desabilitada por padrão.

## Consequências

- O foco de latência passa a ser o texto (STT + tradução), não a síntese.
- O critério de aceite de TTS do P0 passa a ser reprodução sob demanda sem realimentação.
- O isolamento de captura (ADR-007) continua obrigatório, pois a voz pode tocar durante a reunião.
