using ETGDriverApp.Core.Models;
using MauiLocation = Microsoft.Maui.Devices.Sensors.Location;

namespace ETGDriverApp.Core.Services;

public enum LocationSessionState { Stopped, Running }

public enum SessionEndReason
{
    ForegroundServiceStopped,
    PermissionRevoked
}

// one subscription for the whole session, foreground and background alike
public interface ILocationListener
{
    event EventHandler<MauiLocation> LocationReceived;

    // fires when a running session ends without StopAsync being called;
    // State is already Stopped by then
    event EventHandler<SessionEndReason> SessionEndedUnexpectedly;

    LocationSessionState State { get; }

    // returns whether BACKGROUND permission was granted; false still leaves
    // a usable foreground-only session
    Task<bool> RequestPermissionAsync();

    // must be called while the app is visible: Android 12+ forbids starting
    // a foreground service from the background
    Task<bool> StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    Task<MauiLocation?> GetForcedFixAsync(CancellationToken ct = default);
}

public class PositionFeed(
    ILocationListener listener,
    IPositionFilterPipeline pipeline,
    IPositionStore store)
{
    private bool _started;

    public event EventHandler<Exception>? BridgeFaulted;

    public bool IsRunning => _started;

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (_started)
            return true;

        listener.LocationReceived += OnLocationReceived;

        var started = await listener.StartAsync(ct);

        if (!started)
            listener.LocationReceived -= OnLocationReceived;

        _started = started;

        return started;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_started)
            return;

        _started = false;

        listener.LocationReceived -= OnLocationReceived;

        await listener.StopAsync(ct);
    }

    public async Task ForcedFixAsync(CancellationToken ct = default)
    {
        var location = await listener.GetForcedFixAsync(ct);

        if (location is not null)
            await IngestAndPersistAsync(ToSample(location, PositionSourceType.Forced), ct);
    }

    internal static RawPositionSample ToSample(MauiLocation location, PositionSourceType source) =>
        new(location.Latitude, location.Longitude, location.Accuracy ?? double.MaxValue,
            location.Timestamp, source);

    private async void OnLocationReceived(object? sender, MauiLocation location)
    {
        try
        {
            await IngestAndPersistAsync(ToSample(location, PositionSourceType.Continuous));
        }
        catch (Exception ex)
        {
            BridgeFaulted?.Invoke(this, ex);
        }
    }

    private async Task IngestAndPersistAsync(RawPositionSample sample, CancellationToken ct = default)
    {
        var accepted = await pipeline.IngestAsync(sample, ct);

        // persistence exists for process death, not backgrounding
        if (accepted is not null)
            await store.PersistAsync(accepted, ct);
    }
}
