using System.Windows;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        SourceInitialized += (_, _) => RestorePlacement();
    }

    /// <summary>
    /// Reopens where the operator left the window, provided that position is still
    /// on a connected monitor — a saved position from an unplugged second screen
    /// would otherwise put the window somewhere unreachable.
    /// </summary>
    private void RestorePlacement()
    {
        if (_settings.WindowWidth > 0 && _settings.WindowHeight > 0)
        {
            Width = Math.Max(MinWidth, _settings.WindowWidth);
            Height = Math.Max(MinHeight, _settings.WindowHeight);
        }

        if (_settings.WindowLeft is { } savedLeft && _settings.WindowTop is { } savedTop &&
            double.IsFinite(savedLeft) && double.IsFinite(savedTop) &&
            IsOnScreen(savedLeft, savedTop, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = savedLeft;
            Top = savedTop;
        }

        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    /// <summary>Copies the current placement into settings. The caller persists them.</summary>
    public void CapturePlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (bounds.Width > 0 && bounds.Height > 0)
        {
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
        }

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        // Require a usable slice of the title bar to remain visible.
        var titleBar = new Rect(left, top, Math.Min(width, 220), 32);
        return virtualScreen.IntersectsWith(titleBar);
    }
}
