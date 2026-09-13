using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using RaceVideoProcessor.App.Services;
using RaceVideoProcessor.App.ViewModels;
using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;
using RaceVideoProcessor.Infrastructure.Backend;
using RaceVideoProcessor.Infrastructure.Data;
using RaceVideoProcessor.Infrastructure.Logging;
using RaceVideoProcessor.Infrastructure.Providers;
using RaceVideoProcessor.Infrastructure.Video;

namespace RaceVideoProcessor.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private IAppLog? _log;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Nothing after startup should be able to close the application silently.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RaceVideoProcessor");
            Directory.CreateDirectory(appData);

            var repository = new SqliteLocalStateRepository(Path.Combine(appData, "race-video-processor.db"));
            await repository.InitializeAsync();
            var settings = await repository.LoadSettingsAsync() ?? new AppSettings();
            settings.Normalize();
            await repository.SaveSettingsAsync(settings);

            var log = new FileAppLog(Path.Combine(appData, "race-video-processor.log"));
            _log = log;

            var services = new ServiceCollection();
            services.AddSingleton(settings);
            services.AddSingleton<ILocalStateRepository>(repository);
            services.AddSingleton<IAppLog>(log);
            // No client-wide timeout: uploads of large videos take minutes. Each
            // backend request applies its own timeout instead.
            services.AddSingleton(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
            services.AddSingleton<RaceBackendClient>();
            services.AddSingleton<IBackendDiagnostics>(sp => sp.GetRequiredService<RaceBackendClient>());
            services.AddSingleton<DemoMediaPublisher>();
            services.AddSingleton<IMediaPublisher, MediaPublisherRouter>();

            services.AddSingleton<MockDataProvider>();
            services.AddSingleton<IDemoEntryController>(sp => sp.GetRequiredService<MockDataProvider>());
            services.AddSingleton<IRealApiPayloadAdapter, RaceApiPayloadAdapter>();
            services.AddSingleton<RealApiDataProvider>();
            services.AddSingleton<IEntryProviderRouter, EntryProviderRouter>();

            services.AddSingleton<EntrySyncService>();
            services.AddSingleton<PollingCoordinator>();

            services.AddSingleton<IFontResolver, FontResolver>();
            services.AddSingleton<IFfprobeService, FfprobeService>();
            services.AddSingleton<IEncoderCapabilityService, EncoderCapabilityService>();
            services.AddSingleton<FfmpegFilterBuilder>();
            services.AddSingleton<IOutputValidator, OutputValidator>();
            services.AddSingleton<IVideoProcessingService, VideoProcessingService>();
            services.AddSingleton<IToolHealthService, ToolHealthService>();
            services.AddSingleton<EntryWorkflowService>();

            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();

            _services = services.BuildServiceProvider();
            var viewModel = _services.GetRequiredService<MainViewModel>();
            var window = _services.GetRequiredService<MainWindow>();
            window.DataContext = viewModel;
            MainWindow = window;

            window.Closing += (_, _) => window.CapturePlacement();
            window.Closed += async (_, _) =>
            {
                try
                {
                    await repository.SaveSettingsAsync(settings);
                }
                catch (Exception ex)
                {
                    log.Error("Could not save window placement: " + ex.Message);
                }

                await viewModel.DisposeAsync();
                _services?.Dispose();
                Shutdown();
            };

            // Show first: startup work must never leave the operator with no window.
            window.Show();

            try
            {
                await viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                log.Error("Initialization failed: " + ex);
                MessageBox.Show(ex.Message, "Race Video Processor — startup warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Race Video Processor — startup failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _services?.Dispose();
            Shutdown(-1);
        }
    }

    /// <summary>
    /// A failure on the UI thread — a binding callback, a command, a converter —
    /// must not take the application down without a word. Log it, show it, and
    /// keep running so an in-flight render and the operator's mappings survive.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI error: " + e.Exception);
        MessageBox.Show(
            e.Exception.Message + "\n\nThe application is still running. See the Logs page for detail.",
            "Race Video Processor — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => _log?.Error("Unhandled background error: " + (e.ExceptionObject as Exception)?.ToString());
}
