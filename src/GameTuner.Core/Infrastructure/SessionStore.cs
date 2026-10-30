using GameTuner.Core.Models;

namespace GameTuner.Core.Infrastructure;

public sealed class SessionStore(AppPaths paths, JsonFileStore files)
{
    public Task SaveAsync(TuningSession session, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        return files.WriteAtomicAsync(
            Path.Combine(paths.Sessions, session.Id.ToString("N") + ".json"),
            session,
            cancellationToken);
    }

    public async Task<TuningSession?> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.Sessions))
        {
            return null;
        }

        var latest = new DirectoryInfo(paths.Sessions)
            .GetFiles("*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        return latest is null
            ? null
            : await files.ReadAsync<TuningSession>(latest.FullName, cancellationToken);
    }
}
