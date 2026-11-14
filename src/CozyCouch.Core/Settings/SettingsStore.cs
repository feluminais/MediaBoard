using System.Text.Json;
using System.Text.Json.Serialization;

namespace CozyCouch.Core.Settings;

/// <summary>The settings file exists but could not be read (another program holds it, or access is denied).</summary>
public sealed class SettingsUnreadableException(string message, Exception inner) : IOException(message, inner);

/// <summary>What <see cref="SettingsStore.TryLoad"/> found, for a caller that can't treat every failure as defaults.</summary>
public abstract record SettingsReadResult
{
    /// <summary>A corrupt file comes back as defaults here too: nothing in it can be recovered.</summary>
    public sealed record Ok(AppSettings Settings) : SettingsReadResult;

    public sealed record Missing : SettingsReadResult;

    /// <summary>The file exists but another program holds it or access is denied, so what it says is unknown.</summary>
    public sealed record Unreadable : SettingsReadResult;
}

/// <summary>
/// Reads and writes settings.json. <see cref="Load"/> yields default settings for a missing, corrupt or unreadable
/// file. Changes go through <see cref="Update"/>, which refuses to write over a file it could not read, so a
/// passing lock can't wipe the saved key.
/// </summary>
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
                return Read();
            }
            catch (SettingsUnreadableException)
            {
                return new AppSettings();
            }
        }
    }

    /// <summary>Like <see cref="Load"/>, for startup, where a file that can't be read must not pass for one that isn't there.</summary>
    public SettingsReadResult TryLoad()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(path) ? new SettingsReadResult.Ok(Read()) : new SettingsReadResult.Missing();
            }
            catch (SettingsUnreadableException)
            {
                return new SettingsReadResult.Unreadable();
            }
        }
    }

    /// <summary>
    /// Reads the file, applies <paramref name="change"/> and saves the result, under one lock. A corrupt file counts
    /// as defaults; a file that can't be read throws <see cref="SettingsUnreadableException"/> and nothing is written.
    /// </summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings saved;
        lock (_gate)
        {
            saved = change(Read());
            Write(saved);
        }

        Changed?.Invoke(saved);
    }

    public void Save(AppSettings settings)
    {
        lock (_gate) Write(settings);

        Changed?.Invoke(settings);
    }

    // Called with _gate held.
    private AppSettings Read()
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsUnreadableException(ex.Message, ex);
        }
    }

    // Called with _gate held. Writes beside the target and swaps, so a crash never leaves a half-written file.
    private void Write(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, path, overwrite: true);
    }
}
