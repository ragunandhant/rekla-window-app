using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Providers;

public sealed class EntryProviderRouter : IEntryProviderRouter
{
    private readonly AppSettings _settings;
    private readonly MockDataProvider _mock;
    private readonly RealApiDataProvider _real;

    public EntryProviderRouter(AppSettings settings, MockDataProvider mock, RealApiDataProvider real)
    {
        _settings = settings;
        _mock = mock;
        _real = real;
    }

    public IEntryDataProvider Current => _settings.DataSourceMode == DataSourceMode.Demo ? _mock : _real;
}
