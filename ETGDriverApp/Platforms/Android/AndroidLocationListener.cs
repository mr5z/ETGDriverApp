using Android.Content;
using Android.Locations;
using Android.OS;
using Android.Runtime;
using ETGDriverApp.Core.Services;
using Java.Lang;
using Java.Util.Functions;
using AndroidLocation = Android.Locations.Location;
using Application = Android.App.Application;
using MauiLocation = Microsoft.Maui.Devices.Sensors.Location;
using Object = Java.Lang.Object;
using PlatformLocationListener = Android.Locations.ILocationListener;
using SessionLocationListener = ETGDriverApp.Core.Services.ILocationListener;

namespace ETGDriverApp;

internal class AndroidLocationListener
    : Object, SessionLocationListener, PlatformLocationListener
{
    private const long MinimumIntervalMs = 5_000;
    private const float MinimumDistanceMeters = 0f;

    private static readonly TimeSpan ForcedFixTimeout = TimeSpan.FromSeconds(10);

    private readonly Context _context = Application.Context;

    private LocationManager? _locationManager;
    private LocationSessionState _state = LocationSessionState.Stopped;

    // distinguishes a requested stop from the OS killing the service
    private bool _stopRequested;

    private EventHandler<MauiLocation>? _locationReceived;
    private EventHandler<SessionEndReason>? _sessionEndedUnexpectedly;

    event EventHandler<MauiLocation> SessionLocationListener.LocationReceived
    {
        add => _locationReceived += value;
        remove => _locationReceived -= value;
    }

    event EventHandler<SessionEndReason> SessionLocationListener.SessionEndedUnexpectedly
    {
        add => _sessionEndedUnexpectedly += value;
        remove => _sessionEndedUnexpectedly -= value;
    }

    LocationSessionState SessionLocationListener.State => _state;

    async Task<bool> SessionLocationListener.RequestPermissionAsync()
    {
        var whenInUse = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();

        if (whenInUse != PermissionStatus.Granted)
            whenInUse = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();

        if (whenInUse != PermissionStatus.Granted)
            return false;

        // ACCESS_BACKGROUND_LOCATION. On Android 11+ this cannot be granted
        // from an in-app prompt, only from the app's Settings page.
        var always = await Permissions.CheckStatusAsync<Permissions.LocationAlways>();

        if (always != PermissionStatus.Granted)
            always = await Permissions.RequestAsync<Permissions.LocationAlways>();

        // the service still runs without notification permission, so this is
        // not part of the result
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var notifications = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();

            if (notifications != PermissionStatus.Granted)
                await Permissions.RequestAsync<Permissions.PostNotifications>();
        }

        return always == PermissionStatus.Granted;
    }

    Task<bool> SessionLocationListener.StartAsync(CancellationToken ct)
    {
        if (_state == LocationSessionState.Running)
            return Task.FromResult(true);

        _locationManager = _context.GetSystemService(Context.LocationService) as LocationManager;

        if (_locationManager is null)
            return Task.FromResult(false);

        // without the service running, Android throttles delivery to a few
        // fixes per hour once the app stops being visible
        if (!StartForegroundService())
            return Task.FromResult(false);

        var provider = SelectProvider(_locationManager);

        if (provider is null)
        {
            StopForegroundService();

            return Task.FromResult(false);
        }

        try
        {
            _locationManager.RequestLocationUpdates(
                provider, MinimumIntervalMs, MinimumDistanceMeters, this, Looper.MainLooper);
        }
        catch (SecurityException)
        {
            StopForegroundService();

            return Task.FromResult(false);
        }

        _stopRequested = false;
        LocationForegroundService.Destroyed += OnForegroundServiceDestroyed;
        _state = LocationSessionState.Running;

        return Task.FromResult(true);
    }

    Task SessionLocationListener.StopAsync(CancellationToken ct)
    {
        if (_state == LocationSessionState.Stopped)
            return Task.CompletedTask;

        _state = LocationSessionState.Stopped;
        _stopRequested = true;

        LocationForegroundService.Destroyed -= OnForegroundServiceDestroyed;

        _locationManager?.RemoveUpdates(this);
        StopForegroundService();

        return Task.CompletedTask;
    }

    async Task<MauiLocation?> SessionLocationListener.GetForcedFixAsync(CancellationToken ct)
    {
        if (_locationManager is null)
            return null;

        var provider = SelectProvider(_locationManager);

        if (provider is null)
            return null;

        var completion = new TaskCompletionSource<MauiLocation?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // GetCurrentLocation is the supported single-fix API from API 30;
        // RequestSingleUpdate was deprecated in the same release
        using var signal = OperatingSystem.IsAndroidVersionAtLeast(30)
            ? new CancellationSignal()
            : null;

        var consumer = new LocationConsumer(location =>
            completion.TrySetResult(location is null ? null : ToMauiLocation(location)));

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                _locationManager.GetCurrentLocation(
                    provider, signal, _context.MainExecutor!, consumer);
            }
            else
            {
#pragma warning disable CA1422, CS0618
                _locationManager.RequestSingleUpdate(provider, consumer, Looper.MainLooper);
#pragma warning restore CA1422, CS0618
            }
        }
        catch (SecurityException)
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = Task.Delay(ForcedFixTimeout, timeoutCts.Token);

        var winner = await Task.WhenAny(completion.Task, timeout);

        if (winner != completion.Task)
        {
            // GetCurrentLocation has a long internal timeout, so an abandoned
            // request must actually be cancelled
            signal?.Cancel();

            if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            {
#pragma warning disable CA1422, CS0618
                _locationManager.RemoveUpdates(consumer);
#pragma warning restore CA1422, CS0618
            }

            var lastKnown = _locationManager.GetLastKnownLocation(provider);

            return lastKnown is null ? null : ToMauiLocation(lastKnown);
        }

        await timeoutCts.CancelAsync();

        var fresh = await completion.Task;

        if (fresh is not null)
            return fresh;

        var fallback = _locationManager.GetLastKnownLocation(provider);

        return fallback is null ? null : ToMauiLocation(fallback);
    }

    void PlatformLocationListener.OnLocationChanged(AndroidLocation location) =>
        _locationReceived?.Invoke(this, ToMauiLocation(location));

    // deprecated on API 29+ but still on the binding
    void PlatformLocationListener.OnStatusChanged(
        string? provider, [GeneratedEnum] Availability status, Bundle? extras)
    {
    }

    void PlatformLocationListener.OnProviderEnabled(string provider)
    {
    }

    void PlatformLocationListener.OnProviderDisabled(string provider)
    {
        // the staleness watchdog handles this as an ordinary signal loss
    }

    private void OnForegroundServiceDestroyed(object? sender, EventArgs e)
    {
        if (_stopRequested)
            return;

        LocationForegroundService.Destroyed -= OnForegroundServiceDestroyed;

        _state = LocationSessionState.Stopped;
        _locationManager?.RemoveUpdates(this);

        _sessionEndedUnexpectedly?.Invoke(this, SessionEndReason.ForegroundServiceStopped);
    }

    private static string? SelectProvider(LocationManager manager)
    {
        var enabled = manager.GetProviders(enabledOnly: true);

        if (enabled?.Contains(LocationManager.GpsProvider) == true)
            return LocationManager.GpsProvider;

        // poor, but the accuracy gate decides what is usable
        if (enabled?.Contains(LocationManager.NetworkProvider) == true)
            return LocationManager.NetworkProvider;

        return null;
    }

    private bool StartForegroundService()
    {
        var intent = new Intent(_context, typeof(LocationForegroundService));

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                _context.StartForegroundService(intent);
            else
                _context.StartService(intent);

            return true;
        }
        catch (IllegalStateException)
        {
            // ForegroundServiceStartNotAllowedException on API 31+ when
            // called with no visible activity
            return false;
        }
    }

    private void StopForegroundService()
    {
        // StopService, not StartService with a stop action: StartService
        // throws from the background on API 26+
        var intent = new Intent(_context, typeof(LocationForegroundService));

        _context.StopService(intent);
    }

    private static MauiLocation ToMauiLocation(AndroidLocation location) => new()
    {
        Latitude = location.Latitude,
        Longitude = location.Longitude,
        Accuracy = location.HasAccuracy ? location.Accuracy : null,
        Altitude = location.HasAltitude ? location.Altitude : null,
        Course = location.HasBearing ? location.Bearing : null,
        Speed = location.HasSpeed ? location.Speed : null,
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(location.Time)
    };

    // GetCurrentLocation delivers through Consumer, RequestSingleUpdate
    // through LocationListener; one class serves both paths
    private class LocationConsumer(Action<AndroidLocation?> onFix)
        : Object, IConsumer, PlatformLocationListener
    {
        void IConsumer.Accept(Object? value) =>
            onFix(value as AndroidLocation);

        void PlatformLocationListener.OnLocationChanged(AndroidLocation location) => onFix(location);

        void PlatformLocationListener.OnStatusChanged(
            string? provider, [GeneratedEnum] Availability status, Bundle? extras)
        {
        }

        void PlatformLocationListener.OnProviderEnabled(string provider)
        {
        }

        void PlatformLocationListener.OnProviderDisabled(string provider) => onFix(null);
    }
}
