namespace ETGDriverApp.Core.Diagnostics;

// Replaces HeadingIntegrationDeadReckoningEstimator's `public static event
// EventHandler<string> Trace`. A static event on a singleton-scoped service
// kept every subscriber alive for the life of the process, could not be
// scoped per session or per test, and interpolated its message string on
// every tick whether or not anything was listening.
//
// IsEnabled is checked by callers before building the message, so the
// formatting cost disappears entirely when nobody is attached.
public interface IPositioningDiagnostics
{
    bool IsEnabled { get; }

    void Trace(string category, string message);
}

public sealed class NullPositioningDiagnostics : IPositioningDiagnostics
{
    public static readonly NullPositioningDiagnostics Instance = new();

    public bool IsEnabled => false;

    public void Trace(string category, string message)
    {
    }
}

// Registered as a singleton. The head project subscribes to Traced; with no
// subscribers IsEnabled is false and callers skip formatting.
public sealed class PositioningDiagnostics : IPositioningDiagnostics
{
    private EventHandler<PositioningTraceEventArgs>? _traced;

    public event EventHandler<PositioningTraceEventArgs> Traced
    {
        add
        {
            _traced += value;
            Volatile.Write(ref _hasSubscribers, 1);
        }
        remove
        {
            _traced -= value;

            if (_traced is null)
                Volatile.Write(ref _hasSubscribers, 0);
        }
    }

    private int _hasSubscribers;

    public bool IsEnabled => Volatile.Read(ref _hasSubscribers) != 0;

    public void Trace(string category, string message) =>
        _traced?.Invoke(this, new PositioningTraceEventArgs(category, message, DateTimeOffset.UtcNow));
}

public sealed record PositioningTraceEventArgs(
    string Category, string Message, DateTimeOffset At);
