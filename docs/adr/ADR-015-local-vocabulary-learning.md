# ADR-015 — Aprendizado local de vocabulário entre sessões

- **Estado:** Aceita (pedido e decisões do usuário em 2026-11-12). Antecipa parte da "memória entre reuniões" do roadmap (V2).
- **Data:** 2026-11-12

## Contexto

O usuário quer que a Iris "aprenda conforme assiste, comparando com um vocabulário da língua em segundo plano, salvando o aprendizado e evoluindo sozinha".

**Treinar os pesos dos modelos com as próprias transcrições foi descartado:**
- sem uma referência do que estava certo, o modelo reforçaria os próprios erros;
- treinar exige horas de GPU e mais VRAM do que sobra durante uma reunião.

O que melhora o resultado sem esse risco é aprender o **vocabulário** do que o usuário assiste e usá-lo como contexto:
- nomes, siglas e jargão viram `hotwords` do faster-whisper;
- e são mantidos em inglês na tradução.

## Decisão

1. **Detecção (worker de STT, `iris_workers/speech/vocabulary.py`).** Cada frase final é comparada com o vocabulário do próprio modelo, o BPE de ~50 mil itens do Whisper, aprendido de texto em inglês. Não há lista externa nem dependência nova. Uma palavra vira candidata quando é:
   - **rara:** com 4 letras ou mais e virando 2 ou mais tokens (palavras comuns são um token só);
   - **nome próprio:** com maiúscula fora do início da frase, exceto palavras como "I", dias e meses;
   - **sigla:** de 2 a 6 maiúsculas.

   Os termos detectados seguem no evento `stt.final`.
2. **Memória (SQLite, tabela `vocabulary`, esquema v2).**
   - Guarda o termo, o tipo, as ocorrências, as frases e sessões distintas, a primeira e a última vez visto, o estado e a origem `auto`.
   - O texto das frases salvas **nunca** é alterado.
   - Os termos são marcados como **aprendidos automaticamente**, ou seja, inferidos e não confirmados (AGENTS.md §2).
3. **Ativação automática** (escolha do usuário). Um termo fica **ativo** quando aparece em **3 frases distintas**, sem pedir confirmação.
   - **No STT,** os termos ativos (até 40, priorizando os mais frequentes e recentes) viram `hotwords`.
   - **Na tradução,** nomes e siglas ativos são protegidos como termos do glossário, ficando em inglês. Palavras raras em minúsculas continuam sendo traduzidas.
   - A lista é enviada aos workers no início da sessão e sempre que muda.
4. **Esquecimento.** Um termo sem aparecer há 30 dias deixa de estar ativo, mas fica no histórico. O usuário pode **listar** os termos e **esquecer** um ou **todos** (`iris-live vocab`, `vocab forget`, `vocab clear`). Termos esquecidos não voltam sozinhos até o usuário apagar tudo.
5. **Privacidade.**
   - O vocabulário é local, como o resto do banco (DATABASE.md), e nunca sai do PC.
   - O aprendizado pode ser desligado por sessão (`--no-learning`). Apagar o banco apaga tudo.
6. **Medição antes de valer como benefício.** O ganho no WER é medido nas reuniões AMI: aprende-se num trecho e mede-se no trecho seguinte (`bench/vocabulary.py`). O resultado fica registrado no refinamento abaixo.

## Consequências

- **Nomes e jargão** que o modelo erra na primeira vez tendem a sair certos depois de vistos algumas vezes.
- **Risco de reforçar um termo errado**, quando o STT erra igual três vezes:
  - o efeito é limitado, porque um hotword só **sugere** a grafia ao modelo e não força nada;
  - o usuário pode esquecer o termo.
- **A memória entre sessões** existe a partir de agora, no escopo de vocabulário, mantida pelo usuário.

## Refinamento 2026-11-12: primeira medição (`bench/vocabulary.py`, `benchmarks/results/vocabulary/`)

Método: aprender nos primeiros 5 minutos de cada reunião AMI e transcrever os 5 minutos seguintes sem e com os termos aprendidos como `hotwords`.

| Reunião | Termos aprendidos | WER offline sem / com | WER streaming (política v3) sem / com |
| --- | --- | --- | --- |
| IS1009a | 1 ("whiteboard") | 0,2615 / 0,2862–0,2937 | 0,2974 / 0,2639 |
| ES2004a | nenhum | 0,2706 / 0,2706 | 0,2721 / 0,2721 |

- **As reuniões AMI quase não repetem nomes nem jargão**, então não servem para medir o benefício.
- **O único hotword aprendido nem aparece no trecho avaliado.** As diferenças, de cerca de ±3 pontos e em direções opostas entre offline e streaming, são perturbação da decodificação, não efeito do vocabulário. Duas rodadas offline idênticas já diferiram 0,75 ponto.
- **Não há evidência de ganho nem de dano sistemático.** A ativação automática segue como o usuário decidiu.
- **Pendente:** medir com conteúdo que repete termos técnicos e nomes, com uma transcrição de referência.
- **Benefício garantido, independentemente do WER:** nomes e siglas aprendidos ficam em inglês na tradução.
