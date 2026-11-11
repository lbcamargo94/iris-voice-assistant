namespace IrisVoiceAssistant.WorkerHost;

/// <summary>Worker lifecycle (ADR-014): shown in the UI next to capture state (RF-UI-003).</summary>
public enum WorkerState
{
    Stopped = 0,
    Starting = 1,
    Ready = 2,
    Restarting = 3,
    Faulted = 4,
}

public sealed record WorkerOptions
{
    public required string Name { get; init; }

    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>No pong for this long means the worker is unresponsive and is restarted.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>This many failures within <see cref="FailureWindow"/> move the worker to <see cref="WorkerState.Faulted"/>.</summary>
    public int MaxFailures { get; init; } = 5;

    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>What the worker announced in its <c>hello</c>.</summary>
public sealed record WorkerHello(string Worker, string Version, int Protocol, IReadOnlyList<string> Capabilities);

/// <summary>The worker is not running right now (starting, restarting, faulted or stopped).</summary>
public sealed class WorkerUnavailableException : Exception
{
    public WorkerUnavailableException()
    {
    }

    public WorkerUnavailableException(string message)
        : base(message)
    {
    }

    public WorkerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The worker answered a request with an <c>error</c> message.</summary>
public sealed class WorkerRequestException : Exception
{
    public WorkerRequestException()
    {
    }

    public WorkerRequestException(string message)
        : base(message)
    {
    }

    public WorkerRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WorkerRequestException(string code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public string? Code { get; }
}
