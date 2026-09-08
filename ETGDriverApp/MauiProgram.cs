using ETGDriverApp.Core;
using ETGDriverApp.Core.Services;
using ETGDriverApp.Core.Services.Jobs;
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

        builder.Services.AddSingleton<SimulatedLocationListener>();
        builder.Services.AddSingleton<ILocationListener>(sp => sp.GetRequiredService<SimulatedLocationListener>());
        builder.Services.AddSingleton<NoOpJobStatusWriter>();
        builder.Services.AddSingleton<IJobStatusWriter>(sp => sp.GetRequiredService<NoOpJobStatusWriter>());
        builder.Services.AddDriverPositioning();
        
#if ANDROID
        //builder.Services.AddSingleton<ILocationListener, AndroidLocationListener>();
#elif IOS
        builder.Services.AddSingleton<ILocationListener, AppleLocationListener>();
#endif
        builder.Services.AddDiscoveredAppStartup();

        return builder.Build();
    }
}