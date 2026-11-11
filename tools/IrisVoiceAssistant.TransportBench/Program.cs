using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using IrisVoiceAssistant.TransportBench;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string repo = Bench.RepoRoot();
string python = Path.Combine(repo, "workers", ".venv", "Scripts", "python.exe");
int count = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 5000;

var results = new List<TransportResult>();
foreach (string transport in new[] { "stdio", "pipe", "http" })
{
    await using ITransport client = await Bench.StartAsync(transport, python);
    results.Add(await Bench.MeasureAsync(transport, client, count));
    TransportResult r = results[^1];
    Console.WriteLine($"{transport,-6} frame RTT p50/p95/p99/max {r.Frame.P50:F3}/{r.Frame.P95:F3}/{r.Frame.P99:F3}/{r.Frame.Max:F2} ms | "
        + $"control p50/p95 {r.Control.P50:F3}/{r.Control.P95:F3} ms | pipelined {r.PipelinedFramesPerSecond:N0} frames/s | startup {r.StartupMs:F0} ms");
}

string outDir = Path.Combine(repo, "benchmarks", "results", "transport");
Directory.CreateDirectory(outDir);
string file = Path.Combine(outDir, $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}_transport.json");
await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
{
    benchmark = "transport",
    createdUtc = DateTime.UtcNow,
    os = Environment.OSVersion.VersionString,
    dotnet = Environment.Version.ToString(),
    messages = count,
    frameBytes = Bench.FrameBytes,
    controlBytes = Bench.ControlBytes,
    results,
}, Bench.Json));
Console.WriteLine($"report: {file}");

namespace IrisVoiceAssistant.TransportBench
{
    internal sealed record Percentiles(double P50, double P95, double P99, double Max);

    internal sealed record TransportResult(string Transport, double StartupMs, Percentiles Frame, Percentiles Control, double PipelinedFramesPerSecond);

    internal interface ITransport : IAsyncDisposable
    {
        Task<byte[]> RoundTripAsync(byte[] payload);

        /// <summary>Sends all payloads back to back while a reader receives the echoes; returns when all came back.</summary>
        Task PipelineAsync(IReadOnlyList<byte[]> payloads);
    }

    internal static class Bench
    {
        public const int FrameBytes = 320 * 4; // one canonical 20 ms frame (ADR-007)
        public const int ControlBytes = 96;    // a small JSON control message
        public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IrisVoiceAssistant.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
        }

        public static async Task<ITransport> StartAsync(string transport, string python)
        {
            var watch = Stopwatch.StartNew();
            ITransport client = transport switch
            {
                "stdio" => FramedTransport.Stdio(python),
                "pipe" => await FramedTransport.NamedPipeAsync(python),
                _ => await HttpTransport.StartAsync(python),
            };
            await client.RoundTripAsync(new byte[1]); // first echo = worker ready
            StartupMs[transport] = watch.Elapsed.TotalMilliseconds;
            return client;
        }

        public static readonly Dictionary<string, double> StartupMs = [];

        public static async Task WaitOrKill(Process process, bool kill = false)
        {
            if (kill && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
        }

        public static async Task<TransportResult> MeasureAsync(string transport, ITransport client, int count)
        {
            byte[] frame = RandomNumberGenerator.GetBytes(FrameBytes);
            byte[] control = RandomNumberGenerator.GetBytes(ControlBytes);
            for (int i = 0; i < 500; i++)
            {
                await client.RoundTripAsync(frame); // warm-up (JIT, caches)
            }

            Percentiles frames = await Latency(client, frame, count);
            Percentiles controls = await Latency(client, control, count);
            byte[][] batch = Enumerable.Range(0, count * 4).Select(_ => frame).ToArray();
            var watch = Stopwatch.StartNew();
            await client.PipelineAsync(batch);
            double rate = batch.Length / watch.Elapsed.TotalSeconds;
            return new TransportResult(transport, StartupMs[transport], frames, controls, rate);
        }

        private static async Task<Percentiles> Latency(ITransport client, byte[] payload, int count)
        {
            double[] ms = new double[count];
            for (int i = 0; i < count; i++)
            {
                long start = Stopwatch.GetTimestamp();
                byte[] echo = await client.RoundTripAsync(payload);
                ms[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (echo.Length != payload.Length)
                {
                    throw new InvalidDataException("echo length mismatch");
                }
            }

            Array.Sort(ms);
            return new Percentiles(ms[count / 2], ms[(int)(count * 0.95)], ms[(int)(count * 0.99)], ms[^1]);
        }
    }

    /// <summary>Length-prefixed framing (4-byte LE size + payload) over stdio or a named pipe.</summary>
    internal sealed class FramedTransport : ITransport
    {
        private readonly Process _process;
        private readonly Stream _write;
        private readonly Stream _read;
        private readonly NamedPipeServerStream? _pipe;

        private FramedTransport(Process process, Stream write, Stream read, NamedPipeServerStream? pipe)
        {
            _process = process;
            _write = write;
            _read = read;
            _pipe = pipe;
        }

        public static FramedTransport Stdio(string python)
        {
            Process process = Spawn(python, "stdio", redirectInput: true);
            return new FramedTransport(process, process.StandardInput.BaseStream, process.StandardOutput.BaseStream, null);
        }

        public static async Task<FramedTransport> NamedPipeAsync(string python)
        {
            string name = $"iris-bench-{Guid.NewGuid():N}";
            // CurrentUserOnly: other users cannot open or impersonate the pipe (SECURITY.md).
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Process process = Spawn(python, $"pipe --name {name}", redirectInput: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await pipe.WaitForConnectionAsync(timeout.Token);
            return new FramedTransport(process, pipe, pipe, pipe);
        }

        public async Task<byte[]> RoundTripAsync(byte[] payload)
        {
            await WriteAsync(payload);
            await _write.FlushAsync();
            return await ReadAsync();
        }

        public async Task PipelineAsync(IReadOnlyList<byte[]> payloads)
        {
            Task reader = Task.Run(async () =>
            {
                for (int i = 0; i < payloads.Count; i++)
                {
                    await ReadAsync();
                }
            });
            foreach (byte[] payload in payloads)
            {
                await WriteAsync(payload);
            }

            await _write.FlushAsync();
            await reader;
        }

        private async Task WriteAsync(byte[] payload)
        {
            byte[] message = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(message, payload.Length);
            payload.CopyTo(message, 4);
            await _write.WriteAsync(message);
        }

        private async Task<byte[]> ReadAsync()
        {
            byte[] header = new byte[4];
            await _read.ReadExactlyAsync(header);
            byte[] payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
            await _read.ReadExactlyAsync(payload);
            return payload;
        }

        public async ValueTask DisposeAsync()
        {
            if (_pipe is null)
            {
                _write.Close(); // EOF on stdin ends the worker
            }
            else
            {
                await _pipe.DisposeAsync();
            }

            await Bench.WaitOrKill(_process);
        }

        internal static Process Spawn(string python, string arguments, bool redirectInput)
        {
            var info = new ProcessStartInfo(python, $"-I -m iris_workers.bench.transport_echo {arguments}")
            {
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process process = Process.Start(info) ?? throw new InvalidOperationException("failed to start worker");
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Console.Error.WriteLine($"worker: {e.Data}");
                }
            };
            process.BeginErrorReadLine();
            return process;
        }
    }

    internal sealed class HttpTransport : ITransport
    {
        private readonly Process _process;
        private readonly HttpClient _http;

        private HttpTransport(Process process, HttpClient http)
        {
            _process = process;
            _http = http;
        }

        public static async Task<HttpTransport> StartAsync(string python)
        {
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            Process process = FramedTransport.Spawn(python, $"http --token {token}", redirectInput: false);
            string? line = await process.StandardOutput.ReadLineAsync();
            if (line is null || !line.StartsWith("PORT ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"unexpected worker output: {line}");
            }

            var handler = new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1, PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
            var http = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{line[5..]}/") };
            http.DefaultRequestHeaders.Add("X-Iris-Token", token);
            http.DefaultRequestVersion = HttpVersion.Version11;
            return new HttpTransport(process, http);
        }

        public async Task<byte[]> RoundTripAsync(byte[] payload)
        {
            using var content = new ByteArrayContent(payload);
            using HttpResponseMessage response = await _http.PostAsync(new Uri("echo", UriKind.Relative), content);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }

        /// <summary>HTTP/1.1 has no pipelining in HttpClient: requests go one after another on the connection.</summary>
        public async Task PipelineAsync(IReadOnlyList<byte[]> payloads)
        {
            foreach (byte[] payload in payloads)
            {
                await RoundTripAsync(payload);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await Bench.WaitOrKill(_process, kill: true);
        }
    }
}
