using System.IO;
using System.Security;
using PdfSharp.Pdf.IO;

namespace MusicValidator;

internal sealed record PurchaseCheckResult(
    bool IsValid,
    bool HasCompleteEvidence,
    bool IsManuallyValidated,
    IReadOnlyList<string> Issues)
{
    public string Status => !IsValid
        ? "Needs attention"
        : IsManuallyValidated ? "Validated manually" : "Validated";

    public string Summary => Issues.Count > 0
        ? string.Join(Environment.NewLine, Issues)
        : IsManuallyValidated
            ? "Manually validated. Required proof may be waived."
            : "All purchase checks passed.";
}

internal static class PurchaseChecker
{
    public static void CheckAll(string purchaseStorageFolder, IEnumerable<PurchaseRecord> purchases)
    {
        var records = purchases.ToList();
        var results = records
            .Select(purchase => Check(purchaseStorageFolder, purchase))
            .ToList();

        for (var index = 0; index < records.Count; index++)
        {
            records[index].SetCheckResult(results[index]);
        }
    }

    public static void CheckOne(string purchaseStorageFolder, PurchaseRecord purchase)
    {
        purchase.SetCheckResult(Check(purchaseStorageFolder, purchase));
    }

    private static PurchaseCheckResult Check(string purchaseStorageFolder, PurchaseRecord purchase)
    {
        var issues = new List<string>();
        if (!purchase.IsUnknownPhysicalAlbum && !purchase.PurchaseDate.HasValue)
        {
            issues.Add("Purchase date is missing.");
        }

        if (!purchase.IsUnknownPhysicalAlbum && string.IsNullOrWhiteSpace(purchase.Store))
        {
            issues.Add("Store is missing.");
        }

        if (string.IsNullOrWhiteSpace(purchase.Remark))
        {
            issues.Add("Remark is required.");
        }

        if (!purchase.IsUnknownPhysicalAlbum && !purchase.IsPriceUnknown && !purchase.Price.HasValue)
        {
            issues.Add("Price is missing.");
        }
        else if (!purchase.IsUnknownPhysicalAlbum && purchase.IsPriceUnknown && purchase.Price.HasValue)
        {
            issues.Add("Price is marked unknown but a price is present.");
        }
        else if (purchase.Price < 0)
        {
            issues.Add("Price cannot be negative.");
        }

        if (!purchase.Kind.HasValue)
        {
            issues.Add("Music medium is missing.");
        }

        if (!purchase.IsFreeDigitalRelease && !purchase.PaymentMethod.HasValue)
        {
            issues.Add("Payment method is missing.");
        }

        if (purchase.IsFreeDigitalRelease
            && (purchase.Kind != PurchaseKind.Digital
                || purchase.PaymentMethod.HasValue
                || purchase.Price != 0
                || purchase.IsPriceUnknown))
        {
            issues.Add("A free release must be digital, have a price of zero, and have no payment method.");
        }

        if (purchase.IsUnknownPhysicalAlbum && purchase.Kind != PurchaseKind.Physical)
        {
            issues.Add("Unknown purchase details are only valid for a physical album.");
        }

        if (purchase.IsUnknownPhysicalAlbum && purchase.PaymentMethod != PaymentMethod.Cash)
        {
            issues.Add("An unknown physical album must use cash as the payment method.");
        }

        var allEvidenceFilesValid = true;
        var purchaseProofRequired = !purchase.IsUnknownPhysicalAlbum && !purchase.IsFreeDigitalRelease;
        var purchaseProof = CheckEvidenceGroup(
            purchaseStorageFolder,
            purchase,
            "Proof of purchase",
            purchase.PurchaseProofPdfFile,
            purchase.PurchaseProofNativeFile,
            purchase.PurchaseProofFile,
            isRequired: purchaseProofRequired,
            ref allEvidenceFilesValid,
            issues);
        var freeReleaseProof = CheckEvidenceGroup(
            purchaseStorageFolder,
            purchase,
            "Proof of free release",
            purchase.FreeReleaseProofPdfFile,
            purchase.FreeReleaseProofNativeFile,
            legacyFile: null,
            isRequired: purchase.IsFreeDigitalRelease,
            ref allEvidenceFilesValid,
            issues);
        var possessionProof = CheckEvidenceGroup(
            purchaseStorageFolder,
            purchase,
            "Proof of ownership",
            purchase.PossessionProofPdfFile,
            purchase.PossessionProofNativeFile,
            purchase.PossessionProofFile,
            isRequired: purchase.Kind == PurchaseKind.Physical,
            ref allEvidenceFilesValid,
            issues);
        var paymentProof = CheckEvidenceGroup(
            purchaseStorageFolder,
            purchase,
            "Proof of payment",
            purchase.PaymentProofPdfFile,
            purchase.PaymentProofNativeFile,
            purchase.PaymentProofFile,
            isRequired: !purchase.IsFreeDigitalRelease
                && purchase.PaymentMethod == PaymentMethod.Digital,
            ref allEvidenceFilesValid,
            issues);

        var hasCompleteEvidence =
            purchaseProof.IsComplete
            && freeReleaseProof.IsComplete
            && possessionProof.IsComplete
            && paymentProof.IsComplete
            && allEvidenceFilesValid;

        if (!purchase.IsUnknownPhysicalAlbum
            && !purchase.IsFreeDigitalRelease
            && !purchase.ManuallyValidated
            && !purchaseProof.IsComplete
            && !purchaseProof.HasEvidence)
        {
            issues.Add("Proof of purchase is required.");
        }

        if (purchase.IsFreeDigitalRelease
            && !freeReleaseProof.IsComplete
            && !freeReleaseProof.HasEvidence)
        {
            issues.Add("Proof of free release is required.");
        }

        if ((!purchase.ManuallyValidated || purchase.IsUnknownPhysicalAlbum)
            && purchase.Kind == PurchaseKind.Physical
            && !possessionProof.IsComplete
            && !possessionProof.HasEvidence)
        {
            issues.Add("Proof of ownership is required for a physical purchase.");
        }

        if (!purchase.ManuallyValidated
            && purchase.PaymentMethod == PaymentMethod.Digital
            && !paymentProof.IsComplete
            && !paymentProof.HasEvidence)
        {
            issues.Add("Proof of payment is required for a digital payment.");
        }

        var isManuallyValidated = purchase.ManuallyValidated && !purchase.IsUnknownPhysicalAlbum;
        var isValid = issues.Count == 0
            && (isManuallyValidated || hasCompleteEvidence);
        return new PurchaseCheckResult(
            isValid,
            hasCompleteEvidence,
            isManuallyValidated,
            issues);
    }

    private static EvidenceGroupResult CheckEvidenceGroup(
        string purchaseStorageFolder,
        PurchaseRecord purchase,
        string label,
        string? pdfFile,
        string? nativeFile,
        string? legacyFile,
        bool isRequired,
        ref bool allEvidenceFilesValid,
        ICollection<string> issues)
    {
        var hasEvidence = false;
        var hasValidEvidence = false;

        if (!string.IsNullOrWhiteSpace(pdfFile))
        {
            hasEvidence = true;
            var isValid = CheckEvidenceFile(
                purchaseStorageFolder,
                purchase,
                $"{label} PDF",
                pdfFile,
                isPdf: true,
                issues);
            hasValidEvidence |= isValid;
            allEvidenceFilesValid &= isValid;
        }

        if (!string.IsNullOrWhiteSpace(nativeFile))
        {
            hasEvidence = true;
            var isValid = CheckEvidenceFile(
                purchaseStorageFolder,
                purchase,
                $"{label} file",
                nativeFile,
                isPdf: false,
                issues);
            hasValidEvidence |= isValid;
            allEvidenceFilesValid &= isValid;
        }

        if (!string.IsNullOrWhiteSpace(legacyFile))
        {
            hasEvidence = true;
            var isPdf = string.Equals(
                Path.GetExtension(legacyFile),
                ".pdf",
                StringComparison.OrdinalIgnoreCase);
            var isValid = CheckEvidenceFile(
                purchaseStorageFolder,
                purchase,
                label,
                legacyFile,
                isPdf,
                issues);
            hasValidEvidence |= isValid;
            allEvidenceFilesValid &= isValid;
        }

        var isComplete = !isRequired || (hasEvidence && hasValidEvidence);
        return new EvidenceGroupResult(isComplete, hasEvidence);
    }

    private static bool CheckEvidenceFile(
        string purchaseStorageFolder,
        PurchaseRecord purchase,
        string label,
        string relativeFilePath,
        bool isPdf,
        ICollection<string> issues)
    {
        string filePath;
        try
        {
            filePath = PurchaseStore.GetEvidenceFilePath(
                purchaseStorageFolder,
                purchase,
                relativeFilePath);
        }
        catch (InvalidDataException)
        {
            issues.Add($"{label} has an invalid file path.");
            return false;
        }
        catch (ArgumentException)
        {
            issues.Add($"{label} has an invalid file path.");
            return false;
        }
        catch (NotSupportedException)
        {
            issues.Add($"{label} has an invalid file path.");
            return false;
        }
        catch (IOException)
        {
            issues.Add($"{label} could not be located.");
            return false;
        }

        var fileName = Path.GetFileName(relativeFilePath);
        if (!File.Exists(filePath))
        {
            issues.Add($"{label} is missing: {fileName}.");
            return false;
        }

        var hasPdfExtension = string.Equals(
            Path.GetExtension(filePath),
            ".pdf",
            StringComparison.OrdinalIgnoreCase);
        if (hasPdfExtension != isPdf)
        {
            issues.Add(isPdf
                ? $"{label} must have a .pdf extension."
                : $"{label} must be a non-PDF file.");
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length == 0)
            {
                issues.Add($"{label} is empty: {fileName}.");
                return false;
            }

            if (isPdf)
            {
                using var document = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);
                if (document.Pages.Count == 0)
                {
                    issues.Add($"{label} has no pages: {fileName}.");
                    return false;
                }
            }

            return true;
        }
        catch (PdfReaderException)
        {
            issues.Add($"{label} is not a readable PDF: {fileName}.");
            return false;
        }
        catch (IOException)
        {
            issues.Add($"{label} could not be read: {fileName}.");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            issues.Add($"{label} could not be accessed: {fileName}.");
            return false;
        }
        catch (SecurityException)
        {
            issues.Add($"{label} could not be accessed: {fileName}.");
            return false;
        }
    }

    private sealed record EvidenceGroupResult(bool IsComplete, bool HasEvidence);
}
