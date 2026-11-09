# Estratégia de testes

## Níveis

1. Unitários: domínio, segmentação, estados, filas e regras de negócio.
2. Contrato: comunicação C# ↔ workers.
3. Integração: SQLite, modelos e áudio.
4. Hardware/performance: CPU, RAM, VRAM e latência.
5. Plataforma: Windows no P0; macOS/Linux em fases próprias.
6. Qualidade linguística: WER/CER para STT e avaliação de tradução com referência PT-BR revisada.

## Conjunto de referência

Usar gravações próprias ou licenciadas, com consentimento e transcrições de referência. Base sugerida: **AMI Meeting Corpus** (CC-BY 4.0), com reuniões reais em inglês, transcrição e falantes anotados; complementar com gravações próprias para termos técnicos. Incluir sotaques, velocidades, ruído moderado, termos técnicos e interrupções. Não utilizar gravações confidenciais de trabalho sem autorização. Registrar origem e licença de cada arquivo.

## Harness de reunião virtual

- **Modo arquivo:** fonte de captura falsa que lê o áudio de referência em ritmo real ou acelerado; determinístico, roda em `dotnet test`.
- **Modo real:** o áudio de referência é reproduzido no Windows e capturado pelo loopback real.
- Ambos produzem relatório com cobertura, WER, latências, gaps e conciliação.

## Verificação de perda (ADR-009)

- Zero buracos de `SequenceNumber` em segmentos finais.
- Conciliação: amostras capturadas = processadas + descartadas.
- Todo descarte registrado como `AudioGap` e exibido.
- Cobertura: proporção de falas de referência com pelo menos um segmento final sobreposto; limiar definido após a primeira linha de base.

## Cenários obrigatórios P0

- Fala clara em inglês.
- Diferentes sotaques e velocidades.
- Ruído moderado.
- Silêncio e pausas.
- Usuário e participantes falando alternadamente e em sobreposição.
- Dispositivo desconectado.
- Falha e reinício de worker.
- Cancelamento de sessão.
- TTS sob demanda durante fala dos participantes, sem realimentação.
- Saída em caixa de som (aviso exibido).
- Pressão de VRAM/RAM.
- Sessão contínua de 60 minutos.

## Métricas

- Latência até primeira hipótese parcial e até segmento final.
- Latência de tradução por oração confirmada.
- Latência até início da leitura sob demanda.
- WER/CER de transcrição.
- Avaliação humana de fidelidade da tradução e contagem de marcadores PT-PT.
- Segmentos perdidos/duplicados; gaps e frames descartados.
- CPU, RAM, VRAM e temperatura quando disponível.

## Metas provisórias

As metas de RNF-015 são hipóteses de engenharia, não garantias. Registrar percentis (p50/p95), condições do teste e modelo utilizado. O critério de 60 minutos sem perda silenciosa é obrigatório para aceite P0.

## Testes da agente Iris

Consultar `IRIS-TESTS.md` para casos e métricas detalhados. A avaliação da Iris deve medir falsos alertas, precisão/recall de perguntas dirigidas, latência de intervenção e impacto de recursos. Falha do LLM/Iris não pode interromper captura, transcrição, tradução ou persistência.
