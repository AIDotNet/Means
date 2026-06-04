using Means.Configuration;
using Means.Core;
using Microsoft.Extensions.Options;

namespace Means.Services;

public sealed class SystemSettingsService
{
    private readonly IConsoleStore _consoleStore;
    private readonly IOptions<RequestLimitsOptions> _defaults;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private SystemSettings? _cached;

    public SystemSettingsService(IConsoleStore consoleStore, IOptions<RequestLimitsOptions> defaults)
    {
        _consoleStore = consoleStore;
        _defaults = defaults;
    }

    public async Task<SystemSettings> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            var stored = await _consoleStore.GetSystemSettingsAsync(cancellationToken);
            _cached = stored is not null
                ? Validate(stored)
                : new SystemSettings(SystemSettings.ValidateMaxUploadSizeBytes(_defaults.Value.MaxUploadSizeBytes));
            return _cached;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<SystemSettings> SaveAsync(SystemSettings settings, CancellationToken cancellationToken)
    {
        var validated = Validate(settings);
        await _consoleStore.SaveSystemSettingsAsync(validated, cancellationToken);
        _cached = validated;
        return validated;
    }

    private static SystemSettings Validate(SystemSettings settings)
    {
        return new SystemSettings(
            SystemSettings.ValidateMaxUploadSizeBytes(settings.MaxUploadSizeBytes),
            SystemSettings.NormalizePublicOrigin(settings.PublicOrigin));
    }
}
