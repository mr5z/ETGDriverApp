using ETGDriverApp.Core.Configuration;
using ETGDriverApp.Core.Diagnostics;
using ETGDriverApp.Core.Models;
using ETGDriverApp.Core.Services;
using ETGDriverApp.Core.Services.DeadReckoning;
using ETGDriverApp.Core.Services.DeadReckoning.Sensors;
using ETGDriverApp.Core.Services.Filters;
using ETGDriverApp.Core.Services.Sites;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ETGDriverApp.Core;

public static class PositioningServiceCollectionExtensions
{
    // Everything is a singleton: the pipeline, state machine and DR estimator
    // hold running state, and every subsystem must read the same instance.
    //
    // `configuration` is the app's root IConfiguration. The "Positioning"
    // section is bound with ValidateOnStart, so a bad appsettings.json fails
    // at startup rather than in the field. When the remote source is added,
    // it becomes another IConfigurationProvider layered on top of this same
    // root - no service in here changes.
    public static IServiceCollection AddDriverPositioning(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<PositioningOptions>()
            .Bind(configuration.GetSection(PositioningOptions.SectionName))
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<PositioningOptions>, PositioningOptionsValidator>());

        services.AddSingleton<IPositioningDiagnostics, PositioningDiagnostics>();

        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IAccuracyGate, AccuracyGate>();
        services.AddSingleton<IPlausibilityGate, PlausibilityGate>();
        services.AddSingleton<IAdmissionGate, AdmissionGate>();
        services.AddSingleton<IMapMatcher, NoOpMapMatcher>();

        services.AddSingleton<DeviceOrientationReference>();
        services.AddSingleton<IPeriodicScheduler, PeriodicScheduler>();
        services.AddSingleton<IDeadReckoningSensorInput, AccelerometerMotionStateSource>();
        services.AddSingleton<IDeadReckoningSensorInput, GyroscopeHeadingRateSource>();
        services.AddSingleton<IDeadReckoningEstimator, HeadingIntegrationDeadReckoningEstimator>();

        services.AddSingleton<IPositionStateMachine, PositionStateMachine>();
        services.AddSingleton<IPositionFilterPipeline, PositionFilterPipeline>();
        services.AddSingleton<IPositionStore, InMemoryPositionStore>();
        services.AddSingleton<ISessionRecovery, SessionRecovery>();

        services.AddSingleton<PositionFeed>();
        services.AddSingleton<DeadReckoningFeed>();
        services.AddSingleton<IStalenessWatchdog, StalenessWatchdog>();
        services.AddSingleton<IPositioningSession, PositioningSession>();

        // one instance under both faces
        services.AddSingleton<GeofenceEvaluator>();
        services.AddSingleton<IGeofenceEvaluator>(sp => sp.GetRequiredService<GeofenceEvaluator>());
        services.AddSingleton<IGeofenceRegistry>(sp => sp.GetRequiredService<GeofenceEvaluator>());

        // Same two-face pattern: the concrete type is registered so the
        // container owns its lifetime and disposes it, since the monitor
        // holds a subscription to the evaluator.
        services.AddSingleton<SiteArrivalMonitor>();
        services.AddSingleton<ISiteArrivalMonitor>(sp => sp.GetRequiredService<SiteArrivalMonitor>());

        return services;
    }
}
