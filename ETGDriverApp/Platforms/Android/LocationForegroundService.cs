using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;

namespace ETGDriverApp;

// Holds the process alive with a persistent notification so Android will
// deliver location to a non-visible app. It does not subscribe to location
// itself. Session-scoped, not app-scoped.
[Service(
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeLocation)]
internal class LocationForegroundService : Service
{
    public const string ChannelId = "etg_driver_location";
    public const int NotificationId = 4711;

    // also fires when the user stops the service from the API 33+ task
    // manager or an OEM battery manager kills it
    public static event EventHandler? Destroyed;

    // placeholders; replace with localized strings and a real icon
    private const string ChannelName = "Trip tracking";
    private const string NotificationTitle = "Trip in progress";
    private const string NotificationText = "Recording your route.";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(
        Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateChannel();
        StartForeground(NotificationId, BuildNotification());

        // NotSticky: an OS-restarted service would re-post the notification
        // without re-subscribing the listener, showing tracking that is not
        // happening. Recovery goes through ISessionRecovery instead.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        // some OEM builds leave the notification behind otherwise
        StopForeground(StopForegroundFlags.Remove);

        base.OnDestroy();

        Destroyed?.Invoke(this, EventArgs.Empty);
    }

    private void CreateChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return;

        var manager = (NotificationManager?)GetSystemService(NotificationService);

        if (manager is null)
            return;

        var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
        {
            LockscreenVisibility = NotificationVisibility.Public
        };

        manager.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification()
    {
        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle(NotificationTitle)!
            .SetContentText(NotificationText)!
            .SetSmallIcon(Android.Resource.Drawable.IcMenuMyLocation)!
            .SetOngoing(true)!
            .SetPriority((int)NotificationPriority.Low)!
            .SetCategory(NotificationCompat.CategoryService)!;

        // No stop action: ending a shift must go through the app so the
        // backend hears about it.
        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName!);

        if (launch is not null)
        {
            launch.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);

            var pending = PendingIntent.GetActivity(
                this, 0, launch,
                OperatingSystem.IsAndroidVersionAtLeast(31)
                    ? PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent
                    : PendingIntentFlags.UpdateCurrent);

            builder.SetContentIntent(pending);
        }

        return builder.Build()!;
    }
}
