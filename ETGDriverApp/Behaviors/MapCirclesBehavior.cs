using System.Collections.Specialized;
using ETGDriverApp.ViewModels;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using Map = Microsoft.Maui.Controls.Maps.Map;

namespace ETGDriverApp.Behaviors;

// Map.MapElements is not bindable, so the collection is mirrored by hand
public static class MapCirclesBehavior
{
    public static readonly BindableProperty ItemsSourceProperty =
        BindableProperty.CreateAttached("ItemsSource", typeof(IEnumerable<MapCircleViewModel>),
            typeof(MapCirclesBehavior), null, propertyChanged: OnItemsSourceChanged);

    public static IEnumerable<MapCircleViewModel>? GetItemsSource(BindableObject o) =>
        (IEnumerable<MapCircleViewModel>?)o.GetValue(ItemsSourceProperty);

    public static void SetItemsSource(BindableObject o, IEnumerable<MapCircleViewModel>? v) =>
        o.SetValue(ItemsSourceProperty, v);

    private static void OnItemsSourceChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not Map map)
            return;

        if (oldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= Handler(map);

        if (newValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += Handler(map);

        Sync(map);
    }

    private static NotifyCollectionChangedEventHandler Handler(Map map) =>
        (_, _) => MainThread.BeginInvokeOnMainThread(() => Sync(map));

    private static void Sync(Map map)
    {
        foreach (var element in map.MapElements.OfType<Circle>().ToList())
            map.MapElements.Remove(element);

        if (GetItemsSource(map) is not { } items)
            return;

        foreach (var item in items)
        {
            map.MapElements.Add(new Circle
            {
                Center = item.Center,
                Radius = Distance.FromMeters(item.RadiusMeters),
                StrokeColor = item.StrokeColor,
                StrokeWidth = 2,
                FillColor = item.FillColor
            });
        }
    }
}