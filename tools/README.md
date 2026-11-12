# Ferramentas

## iris-audio-diag (POC-001B)

Diagnóstico da captura de áudio no Windows. A captura é visível, iniciada pelo usuário, e termina sozinha. Nada é gravado em disco sem `--wav`; com ele, os arquivos vão para `tmp/audio-diagnostics/`, que fica fora do git.

```powershell
dotnet build tools\IrisVoiceAssistant.AudioDiagnostics -c Release
$diag = "tools\IrisVoiceAssistant.AudioDiagnostics\bin\Release\net10.0-windows\iris-audio-diag.exe"
& $diag devices                                          # dispositivos, tipo de saída, aviso de caixa de som
& $diag capture --seconds 20 --wav                       # sistema (process loopback) + microfone
& $diag capture --seconds 12 --mic off --self-tone       # isolamento: o tom do próprio app não pode aparecer
& $diag capture --seconds 12 --mic off --system endpoint --self-tone   # controle: aqui o tom deve aparecer
& $diag capture --seconds 30 --pause-at 10 --resume-at 15
& $diag capture --seconds 8 --mic off --slow-consumer-ms 40 --queue-frames 50   # sobrecarga forçada
```

Durante a captura, `p` pausa e retoma, e `q` encerra. O resumo final mostra contadores, lacunas, deriva do relógio, conciliação ("capturadas = processadas + descartadas + na fila") e continuidade da sequência. O código de saída é 0 só quando a conciliação fecha e a sequência é contínua.

## iris-transport-bench (POC-001C)

Compara transportes entre o app e um worker de eco Python (`iris_workers.bench.transport_echo`): stdio, named pipe com `CurrentUserOnly` e HTTP loopback com token. Para cada um, mede a ida e volta de blocos de 20 ms e de mensagens de controle, e a vazão com pipelining. O resultado embasou o ADR-014; o relatório fica em `benchmarks/results/transport/`.

```powershell
dotnet build tools\IrisVoiceAssistant.TransportBench -c Release
tools\IrisVoiceAssistant.TransportBench\bin\Release\net10.0-windows\iris-transport-bench.exe 5000
```

## iris-live (POC-001D): demo ao vivo

Transcreve em inglês e traduz para PT-BR, ao vivo, o áudio do sistema (Participantes). Com `--mic`, transcreve também o seu microfone (Você), sem traduzir. A cada frase mostra a latência desde o fim da fala; ao parar, mostra o resumo com latências p50/p95, lacunas e conciliação.

```powershell
cd workers; $env:UV_CACHE_DIR = "..\.cache\uv"; uv sync --group ai; cd ..   # uma vez
# modelos: benchmarks\run-stt.ps1 -AllowDownload (Whisper) e benchmarks\fetch-translation-models.ps1
dotnet build tools\IrisVoiceAssistant.LiveDemo -c Release
$live = "tools\IrisVoiceAssistant.LiveDemo\bin\Release\net10.0-windows\iris-live.exe"
& $live                 # q para parar, p para pausar
& $live --mic           # também o microfone
& $live delete --yes    # apaga o texto salvo (data\iris-live.db)
```

- **Na abertura**, mostra um aviso de consentimento.
- **Nenhum áudio é gravado.** O texto fica em `data\iris-live.db`, fora do git.
- **Primeira partida mais lenta:** carregar os modelos leva até ~1 minuto lendo do HD. Isso inclui a conferência do SHA-256, que só é refeita quando o arquivo muda.
