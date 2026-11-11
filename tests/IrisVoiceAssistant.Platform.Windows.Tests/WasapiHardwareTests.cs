using System.Diagnostics;
using IrisVoiceAssistant.Core.Audio;
using IrisVoiceAssistant.Platform.Windows;

namespace IrisVoiceAssistant.Platform.Windows.Tests;

/// <summary>A fact that is skipped when the machine has no active endpoint of the given flow (e.g. CI runners).</summary>
public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute(AudioDeviceFlow flow)
    {
        Flow = flow;
        try
        {
            if (new WasapiDeviceEnumerator().GetDefault(flow) is null)
            {
                Skip = $"No active {flow} audio device on this machine.";
            }
        }
        catch (Exception error) when (error is AudioInteropException or AudioDeviceLostException
            or System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            Skip = $"Audio service unavailable: {error.Message}";
        }
    }

    public AudioDeviceFlow Flow { get; }
}

/// <summary>Opens real endpoints for a short capture. Captured audio is only counted, never stored.</summary>
public sealed class WasapiHardwareTests
{
    private sealed class CountingSink : IRawAudioSink
    {
        public long Frames;
        public int Packets;
        public int Idles;

        public void OnAudio(ReadOnlySpan<byte> data, int frameCount, long timestamp, RawBlockMarkers flags)
        {
            Frames += frameCount;
            Packets++;
        }

        public void OnIdle(long timestamp) => Idles++;
    }

    private static CountingSink RunFor(WasapiCaptureSource source, TimeSpan duration)
    {
        var sink = new CountingSink();
        using var cancel = new CancellationTokenSource(duration);
        Assert.Throws<OperationCanceledException>(() => source.Run(sink, cancel.Token));
        return sink;
    }

    [HardwareFact(AudioDeviceFlow.Capture)]
    public void Default_microphone_delivers_audio_at_its_native_rate()
    {
        using WasapiCaptureSource mic = WasapiCaptureSource.OpenMicrophone();
        Assert.Equal(AudioStreamKind.Microphone, mic.Stream);
        var watch = Stopwatch.StartNew();
        CountingSink sink = RunFor(mic, TimeSpan.FromMilliseconds(600));
        double seconds = (double)sink.Frames / mic.Format.SampleRate;
        Assert.InRange(seconds, 0.3, watch.Elapsed.TotalSeconds + 0.1); // a microphone always streams, even silence
    }

    [HardwareFact(AudioDeviceFlow.Render)]
    public void Endpoint_loopback_opens_and_runs_even_when_nothing_plays()
    {
        using WasapiCaptureSource loopback = WasapiCaptureSource.OpenEndpointLoopback();
        CountingSink sink = RunFor(loopback, TimeSpan.FromMilliseconds(400));
        Assert.True(sink.Packets > 0 || sink.Idles > 0, "the loop must report audio or idleness, not block");
    }

    [HardwareFact(AudioDeviceFlow.Render)]
    public void Process_loopback_activates_excluding_this_process()
    {
        if (!WasapiCaptureSource.IsProcessLoopbackSupported)
        {
            return; // older Windows: OpenSystemAudio falls back with a warning (covered below)
        }

        using WasapiCaptureSource system = WasapiCaptureSource.OpenProcessLoopback(Environment.ProcessId);
        Assert.Equal(AudioStreamKind.SystemAudio, system.Stream);
        Assert.Contains("excludes this app", system.Description, StringComparison.Ordinal);
        Assert.Equal(RawSampleType.IeeeFloat, system.Format.SampleType);
        CountingSink sink = RunFor(system, TimeSpan.FromMilliseconds(400));
        Assert.True(sink.Packets > 0 || sink.Idles > 0);
    }

    [HardwareFact(AudioDeviceFlow.Render)]
    public void System_audio_prefers_process_loopback_and_explains_any_fallback()
    {
        (WasapiCaptureSource source, string? warning) = WasapiCaptureSource.OpenSystemAudio(Environment.ProcessId);
        using (source)
        {
            if (source.Description.StartsWith("process loopback", StringComparison.Ordinal))
            {
                Assert.Null(warning);
            }
            else
            {
                Assert.NotNull(warning); // never silently lose the isolation (ADR-007)
            }
        }
    }

    [HardwareFact(AudioDeviceFlow.Render)]
    public void Enumerator_lists_endpoints_with_one_default_per_flow()
    {
        var enumerator = new WasapiDeviceEnumerator();
        foreach (AudioDeviceFlow flow in new[] { AudioDeviceFlow.Render, AudioDeviceFlow.Capture })
        {
            IReadOnlyList<AudioDeviceInfo> devices = enumerator.GetDevices(flow);
            Assert.All(devices, d => Assert.Equal(flow, d.Flow));
            Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
            Assert.True(devices.Count(d => d.IsDefault) <= 1);
        }
    }

    [HardwareFact(AudioDeviceFlow.Capture)]
    public void Unknown_device_id_is_reported_as_device_lost()
    {
        Assert.Throws<AudioDeviceLostException>(() => WasapiCaptureSource.OpenMicrophone("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}"));
    }
}
