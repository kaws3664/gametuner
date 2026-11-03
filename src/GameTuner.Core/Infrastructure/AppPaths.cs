namespace GameTuner.Core.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null, string? rdr2ConfigOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameTuner");
        Sessions = Path.Combine(Root, "sessions");
        Backups = Path.Combine(Root, "backups");
        Logs = Path.Combine(Root, "logs");
        Captures = Path.Combine(Root, "captures");
        SettingsFile = Path.Combine(Root, "settings.json");
        Rdr2ConfigFile = rdr2ConfigOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Rockstar Games",
            "Red Dead Redemption 2",
            "Settings",
            "system.xml");
        Rdr2BenchmarksDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Rockstar Games",
            "Red Dead Redemption 2",
            "Benchmarks");
    }

    public string Root { get; }
    public string Sessions { get; }
    public string Backups { get; }
    public string Logs { get; }
    public string Captures { get; }
    public string PresentMonExecutable { get; } = FindPresentMon();
    public string SettingsFile { get; }
    public string Rdr2ConfigFile { get; }
    public string Rdr2BenchmarksDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Sessions);
        Directory.CreateDirectory(Backups);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Captures);
    }

    private static string FindPresentMon()
    {
        return Path.Combine(AppContext.BaseDirectory, "Tools", "PresentMon", "PresentMon.exe");
    }
}
