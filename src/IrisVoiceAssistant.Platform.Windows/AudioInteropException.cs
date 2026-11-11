using System.Runtime.InteropServices;

namespace IrisVoiceAssistant.Platform.Windows;

/// <summary>A Core Audio call failed with an HRESULT that does not mean "device lost".</summary>
public sealed class AudioInteropException : Exception
{
    public AudioInteropException()
    {
    }

    public AudioInteropException(string message)
        : base(message)
    {
    }

    public AudioInteropException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AudioInteropException(string operation, int hresult)
        : base($"{operation} failed (0x{hresult:X8})", Marshal.GetExceptionForHR(hresult))
    {
        HResult = hresult;
        Operation = operation;
    }

    public string? Operation { get; }
}
