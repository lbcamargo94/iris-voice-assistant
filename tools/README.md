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
