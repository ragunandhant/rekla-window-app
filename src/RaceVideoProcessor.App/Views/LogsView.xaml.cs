using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace RaceVideoProcessor.App.Views;

public partial class LogsView : UserControl
{
    private INotifyCollectionChanged? _observed;

    public LogsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // Keep the newest row in view, the way a console tail behaves.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_observed is not null || LogGrid.ItemsSource is not INotifyCollectionChanged collection)
            return;
        _observed = collection;
        _observed.CollectionChanged += OnLogsChanged;
        ScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_observed is null)
            return;
        _observed.CollectionChanged -= OnLogsChanged;
        _observed = null;
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && IsVisible)
            ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (LogGrid.Items.Count > 0)
            LogGrid.ScrollIntoView(LogGrid.Items[LogGrid.Items.Count - 1]);
    }
}
