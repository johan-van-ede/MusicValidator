using System.Globalization;
using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace MusicValidator;

internal static class PurchaseReportGenerator
{
    public static string Save(string purchaseStorageFolder, PurchaseRecord purchase)
    {
        var reportPath = PurchaseStore.GetPurchaseReportFilePath(purchaseStorageFolder, purchase);
        var temporaryPath = $"{reportPath}.tmp";

        try
        {
            using var document = new PdfDocument();
            document.Info.Title = $"Purchase report - {purchase.Store}";
            AddPurchaseSummary(document, purchase);
            AppendProofPdfs(document, purchaseStorageFolder, purchase);
            document.Save(temporaryPath);
            File.Move(temporaryPath, reportPath, overwrite: true);
            return reportPath;
        }
        catch (PdfReaderException exception)
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw new InvalidDataException("A proof PDF could not be included in the purchase report.", exception);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static void AddPurchaseSummary(PdfDocument document, PurchaseRecord purchase)
    {
        using var writer = new ReportPageWriter(document);
        writer.WriteHeading("Purchase report", 18);
        writer.WriteField("Purchase date", purchase.PurchaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.WriteField("Store", purchase.Store);
        writer.WriteField("Remark", string.IsNullOrWhiteSpace(purchase.Remark) ? "None" : purchase.Remark);
        writer.WriteField("Price", purchase.Price.ToString("C", CultureInfo.CurrentCulture));
        writer.WriteField("Purchase type", purchase.Kind?.ToString() ?? "Not specified");
        writer.WriteField("Payment method", purchase.PaymentMethod?.ToString() ?? "Not specified");
        writer.WriteField("Validation", purchase.ValidationStatus);

        writer.WriteHeading("Evidence", 13);
        var evidenceFiles = GetEvidenceFiles(purchase).ToList();
        if (evidenceFiles.Count == 0)
        {
            writer.WriteLine("No evidence files attached.");
        }
        else
        {
            foreach (var evidence in evidenceFiles)
            {
                var location = evidence.IsPdf
                    ? "PDF pages are appended to this report."
                    : "Native file is stored separately in the evidence folder.";
                writer.WriteLine($"{evidence.Label}: {Path.GetFileName(evidence.RelativePath)}. {location}");
            }
        }

        writer.WriteHeading($"Songs ({purchase.Songs.Count:N0})", 13);
        if (purchase.Songs.Count == 0)
        {
            writer.WriteLine("No songs are assigned to this purchase.");
        }
        else
        {
            for (var index = 0; index < purchase.Songs.Count; index++)
            {
                var song = purchase.Songs[index];
                var trackNumber = song.TrackNumber is uint number
                    ? $"Track {number}: "
                    : string.Empty;
                var details = string.Join(
                    " | ",
                    new[]
                    {
                        song.Title,
                        song.Artist,
                        song.Album,
                        song.Duration,
                        song.FilePath
                    }.Where(value => !string.IsNullOrWhiteSpace(value)));
                writer.WriteLine($"{index + 1:N0}. {trackNumber}{details}");
                if (!string.IsNullOrWhiteSpace(song.MetadataError))
                {
                    writer.WriteLine($"Metadata note: {song.MetadataError}", 12);
                }
            }
        }
    }

    private static void AppendProofPdfs(
        PdfDocument report,
        string purchaseStorageFolder,
        PurchaseRecord purchase)
    {
        foreach (var evidence in GetEvidenceFiles(purchase).Where(file => file.IsPdf))
        {
            var filePath = PurchaseStore.GetEvidenceFilePath(
                purchaseStorageFolder,
                purchase,
                evidence.RelativePath);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    $"The {evidence.Label} PDF could not be found.",
                    filePath);
            }

            using var source = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);
            AddAttachmentHeading(report, evidence.Label, Path.GetFileName(filePath));
            foreach (var page in source.Pages)
            {
                report.AddPage(page);
            }
        }
    }

    private static void AddAttachmentHeading(PdfDocument document, string label, string fileName)
    {
        var page = document.AddPage();
        using var graphics = XGraphics.FromPdfPage(page);
        var headingFont = new XFont("Arial", 16, XFontStyleEx.Bold);
        var bodyFont = new XFont("Arial", 10);
        graphics.DrawString(
            label,
            headingFont,
            XBrushes.Black,
            new XPoint(42, 52),
            XStringFormats.TopLeft);
        graphics.DrawString(
            $"Attached PDF: {fileName}",
            bodyFont,
            XBrushes.Black,
            new XPoint(42, 82),
            XStringFormats.TopLeft);
    }

    private static IEnumerable<EvidenceFile> GetEvidenceFiles(PurchaseRecord purchase)
    {
        if (!string.IsNullOrWhiteSpace(purchase.PossessionProofPdfFile))
        {
            yield return new EvidenceFile("Proof of ownership", purchase.PossessionProofPdfFile, true);
        }

        if (!string.IsNullOrWhiteSpace(purchase.PossessionProofNativeFile))
        {
            yield return new EvidenceFile("Proof of ownership (native file)", purchase.PossessionProofNativeFile, false);
        }

        if (!string.IsNullOrWhiteSpace(purchase.PaymentProofPdfFile))
        {
            yield return new EvidenceFile("Proof of payment", purchase.PaymentProofPdfFile, true);
        }

        if (!string.IsNullOrWhiteSpace(purchase.PaymentProofNativeFile))
        {
            yield return new EvidenceFile("Proof of payment (native file)", purchase.PaymentProofNativeFile, false);
        }

        if (!string.IsNullOrWhiteSpace(purchase.PurchaseProofPdfFile))
        {
            yield return new EvidenceFile("Proof of purchase", purchase.PurchaseProofPdfFile, true);
        }

        if (!string.IsNullOrWhiteSpace(purchase.PurchaseProofNativeFile))
        {
            yield return new EvidenceFile("Proof of purchase (native file)", purchase.PurchaseProofNativeFile, false);
        }
    }

    private sealed record EvidenceFile(string Label, string RelativePath, bool IsPdf);

    private sealed class ReportPageWriter : IDisposable
    {
        private const double Margin = 42;
        private readonly PdfDocument _document;
        private PdfPage _page;
        private XGraphics _graphics;
        private double _cursorY;

        public ReportPageWriter(PdfDocument document)
        {
            _document = document;
            _page = document.AddPage();
            _graphics = XGraphics.FromPdfPage(_page);
            _cursorY = Margin;
        }

        public void WriteHeading(string text, double fontSize)
        {
            var font = new XFont("Arial", fontSize, XFontStyleEx.Bold);
            WriteText(text, font, fontSize * 1.8);
            _cursorY += 7;
        }

        public void WriteField(string label, string value)
        {
            WriteLine($"{label}: {value}");
        }

        public void WriteLine(string text, double lineHeight = 14)
        {
            var font = new XFont("Arial", 9);
            foreach (var line in WrapText(text, font))
            {
                EnsureSpace(lineHeight);
                _graphics.DrawString(
                    line,
                    font,
                    XBrushes.Black,
                    new XPoint(Margin, _cursorY),
                    XStringFormats.TopLeft);
                _cursorY += lineHeight;
            }
        }

        public void Dispose()
        {
            _graphics.Dispose();
        }

        private void WriteText(string text, XFont font, double lineHeight)
        {
            EnsureSpace(lineHeight);
            _graphics.DrawString(
                text,
                font,
                XBrushes.Black,
                new XPoint(Margin, _cursorY),
                XStringFormats.TopLeft);
            _cursorY += lineHeight;
        }

        private IEnumerable<string> WrapText(string text, XFont font)
        {
            var maxWidth = _page.Width.Point - Margin * 2;
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var currentLine = string.Empty;

            foreach (var word in words)
            {
                var candidate = currentLine.Length == 0 ? word : $"{currentLine} {word}";
                if (_graphics.MeasureString(candidate, font).Width <= maxWidth)
                {
                    currentLine = candidate;
                    continue;
                }

                if (currentLine.Length > 0)
                {
                    yield return currentLine;
                    currentLine = string.Empty;
                }

                if (_graphics.MeasureString(word, font).Width <= maxWidth)
                {
                    currentLine = word;
                    continue;
                }

                var fragment = string.Empty;
                foreach (var character in word)
                {
                    var next = fragment + character;
                    if (fragment.Length > 0 && _graphics.MeasureString(next, font).Width > maxWidth)
                    {
                        yield return fragment;
                        fragment = character.ToString();
                    }
                    else
                    {
                        fragment = next;
                    }
                }

                currentLine = fragment;
            }

            if (currentLine.Length > 0)
            {
                yield return currentLine;
            }
        }

        private void EnsureSpace(double height)
        {
            if (_cursorY + height <= _page.Height.Point - Margin)
            {
                return;
            }

            _graphics.Dispose();
            _page = _document.AddPage();
            _graphics = XGraphics.FromPdfPage(_page);
            _cursorY = Margin;
        }
    }
}
