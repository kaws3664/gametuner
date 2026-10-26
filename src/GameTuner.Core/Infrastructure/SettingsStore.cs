using GameTuner.Core.Models;

namespace GameTuner.Core.Infrastructure;

public sealed class SettingsStore(AppPaths paths, JsonFileStore files)
{
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        await files.ReadAsync<AppSettings>(paths.SettingsFile, cancellationToken) ?? new AppSettings();

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        files.WriteAtomicAsync(paths.SettingsFile, settings, cancellationToken);
}
