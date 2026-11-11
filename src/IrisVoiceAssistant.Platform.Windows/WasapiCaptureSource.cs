using System.Diagnostics;
using System.Runtime.InteropServices;
using IrisVoiceAssistant.Core.Audio;
using IrisVoiceAssistant.Platform.Windows.Interop;

namespace IrisVoiceAssistant.Platform.Windows;

/// <summary>
/// A shared-mode, event-driven WASAPI capture stream: microphone, endpoint loopback or process loopback.
/// Opening (the static factories) initializes the client, so configuration errors surface before capture starts.
/// </summary>
public sealed class WasapiCaptureSource : IAudioCaptureSource
{
    private const long BufferDuration = 2_000_000; // 200 ms in 100-ns units
    private static readonly TimeSpan IdleWake = TimeSpan.FromMilliseconds(100);
    private static readonly Guid IidAudioClient = typeof(IAudioClient).GUID;
    private static readonly Guid IidAudioCaptureClient = typeof(IAudioCaptureClient).GUID;

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly AutoResetEvent _ready = new(false);
    private bool _disposed;

    private WasapiCaptureSource(AudioStreamKind stream, IAudioClient client, RawAudioFormat format, IntPtr formatPointer, uint streamFlags, string description)
    {
        Stream = stream;
        Format = format;
        Description = description;
        _client = client;
        try
        {
            ComHelpers.Check(
                client.Initialize(AudioClientConstants.SharedMode, streamFlags | AudioClientConstants.StreamFlagsEventCallback, BufferDuration, 0, formatPointer, IntPtr.Zero),
                "IAudioClient.Initialize");
            ComHelpers.Check(client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            Guid iid = IidAudioCaptureClient;
            ComHelpers.Check(client.GetService(ref iid, out object service), "GetService(IAudioCaptureClient)");
            _capture = (IAudioCaptureClient)service;
        }
        catch
        {
            ComHelpers.Release(client);
            _ready.Dispose();
            throw;
        }
    }

    public AudioStreamKind Stream { get; }

    public RawAudioFormat Format { get; }

    public string Description { get; }

    /// <summary>Process loopback needs Windows 10 build 20348 or later (Windows 11).</summary>
    public static bool IsProcessLoopbackSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    /// <summary>Opens a microphone (default capture device when <paramref name="deviceId"/> is null).</summary>
    public static WasapiCaptureSource OpenMicrophone(string? deviceId = null) =>
        OpenEndpoint(AudioStreamKind.Microphone, EDataFlow.Capture, deviceId, extraFlags: 0, "microphone");

    /// <summary>Opens loopback of a whole render endpoint. Includes this app's own audio: no isolation.</summary>
    public static WasapiCaptureSource OpenEndpointLoopback(string? renderDeviceId = null) =>
        OpenEndpoint(AudioStreamKind.SystemAudio, EDataFlow.Render, renderDeviceId, AudioClientConstants.StreamFlagsLoopback, "endpoint loopback (includes this app)");

    /// <summary>
    /// Opens process loopback of everything rendered on the system except <paramref name="processId"/> and its
    /// children (ADR-007, layer A), or only that tree when <paramref name="includeOnly"/> is true (layer A').
    /// </summary>
    public static WasapiCaptureSource OpenProcessLoopback(int processId, bool includeOnly = false)
    {
        if (!IsProcessLoopbackSupported)
        {
            throw new PlatformNotSupportedException("Process loopback requires Windows 10 build 20348 or later.");
        }

        IAudioClient client = ActivateProcessLoopback((uint)processId, includeOnly);

        // Process loopback has no mix format; use the default render device's rate and channels as float32.
        (int rate, int channels) = DefaultRenderLayout();
        IntPtr format = ComHelpers.AllocFloatFormat(rate, channels);
        try
        {
            string description = includeOnly ? $"process loopback (only process {processId})" : "process loopback (excludes this app)";
            return new WasapiCaptureSource(
                AudioStreamKind.SystemAudio,
                client,
                new RawAudioFormat(rate, channels, RawSampleType.IeeeFloat),
                format,
                AudioClientConstants.StreamFlagsLoopback | AudioClientConstants.StreamFlagsAutoConvertPcm | AudioClientConstants.StreamFlagsSrcDefaultQuality,
                description);
        }
        finally
        {
            Marshal.FreeHGlobal(format);
        }
    }

    /// <summary>
    /// System audio with isolation when possible: process loopback excluding <paramref name="excludeProcessId"/>,
    /// falling back to endpoint loopback with an explicit warning (ADR-007).
    /// </summary>
    public static (WasapiCaptureSource Source, string? Warning) OpenSystemAudio(int excludeProcessId)
    {
        if (IsProcessLoopbackSupported)
        {
            try
            {
                return (OpenProcessLoopback(excludeProcessId), null);
            }
            catch (Exception error) when (error is AudioInteropException or AudioDeviceLostException or TimeoutException)
            {
                return (OpenEndpointLoopback(), $"Process loopback failed ({error.Message}); capturing the whole output device, including this app's own audio.");
            }
        }

        return (OpenEndpointLoopback(), "Process loopback is not available on this Windows version; capturing the whole output device, including this app's own audio.");
    }

    public void Run(IRawAudioSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ComHelpers.Check(_client.Start(), "IAudioClient.Start");
        try
        {
            WaitHandle[] handles = [_ready, cancellationToken.WaitHandle];
            int blockAlign = Format.BlockAlign;
            while (true)
            {
                int signaled = WaitHandle.WaitAny(handles, IdleWake);
                cancellationToken.ThrowIfCancellationRequested();
                bool delivered = false;
                while (true)
                {
                    ComHelpers.Check(_capture.GetNextPacketSize(out uint packet), "GetNextPacketSize");
                    if (packet == 0)
                    {
                        break;
                    }

                    int hr = _capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out ulong qpc);
                    if (hr == HResult.AUDCLNT_S_BUFFER_EMPTY)
                    {
                        break;
                    }

                    ComHelpers.Check(hr, "GetBuffer");
                    try
                    {
                        long timestamp = (flags & AudioClientConstants.BufferFlagsTimestampError) != 0 || qpc == 0
                            ? Stopwatch.GetTimestamp()
                            : (long)(qpc * (double)Stopwatch.Frequency / 10_000_000);
                        RawBlockMarkers markers = ((flags & AudioClientConstants.BufferFlagsSilent) != 0 ? RawBlockMarkers.Silent : 0)
                            | ((flags & AudioClientConstants.BufferFlagsDataDiscontinuity) != 0 ? RawBlockMarkers.Discontinuity : 0);
                        unsafe
                        {
                            var span = new ReadOnlySpan<byte>((void*)data, checked((int)frames * blockAlign));
                            sink.OnAudio(span, (int)frames, timestamp, markers);
                        }
                    }
                    finally
                    {
                        ComHelpers.Check(_capture.ReleaseBuffer(frames), "ReleaseBuffer");
                    }

                    delivered = true;
                }

                if (!delivered && signaled == WaitHandle.WaitTimeout)
                {
                    sink.OnIdle(Stopwatch.GetTimestamp());
                }
            }
        }
        finally
        {
            _ = _client.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ComHelpers.Release(_capture);
        ComHelpers.Release(_client);
        _ready.Dispose();
    }

    private static WasapiCaptureSource OpenEndpoint(AudioStreamKind stream, EDataFlow flow, string? deviceId, uint extraFlags, string kind)
    {
        IMMDeviceEnumerator enumerator = WasapiDeviceEnumerator.CreateEnumerator();
        IMMDevice? device = null;
        IntPtr mixFormat = IntPtr.Zero;
        object? clientObject = null;
        try
        {
            int hr = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(flow, ERole.Console, out device)
                : enumerator.GetDevice(deviceId, out device);
            ComHelpers.Check(hr, deviceId is null ? $"default {flow} device" : $"device {deviceId}");
            Guid iid = IidAudioClient;
            ComHelpers.Check(device.Activate(ref iid, AudioClientConstants.ClsctxAll, IntPtr.Zero, out clientObject), "IMMDevice.Activate");
            var client = (IAudioClient)clientObject;
            ComHelpers.Check(client.GetMixFormat(out mixFormat), "GetMixFormat");
            RawAudioFormat format = ComHelpers.ToRawFormat(mixFormat)
                ?? throw new NotSupportedException("The device mix format is not PCM or IEEE float.");
            string name = DeviceName(device);
            var source = new WasapiCaptureSource(stream, client, format, mixFormat, extraFlags, $"{kind}: {name}");
            clientObject = null; // owned by the source now
            return source;
        }
        finally
        {
            if (mixFormat != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(mixFormat);
            }

            ComHelpers.Release(clientObject);
            ComHelpers.Release(device);
            ComHelpers.Release(enumerator);
        }
    }

    private static string DeviceName(IMMDevice device)
    {
        ComHelpers.Check(device.GetId(out string id), "GetId");
        AudioDeviceInfo? info = new WasapiDeviceEnumerator().GetDevices(AudioDeviceFlow.Render)
            .Concat(new WasapiDeviceEnumerator().GetDevices(AudioDeviceFlow.Capture))
            .FirstOrDefault(d => d.Id == id);
        return info?.Name ?? id;
    }

    private static (int Rate, int Channels) DefaultRenderLayout()
    {
        try
        {
            using WasapiCaptureSource probe = OpenEndpointLoopback();
            return (probe.Format.SampleRate, Math.Min(probe.Format.Channels, 2));
        }
        catch (Exception error) when (error is AudioInteropException or AudioDeviceLostException or NotSupportedException)
        {
            return (48_000, 2);
        }
    }

    private static IAudioClient ActivateProcessLoopback(uint processId, bool includeOnly)
    {
        var parameters = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationParams.TypeProcessLoopback,
            TargetProcessId = processId,
            ProcessLoopbackMode = includeOnly
                ? AudioClientActivationParams.LoopbackModeIncludeTargetProcessTree
                : AudioClientActivationParams.LoopbackModeExcludeTargetProcessTree,
        };
        int size = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr blob = Marshal.AllocHGlobal(size);
        var handler = new ActivationHandler();
        IntPtr handlerPointer = IntPtr.Zero;
        IntPtr operationPointer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(parameters, blob, fDeleteOld: false);
            PropVariant variant = PropVariant.Blob(blob, size);
            handlerPointer = Marshal.GetComInterfaceForObject(handler, typeof(IActivateAudioInterfaceCompletionHandler));
            Guid iid = IidAudioClient;
            ComHelpers.Check(
                NativeMethods.ActivateAudioInterfaceAsync(AudioClientConstants.VirtualAudioDeviceProcessLoopback, ref iid, ref variant, handlerPointer, out operationPointer),
                "ActivateAudioInterfaceAsync");
            if (!handler.Completed.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Process loopback activation did not complete in 5 s.");
            }

            var operation = (IActivateAudioInterfaceAsyncOperation)Marshal.GetObjectForIUnknown(operationPointer);
            try
            {
                ComHelpers.Check(operation.GetActivateResult(out int activateResult, out object activated), "GetActivateResult");
                ComHelpers.Check(activateResult, "process loopback activation");
                return (IAudioClient)activated;
            }
            finally
            {
                ComHelpers.Release(operation);
            }
        }
        finally
        {
            if (operationPointer != IntPtr.Zero)
            {
                Marshal.Release(operationPointer);
            }

            if (handlerPointer != IntPtr.Zero)
            {
                Marshal.Release(handlerPointer);
            }

            handler.Completed.Dispose();
            Marshal.FreeHGlobal(blob);
        }
    }

    [ComVisible(true)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Completed { get; } = new(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            Completed.Set();
            return HResult.S_OK;
        }
    }
}
