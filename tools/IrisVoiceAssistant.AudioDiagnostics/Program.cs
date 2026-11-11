using System.Diagnostics;
using System.Globalization;
using IrisVoiceAssistant.Audio;
using IrisVoiceAssistant.Audio.Pipeline;
using IrisVoiceAssistant.AudioDiagnostics;
using IrisVoiceAssistant.Core.Audio;
using IrisVoiceAssistant.Platform.Windows;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string command = args.Length > 0 ? args[0] : "help";
return command switch
{
    "devices" => Commands.Devices(),
    "capture" => Commands.Capture(Options.Parse(args.Skip(1).ToArray())),
    _ => Commands.Help(),
};

namespace IrisVoiceAssistant.AudioDiagnostics
{
    internal sealed record Options(
        double Seconds,
        string System,
        string Mic,
        bool Wav,
        double? PauseAt,
        double? ResumeAt,
        int SlowConsumerMs,
        int QueueFrames,
        bool SelfTone)
    {
        public static Options Parse(string[] args)
        {
            var options = new Options(30, "process", "default", false, null, null, 0, 1500, false);
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                options = args[i] switch
                {
                    "--seconds" => options with { Seconds = double.Parse(Next(), CultureInfo.InvariantCulture) },
                    "--system" => options with { System = Next() },
                    "--mic" => options with { Mic = Next() },
                    "--wav" => options with { Wav = true },
                    "--pause-at" => options with { PauseAt = double.Parse(Next(), CultureInfo.InvariantCulture) },
                    "--resume-at" => options with { ResumeAt = double.Parse(Next(), CultureInfo.InvariantCulture) },
                    "--slow-consumer-ms" => options with { SlowConsumerMs = int.Parse(Next(), CultureInfo.InvariantCulture) },
                    "--queue-frames" => options with { QueueFrames = int.Parse(Next(), CultureInfo.InvariantCulture) },
                    "--self-tone" => options with { SelfTone = true },
                    _ => throw new ArgumentException($"unknown option {args[i]}"),
                };
            }

            return options;
        }
    }

    internal static class Commands
    {
        public static int Help()
        {
            Console.WriteLine("""
                iris-audio-diag devices
                iris-audio-diag capture [--seconds 30] [--system process|endpoint|off] [--mic default|off|<device id>]
                                        [--wav] [--pause-at S --resume-at S] [--slow-consumer-ms N] [--queue-frames N]
                                        [--self-tone]   (this process plays a 1234 Hz tone from 3 s to 8 s: isolation test)

                Capture is visible and stops on its own after --seconds (or 'q'); 'p' pauses/resumes.
                Nothing is written to disk unless --wav is given (then: tmp/audio-diagnostics/, git-ignored).
                """);
            return 0;
        }

        public static int Devices()
        {
            var enumerator = new WasapiDeviceEnumerator();
            foreach (AudioDeviceFlow flow in new[] { AudioDeviceFlow.Render, AudioDeviceFlow.Capture })
            {
                Console.WriteLine(flow == AudioDeviceFlow.Render ? "Output devices:" : "Input devices:");
                foreach (AudioDeviceInfo device in enumerator.GetDevices(flow))
                {
                    string echo = device.MayEchoIntoMicrophone ? "  [speaker: Iris may echo into the mic; headphones recommended]" : string.Empty;
                    Console.WriteLine($"  {(device.IsDefault ? "*" : " ")} {device.Name} ({device.FormFactor}){echo}");
                    Console.WriteLine($"      {device.Id}");
                }
            }

            Console.WriteLine($"Process loopback supported: {WasapiCaptureSource.IsProcessLoopbackSupported}");
            return 0;
        }

        public static int Capture(Options options)
        {
            Console.WriteLine("Capturing audio for diagnostics. Press 'q' to stop, 'p' to pause/resume.");
            Console.WriteLine(options.Wav
                ? "WAV files will be written to tmp/audio-diagnostics/ (temporary test files)."
                : "Nothing is written to disk.");

            long sessionStart = Stopwatch.GetTimestamp();
            var runners = new List<CaptureStreamRunner>();
            string? warning = null;
            if (options.System != "off")
            {
                runners.Add(new CaptureStreamRunner(AudioStreamKind.SystemAudio, () =>
                {
                    if (options.System == "endpoint")
                    {
                        return WasapiCaptureSource.OpenEndpointLoopback();
                    }

                    (WasapiCaptureSource source, string? w) = WasapiCaptureSource.OpenSystemAudio(Environment.ProcessId);
                    warning ??= w;
                    return source;
                }, sessionStart, queueCapacityFrames: options.QueueFrames));
            }

            if (options.Mic != "off")
            {
                string? id = options.Mic == "default" ? null : options.Mic;
                runners.Add(new CaptureStreamRunner(AudioStreamKind.Microphone, () => WasapiCaptureSource.OpenMicrophone(id), sessionStart, queueCapacityFrames: options.QueueFrames));
            }

            AudioDeviceInfo? output = new WasapiDeviceEnumerator().GetDefault(AudioDeviceFlow.Render);
            if (output?.MayEchoIntoMicrophone == true)
            {
                Console.WriteLine($"Warning: output '{output.Name}' is {output.FormFactor}; Iris speaking could echo into the microphone. Headphones are recommended.");
            }

            foreach (CaptureStreamRunner runner in runners)
            {
                runner.StateChanged += (stream, state, error) =>
                    Console.WriteLine($"[{Elapsed(sessionStart):F1}s] {stream}: {state}{(error is null ? "" : $" ({error})")}");
                runner.Start();
                runner.Pipeline.GapRecorded += gap =>
                {
                    if (gap.Cause != AudioGapCause.Overflow) // overflow episodes grow frame by frame; shown in the status line and summary
                    {
                        Console.WriteLine($"[{Elapsed(sessionStart):F1}s] {gap.Stream}: gap {gap.Cause} at {gap.Start.TotalSeconds:F2}s, {gap.Duration.TotalMilliseconds:F0} ms");
                    }
                };
                Console.WriteLine($"{runner.Stream}: {runner.SourceDescription}, native {runner.Pipeline.Format.SampleRate} Hz x{runner.Pipeline.Format.Channels} {runner.Pipeline.Format.SampleType}");
            }

            if (warning is not null)
            {
                Console.WriteLine($"Warning: {warning}");
            }

            using var stop = new CancellationTokenSource();
            var consumers = runners.Select(r => new StreamConsumer(r.Pipeline, options)).ToList();
            List<Task> tasks = consumers.Select(c => Task.Run(() => c.RunAsync(stop.Token))).ToList();

            bool pausedByScript = false, resumedByScript = false;
            using var tone = new SelfTone();
            var watch = Stopwatch.StartNew();
            double nextStatus = 1;
            while (watch.Elapsed.TotalSeconds < options.Seconds)
            {
                Thread.Sleep(options.SelfTone ? 100 : 1000);
                double t = watch.Elapsed.TotalSeconds;
                if (options.SelfTone)
                {
                    double session = Elapsed(sessionStart);
                    if (session >= StreamConsumer.ToneOn && session < StreamConsumer.ToneOff)
                    {
                        tone.Start();
                    }
                    else
                    {
                        tone.Stop();
                    }

                    if (t < nextStatus)
                    {
                        continue; // status once per second
                    }

                    nextStatus += 1;
                }

                if (options.PauseAt is double pauseAt && !pausedByScript && t >= pauseAt)
                {
                    runners.ForEach(r => r.Pause());
                    pausedByScript = true;
                }

                if (options.ResumeAt is double resumeAt && pausedByScript && !resumedByScript && t >= resumeAt)
                {
                    runners.ForEach(r => r.Resume());
                    resumedByScript = true;
                }

                if (!Console.IsInputRedirected && Console.KeyAvailable)
                {
                    char key = char.ToLowerInvariant(Console.ReadKey(intercept: true).KeyChar);
                    if (key == 'q')
                    {
                        break;
                    }

                    if (key == 'p')
                    {
                        runners.ForEach(r => (r.State == AudioCaptureState.Paused ? (Action)r.Resume : r.Pause)());
                    }
                }

                Console.WriteLine($"[{t:F0}s] " + string.Join(" | ", consumers.Select(c => c.StatusLine())));
            }

            tone.Stop();
            runners.ForEach(r => r.Stop());
            Task.WaitAll([.. tasks], TimeSpan.FromSeconds(10));
            stop.Cancel();

            bool ok = true;
            Console.WriteLine();
            foreach (StreamConsumer consumer in consumers)
            {
                ok &= consumer.Summary();
            }

            runners.ForEach(r => r.Dispose());
            return ok ? 0 : 1;
        }

        private static double Elapsed(long start) => Stopwatch.GetElapsedTime(start).TotalSeconds;
    }

    internal sealed class StreamConsumer(CaptureStreamPipeline pipeline, Options options)
    {
        private readonly object _gate = new();
        private readonly List<string> _sequenceProblems = [];
        private float _peak;
        private double _sumSquares;
        private int _count;
        private long _frames;
        private long _expectedSequence;
        private long _expectedPosition;
        private string? _wavPath;
        private readonly List<double> _toneOnAmplitudes = [];
        private readonly List<double> _toneOffAmplitudes = [];
        private double _blockRe;
        private double _blockIm;
        private int _blockCount;
        private long _blockStart = -1;

        public const double ToneOn = 3.0;
        public const double ToneOff = 8.0;
        private const int ToneBlock = 4_000; // 0.25 s: 4 Hz bins, 1234 Hz falls on a bin

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            WavWriter? wav = null;
            if (options.Wav)
            {
                string dir = Path.Combine(RepoRoot(), "tmp", "audio-diagnostics");
                Directory.CreateDirectory(dir);
                _wavPath = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}_{pipeline.Stream}.wav");
                wav = new WavWriter(_wavPath, CanonicalAudioFormat.SampleRate);
            }

            try
            {
                await foreach (AudioFrame frame in pipeline.ReadAllAsync(cancellationToken))
                {
                    Check(frame);
                    if (options.SelfTone && pipeline.Stream == AudioStreamKind.SystemAudio)
                    {
                        AnalyzeTone(frame);
                    }
                    lock (_gate)
                    {
                        foreach (float s in frame.Samples.Span)
                        {
                            _peak = Math.Max(_peak, Math.Abs(s));
                            _sumSquares += s * (double)s;
                        }

                        _count += frame.Samples.Length;
                        _frames++;
                    }

                    wav?.Write(frame.Samples.Span);
                    if (options.SlowConsumerMs > 0)
                    {
                        await Task.Delay(options.SlowConsumerMs, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                wav?.Dispose();
            }
        }

        public string StatusLine()
        {
            AudioStreamCounters c = pipeline.Counters;
            double peak, rms;
            lock (_gate)
            {
                peak = _peak <= 0 ? double.NegativeInfinity : 20 * Math.Log10(_peak);
                rms = _count == 0 || _sumSquares <= 0 ? double.NegativeInfinity : 20 * Math.Log10(Math.Sqrt(_sumSquares / _count));
                _peak = 0;
                _sumSquares = 0;
                _count = 0;
            }

            string name = pipeline.Stream == AudioStreamKind.SystemAudio ? "system" : "mic";
            return $"{name} peak {Db(peak)} rms {Db(rms)} cap {c.CapturedSamples / 16000.0:F1}s fill {c.FilledSilenceSamples / 16000.0:F1}s drop {c.DroppedSamples} gaps {c.Gaps}";
        }

        public bool Summary()
        {
            AudioStreamCounters c = pipeline.Counters;
            Console.WriteLine($"{pipeline.Stream}:");
            Console.WriteLine($"  frames read {_frames}, captured {c.CapturedSamples} samples, filled silence {c.FilledSilenceSamples}, processed {c.ProcessedSamples}, dropped {c.DroppedSamples}, queued {c.QueuedSamples}, not captured {c.NotCapturedSamples}");
            Console.WriteLine($"  max clock drift {pipeline.MaxClockDrift.TotalMilliseconds:F1} ms");
            foreach (AudioGap gap in pipeline.Gaps)
            {
                string loss = gap.IsLoss ? $" (LOSS: {gap.Missing.TotalMilliseconds:F0} ms of audio dropped)" : "";
                Console.WriteLine($"  gap {gap.Cause} at {gap.Start.TotalSeconds:F2}s spanning {gap.Duration.TotalMilliseconds:F0} ms{loss}");
            }

            foreach (string problem in _sequenceProblems.Take(5))
            {
                Console.WriteLine($"  SEQUENCE PROBLEM: {problem}");
            }

            if (options.SelfTone && pipeline.Stream == AudioStreamKind.SystemAudio)
            {
                double on = Mean(_toneOnAmplitudes), off = Mean(_toneOffAmplitudes);
                double delta = ToDb(on) - ToDb(off);
                Console.WriteLine($"  self tone {SelfTone.Frequency:F0} Hz: while playing {ToDb(on):F1} dBFS, otherwise {ToDb(off):F1} dBFS (difference {delta:F1} dB; played at {ToDb(SelfTone.Amplitude):F1} dBFS)");
                Console.WriteLine(delta < 6
                    ? "  ISOLATION: this app's own audio was NOT captured (ADR-007 layer A holds)"
                    : "  ISOLATION: this app's own audio WAS captured (expected only for endpoint loopback)");
            }

            bool ok = c.Reconciles && _sequenceProblems.Count == 0;
            Console.WriteLine($"  reconciliation: {(c.Reconciles ? "OK" : "FAILED")}, sequence: {(_sequenceProblems.Count == 0 ? "contiguous" : "BROKEN")}");
            if (_wavPath is not null)
            {
                Console.WriteLine($"  wav: {_wavPath}");
            }

            return ok;
        }

        private void Check(AudioFrame frame)
        {
            // Overflow drops are visible as gaps; anything else that breaks continuity is a bug.
            IReadOnlyList<AudioGap> gaps = pipeline.Gaps;
            if (frame.Sequence != _expectedSequence && !gaps.Any(g => g.Cause == AudioGapCause.Overflow))
            {
                _sequenceProblems.Add($"expected sequence {_expectedSequence}, got {frame.Sequence}");
            }

            if (frame.StreamPosition != _expectedPosition
                && !gaps.Any(g => g.StartPosition + g.LengthSamples == frame.StreamPosition))
            {
                _sequenceProblems.Add($"position jump {_expectedPosition} -> {frame.StreamPosition} without a gap");
            }

            _expectedSequence = frame.Sequence + 1;
            _expectedPosition = frame.StreamPosition + frame.Samples.Length;
        }

        private void AnalyzeTone(AudioFrame frame)
        {
            ReadOnlySpan<float> samples = frame.Samples.Span;
            for (int i = 0; i < samples.Length; i++)
            {
                long n = frame.StreamPosition + i;
                if (_blockStart < 0)
                {
                    _blockStart = n;
                }

                double phase = 2 * Math.PI * SelfTone.Frequency * n / CanonicalAudioFormat.SampleRate;
                _blockRe += samples[i] * Math.Cos(phase);
                _blockIm -= samples[i] * Math.Sin(phase);
                if (++_blockCount == ToneBlock)
                {
                    double amplitude = 2 * Math.Sqrt((_blockRe * _blockRe) + (_blockIm * _blockIm)) / ToneBlock;
                    double start = (double)_blockStart / CanonicalAudioFormat.SampleRate;
                    double end = start + ((double)ToneBlock / CanonicalAudioFormat.SampleRate);
                    if (start >= ToneOn + 0.5 && end <= ToneOff)
                    {
                        _toneOnAmplitudes.Add(amplitude); // skip the first 0.5 s: playback start-up latency
                    }
                    else if (start >= ToneOff + 0.5 || end <= ToneOn)
                    {
                        _toneOffAmplitudes.Add(amplitude);
                    }

                    _blockRe = _blockIm = 0;
                    _blockCount = 0;
                    _blockStart = -1;
                }
            }
        }

        private static double Mean(List<double> values) => values.Count == 0 ? 0 : values.Average();

        private static double ToDb(double amplitude) => amplitude <= 0 ? double.NegativeInfinity : 20 * Math.Log10(amplitude);

        private static string Db(double value) => double.IsNegativeInfinity(value) ? " -inf" : $"{value,5:F1}";

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IrisVoiceAssistant.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? Directory.GetCurrentDirectory();
        }
    }

    /// <summary>16-bit PCM mono WAV, header patched on dispose.</summary>
    internal sealed class WavWriter : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private long _samples;

        public WavWriter(string path, int sampleRate)
        {
            Path = path;
            _stream = File.Create(path);
            _writer = new BinaryWriter(_stream);
            _writer.Write("RIFF"u8);
            _writer.Write(0);
            _writer.Write("WAVEfmt "u8);
            _writer.Write(16);
            _writer.Write((short)1);
            _writer.Write((short)1);
            _writer.Write(sampleRate);
            _writer.Write(sampleRate * 2);
            _writer.Write((short)2);
            _writer.Write((short)16);
            _writer.Write("data"u8);
            _writer.Write(0);
        }

        public string Path { get; }

        public void Write(ReadOnlySpan<float> samples)
        {
            foreach (float s in samples)
            {
                _writer.Write((short)Math.Clamp(s * 32767f, -32768f, 32767f));
            }

            _samples += samples.Length;
        }

        public void Dispose()
        {
            _writer.Flush();
            _stream.Position = 4;
            _writer.Write((int)(36 + (_samples * 2)));
            _stream.Position = 40;
            _writer.Write((int)(_samples * 2));
            _writer.Dispose();
        }
    }
}
