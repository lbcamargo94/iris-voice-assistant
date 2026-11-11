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

## Refinamento 2026-11-11 (benchmark POC-001A, passo 5)

1. **Voz da Iris: Kokoro-82M v1.0** (pesos Apache-2.0), com a mistura de estilos **75% `if_sara` + 25% `pf_dora`** na velocidade 1,0. A mistura foi escolhida pelo usuário, de ouvido, entre várias opções.
   - O critério foi uma voz feminina, jovem e humanizada, com sotaque brasileiro, coerente com a persona.
   - A receita fica em `benchmarks/models/kokoro.manifest.json` (`irisVoice`).
   - A voz continua configurável (IRIS-IDENTITY §7).
2. **Processo separado:** o Kokoro depende do `phonemizer` e do espeak-ng, ambos GPL. Ele roda num ambiente próprio, pelo host `hosts/kokoro/kokoro_host.py` (GPL-3.0-or-later). O código MIT só conversa com ele pelo protocolo de stdio v1 (`hosts/README.md`), que tem fim de fala explícito.
3. **Latência:** na CPU com 8 threads, o primeiro áudio sai com p95 de 1,03 s e máximo de 1,19 s, porque a síntese começa pela primeira oração das frases longas. Isso atende a meta de 1,5 s (RNF-015). Com 4 threads, o p95 fica em 1,47 s, no limite. A partida a frio leva ~2,2 s, então **o processo deve ser aberto antes do primeiro "Ouvir"**.
4. **Alternativas medidas e descartadas:**
   - Piper com cadu/jeff: cadeia de treino toda CC0 e ~0,1 s até o primeiro áudio, mas só vozes masculinas.
   - Voz "Maria" do Windows (SAPI): recusada por soar robótica.
   - Vozes de serviços de nuvem e cópia de vozes comerciais de terceiros: excluídas por privacidade (ADR-002) e por direitos de terceiros.
5. **Pendências:**
   - CPU ou GPU e número de threads: decisão do passo 6, com a contenção medida.
   - Teste de realimentação: POC-001B.
   - V1: voz própria gerada a partir de uma gravação de referência com direitos de uso.
