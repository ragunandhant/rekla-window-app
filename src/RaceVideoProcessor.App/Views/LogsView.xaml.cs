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

    // Keep the newest line in view, the way a console tail behaves.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_observed is not null || LogList.ItemsSource is not INotifyCollectionChanged collection)
            return;
        _observed = collection;
        _observed.CollectionChanged += OnLogLinesChanged;
        ScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_observed is null)
            return;
        _observed.CollectionChanged -= OnLogLinesChanged;
        _observed = null;
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }
}
