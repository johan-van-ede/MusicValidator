using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicValidator;

internal sealed class PurchaseRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime PurchaseDate { get; set; } = DateTime.Today;
    public string Store { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public List<PurchaseSong> Songs { get; set; } = [];

    [JsonIgnore]
    public int SongCount => Songs.Count;

    [JsonIgnore]
    public string Details =>
        $"{PurchaseDate:d} | {Store} | {Price:C} | {SongCount:N0} songs";

    [JsonIgnore]
    public string DisplayLabel =>
        $"{PurchaseDate:d} | {Store} | {Price:C} | {SongCount:N0} songs";

    public PurchaseRecord()
    {
    }
}

internal sealed class PurchaseSong
{
    public uint? TrackNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? MetadataError { get; set; }

    [JsonIgnore]
    public string Details => MetadataError is null
        ? FilePath
        : $"{FilePath}{Environment.NewLine}Metadata could not be read: {MetadataError}";

    public PurchaseSong()
    {
    }

    public static PurchaseSong FromTrack(MusicTrack track) => new()
    {
        TrackNumber = track.TrackNumber,
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        Duration = track.Duration,
        FilePath = track.FilePath,
        MetadataError = track.MetadataError
    };
}

internal static class PurchaseStore
{
    public static List<PurchaseRecord> Load(string folderPath)
    {
        var purchasesPath = GetPurchasesPath(folderPath);
        if (!File.Exists(purchasesPath))
        {
            return [];
        }

        var purchases = JsonSerializer.Deserialize<List<PurchaseRecord>>(File.ReadAllText(purchasesPath))
            ?? throw new InvalidDataException("The purchases file is empty or invalid.");

        if (purchases.Any(purchase =>
                purchase is null
                || purchase.Id == Guid.Empty
                || purchase.PurchaseDate == default
                || string.IsNullOrWhiteSpace(purchase.Store)
                || purchase.Price < 0
                || purchase.Songs is null
                || purchase.Songs.Any(song => song is null || string.IsNullOrWhiteSpace(song.FilePath))))
        {
            throw new InvalidDataException("The purchases file contains invalid purchase records.");
        }

        return purchases
            .OrderByDescending(purchase => purchase.PurchaseDate)
            .ToList();
    }

    public static void Save(string folderPath, IEnumerable<PurchaseRecord> purchases)
    {
        var purchasesPath = GetPurchasesPath(folderPath);
        var directory = Path.GetDirectoryName(purchasesPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(purchasesPath, JsonSerializer.Serialize(purchases, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static string GetPurchasesPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new InvalidDataException("Choose a folder for purchase records.");
        }

        return Path.Combine(folderPath, "purchases.json");
    }
}
