using System.Globalization;
using System.Text;
using IrisVoiceAssistant.Application.Live;
using IrisVoiceAssistant.Core.Audio;
using IrisVoiceAssistant.Core.Transcripts;
using IrisVoiceAssistant.LiveDemo;
using IrisVoiceAssistant.Platform.Windows;
using IrisVoiceAssistant.WorkerHost;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;
DemoOptions options = DemoOptions.Parse(args);
return options.Command switch
{
    "delete" => Demo.Delete(options),
    "help" => Demo.Help(),
    _ => await Demo.RunAsync(options),
};

namespace IrisVoiceAssistant.LiveDemo
{
    internal sealed record DemoOptions(string Command, bool Microphone, bool TranslateMicrophone, double Minutes, string Database, bool Yes)
    {
        public static DemoOptions Parse(string[] args)
        {
            var options = new DemoOptions("run", false, false, 0, Path.Combine(Demo.RepoRoot(), "data", "iris-live.db"), false);
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                options = args[i] switch
                {
                    "delete" or "help" => options with { Command = args[i] },
                    "--mic" => options with { Microphone = true },
                    "--translate-mic" => options with { Microphone = true, TranslateMicrophone = true },
                    "--minutes" => options with { Minutes = double.Parse(Next(), CultureInfo.InvariantCulture) },
                    "--db" => options with { Database = Path.GetFullPath(Next()) },
                    "--yes" => options with { Yes = true },
                    _ => throw new ArgumentException($"unknown option {args[i]} (try: iris-live help)"),
                };
            }

            return options;
        }
    }

    internal static class Demo
    {
        private static readonly object Console_ = new();
        private static string _partial = string.Empty;

        public static int Help()
        {
            Console.WriteLine("""
                iris-live [--mic] [--translate-mic] [--minutes N] [--db PATH]
                    Transcreve em inglês e traduz para PT-BR, ao vivo, o áudio do sistema (Participantes).
                    --mic            também transcreve o seu microfone (Você), sem traduzir
                    --translate-mic  também traduz o microfone
                    --minutes N      para sozinho depois de N minutos (senão: tecla q)
                iris-live delete [--db PATH] --yes
                    Apaga o banco local com o texto das sessões da demo.

                Requer: cd workers; uv sync --group ai   (modelos: benchmarks\fetch-*.ps1 e run-stt.ps1 -AllowDownload)
                """);
            return 0;
        }

        public static int Delete(DemoOptions options)
        {
            string[] files = [options.Database, options.Database + "-wal", options.Database + "-shm"];
            if (!options.Yes)
            {
                Console.WriteLine($"Isto apaga {options.Database} (todas as sessões da demo). Repita com --yes para confirmar.");
                return 1;
            }

            foreach (string file in files.Where(File.Exists))
            {
                File.Delete(file);
                Console.WriteLine($"apagado: {file}");
            }

            return 0;
        }

        public static async Task<int> RunAsync(DemoOptions options)
        {
            string python = Path.Combine(RepoRoot(), "workers", ".venv", "Scripts", "python.exe");
            if (!File.Exists(python))
            {
                Console.WriteLine("Ambiente dos workers não encontrado. Rode: cd workers; uv sync --group ai");
                return 2;
            }

            Console.WriteLine("Iris — demo ao vivo (POC-001D)");
            Console.WriteLine("Aviso: esta sessão captura o áudio do sistema" + (options.Microphone ? " e o seu microfone" : "") +
                              ". Tenha autorização dos participantes. Nenhum áudio é gravado; o texto fica em:");
            Console.WriteLine($"  {options.Database}   (apagar: iris-live delete --yes)");
            Console.WriteLine("Teclas: q = parar, p = pausar/retomar.\n");

            AudioDeviceInfo? output = new WasapiDeviceEnumerator().GetDefault(AudioDeviceFlow.Render);
            if (output?.MayEchoIntoMicrophone == true && options.Microphone)
            {
                Console.WriteLine($"Atenção: a saída '{output.Name}' é {output.FormFactor}; prefira fones para o microfone não captar o som do PC.\n");
            }

            WorkerOptions Worker(string module) => new()
            {
                Name = module,
                FileName = python,
                Arguments = ["-I", "-m", $"iris_workers.workers.{module}"],
                HelloTimeout = TimeSpan.FromSeconds(30),
            };

            string? isolationWarning = null;
            var sources = new List<CaptureSourceSpec>
            {
                new(AudioStreamKind.SystemAudio, () =>
                {
                    (WasapiCaptureSource source, string? warning) = WasapiCaptureSource.OpenSystemAudio(Environment.ProcessId);
                    isolationWarning ??= warning;
                    return source;
                }),
            };
            if (options.Microphone)
            {
                sources.Add(new(AudioStreamKind.Microphone, () => WasapiCaptureSource.OpenMicrophone()));
            }

            await using var session = new LiveSession(new LiveSessionOptions
            {
                DatabasePath = options.Database,
                Speech = Worker("speech"),
                Translation = Worker("translation"),
                TranslateMicrophone = options.TranslateMicrophone,
                Title = $"Demo {DateTime.Now:yyyy-MM-dd HH:mm}",
            });
            session.Status += message => Write($"· {message}", ConsoleColor.DarkGray);
            session.PartialUpdated += (stream, text) => ShowPartial(stream, text);
            session.SegmentFinalized += (segment, latency) =>
                Write($"[{Label(segment.Stream)} {Clock(segment.Start)}] {segment.Text}  ({latency.TotalSeconds:F1} s{(segment.Forced ? ", cortada" : "")})", ConsoleColor.White);
            session.SegmentTranslated += (segment, text, _) => Write($"{new string(' ', Label(segment.Stream).Length + 9)}{text}", ConsoleColor.Cyan);
            session.GapRecorded += gap => Write($"· lacuna {gap.Cause} em {Clock(gap.Start)} ({gap.Duration.TotalSeconds:F1} s){(gap.IsLoss ? " — áudio perdido" : "")}", ConsoleColor.Yellow);
            session.WorkerStateChanged += (worker, state, reason) =>
            {
                if (state is WorkerState.Restarting or WorkerState.Faulted)
                {
                    Write($"· worker {worker}: {state} ({reason})", ConsoleColor.Yellow);
                }
            };
            session.CaptureStateChanged += (stream, state, error) =>
            {
                if (state is AudioCaptureState.Recovering or AudioCaptureState.Faulted or AudioCaptureState.Paused)
                {
                    Write($"· captura {Label(stream)}: {state}{(error is null ? "" : $" ({error})")}", ConsoleColor.Yellow);
                }
            };

            try
            {
                await session.StartAsync(sources);
            }
            catch (Exception error) when (error is WorkerUnavailableException or TimeoutException or OperationCanceledException or AudioDeviceLostException)
            {
                Console.WriteLine($"Não foi possível iniciar: {error.Message}");
                Console.WriteLine("Verifique: cd workers; uv sync --group ai  e os modelos em models\\ (benchmarks\\README.md).");
                return 3;
            }

            if (isolationWarning is not null)
            {
                Write($"Atenção: {isolationWarning}", ConsoleColor.Yellow);
            }

            Write("Pronto. Toque um vídeo ou entre numa reunião em inglês.\n", ConsoleColor.Green);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool paused = false;
            while (options.Minutes <= 0 || watch.Elapsed.TotalMinutes < options.Minutes)
            {
                await Task.Delay(100);
                if (Console.IsInputRedirected || !Console.KeyAvailable)
                {
                    continue;
                }

                char key = char.ToLowerInvariant(Console.ReadKey(intercept: true).KeyChar);
                if (key == 'q')
                {
                    break;
                }

                if (key == 'p')
                {
                    paused = !paused;
                    if (paused)
                    {
                        session.Pause();
                    }
                    else
                    {
                        session.Resume();
                    }

                    Write(paused ? "· pausado (p para retomar)" : "· retomado", ConsoleColor.DarkGray);
                }
            }

            Write("\nParando...", ConsoleColor.DarkGray);
            LiveSessionSummary summary = await session.StopAsync();
            ShowPartial(AudioStreamKind.SystemAudio, string.Empty);
            Console.WriteLine();
            Console.WriteLine($"Frases finais: {summary.Finals}   traduções: {summary.Translations}   falhas de tradução: {summary.TranslationFailures}");
            Console.WriteLine($"Latência desde o fim da fala — frase final: {Percentiles(summary.FinalLatencies)}   tradução: {Percentiles(summary.TranslationLatencies)}");
            Console.WriteLine($"Reinícios do STT: {summary.SpeechRestarts}   frases duplicadas descartadas: {summary.DuplicateFinalsDropped}");
            Console.WriteLine($"Lacunas: {(summary.Gaps.Count == 0 ? "nenhuma" : string.Join(", ", summary.Gaps.Select(g => $"{g.Cause} {g.Duration.TotalSeconds:F1}s")))}");
            foreach (AudioStreamCounters c in summary.Counters)
            {
                Console.WriteLine($"Conciliação {Label(c.Stream)}: {(c.Reconciles ? "OK" : "FALHOU")} (descartadas {c.DroppedSamples} amostras)");
            }

            Console.WriteLine($"Texto salvo em {session.DatabasePath}");
            return summary.Counters.All(c => c.Reconciles) ? 0 : 1;
        }

        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IrisVoiceAssistant.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? Directory.GetCurrentDirectory();
        }

        private static string Label(AudioStreamKind stream) => stream == AudioStreamKind.SystemAudio ? "Participantes" : "Você";

        private static string Clock(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

        private static string Percentiles(IReadOnlyList<TimeSpan> values)
        {
            if (values.Count == 0)
            {
                return "-";
            }

            double[] s = [.. values.Select(v => v.TotalSeconds).Order()];
            return $"p50 {s[s.Length / 2]:F2} s / p95 {s[Math.Min(s.Length - 1, (int)(s.Length * 0.95))]:F2} s";
        }

        private static void ShowPartial(AudioStreamKind stream, string text)
        {
            lock (Console_)
            {
                ClearPartial();
                _partial = string.IsNullOrWhiteSpace(text) ? string.Empty : $"  … {Label(stream)}: {text}";
                DrawPartial();
            }
        }

        private static void Write(string line, ConsoleColor color)
        {
            lock (Console_)
            {
                ClearPartial();
                ConsoleColor previous = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(line);
                Console.ForegroundColor = previous;
                DrawPartial();
            }
        }

        private static void ClearPartial()
        {
            if (_partial.Length > 0 && !Console.IsOutputRedirected)
            {
                Console.Write("\r" + new string(' ', Math.Max(0, Console.WindowWidth - 1)) + "\r");
            }
        }

        private static void DrawPartial()
        {
            if (_partial.Length == 0 || Console.IsOutputRedirected)
            {
                return;
            }

            int width = Math.Max(20, Console.WindowWidth - 1);
            string shown = _partial.Length > width ? "  …" + _partial[^(width - 3)..] : _partial;
            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(shown);
            Console.ForegroundColor = previous;
        }
    }
}
