using Microsoft.Maui.Maps;
using Map = Microsoft.Maui.Controls.Maps.Map;
using MauiLocation = Microsoft.Maui.Devices.Sensors.Location;

namespace ETGDriverApp.Behaviors;

public static class MapRegionBehavior
{
    public static readonly BindableProperty CenterProperty =
        BindableProperty.CreateAttached("Center", typeof(MauiLocation), typeof(MapRegionBehavior),
            null, propertyChanged: OnChanged);

    public static readonly BindableProperty RadiusMetersProperty =
        BindableProperty.CreateAttached("RadiusMeters", typeof(double), typeof(MapRegionBehavior),
            1000d, propertyChanged: OnChanged);

    public static MauiLocation? GetCenter(BindableObject o) => (MauiLocation?)o.GetValue(CenterProperty);

    public static void SetCenter(BindableObject o, MauiLocation? v) => o.SetValue(CenterProperty, v);

    public static double GetRadiusMeters(BindableObject o) => (double)o.GetValue(RadiusMetersProperty);

    public static void SetRadiusMeters(BindableObject o, double v) => o.SetValue(RadiusMetersProperty, v);

    private static void OnChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is Map map && GetCenter(map) is { } center)
            map.MoveToRegion(MapSpan.FromCenterAndRadius(center, Distance.FromMeters(GetRadiusMeters(map))));
    }
}