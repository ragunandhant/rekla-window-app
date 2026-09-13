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
    /// Places the window so that all of it is reachable.
    ///
    /// The work area is measured in device-independent pixels, so Windows display
    /// scaling shrinks it: a 1920x1080 screen at 125% is 1536x864 DIP, and at 150%
    /// a 1366x768 laptop is only 910x512. A window sized for a desktop and then
    /// centred on such a screen hangs off the top edge, taking the top bar with
    /// it. Everything here is therefore clamped to the current work area first.
    /// </summary>
    private void RestorePlacement()
    {
        var work = SystemParameters.WorkArea;

        var width = Math.Max(MinWidth, _settings.WindowWidth > 0 ? _settings.WindowWidth : Width);
        var height = Math.Max(MinHeight, _settings.WindowHeight > 0 ? _settings.WindowHeight : Height);

        // Never larger than the space actually available.
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);
        Width = width;
        Height = height;

        if (_settings.WindowLeft is { } savedLeft && _settings.WindowTop is { } savedTop &&
            double.IsFinite(savedLeft) && double.IsFinite(savedTop))
        {
            // Pull a remembered position back inside the work area rather than
            // discarding it: a monitor may have been unplugged or rescaled.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Clamp(savedLeft, work.Left, Math.Max(work.Left, work.Right - width));
            Top = Math.Clamp(savedTop, work.Top, Math.Max(work.Top, work.Bottom - height));
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = work.Left + Math.Max(0, (work.Width - width) / 2);
            Top = work.Top + Math.Max(0, (work.Height - height) / 2);
        }

        // On a screen too small for the layout, start maximised: the operator gets
        // every pixel there is, and nothing sits off an edge.
        if (_settings.WindowMaximized || work.Width < 1180 || work.Height < 740)
            WindowState = WindowState.Maximized;
    }

    /// <summary>Copies the current placement into settings. The caller persists them.</summary>
    public void CapturePlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (bounds.Width > 0 && bounds.Height > 0 &&
            double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top))
        {
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
        }

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
    }
}
