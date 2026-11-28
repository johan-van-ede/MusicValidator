using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MusicValidator;

internal enum PurchaseKind
{
    Physical,
    Digital
}

internal enum PaymentMethod
{
    Cash,
    Digital,
    Unknown
}

internal sealed class PurchaseRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime? PurchaseDate { get; set; } = DateTime.Today;
    public string? Store { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
    public decimal? Price { get; set; } = 0m;
    public PurchaseKind? Kind { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }
    public bool IsUnknownPhysicalAlbum { get; set; }
    public bool IsPriceUnknown { get; set; }
    public bool IsFreeDigitalRelease { get; set; }
    public bool ManuallyValidated { get; set; }
    public string? PossessionProofPdfFile { get; set; }
    public string? PossessionProofNativeFile { get; set; }
    public string? PaymentProofPdfFile { get; set; }
    public string? PaymentProofNativeFile { get; set; }
    public string? PurchaseProofPdfFile { get; set; }
    public string? PurchaseProofNativeFile { get; set; }
    public string? FreeReleaseProofPdfFile { get; set; }
    public string? FreeReleaseProofNativeFile { get; set; }
    public List<string> DigitalMusicFiles { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PossessionProofFile { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PaymentProofFile { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PurchaseProofFile { get; set; }

    public List<PurchaseSong> Songs { get; set; } = [];

    [JsonIgnore]
    public string? PossessionProofPdfSourcePath { get; set; }

    [JsonIgnore]
    public string? PossessionProofNativeSourcePath { get; set; }

    [JsonIgnore]
    public string? PaymentProofPdfSourcePath { get; set; }

    [JsonIgnore]
    public string? PaymentProofNativeSourcePath { get; set; }

    [JsonIgnore]
    public string? PurchaseProofPdfSourcePath { get; set; }

    [JsonIgnore]
    public string? PurchaseProofNativeSourcePath { get; set; }

    [JsonIgnore]
    public string? FreeReleaseProofPdfSourcePath { get; set; }

    [JsonIgnore]
    public string? FreeReleaseProofNativeSourcePath { get; set; }

    [JsonIgnore]
    public List<string> DigitalMusicSourcePaths { get; set; } = [];

    [JsonIgnore]
    public int SongCount => Songs.Count;

    [JsonIgnore]
    public PurchaseCheckResult? CheckResult { get; private set; }

    [JsonIgnore]
    public bool HasCompleteEvidence => CheckResult?.HasCompleteEvidence == true;

    [JsonIgnore]
    public bool IsValidated => CheckResult?.IsValid == true;

    [JsonIgnore]
    public string ValidationStatus => CheckResult?.Status ?? "Not checked";

    [JsonIgnore]
    public string ValidationSummary => CheckResult?.Summary ?? "This purchase has not been checked yet.";

    [JsonIgnore]
    public string Details =>
        string.Join(
            " | ",
            new[]
            {
                PurchaseDateDisplay,
                StoreDisplay,
                Remark,
                MusicMediumDisplay,
                PaymentMethodDisplay,
                PriceDisplay,
                ValidationStatus,
                $"{SongCount:N0} songs"
            }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    [JsonIgnore]
    public string MusicMediumDisplay => IsUnknownPhysicalAlbum
        ? "Physical album (purchase details unknown)"
        : IsFreeDigitalRelease
            ? "Digital (free release)"
            : Kind?.ToString() ?? "Not specified";

    [JsonIgnore]
    public string PaymentMethodDisplay => IsFreeDigitalRelease
        ? "Not applicable"
        : PaymentMethod?.ToString() ?? "Not specified";

    [JsonIgnore]
    public string PurchaseDateDisplay => PurchaseDate?.ToString("d") ?? "Unknown";

    [JsonIgnore]
    public string StoreDisplay => string.IsNullOrWhiteSpace(Store) ? "Unknown" : Store;

    [JsonIgnore]
    public string PriceDisplay => Price?.ToString("C", CultureInfo.GetCultureInfo("nl-NL")) ?? "Unknown";

    [JsonIgnore]
    public string DisplayLabel => Details;

    [JsonIgnore]
    public string? StorageFolderName { get; set; }

    public PurchaseRecord()
    {
    }

    internal void SetCheckResult(PurchaseCheckResult result)
    {
        CheckResult = result;
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
    private const string LegacyFileName = "purchases.json";
    private const string LegacyPurchaseFileName = "purchase.json";
    private const string UnknownPurchasesFolderName = "Onbekend";
    private static readonly JsonSerializerOptions PurchaseJsonOptions = new() { WriteIndented = true };

    public static List<PurchaseRecord> Load(string folderPath)
    {
        var root = GetRootPath(folderPath);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var purchases = ReadPurchaseFolders(root);
        var legacyPath = Path.Combine(root, LegacyFileName);
        if (File.Exists(legacyPath))
        {
            var legacyPurchases = JsonSerializer.Deserialize<List<PurchaseRecord>>(File.ReadAllText(legacyPath))
                ?? throw new InvalidDataException("The legacy purchases file is empty or invalid.");
            var existingIds = purchases.Select(purchase => purchase.Id).ToHashSet();

            foreach (var purchase in legacyPurchases)
            {
                NormalizeUnknownPhysicalPaymentMethod(purchase);
                ValidatePurchase(purchase);
                MigrateLegacyEvidence(purchase);
                if (existingIds.Add(purchase.Id))
                {
                    purchases.Add(purchase);
                }
            }

            Save(root, purchases);
            File.Move(legacyPath, GetLegacyArchivePath(root));
        }

        return purchases
            .OrderByDescending(purchase => purchase.PurchaseDate)
            .ToList();
    }

    public static HashSet<Guid> Save(string folderPath, IEnumerable<PurchaseRecord> purchases)
    {
        var root = GetRootPath(folderPath);
        Directory.CreateDirectory(root);

        var purchaseList = purchases.ToList();
        foreach (var purchase in purchaseList)
        {
            ValidatePurchase(purchase);
            MigrateLegacyEvidence(purchase);
        }

        if (purchaseList.Select(purchase => purchase.Id).Distinct().Count() != purchaseList.Count)
        {
            throw new InvalidDataException("Purchase records must have unique identifiers.");
        }

        var existingPurchases = ReadPurchaseFolders(root)
            .ToDictionary(purchase => purchase.Id);
        var existingNames = Directory
            .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(directory => Path.GetRelativePath(root, directory))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changedPurchaseIds = new HashSet<Guid>();

        foreach (var purchase in purchaseList)
        {
            var hasExistingPurchase = existingPurchases.TryGetValue(purchase.Id, out var existingPurchase);
            var folderName = hasExistingPurchase
                ? existingPurchase!.StorageFolderName!
                : CreateFolderName(purchase, existingNames);
            var purchaseFolder = Path.Combine(root, folderName);
            var hasSelectedFiles = HasSelectedAttachments(purchase);
            if (hasExistingPurchase
                && !hasSelectedFiles
                && AreEquivalentPurchases(purchase, existingPurchase!))
            {
                purchase.StorageFolderName = folderName;
                continue;
            }

            Directory.CreateDirectory(purchaseFolder);
            var oldProofFiles = new[]
            {
                purchase.PossessionProofPdfFile,
                purchase.PossessionProofNativeFile,
                purchase.PaymentProofPdfFile,
                purchase.PaymentProofNativeFile,
                purchase.PurchaseProofPdfFile,
                purchase.PurchaseProofNativeFile,
                purchase.FreeReleaseProofPdfFile,
                purchase.FreeReleaseProofNativeFile
            };
            var oldDigitalMusicFiles = purchase.DigitalMusicFiles.ToList();
            var copiedFiles = new List<string>();
            try
            {
                purchase.PossessionProofPdfFile = CopyEvidenceIfSelected(
                    purchase.PossessionProofPdfSourcePath,
                    purchase.PossessionProofPdfFile,
                    purchaseFolder,
                    purchase,
                    "Proof of ownership",
                    copiedFiles);
                purchase.PossessionProofNativeFile = CopyEvidenceIfSelected(
                    purchase.PossessionProofNativeSourcePath,
                    purchase.PossessionProofNativeFile,
                    purchaseFolder,
                    purchase,
                    "Proof of ownership",
                    copiedFiles);
                purchase.PaymentProofPdfFile = CopyEvidenceIfSelected(
                    purchase.PaymentProofPdfSourcePath,
                    purchase.PaymentProofPdfFile,
                    purchaseFolder,
                    purchase,
                    "Proof of payment",
                    copiedFiles);
                purchase.PaymentProofNativeFile = CopyEvidenceIfSelected(
                    purchase.PaymentProofNativeSourcePath,
                    purchase.PaymentProofNativeFile,
                    purchaseFolder,
                    purchase,
                    "Proof of payment",
                    copiedFiles);
                purchase.PurchaseProofPdfFile = CopyEvidenceIfSelected(
                    purchase.PurchaseProofPdfSourcePath,
                    purchase.PurchaseProofPdfFile,
                    purchaseFolder,
                    purchase,
                    "Proof of purchase",
                    copiedFiles);
                purchase.PurchaseProofNativeFile = CopyEvidenceIfSelected(
                    purchase.PurchaseProofNativeSourcePath,
                    purchase.PurchaseProofNativeFile,
                    purchaseFolder,
                    purchase,
                    "Proof of purchase",
                    copiedFiles);
                purchase.FreeReleaseProofPdfFile = CopyEvidenceIfSelected(
                    purchase.FreeReleaseProofPdfSourcePath,
                    purchase.FreeReleaseProofPdfFile,
                    purchaseFolder,
                    purchase,
                    "Proof of free release",
                    copiedFiles);
                purchase.FreeReleaseProofNativeFile = CopyEvidenceIfSelected(
                    purchase.FreeReleaseProofNativeSourcePath,
                    purchase.FreeReleaseProofNativeFile,
                    purchaseFolder,
                    purchase,
                    "Proof of free release",
                    copiedFiles);
                purchase.DigitalMusicFiles = CopyDigitalMusicIfSelected(
                    purchase.DigitalMusicSourcePaths,
                    purchase.DigitalMusicFiles,
                    purchaseFolder,
                    copiedFiles);

                var purchasePath = Path.Combine(purchaseFolder, CreatePurchaseDataFileName(purchase));
                var temporaryPath = $"{purchasePath}.tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(purchase, PurchaseJsonOptions));
                File.Move(temporaryPath, purchasePath, overwrite: true);
                purchase.PossessionProofPdfSourcePath = null;
                purchase.PossessionProofNativeSourcePath = null;
                purchase.PaymentProofPdfSourcePath = null;
                purchase.PaymentProofNativeSourcePath = null;
                purchase.PurchaseProofPdfSourcePath = null;
                purchase.PurchaseProofNativeSourcePath = null;
                purchase.FreeReleaseProofPdfSourcePath = null;
                purchase.FreeReleaseProofNativeSourcePath = null;
                purchase.DigitalMusicSourcePaths.Clear();
                purchase.StorageFolderName = folderName;
                existingNames.Add(folderName);
                changedPurchaseIds.Add(purchase.Id);
            }
            catch
            {
                purchase.PossessionProofPdfFile = oldProofFiles[0];
                purchase.PossessionProofNativeFile = oldProofFiles[1];
                purchase.PaymentProofPdfFile = oldProofFiles[2];
                purchase.PaymentProofNativeFile = oldProofFiles[3];
                purchase.PurchaseProofPdfFile = oldProofFiles[4];
                purchase.PurchaseProofNativeFile = oldProofFiles[5];
                purchase.FreeReleaseProofPdfFile = oldProofFiles[6];
                purchase.FreeReleaseProofNativeFile = oldProofFiles[7];
                purchase.DigitalMusicFiles = oldDigitalMusicFiles;
                foreach (var copiedFile in copiedFiles)
                {
                    if (File.Exists(copiedFile))
                    {
                        File.Delete(copiedFile);
                    }
                }

                var musicFolder = Path.Combine(purchaseFolder, "music");
                if (Directory.Exists(musicFolder) && !Directory.EnumerateFileSystemEntries(musicFolder).Any())
                {
                    Directory.Delete(musicFolder);
                }

                throw;
            }
        }

        return changedPurchaseIds;
    }

    private static bool HasSelectedAttachments(PurchaseRecord purchase) =>
        !string.IsNullOrWhiteSpace(purchase.PossessionProofPdfSourcePath)
        || !string.IsNullOrWhiteSpace(purchase.PossessionProofNativeSourcePath)
        || !string.IsNullOrWhiteSpace(purchase.PaymentProofPdfSourcePath)
        || !string.IsNullOrWhiteSpace(purchase.PaymentProofNativeSourcePath)
        || !string.IsNullOrWhiteSpace(purchase.PurchaseProofPdfSourcePath)
        || !string.IsNullOrWhiteSpace(purchase.PurchaseProofNativeSourcePath)
        || !string.IsNullOrWhiteSpace(purchase.FreeReleaseProofPdfSourcePath)
        || !string.IsNullOrWhiteSpace(purchase.FreeReleaseProofNativeSourcePath)
        || purchase.DigitalMusicSourcePaths.Count > 0;

    private static bool AreEquivalentPurchases(PurchaseRecord purchase, PurchaseRecord existingPurchase)
    {
        var purchaseJson = JsonSerializer.SerializeToNode(purchase, PurchaseJsonOptions);
        var existingPurchaseJson = JsonSerializer.SerializeToNode(existingPurchase, PurchaseJsonOptions);
        NormalizePurchaseDate(purchaseJson, purchase.PurchaseDate);
        NormalizePurchaseDate(existingPurchaseJson, existingPurchase.PurchaseDate);
        return JsonNode.DeepEquals(purchaseJson, existingPurchaseJson);
    }

    private static void NormalizePurchaseDate(JsonNode? purchaseJson, DateTime? purchaseDate)
    {
        if (purchaseJson is JsonObject purchaseObject)
        {
            purchaseObject[nameof(PurchaseRecord.PurchaseDate)] = purchaseDate.HasValue
                ? JsonValue.Create(purchaseDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                : null;
        }
    }

    public static void Delete(string folderPath, PurchaseRecord purchase)
    {
        var root = GetRootPath(folderPath);
        var folderName = purchase.StorageFolderName;
        var purchaseFolder = GetPurchaseFolderPath(folderPath, folderName);
        var purchasePath = FindPurchaseDataFile(purchaseFolder);
        if (purchasePath is null)
        {
            throw new FileNotFoundException(
                "The purchase record file could not be found.",
                Path.Combine(purchaseFolder, LegacyPurchaseFileName));
        }

        var storedPurchase = JsonSerializer.Deserialize<PurchaseRecord>(File.ReadAllText(purchasePath))
            ?? throw new InvalidDataException("The purchase record file is empty or invalid.");
        if (storedPurchase.Id != purchase.Id)
        {
            throw new InvalidDataException("The purchase folder contains a different purchase record.");
        }

        storedPurchase.StorageFolderName = folderName;
        var reportFile = GetPurchaseReportFilePath(folderPath, storedPurchase);
        if (File.Exists(reportFile))
        {
            File.Delete(reportFile);
        }

        var evidenceFiles = new[]
        {
            storedPurchase.PossessionProofPdfFile,
            storedPurchase.PossessionProofNativeFile,
            storedPurchase.PaymentProofPdfFile,
            storedPurchase.PaymentProofNativeFile,
            storedPurchase.PurchaseProofPdfFile,
            storedPurchase.PurchaseProofNativeFile,
            storedPurchase.FreeReleaseProofPdfFile,
            storedPurchase.FreeReleaseProofNativeFile,
            storedPurchase.PossessionProofFile,
            storedPurchase.PaymentProofFile,
            storedPurchase.PurchaseProofFile
        }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => GetEvidenceFilePath(folderPath, storedPurchase, path!))
            .ToList();
        var digitalMusicFiles = (storedPurchase.DigitalMusicFiles ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => GetDigitalMusicFilePath(folderPath, storedPurchase, path))
            .ToList();
        foreach (var evidenceFile in evidenceFiles)
        {
            if (File.Exists(evidenceFile))
            {
                File.Delete(evidenceFile);
            }
        }

        foreach (var digitalMusicFile in digitalMusicFiles)
        {
            if (File.Exists(digitalMusicFile))
            {
                File.Delete(digitalMusicFile);
            }
        }

        File.Delete(purchasePath);
        var evidenceFolder = Path.Combine(purchaseFolder, "evidence");
        if (Directory.Exists(evidenceFolder) && !Directory.EnumerateFileSystemEntries(evidenceFolder).Any())
        {
            Directory.Delete(evidenceFolder);
        }

        var musicFolder = Path.Combine(purchaseFolder, "music");
        if (Directory.Exists(musicFolder) && !Directory.EnumerateFileSystemEntries(musicFolder).Any())
        {
            Directory.Delete(musicFolder);
        }

        if (!Directory.EnumerateFileSystemEntries(purchaseFolder).Any())
        {
            Directory.Delete(purchaseFolder);
        }

        var unknownFolder = Path.GetDirectoryName(purchaseFolder);
        if (unknownFolder is not null
            && string.Equals(
                Path.GetDirectoryName(unknownFolder),
                root,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetFileName(unknownFolder),
                UnknownPurchasesFolderName,
                StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(unknownFolder)
            && !Directory.EnumerateFileSystemEntries(unknownFolder).Any())
        {
            Directory.Delete(unknownFolder);
        }

        purchase.StorageFolderName = null;
    }

    public static string GetEvidenceFilePath(
        string folderPath,
        PurchaseRecord purchase,
        string relativeFilePath) =>
        GetPurchaseAssetFilePath(folderPath, purchase, relativeFilePath, "evidence", "evidence");

    public static string GetDigitalMusicFilePath(
        string folderPath,
        PurchaseRecord purchase,
        string relativeFilePath) =>
        GetPurchaseAssetFilePath(folderPath, purchase, relativeFilePath, "music", "digital music");

    private static string GetPurchaseAssetFilePath(
        string folderPath,
        PurchaseRecord purchase,
        string relativeFilePath,
        string assetFolderName,
        string assetLabel)
    {
        if (string.IsNullOrWhiteSpace(purchase.StorageFolderName)
            || string.IsNullOrWhiteSpace(relativeFilePath)
            || Path.IsPathRooted(relativeFilePath))
        {
            throw new InvalidDataException($"The {assetLabel} file path is invalid.");
        }

        var purchaseFolder = GetPurchaseFolderPath(folderPath, purchase.StorageFolderName);
        var assetFolder = Path.GetFullPath(Path.Combine(purchaseFolder, assetFolderName))
            + Path.DirectorySeparatorChar;
        var assetFile = Path.GetFullPath(Path.Combine(purchaseFolder, relativeFilePath));
        if (!assetFile.StartsWith(assetFolder, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The {assetLabel} file must be inside the purchase folder's {assetFolderName} directory.");
        }

        return assetFile;
    }

    public static string GetPurchaseReportFilePath(string folderPath, PurchaseRecord purchase)
    {
        var purchaseFolder = GetPurchaseFolderPath(folderPath, purchase.StorageFolderName);
        return Path.Combine(purchaseFolder, CreatePurchaseReportFileName(purchase));
    }

    private static List<PurchaseRecord> ReadPurchaseFolders(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var purchases = new List<PurchaseRecord>();
        var identifiers = new HashSet<Guid>();
        var purchaseFolders = Directory
            .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .ToList();
        var existingFolders = purchaseFolders
            .Select(directory => Path.GetRelativePath(root, directory))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var originalPurchaseFolder in purchaseFolders)
        {
            if (!Directory.Exists(originalPurchaseFolder))
            {
                continue;
            }

            var purchaseFolder = originalPurchaseFolder;
            var purchasePath = FindPurchaseDataFile(purchaseFolder);
            if (purchasePath is null)
            {
                continue;
            }

            var purchase = JsonSerializer.Deserialize<PurchaseRecord>(File.ReadAllText(purchasePath))
                ?? throw new InvalidDataException($"The purchase record in \"{purchaseFolder}\" is empty or invalid.");
            NormalizeUnknownPhysicalPaymentMethod(purchase);
            ValidatePurchase(purchase);
            MigrateLegacyEvidence(purchase);
            if (string.Equals(
                    Path.GetFileName(purchasePath),
                    LegacyPurchaseFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                var migratedPath = Path.Combine(purchaseFolder, CreatePurchaseDataFileName(purchase));
                if (File.Exists(migratedPath))
                {
                    throw new InvalidDataException(
                        $"A purchase data file already exists in \"{purchaseFolder}\".");
                }

                File.Move(purchasePath, migratedPath);
            }

            if (!purchase.PurchaseDate.HasValue)
            {
                purchaseFolder = MoveUnknownPurchaseFolder(root, purchaseFolder, existingFolders);
            }

            if (!identifiers.Add(purchase.Id))
            {
                throw new InvalidDataException($"Duplicate purchase identifier found in \"{purchaseFolder}\".");
            }

            purchase.StorageFolderName = Path.GetRelativePath(root, purchaseFolder);
            purchases.Add(purchase);
        }

        return purchases;
    }

    private static string? FindPurchaseDataFile(string purchaseFolder)
    {
        var namedFiles = Directory
            .EnumerateFiles(purchaseFolder, "* - purchase-data.json", SearchOption.TopDirectoryOnly)
            .ToList();
        if (namedFiles.Count > 1)
        {
            throw new InvalidDataException($"Multiple purchase data files were found in \"{purchaseFolder}\".");
        }

        if (namedFiles.Count == 1)
        {
            return namedFiles[0];
        }

        var legacyPath = Path.Combine(purchaseFolder, LegacyPurchaseFileName);
        return File.Exists(legacyPath) ? legacyPath : null;
    }

    private static string CreatePurchaseDataFileName(PurchaseRecord purchase)
    {
        if (purchase.IsUnknownPhysicalAlbum)
        {
            return $"{CreateUnknownPhysicalAlbumName(purchase)} - purchase-data.json";
        }

        var date = purchase.PurchaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown date";
        var store = SanitizeFolderPart(purchase.Store ?? string.Empty, "Unknown store");
        return $"{date} - {store} - purchase-data.json";
    }

    private static string CreatePurchaseReportFileName(PurchaseRecord purchase)
    {
        if (purchase.IsUnknownPhysicalAlbum)
        {
            return $"{CreateUnknownPhysicalAlbumName(purchase)} - purchase-report.pdf";
        }

        var date = purchase.PurchaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown date";
        var store = SanitizeFolderPart(purchase.Store ?? string.Empty, "Unknown store");
        return $"{date} - {store} - purchase-report.pdf";
    }

    private static void ValidatePurchase(PurchaseRecord purchase)
    {
        if (purchase is null)
        {
            throw new InvalidDataException("A purchase record contains invalid data.");
        }

        var purchaseDetailsInvalid = purchase.IsUnknownPhysicalAlbum
            ? purchase.Kind != PurchaseKind.Physical
                || purchase.PaymentMethod != PaymentMethod.Unknown
                || purchase.PurchaseDate.HasValue
                || !string.IsNullOrWhiteSpace(purchase.Store)
                || purchase.Price.HasValue
            : !purchase.PurchaseDate.HasValue
                || purchase.PurchaseDate == default
                || string.IsNullOrWhiteSpace(purchase.Store)
                || (!purchase.Price.HasValue && !purchase.IsPriceUnknown)
                || (purchase.Price.HasValue && purchase.IsPriceUnknown)
                || purchase.Price < 0;
        var freeReleaseDetailsInvalid = purchase.IsFreeDigitalRelease
            && (purchase.Kind != PurchaseKind.Digital
                || purchase.PaymentMethod.HasValue
                || purchase.Price != 0
                || purchase.IsPriceUnknown);
        if (purchase.Id == Guid.Empty
            || purchaseDetailsInvalid
            || freeReleaseDetailsInvalid
            || purchase.Remark is null
            || purchase.Songs is null
            || purchase.DigitalMusicFiles is null
            || purchase.DigitalMusicSourcePaths is null
            || (purchase.Kind != PurchaseKind.Digital && purchase.DigitalMusicFiles.Count > 0)
            || (purchase.Kind != PurchaseKind.Digital && purchase.DigitalMusicSourcePaths.Count > 0)
            || purchase.DigitalMusicFiles.Any(path => !IsValidDigitalMusicRelativePath(path))
            || purchase.Songs.Any(song => song is null || string.IsNullOrWhiteSpace(song.FilePath)))
        {
            throw new InvalidDataException("A purchase record contains invalid data.");
        }
    }

    private static bool IsValidDigitalMusicRelativePath(string? relativeFilePath)
    {
        if (string.IsNullOrWhiteSpace(relativeFilePath) || Path.IsPathRooted(relativeFilePath))
        {
            return false;
        }

        var normalizedPath = relativeFilePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var pathParts = normalizedPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return pathParts.Length == 2
            && string.Equals(pathParts[0], "music", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pathParts[1], ".", StringComparison.Ordinal)
            && !string.Equals(pathParts[1], "..", StringComparison.Ordinal)
            && string.Equals(Path.GetFileName(pathParts[1]), pathParts[1], StringComparison.Ordinal);
    }

    private static void NormalizeUnknownPhysicalPaymentMethod(PurchaseRecord? purchase)
    {
        if (purchase?.IsUnknownPhysicalAlbum == true && purchase.PaymentMethod == PaymentMethod.Cash)
        {
            purchase.PaymentMethod = PaymentMethod.Unknown;
        }
    }

    private static void MigrateLegacyEvidence(PurchaseRecord purchase)
    {
        MigrateLegacyEvidence(
            purchase.PossessionProofFile,
            value => purchase.PossessionProofPdfFile ??= value,
            value => purchase.PossessionProofNativeFile ??= value);
        MigrateLegacyEvidence(
            purchase.PaymentProofFile,
            value => purchase.PaymentProofPdfFile ??= value,
            value => purchase.PaymentProofNativeFile ??= value);
        MigrateLegacyEvidence(
            purchase.PurchaseProofFile,
            value => purchase.PurchaseProofPdfFile ??= value,
            value => purchase.PurchaseProofNativeFile ??= value);
        purchase.PossessionProofFile = null;
        purchase.PaymentProofFile = null;
        purchase.PurchaseProofFile = null;
    }

    private static void MigrateLegacyEvidence(
        string? path,
        Action<string> assignPdf,
        Action<string> assignNative)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            assignPdf(path);
        }
        else
        {
            assignNative(path);
        }
    }

    private static string? CopyEvidenceIfSelected(
        string? sourcePath,
        string? existingRelativePath,
        string purchaseFolder,
        PurchaseRecord purchase,
        string proofType,
        ICollection<string> copiedFiles)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return existingRelativePath;
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The selected evidence file could not be found.", sourcePath);
        }

        var evidenceFolder = Path.Combine(purchaseFolder, "evidence");
        Directory.CreateDirectory(evidenceFolder);
        var date = purchase.PurchaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Onbekend";
        var remark = SanitizeFolderPart(
            string.IsNullOrWhiteSpace(purchase.Remark) ? "No remark" : purchase.Remark,
            "No remark");
        var safeProofType = SanitizeFolderPart(proofType, "Evidence");
        var extension = Path.GetExtension(sourcePath);
        var fileName = $"{date} - {remark} - {safeProofType}{extension}";
        var destinationPath = Path.Combine(evidenceFolder, fileName);
        var suffix = 2;
        while (File.Exists(destinationPath))
        {
            fileName = $"{date} - {remark} - {safeProofType} - {suffix++}{extension}";
            destinationPath = Path.Combine(evidenceFolder, fileName);
        }

        File.Copy(sourcePath, destinationPath);
        copiedFiles.Add(destinationPath);
        return Path.Combine("evidence", fileName);
    }

    private static List<string> CopyDigitalMusicIfSelected(
        IEnumerable<string> sourcePaths,
        IEnumerable<string> existingRelativePaths,
        string purchaseFolder,
        ICollection<string> copiedFiles)
    {
        var relativePaths = existingRelativePaths.ToList();
        var usedPaths = relativePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var musicFolder = Path.Combine(purchaseFolder, "music");

        foreach (var sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("A selected digital music file could not be found.", sourcePath);
            }

            var extension = Path.GetExtension(sourcePath);
            if (!AppSettings.SupportedAudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A selected digital music file has an unsupported audio format.");
            }

            var baseFileName = Path.GetFileNameWithoutExtension(sourcePath);
            var fileName = Path.GetFileName(sourcePath);
            var relativePath = Path.Combine("music", fileName);
            var destinationPath = Path.Combine(purchaseFolder, relativePath);
            var suffix = 2;
            while (usedPaths.Contains(relativePath) || File.Exists(destinationPath))
            {
                fileName = $"{baseFileName} ({suffix++}){extension}";
                relativePath = Path.Combine("music", fileName);
                destinationPath = Path.Combine(purchaseFolder, relativePath);
            }

            Directory.CreateDirectory(musicFolder);
            File.Copy(sourcePath, destinationPath);
            copiedFiles.Add(destinationPath);
            relativePaths.Add(relativePath);
            usedPaths.Add(relativePath);
        }

        return relativePaths;
    }

    private static string CreateFolderName(PurchaseRecord purchase, HashSet<string> existingNames)
    {
        if (!purchase.PurchaseDate.HasValue)
        {
            var unknownBaseName = CreateUnknownPhysicalAlbumName(purchase);
            var unknownCandidate = Path.Combine(UnknownPurchasesFolderName, unknownBaseName);
            var unknownSuffix = 2;
            while (!existingNames.Add(unknownCandidate))
            {
                unknownCandidate = Path.Combine(
                    UnknownPurchasesFolderName,
                    $"{unknownBaseName} - {unknownSuffix++}");
            }

            return unknownCandidate;
        }

        var store = SanitizeFolderPart(purchase.Store ?? string.Empty, "Unknown store");
        var remark = SanitizeFolderPart(
            string.IsNullOrWhiteSpace(purchase.Remark) ? "No remark" : purchase.Remark,
            "No remark");
        var date = purchase.PurchaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown date";
        var baseName = $"{date} - {store} - {remark}";

        if (existingNames.Add(baseName))
        {
            return baseName;
        }

        var identifier = purchase.Id.ToString("N")[..8];
        var candidate = $"{baseName} - {identifier}";
        var suffix = 2;
        while (!existingNames.Add(candidate))
        {
            candidate = $"{baseName} - {identifier}-{suffix++}";
        }

        return candidate;
    }

    private static string CreateUnknownPhysicalAlbumName(PurchaseRecord purchase)
    {
        var remark = SanitizeFolderPart(
            string.IsNullOrWhiteSpace(purchase.Remark) ? "Geen opmerking" : purchase.Remark,
            "Geen opmerking");
        return $"Onbekend - Fysiek album - {remark}";
    }

    private static string SanitizeFolderPart(string value, string fallback)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Select(character => invalidCharacters.Contains(character) || char.IsControl(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');
        if (sanitized.Length == 0)
        {
            return fallback;
        }

        var limited = sanitized.Length <= 64
            ? sanitized
            : sanitized[..64].TrimEnd('.', ' ');
        return limited.Length == 0 ? fallback : limited;
    }

    private static string GetRootPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new InvalidDataException("Choose a folder for purchase records.");
        }

        return Path.GetFullPath(folderPath);
    }

    private static string GetPurchaseFolderPath(string folderPath, string? relativeFolderName)
    {
        if (string.IsNullOrWhiteSpace(relativeFolderName) || Path.IsPathRooted(relativeFolderName))
        {
            throw new InvalidDataException("The purchase folder name is invalid.");
        }

        var root = GetRootPath(folderPath);
        var purchaseFolder = Path.GetFullPath(Path.Combine(root, relativeFolderName));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!purchaseFolder.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The purchase folder must be inside the purchase storage folder.");
        }

        return purchaseFolder;
    }

    private static string MoveUnknownPurchaseFolder(
        string root,
        string purchaseFolder,
        HashSet<string> existingFolders)
    {
        var relativeFolderName = Path.GetRelativePath(root, purchaseFolder);
        var pathParts = relativeFolderName.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length > 0
            && string.Equals(pathParts[0], UnknownPurchasesFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return purchaseFolder;
        }

        var originalFolderName = Path.GetFileName(purchaseFolder);
        var folderName = Path.Combine(UnknownPurchasesFolderName, originalFolderName);
        var candidate = folderName;
        var suffix = 2;
        while (existingFolders.Contains(candidate)
            || Directory.Exists(Path.Combine(root, candidate)))
        {
            candidate = Path.Combine(UnknownPurchasesFolderName, $"{originalFolderName} - {suffix++}");
        }

        Directory.CreateDirectory(Path.Combine(root, UnknownPurchasesFolderName));
        var destination = Path.Combine(root, candidate);
        Directory.Move(purchaseFolder, destination);
        existingFolders.Add(candidate);
        return destination;
    }

    private static string GetLegacyArchivePath(string root)
    {
        var archivePath = Path.Combine(root, "purchases.legacy.json");
        return File.Exists(archivePath) || Directory.Exists(archivePath)
            ? Path.Combine(root, $"purchases.legacy-{Guid.NewGuid():N}.json")
            : archivePath;
    }
}
