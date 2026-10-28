namespace MediaBoard.Core;

public static class AppPaths
{
    public const string DataDirEnvVar = "MEDIABOARD_DATA_DIR";

    // Read on every access so a test can override the root per run.
    public static string DataDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(DataDirEnvVar);
            var dir = string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaBoard")
                : overridden;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DatabasePath => Path.Combine(DataDir, "library.db");

    public static string ImagesDir => Path.Combine(DataDir, "images");

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
}
