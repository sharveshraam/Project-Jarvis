namespace Jarvis.Infrastructure;

/// <summary>Where JARVIS keeps its own data. Entirely separate from any other application in the repository.</summary>
public sealed class AppPaths
{
    public AppPaths(string? overrideRoot = null)
    {
        var root = overrideRoot;
        if (string.IsNullOrWhiteSpace(root)) root = Environment.GetEnvironmentVariable("JARVIS_DATA_DIR");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JARVIS");
        DataDirectory = Path.GetFullPath(root);
        LogDirectory = Path.Combine(DataDirectory, "logs");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "jarvis.db");
    public string SecretsPath => Path.Combine(DataDirectory, "secrets.dat");
}
