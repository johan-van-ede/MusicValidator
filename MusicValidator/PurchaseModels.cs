using System.Globalization;
using System.IO;
using System.Text.Json;
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
    Digital
}

internal sealed class PurchaseRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime PurchaseDate { get; set; } = DateTime.Today;
    public string Store { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public PurchaseKind? Kind { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }
    public bool ManuallyValidated { get; set; }
    public string? PossessionProofPdfFile { get; set; }
    public string? PossessionProofNativeFile { get; set; }
    public string? PaymentProofPdfFile { get; set; }
    public string? PaymentProofNativeFile { get; set; }
    public string? PurchaseProofPdfFile { get; set; }
    public string? PurchaseProofNativeFile { get; set; }

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
    public int SongCount => Songs.Count;

    [JsonIgnore]
    public bool HasCompleteEvidence =>
        Kind.HasValue
        && PaymentMethod.HasValue
        && HasEvidence(PurchaseProofPdfFile, PurchaseProofNativeFile, PurchaseProofFile)
        && (Kind != PurchaseKind.Physical
            || HasEvidence(PossessionProofPdfFile, PossessionProofNativeFile, PossessionProofFile))
        && (PaymentMethod != global::MusicValidator.PaymentMethod.Digital
            || HasEvidence(PaymentProofPdfFile, PaymentProofNativeFile, PaymentProofFile));

    [JsonIgnore]
    public bool IsValidated => ManuallyValidated || HasCompleteEvidence;

    [JsonIgnore]
    public string ValidationStatus => ManuallyValidated
        ? "Validated manually"
        : HasCompleteEvidence ? "Validated" : "Evidence missing";

    [JsonIgnore]
    public string Details =>
        string.Join(
            " | ",
            new[]
            {
                PurchaseDate.ToString("d"),
                Store,
                Remark,
                Kind?.ToString(),
                PaymentMethod?.ToString(),
                $"{Price:C}",
                ValidationStatus,
                $"{SongCount:N0} songs"
            }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    [JsonIgnore]
    public string DisplayLabel => Details;

    [JsonIgnore]
    public string? StorageFolderName { get; set; }

    private static bool HasEvidence(string? pdfFile, string? nativeFile, string? legacyFile) =>
        !string.IsNullOrWhiteSpace(pdfFile)
        || !string.IsNullOrWhiteSpace(nativeFile)
        || !string.IsNullOrWhiteSpace(legacyFile);

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
    private const string LegacyFileName = "purchases.json";
    private const string LegacyPurchaseFileName = "purchase.json";

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

    public static void Save(string folderPath, IEnumerable<PurchaseRecord> purchases)
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
            .ToDictionary(purchase => purchase.Id, purchase => purchase.StorageFolderName!);
        var existingNames = Directory
            .EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var purchase in purchaseList)
        {
            var folderName = existingPurchases.TryGetValue(purchase.Id, out var existingFolderName)
                ? existingFolderName
                : CreateFolderName(purchase, existingNames);
            var purchaseFolder = Path.Combine(root, folderName);
            Directory.CreateDirectory(purchaseFolder);
            var oldProofFiles = new[]
            {
                purchase.PossessionProofPdfFile,
                purchase.PossessionProofNativeFile,
                purchase.PaymentProofPdfFile,
                purchase.PaymentProofNativeFile,
                purchase.PurchaseProofPdfFile,
                purchase.PurchaseProofNativeFile
            };
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

                var purchasePath = Path.Combine(purchaseFolder, CreatePurchaseDataFileName(purchase));
                var temporaryPath = $"{purchasePath}.tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(purchase, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, purchasePath, overwrite: true);
                purchase.PossessionProofPdfSourcePath = null;
                purchase.PossessionProofNativeSourcePath = null;
                purchase.PaymentProofPdfSourcePath = null;
                purchase.PaymentProofNativeSourcePath = null;
                purchase.PurchaseProofPdfSourcePath = null;
                purchase.PurchaseProofNativeSourcePath = null;
                purchase.StorageFolderName = folderName;
                existingNames.Add(folderName);
            }
            catch
            {
                purchase.PossessionProofPdfFile = oldProofFiles[0];
                purchase.PossessionProofNativeFile = oldProofFiles[1];
                purchase.PaymentProofPdfFile = oldProofFiles[2];
                purchase.PaymentProofNativeFile = oldProofFiles[3];
                purchase.PurchaseProofPdfFile = oldProofFiles[4];
                purchase.PurchaseProofNativeFile = oldProofFiles[5];
                foreach (var copiedFile in copiedFiles)
                {
                    if (File.Exists(copiedFile))
                    {
                        File.Delete(copiedFile);
                    }
                }

                throw;
            }
        }
    }

    public static void Delete(string folderPath, PurchaseRecord purchase)
    {
        var root = GetRootPath(folderPath);
        var folderName = purchase.StorageFolderName;
        if (string.IsNullOrWhiteSpace(folderName)
            || folderName is "." or ".."
            || !string.Equals(Path.GetFileName(folderName), folderName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The purchase folder name is invalid.");
        }

        var purchaseFolder = Path.Combine(root, folderName);
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
            storedPurchase.PossessionProofFile,
            storedPurchase.PaymentProofFile,
            storedPurchase.PurchaseProofFile
        }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => GetEvidenceFilePath(folderPath, storedPurchase, path!))
            .ToList();
        foreach (var evidenceFile in evidenceFiles)
        {
            if (File.Exists(evidenceFile))
            {
                File.Delete(evidenceFile);
            }
        }

        File.Delete(purchasePath);
        var evidenceFolder = Path.Combine(purchaseFolder, "evidence");
        if (Directory.Exists(evidenceFolder) && !Directory.EnumerateFileSystemEntries(evidenceFolder).Any())
        {
            Directory.Delete(evidenceFolder);
        }

        if (!Directory.EnumerateFileSystemEntries(purchaseFolder).Any())
        {
            Directory.Delete(purchaseFolder);
        }

        purchase.StorageFolderName = null;
    }

    public static string GetEvidenceFilePath(
        string folderPath,
        PurchaseRecord purchase,
        string relativeFilePath)
    {
        if (string.IsNullOrWhiteSpace(purchase.StorageFolderName)
            || string.IsNullOrWhiteSpace(relativeFilePath)
            || Path.IsPathRooted(relativeFilePath))
        {
            throw new InvalidDataException("The evidence file path is invalid.");
        }

        var purchaseFolder = Path.GetFullPath(Path.Combine(GetRootPath(folderPath), purchase.StorageFolderName));
        var evidenceFolder = Path.GetFullPath(Path.Combine(purchaseFolder, "evidence"))
            + Path.DirectorySeparatorChar;
        var evidenceFile = Path.GetFullPath(Path.Combine(purchaseFolder, relativeFilePath));
        if (!evidenceFile.StartsWith(evidenceFolder, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The evidence file must be inside the purchase folder's evidence directory.");
        }

        return evidenceFile;
    }

    public static string GetPurchaseReportFilePath(string folderPath, PurchaseRecord purchase)
    {
        var folderName = purchase.StorageFolderName;
        if (string.IsNullOrWhiteSpace(folderName)
            || folderName is "." or ".."
            || !string.Equals(Path.GetFileName(folderName), folderName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The purchase folder name is invalid.");
        }

        return Path.Combine(
            GetRootPath(folderPath),
            folderName,
            CreatePurchaseReportFileName(purchase));
    }

    private static List<PurchaseRecord> ReadPurchaseFolders(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var purchases = new List<PurchaseRecord>();
        var identifiers = new HashSet<Guid>();
        foreach (var purchaseFolder in Directory.EnumerateDirectories(root))
        {
            var purchasePath = FindPurchaseDataFile(purchaseFolder);
            if (purchasePath is null)
            {
                continue;
            }

            var purchase = JsonSerializer.Deserialize<PurchaseRecord>(File.ReadAllText(purchasePath))
                ?? throw new InvalidDataException($"The purchase record in \"{purchaseFolder}\" is empty or invalid.");
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

            if (!identifiers.Add(purchase.Id))
            {
                throw new InvalidDataException($"Duplicate purchase identifier found in \"{purchaseFolder}\".");
            }

            purchase.StorageFolderName = Path.GetFileName(purchaseFolder);
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
        var date = purchase.PurchaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var store = SanitizeFolderPart(purchase.Store, "Unknown store");
        return $"{date} - {store} - purchase-data.json";
    }

    private static string CreatePurchaseReportFileName(PurchaseRecord purchase)
    {
        var date = purchase.PurchaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var store = SanitizeFolderPart(purchase.Store, "Unknown store");
        return $"{date} - {store} - purchase-report.pdf";
    }

    private static void ValidatePurchase(PurchaseRecord purchase)
    {
        if (purchase is null
            || purchase.Id == Guid.Empty
            || purchase.PurchaseDate == default
            || string.IsNullOrWhiteSpace(purchase.Store)
            || purchase.Remark is null
            || purchase.Price < 0
            || purchase.Songs is null
            || purchase.Songs.Any(song => song is null || string.IsNullOrWhiteSpace(song.FilePath)))
        {
            throw new InvalidDataException("A purchase record contains invalid data.");
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
        var date = purchase.PurchaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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

    private static string CreateFolderName(PurchaseRecord purchase, HashSet<string> existingNames)
    {
        var store = SanitizeFolderPart(purchase.Store, "Unknown store");
        var remark = SanitizeFolderPart(
            string.IsNullOrWhiteSpace(purchase.Remark) ? "No remark" : purchase.Remark,
            "No remark");
        var date = purchase.PurchaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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

    private static string GetLegacyArchivePath(string root)
    {
        var archivePath = Path.Combine(root, "purchases.legacy.json");
        return File.Exists(archivePath) || Directory.Exists(archivePath)
            ? Path.Combine(root, $"purchases.legacy-{Guid.NewGuid():N}.json")
            : archivePath;
    }
}
