using ETGDriverApp.Core;
using ETGDriverApp.Core.Services;
using ETGDriverApp.Pages;
using ETGDriverApp.Services;
using ETGDriverApp.ViewModels;
using Microsoft.Extensions.Logging;
using Nkraft.MvvmEssentials;

namespace ETGDriverApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureMvvmEssentials(registry =>
            {
                registry.MapPage<MainPage, MainViewModel>(isInitial: true);
            })
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.UseMauiMaps();

        builder.Configuration
            .AddPackagedJsonFile("appsettings.json", optional: false);

        //builder.Services.AddSingleton(Accelerometer.Default);
        //builder.Services.AddSingleton(Gyroscope.Default);
        // simulated
        builder.Services.AddSingleton<SimulatedVehicleState>();
        builder.Services.AddSingleton<IAccelerometer, SimulatedAccelerometer>();
        builder.Services.AddSingleton<IGyroscope, SimulatedGyroscope>();

        builder.Services.AddSingleton<SimulatedLocationListener>();
        builder.Services.AddSingleton<ILocationListener>(sp => sp.GetRequiredService<SimulatedLocationListener>());

        // binds the "Positioning" section and validates it on startup
        builder.Services.AddDriverPositioning(builder.Configuration);

#if ANDROID
        //builder.Services.AddSingleton<ILocationListener, AndroidLocationListener>();
#elif IOS
        builder.Services.AddSingleton<ILocationListener, AppleLocationListener>();
#endif
        builder.Services.AddDiscoveredAppStartup();

        return builder.Build();
    }
}