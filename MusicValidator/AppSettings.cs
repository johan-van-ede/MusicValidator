using System.IO;
using System.Text.Json;

namespace MusicValidator;

internal sealed class AppSettings
{
    public static readonly string[] SupportedAudioExtensions =
    [
        ".aac", ".aiff", ".alac", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav", ".wma"
    ];

    public bool IncludeSubfolders { get; set; } = true;
    public List<string> AudioExtensions { get; set; } = [.. SupportedAudioExtensions];
    public string? MusicFolderPath { get; set; }
    public string PurchaseStorageFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MusicValidator");

    public AppSettings()
    {
    }

    public AppSettings Copy() => new()
    {
        IncludeSubfolders = IncludeSubfolders,
        AudioExtensions = [.. AudioExtensions],
        MusicFolderPath = MusicFolderPath,
        PurchaseStorageFolder = PurchaseStorageFolder
    };
}

internal static class SettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MusicValidator",
        "settings.json");

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))
            ?? throw new InvalidDataException("The settings file is empty or invalid.");
        var supportedExtensions = AppSettings.SupportedAudioExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (settings.AudioExtensions is null
            || settings.AudioExtensions.Count == 0
            || settings.AudioExtensions.Any(extension => !supportedExtensions.Contains(extension))
            || string.IsNullOrWhiteSpace(settings.PurchaseStorageFolder))
        {
            throw new InvalidDataException("The settings file contains invalid scan or purchase storage settings.");
        }

        settings.AudioExtensions = settings.AudioExtensions
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
