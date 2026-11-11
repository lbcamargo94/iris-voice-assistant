# ADR-014 — Transporte dos workers: stdio com o Iris Worker Protocol v1

- **Estado:** Aceita (decisão do usuário em 2026-11-11). Refina o ADR-003.
- **Data:** 2026-11-11

## Contexto

O ADR-003 deixou o transporte entre o app .NET e os workers Python para depois de um benchmark. Os requisitos vêm do ARCHITECTURE §6, do SECURITY e do ADR-009:
- áudio binário sem base64, com controle separado;
- cancelamento, verificação de vida e versão do protocolo;
- apenas IPC ou loopback, validando a origem e o tamanho das mensagens;
- retenção de áudio durante o reinício de um worker.

O benchmark (`tools/IrisVoiceAssistant.TransportBench`, `benchmarks/results/transport/`) usou um worker de eco com 5.000 mensagens por teste:

| Transporte | Bloco de 20 ms, ida e volta: p50 / p95 / p99 | Controle de 96 B: p50 / p95 | Vazão em sequência |
| --- | --- | --- | --- |
| stdio (pipes anônimos) | 0,025 / 0,043 / 0,053 ms | 0,017 / 0,034 ms | 145 mil blocos/s |
| Named pipe (`CurrentUserOnly`) | 0,029 / 0,048 / 0,073 ms | 0,029 / 0,045 ms | 52 mil blocos/s |
| HTTP/1.1 loopback com token | 0,132 / 0,184 / 0,241 ms | 0,105 / 0,148 ms | 8 mil blocos/s |

O pipeline precisa de ~100 blocos/s (dois fluxos), com orçamento de latência de centenas de milissegundos. **Os três atendem com folga**, então o desempenho não decide.

## Decisão

1. **Transporte: stdio do processo filho.**
   - O host abre cada worker como processo filho, com `python -I`.
   - stdin carrega host → worker; stdout carrega worker → host, reservado ao protocolo; stderr leva os logs, sem conteúdo de reunião.
   - **Só o processo pai tem acesso:** não há porta nem nome que outro processo possa descobrir.
   - **Quando o app termina, o stdin fecha** e o worker encerra.
   - Funciona igual no macOS e no Linux.
2. **Formato de cada mensagem:** `[u32 LE tamanho][tipo u8][conteúdo]`, com **limite de 1 MiB** nos dois sentidos. Mensagem maior derruba a conexão, e o worker é reiniciado.
   - **Tipo `0x01` (controle):** JSON UTF-8 com o campo `type`.
   - **Tipo `0x02` (áudio):** cabeçalho fixo de 24 bytes (fluxo u8, marcadores u16, sequência i64, posição i64, número de amostras u32), seguido das amostras float32 LE no formato canônico (ADR-007).
3. **Mensagens de controle:**
   - **Apresentação:** o worker envia `hello` primeiro (`protocol`, `worker`, `version`, `capabilities`). Versão diferente de 1 é recusada.
   - **Verificação de vida:** o host manda `ping` com `seq` e espera `pong`.
   - **Requisições:** `request` (`id`, `method`, `params`) é respondida com `result`, `error` (`code`, `message`) ou `cancelled`. O host pode mandar `cancel` (`id`); resultados que chegam depois de um cancelamento são descartados.
   - **Encerramento:** `shutdown` encerra o worker de forma ordenada.
4. **Ciclo de vida (WorkerHost):**
   - estados `Stopped`, `Starting`, `Ready`, `Restarting` e `Faulted`;
   - ping a cada 1 s; **sem resposta em 3 s, o worker é reiniciado**;
   - queda ou travamento geram reinício com espera crescente de 1 s até 30 s; **5 falhas em 2 minutos** levam a `Faulted`, visível na UI (RF-UI-003);
   - ao parar, o host manda `shutdown`, espera 2 s e mata a árvore de processos.
5. **Nenhuma perda silenciosa num reinício (ADR-009):**
   - enquanto o worker está indisponível, o host para de consumir a fila de áudio, que segura até 30 s; o excedente vira um episódio `Overflow` visível;
   - workers com estado (STT) informam até que posição já confirmaram, e o host reenvia o áudio a partir daí;
   - a política de reenvio é implementada no cliente de STT, na etapa D.
6. **Descartados para os workers:**
   - **HTTP loopback:** exige porta e token, não termina junto com o app e é ~5 vezes mais lento. O Ollama continua em HTTP porque é um runtime de terceiros, restrito a loopback (`docs/AI-RUNTIME.md`).
   - **Named pipe:** fica como opção futura, se um worker precisar atender vários clientes ou sobreviver ao app.

## Consequências

- **Um canal por worker**, compartilhado por controle e áudio dos dois fluxos. Isso cabe com folga na vazão medida.
- **Um worker travado na inferência para de ler o stdin**, e o pipe enche. O host escreve num envio dedicado: a captura nunca bloqueia, e a fila limitada absorve a espera.
- **Qualquer `print` em stdout corromperia o protocolo.** O worker Python guarda o stdout binário e redireciona `sys.stdout` para o stderr logo ao iniciar.
- **Se o app for morto à força**, o Windows fecha os handles e o worker recebe fim de arquivo no stdin. Um Job Object com `KILL_ON_JOB_CLOSE` fica como reforço futuro.
- **Os testes de contrato precisam de Python**, então o job .NET do CI passa a sincronizar o ambiente `workers/` com `uv`.
