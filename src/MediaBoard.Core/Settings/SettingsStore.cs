using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaBoard.Core.Settings;

/// <summary>Reads and writes settings.json. A missing or unreadable file yields default settings.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    public event Action<AppSettings>? Changed;

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(path)) return new AppSettings();
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Write beside the target and swap, so a crash never leaves a half-written file.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, path, overwrite: true);
        }

        Changed?.Invoke(settings);
    }
}
