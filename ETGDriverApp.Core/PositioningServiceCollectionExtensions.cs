using ETGDriverApp.Core.Models;
using ETGDriverApp.Core.Services;
using ETGDriverApp.Core.Services.DeadReckoning;
using ETGDriverApp.Core.Services.DeadReckoning.Sensors;
using ETGDriverApp.Core.Services.Filters;
using ETGDriverApp.Core.Services.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.Devices.Sensors;

namespace ETGDriverApp.Core;

internal static class PositioningServiceCollectionExtensions
{
    // Everything is a singleton: the pipeline, state machine and DR estimator
    // hold running state, and every subsystem must read the same instance.
    public static IServiceCollection AddDriverPositioning(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IAccuracyGate>(_ => new AccuracyGate());
        services.AddSingleton<IPlausibilityGate>(_ => new PlausibilityGate());
        services.AddSingleton<IMapMatcher, NoOpMapMatcher>();
        
        // TODO delete
        // services.AddSingleton<IPositionBlender, UncertaintyWeightedBlender>();
        // services.AddSingleton<IPositionSmoother, InverseVarianceSmoother>();
        // services.AddSingleton<ISpeedSanityChecker>(_ => new SpeedSanityChecker());

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

        // IJobStatusWriter must come from the head project
        services.AddSingleton<IJobSiteMonitor, JobSiteMonitor>();

        return services;
    }
}
