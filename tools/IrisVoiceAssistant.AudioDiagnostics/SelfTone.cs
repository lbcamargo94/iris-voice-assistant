using System.Runtime.InteropServices;

namespace IrisVoiceAssistant.AudioDiagnostics;

/// <summary>
/// Plays a test tone from this process (winmm PlaySound), to verify ADR-007 layer A: process loopback that
/// excludes this process tree must not capture it, while endpoint loopback must.
/// </summary>
internal sealed partial class SelfTone : IDisposable
{
    public const double Frequency = 1234.0;
    public const double Amplitude = 0.25; // -12 dBFS
    private const int SampleRate = 44_100;
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;
    private const uint SndLoop = 0x0008;

    private readonly byte[] _wav = BuildWav();
    private GCHandle _pin;
    private bool _playing;

    public void Start()
    {
        if (_playing)
        {
            return;
        }

        _pin = GCHandle.Alloc(_wav, GCHandleType.Pinned); // PlaySound reads the buffer asynchronously
        if (!PlaySound(_pin.AddrOfPinnedObject(), IntPtr.Zero, SndMemory | SndAsync | SndLoop | SndNoDefault))
        {
            _pin.Free();
            throw new InvalidOperationException("PlaySound failed to start the test tone.");
        }

        _playing = true;
    }

    public void Stop()
    {
        if (!_playing)
        {
            return;
        }

        _ = PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        _pin.Free();
        _playing = false;
    }

    public void Dispose() => Stop();

    /// <summary>One second of 16-bit mono sine: 1234 whole cycles, so looping is seamless.</summary>
    private static byte[] BuildWav()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + (SampleRate * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(SampleRate * 2);
        for (int n = 0; n < SampleRate; n++)
        {
            writer.Write((short)(Amplitude * 32767 * Math.Sin(2 * Math.PI * Frequency * n / SampleRate)));
        }

        writer.Flush();
        return stream.ToArray();
    }

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
