using System.Globalization;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MusicValidator;

public partial class AddPurchaseWindow : Window
{
    private readonly PurchaseRecord? _existingPurchase;
    private string? _possessionProofPdfSourcePath;
    private string? _possessionProofNativeSourcePath;
    private string? _paymentProofPdfSourcePath;
    private string? _paymentProofNativeSourcePath;
    private string? _purchaseProofPdfSourcePath;
    private string? _purchaseProofNativeSourcePath;

    internal PurchaseRecord? Purchase { get; private set; }

    internal AddPurchaseWindow(IEnumerable<string?> previousStores, PurchaseRecord? purchaseToEdit = null)
    {
        InitializeComponent();
        _existingPurchase = purchaseToEdit;
        RemarkTextBox.TextChanged += RemarkTextBox_TextChanged;
        UpdateRemarkCharacterCount();
        StoreComboBox.ItemsSource = previousStores
            .Where(store => !string.IsNullOrWhiteSpace(store))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        PurchaseDatePicker.SelectedDate = DateTime.Today;
        PriceTextBox.Text = 0m.ToString("0.00", CultureInfo.CurrentCulture);
        if (_existingPurchase is not null)
        {
            LegacyImportButton.Visibility = Visibility.Collapsed;
            PopulateExistingPurchase(_existingPurchase);
        }

        UpdateProofVisibility();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void PurchaseKindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProofVisibility();
    }

    private void UnknownPhysicalAlbumCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (UnknownPhysicalAlbumCheckBox.IsChecked == true
            && ((PurchaseKindComboBox.SelectedItem as ComboBoxItem)?.Tag as string)
                != nameof(PurchaseKind.Physical))
        {
            UnknownPhysicalAlbumCheckBox.IsChecked = false;
            return;
        }

        UpdateUnknownPhysicalAlbumFields();
        UpdateProofVisibility();
    }

    private void PriceUnknownCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var isPriceUnknown = PriceUnknownCheckBox.IsChecked == true;
        PriceTextBox.Text = isPriceUnknown ? "Unknown" : string.Empty;
        PriceTextBox.IsEnabled = !isPriceUnknown && UnknownPhysicalAlbumCheckBox.IsChecked != true;
    }

    private void PaymentMethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProofVisibility();
    }

    private void ImportLegacy_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a legacy legality report",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var legacyInfo = ReadLegacyPurchaseInfo(dialog.FileName);
            UnknownPhysicalAlbumCheckBox.IsChecked = false;
            var importPossessionProof = legacyInfo.Evidence.HasPossessionProof
                && MessageBox.Show(
                    this,
                    "Was this purchase for a single album? Import the proof of possession from the legacy report?",
                    "Import proof of possession",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes;

            StoreComboBox.Text = legacyInfo.Store ?? string.Empty;
            SelectComboItem(PurchaseKindComboBox, legacyInfo.Kind?.ToString());
            PurchaseDatePicker.SelectedDate = legacyInfo.PurchaseDate;
            if (importPossessionProof)
            {
                _possessionProofPdfSourcePath = legacyInfo.Evidence.PossessionPdf;
                _possessionProofNativeSourcePath = legacyInfo.Evidence.PossessionNative;
            }

            _purchaseProofPdfSourcePath = legacyInfo.Evidence.PurchasePdf ?? _purchaseProofPdfSourcePath;
            _purchaseProofNativeSourcePath = legacyInfo.Evidence.PurchaseNative ?? _purchaseProofNativeSourcePath;
            _paymentProofPdfSourcePath = legacyInfo.Evidence.PaymentPdf ?? _paymentProofPdfSourcePath;
            _paymentProofNativeSourcePath = legacyInfo.Evidence.PaymentNative ?? _paymentProofNativeSourcePath;

            UpdatePossessionProofFileText();
            UpdatePurchaseProofFileText();
            UpdatePaymentProofFileText();

            var missingKindMessage = legacyInfo.Kind is null
                ? " The file has no purchase type; select it manually."
                : string.Empty;
            var missingEvidenceMessage = legacyInfo.Evidence.MissingFiles.Count == 0
                ? string.Empty
                : $"{Environment.NewLine}Evidence files not found in an Album folder: {string.Join(", ", legacyInfo.Evidence.MissingFiles)}.";
            MessageBox.Show(
                this,
                legacyInfo.PurchaseDate is null
                    ? $"Legacy information was loaded. The file has no known purchase date; enter it manually.{missingKindMessage} Check the imported store and enter the price, payment method, and remark.{missingEvidenceMessage}"
                    : $"Legacy information was loaded. Check the imported store and date, and enter the price, payment method, and remark.{missingKindMessage}{missingEvidenceMessage}",
                "Legacy information loaded",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (JsonException exception)
        {
            ShowLegacyImportError(exception);
        }
        catch (InvalidDataException exception)
        {
            ShowLegacyImportError(exception);
        }
        catch (IOException exception)
        {
            ShowLegacyImportError(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowLegacyImportError(exception);
        }
        catch (SecurityException exception)
        {
            ShowLegacyImportError(exception);
        }
    }

    private static LegacyPurchaseInfo ReadLegacyPurchaseInfo(string filePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        if (!TryGetProperty(document.RootElement, "Legalinfo", out var legalInfo))
        {
            throw new InvalidDataException("The selected file does not contain LegalInfo.");
        }

        JsonElement purchaseInfo;
        if (TryGetProperty(legalInfo, "Sourcemedium", out _)
            || TryGetProperty(legalInfo, "Purchasestore", out _)
            || TryGetProperty(legalInfo, "Purchasedate", out _))
        {
            purchaseInfo = legalInfo;
        }
        else if (TryGetProperty(legalInfo, "Legalgroups", out var groups)
            && groups.ValueKind == JsonValueKind.Array)
        {
            var albumGroups = groups
                .EnumerateArray()
                .Where(group =>
                    string.Equals(GetString(group, "Proofmethod"), "Album", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(GetString(group, "Groupname"), "Album", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (albumGroups.Count == 0 && groups.GetArrayLength() == 1)
            {
                albumGroups.Add(groups[0]);
            }

            if (albumGroups.Count != 1)
            {
                throw new InvalidDataException("The selected file does not contain one unambiguous album purchase group.");
            }

            purchaseInfo = albumGroups[0];
        }
        else
        {
            throw new InvalidDataException("The selected file does not contain purchase information in LegalInfo.");
        }

        var sourceMedium = GetString(purchaseInfo, "Sourcemedium");
        var kind = sourceMedium?.Trim().ToUpperInvariant() switch
        {
            "PHYSICAL" => PurchaseKind.Physical,
            "DIGITAL" => PurchaseKind.Digital,
            _ => (PurchaseKind?)null
        };
        if (!string.IsNullOrWhiteSpace(sourceMedium) && kind is null)
        {
            throw new InvalidDataException($"The legacy file contains an unsupported source medium: {sourceMedium}.");
        }

        var store = GetString(purchaseInfo, "Purchasestore")?.Trim();
        var dateUnknown = TryGetProperty(purchaseInfo, "Dateunknown", out var dateUnknownValue)
            && dateUnknownValue.ValueKind == JsonValueKind.True;
        DateTime? purchaseDate = null;
        var dateText = GetString(purchaseInfo, "Purchasedate");
        if (!dateUnknown && !string.IsNullOrWhiteSpace(dateText))
        {
            if (!DateTime.TryParse(
                    dateText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var parsedDate))
            {
                throw new InvalidDataException("The purchase date in the legacy file is invalid.");
            }

            if (parsedDate.Year > 1600)
            {
                purchaseDate = parsedDate.Date;
            }
        }

        if (kind is null && string.IsNullOrWhiteSpace(store) && purchaseDate is null)
        {
            throw new InvalidDataException("The album purchase group contains no usable purchase details.");
        }

        return new LegacyPurchaseInfo(kind, store, purchaseDate, ReadLegacyEvidenceFiles(purchaseInfo, filePath));
    }

    private static LegacyEvidenceFiles ReadLegacyEvidenceFiles(JsonElement purchaseInfo, string reportFilePath)
    {
        var result = new LegacyEvidenceFiles();
        if (!TryGetProperty(purchaseInfo, "Files", out var files)
            || files.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportFilePath))
            ?? throw new InvalidDataException("The legacy report has no containing folder.");
        var albumDirectories = new List<string>();
        if (string.Equals(Path.GetFileName(reportDirectory), "Album", StringComparison.OrdinalIgnoreCase))
        {
            albumDirectories.Add(reportDirectory);
        }

        albumDirectories.AddRange(
            Directory.EnumerateDirectories(reportDirectory, "Album", SearchOption.AllDirectories));

        foreach (var legacyFile in files.EnumerateArray())
        {
            if (TryGetProperty(legacyFile, "Excluded", out var excluded)
                && excluded.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            if (!TryGetProperty(legacyFile, "File", out var file)
                || file.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var proofType = GetLegacyProofType(GetString(legacyFile, "Filetype"));
            if (proofType is null)
            {
                continue;
            }

            var describedPath = GetString(file, "Customentrypath")
                ?? GetString(file, "Customfilepath");
            var fileName = string.IsNullOrWhiteSpace(describedPath)
                ? string.Empty
                : Path.GetFileName(describedPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var sourcePath = albumDirectories
                .Select(albumDirectory => Path.Combine(albumDirectory, fileName))
                .FirstOrDefault(File.Exists);
            if (sourcePath is null)
            {
                if (!result.MissingFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    result.MissingFiles.Add(fileName);
                }

                continue;
            }

            result.SetSource(proofType.Value, sourcePath);
        }

        return result;
    }

    private static LegacyProofType? GetLegacyProofType(string? fileType)
    {
        if (string.IsNullOrWhiteSpace(fileType))
        {
            return null;
        }

        var normalizedFileType = new string(fileType
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (normalizedFileType.Contains("PURCHASE", StringComparison.Ordinal))
        {
            return LegacyProofType.Purchase;
        }

        if (normalizedFileType.Contains("PAYMENT", StringComparison.Ordinal))
        {
            return LegacyProofType.Payment;
        }

        return normalizedFileType.Contains("POSSES", StringComparison.Ordinal)
            ? LegacyProofType.Possession
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void SelectProof_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string proofKind })
        {
            return;
        }

        var (proofSlot, pdfOnly) = proofKind switch
        {
            "PossessionPdf" => ("PossessionPdf", true),
            "PossessionFile" => ("PossessionNative", false),
            "PaymentPdf" => ("PaymentPdf", true),
            "PaymentFile" => ("PaymentNative", false),
            "PurchasePdf" => ("PurchasePdf", true),
            "PurchaseFile" => ("PurchaseNative", false),
            _ => (string.Empty, false)
        };
        if (proofSlot.Length == 0)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = pdfOnly ? "Select PDF evidence" : "Select evidence file",
            Filter = pdfOnly
                ? "PDF documents (*.pdf)|*.pdf"
                : "All files (*.*)|*.*",
            DefaultExt = pdfOnly ? ".pdf" : string.Empty,
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!pdfOnly && string.Equals(Path.GetExtension(dialog.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "Choose a non-PDF file for the native file slot.",
                "Select a native file",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        switch (proofSlot)
        {
            case "PossessionPdf":
                _possessionProofPdfSourcePath = dialog.FileName;
                UpdatePossessionProofFileText();
                break;
            case "PossessionNative":
                _possessionProofNativeSourcePath = dialog.FileName;
                UpdatePossessionProofFileText();
                break;
            case "PaymentPdf":
                _paymentProofPdfSourcePath = dialog.FileName;
                UpdatePaymentProofFileText();
                break;
            case "PaymentNative":
                _paymentProofNativeSourcePath = dialog.FileName;
                UpdatePaymentProofFileText();
                break;
            case "PurchasePdf":
                _purchaseProofPdfSourcePath = dialog.FileName;
                UpdatePurchaseProofFileText();
                break;
            case "PurchaseNative":
                _purchaseProofNativeSourcePath = dialog.FileName;
                UpdatePurchaseProofFileText();
                break;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var isUnknownPhysicalAlbum = UnknownPhysicalAlbumCheckBox.IsChecked == true;
        var store = StoreComboBox.Text.Trim();
        if (!isUnknownPhysicalAlbum && string.IsNullOrWhiteSpace(store))
        {
            ShowInputError("Enter the store where the songs were purchased.");
            StoreComboBox.Focus();
            return;
        }

        var purchaseDate = PurchaseDatePicker.SelectedDate;
        if (!isUnknownPhysicalAlbum && purchaseDate is null)
        {
            ShowInputError("Select the purchase date.");
            PurchaseDatePicker.Focus();
            return;
        }

        var existingPurchase = _existingPurchase;
        var remark = string.IsNullOrWhiteSpace(existingPurchase?.Remark)
            ? RemarkTextBox.Text.Trim()
            : existingPurchase.Remark;
        if (string.IsNullOrWhiteSpace(remark))
        {
            ShowInputError("Enter a remark; it is used in purchase evidence filenames.");
            RemarkTextBox.Focus();
            return;
        }

        var isPriceUnknown = isUnknownPhysicalAlbum || PriceUnknownCheckBox.IsChecked == true;
        decimal? price = null;
        if (!isPriceUnknown)
        {
            if (!decimal.TryParse(
                    PriceTextBox.Text,
                    NumberStyles.Currency,
                    CultureInfo.CurrentCulture,
                    out var parsedPrice)
                || parsedPrice < 0)
            {
                ShowInputError("Enter a valid, non-negative purchase price.");
                PriceTextBox.Focus();
                return;
            }

            price = parsedPrice;
        }

        if (!Enum.TryParse<PurchaseKind>(
                (PurchaseKindComboBox.SelectedItem as ComboBoxItem)?.Tag as string,
                out var purchaseKind))
        {
            ShowInputError("Select whether this was a physical or digital purchase.");
            PurchaseKindComboBox.Focus();
            return;
        }

        if (isUnknownPhysicalAlbum && purchaseKind != PurchaseKind.Physical)
        {
            ShowInputError("Unknown purchase details can only be used for a physical album.");
            PurchaseKindComboBox.Focus();
            return;
        }

        var paymentMethod = PaymentMethod.Cash;
        if (!isUnknownPhysicalAlbum
            && !Enum.TryParse<PaymentMethod>(
                (PaymentMethodComboBox.SelectedItem as ComboBoxItem)?.Tag as string,
                out paymentMethod))
        {
            ShowInputError("Select whether the payment was cash or digital.");
            PaymentMethodComboBox.Focus();
            return;
        }

        Purchase = new PurchaseRecord
        {
            Id = existingPurchase?.Id ?? Guid.NewGuid(),
            PurchaseDate = isUnknownPhysicalAlbum ? null : purchaseDate?.Date,
            Store = isUnknownPhysicalAlbum ? null : store,
            Remark = remark,
            Price = price,
            Kind = purchaseKind,
            PaymentMethod = paymentMethod,
            IsUnknownPhysicalAlbum = isUnknownPhysicalAlbum,
            IsPriceUnknown = isPriceUnknown,
            ManuallyValidated = !isUnknownPhysicalAlbum && ManualValidationCheckBox.IsChecked == true,
            PossessionProofPdfFile = existingPurchase?.PossessionProofPdfFile,
            PossessionProofNativeFile = existingPurchase?.PossessionProofNativeFile,
            PaymentProofPdfFile = existingPurchase?.PaymentProofPdfFile,
            PaymentProofNativeFile = existingPurchase?.PaymentProofNativeFile,
            PurchaseProofPdfFile = existingPurchase?.PurchaseProofPdfFile,
            PurchaseProofNativeFile = existingPurchase?.PurchaseProofNativeFile,
            PossessionProofFile = existingPurchase?.PossessionProofFile,
            PaymentProofFile = existingPurchase?.PaymentProofFile,
            PurchaseProofFile = existingPurchase?.PurchaseProofFile,
            PossessionProofPdfSourcePath = _possessionProofPdfSourcePath,
            PossessionProofNativeSourcePath = _possessionProofNativeSourcePath,
            PaymentProofPdfSourcePath = _paymentProofPdfSourcePath,
            PaymentProofNativeSourcePath = _paymentProofNativeSourcePath,
            PurchaseProofPdfSourcePath = _purchaseProofPdfSourcePath,
            PurchaseProofNativeSourcePath = _purchaseProofNativeSourcePath,
            Songs = existingPurchase?.Songs.ToList() ?? [],
            StorageFolderName = existingPurchase?.StorageFolderName
        };
        DialogResult = true;
    }

    private void PopulateExistingPurchase(PurchaseRecord purchase)
    {
        Title = "Update purchase";
        WindowTitleText.Text = "Update purchase";
        WindowDescriptionText.Text = "Replace or add evidence, or change the manual validation decision.";
        SavePurchaseButton.Content = "Save changes";

        PurchaseDatePicker.SelectedDate = purchase.PurchaseDate;
        StoreComboBox.Text = purchase.Store ?? string.Empty;
        PriceTextBox.Text = purchase.Price?.ToString("0.00", CultureInfo.CurrentCulture) ?? string.Empty;
        SelectComboItem(PurchaseKindComboBox, purchase.Kind?.ToString());
        SelectComboItem(PaymentMethodComboBox, purchase.PaymentMethod?.ToString());
        ManualValidationCheckBox.IsChecked = purchase.ManuallyValidated;
        UnknownPhysicalAlbumCheckBox.IsChecked = purchase.IsUnknownPhysicalAlbum;
        PriceUnknownCheckBox.IsChecked = purchase.IsPriceUnknown || !purchase.Price.HasValue;
        PriceTextBox.IsEnabled = !purchase.IsUnknownPhysicalAlbum && PriceUnknownCheckBox.IsChecked != true;

        PurchaseDateReadOnlyText.Text = purchase.PurchaseDate?.ToString("d", CultureInfo.CurrentCulture) ?? "Unknown";
        PurchaseDatePicker.Visibility = Visibility.Collapsed;
        PurchaseDateReadOnly.Visibility = Visibility.Visible;

        StoreReadOnlyText.Text = purchase.Store ?? "Unknown";
        StoreComboBox.Visibility = Visibility.Collapsed;
        StoreReadOnly.Visibility = Visibility.Visible;

        if (string.IsNullOrWhiteSpace(purchase.Remark))
        {
            RemarkTextBox.Text = string.Empty;
        }
        else
        {
            RemarkReadOnlyText.Text = purchase.Remark;
            RemarkInputGrid.Visibility = Visibility.Collapsed;
            RemarkReadOnly.Visibility = Visibility.Visible;
        }

        if (purchase.Kind.HasValue)
        {
            PurchaseKindReadOnlyText.Text = purchase.MusicMediumDisplay;
            PurchaseKindComboBox.Visibility = Visibility.Collapsed;
            PurchaseKindReadOnly.Visibility = Visibility.Visible;
        }

        if (purchase.PaymentMethod.HasValue)
        {
            PaymentMethodReadOnlyText.Text = purchase.PaymentMethod.Value.ToString();
            PaymentMethodComboBox.Visibility = Visibility.Collapsed;
            PaymentMethodReadOnly.Visibility = Visibility.Visible;
        }

        UpdatePossessionProofFileText();
        UpdatePaymentProofFileText();
        UpdatePurchaseProofFileText();
    }

    private static void SelectComboItem(ComboBox comboBox, string? value)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, value, StringComparison.Ordinal));
    }

    private void UpdatePossessionProofFileText()
    {
        var pdfPath = _possessionProofPdfSourcePath
            ?? _existingPurchase?.PossessionProofPdfFile
            ?? _existingPurchase?.PossessionProofFile;
        var nativePath = _possessionProofNativeSourcePath ?? _existingPurchase?.PossessionProofNativeFile;
        PossessionProofFileText.Text = FormatProofFileNames(pdfPath, nativePath);
        UpdateProofButton(PossessionProofPdfButton, "PDF", !string.IsNullOrWhiteSpace(pdfPath));
        UpdateProofButton(PossessionProofFileButton, "File", !string.IsNullOrWhiteSpace(nativePath));
    }

    private void UpdatePaymentProofFileText()
    {
        var pdfPath = _paymentProofPdfSourcePath
            ?? _existingPurchase?.PaymentProofPdfFile
            ?? _existingPurchase?.PaymentProofFile;
        var nativePath = _paymentProofNativeSourcePath ?? _existingPurchase?.PaymentProofNativeFile;
        PaymentProofFileText.Text = FormatProofFileNames(pdfPath, nativePath);
        UpdateProofButton(PaymentProofPdfButton, "PDF", !string.IsNullOrWhiteSpace(pdfPath));
        UpdateProofButton(PaymentProofFileButton, "File", !string.IsNullOrWhiteSpace(nativePath));
    }

    private void UpdatePurchaseProofFileText()
    {
        var pdfPath = _purchaseProofPdfSourcePath
            ?? _existingPurchase?.PurchaseProofPdfFile
            ?? _existingPurchase?.PurchaseProofFile;
        var nativePath = _purchaseProofNativeSourcePath ?? _existingPurchase?.PurchaseProofNativeFile;
        PurchaseProofFileText.Text = FormatProofFileNames(pdfPath, nativePath);
        UpdateProofButton(PurchaseProofPdfButton, "PDF", !string.IsNullOrWhiteSpace(pdfPath));
        UpdateProofButton(PurchaseProofFileButton, "File", !string.IsNullOrWhiteSpace(nativePath));
    }

    private static void UpdateProofButton(Button button, string label, bool isSelected)
    {
        button.Content = isSelected ? $"{label} ✓" : label;
    }

    private static string FormatProofFileNames(string? pdfFile, string? nativeFile)
    {
        var fileNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(pdfFile))
        {
            fileNames.Add($"PDF: {Path.GetFileName(pdfFile)}");
        }

        if (!string.IsNullOrWhiteSpace(nativeFile))
        {
            fileNames.Add($"File: {Path.GetFileName(nativeFile)}");
        }

        return fileNames.Count == 0 ? "No files selected" : string.Join(Environment.NewLine, fileNames);
    }

    private void UpdateProofVisibility()
    {
        var purchaseKind = (PurchaseKindComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var paymentMethod = (PaymentMethodComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var isPhysical = purchaseKind == nameof(PurchaseKind.Physical);
        var isUnknownPhysicalAlbum = UnknownPhysicalAlbumCheckBox.IsChecked == true;
        if (!isPhysical && _existingPurchase is null && isUnknownPhysicalAlbum)
        {
            UnknownPhysicalAlbumCheckBox.IsChecked = false;
            isUnknownPhysicalAlbum = false;
        }

        PossessionProofPanel.Visibility = purchaseKind == nameof(PurchaseKind.Physical)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UnknownPhysicalAlbumCheckBox.Visibility = isPhysical
            && (_existingPurchase is null || _existingPurchase.IsUnknownPhysicalAlbum)
                ? Visibility.Visible
                : Visibility.Collapsed;
        UnknownPhysicalAlbumCheckBox.IsEnabled = _existingPurchase is null;
        PurchaseProofRequiredIndicator.Visibility = isUnknownPhysicalAlbum
            ? Visibility.Collapsed
            : Visibility.Visible;
        PaymentProofPanel.Visibility = paymentMethod == nameof(PaymentMethod.Digital)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateUnknownPhysicalAlbumFields()
    {
        var isUnknownPhysicalAlbum = UnknownPhysicalAlbumCheckBox.IsChecked == true;
        PurchaseDatePicker.IsEnabled = !isUnknownPhysicalAlbum;
        StoreComboBox.IsEnabled = !isUnknownPhysicalAlbum;
        PriceTextBox.IsEnabled = !isUnknownPhysicalAlbum;
        PaymentMethodComboBox.IsEnabled = !isUnknownPhysicalAlbum;
        ManualValidationCheckBox.IsEnabled = !isUnknownPhysicalAlbum;
        if (isUnknownPhysicalAlbum)
        {
            PurchaseDatePicker.SelectedDate = null;
            StoreComboBox.Text = string.Empty;
            SelectComboItem(PaymentMethodComboBox, nameof(PaymentMethod.Cash));
            ManualValidationCheckBox.IsChecked = false;
            PriceTextBox.Text = "Unknown";
        }
        else if (PriceUnknownCheckBox.IsChecked != true)
        {
            PriceTextBox.Text = string.Empty;
        }

        PriceUnknownCheckBox.Visibility = isUnknownPhysicalAlbum
            ? Visibility.Collapsed
            : Visibility.Visible;
        PriceUnknownCheckBox.IsEnabled = !isUnknownPhysicalAlbum;
        PriceTextBox.IsEnabled = !isUnknownPhysicalAlbum && PriceUnknownCheckBox.IsChecked != true;
    }

    private void ShowInputError(string message)
    {
        MessageBox.Show(
            this,
            message,
            "Purchase details required",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ShowLegacyImportError(Exception exception)
    {
        MessageBox.Show(
            this,
            $"Could not import legacy purchase information.{Environment.NewLine}{exception.Message}",
            "Legacy information could not be loaded",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void RemarkTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateRemarkCharacterCount();
    }

    private void UpdateRemarkCharacterCount()
    {
        RemarkCharacterCountText.Text = $"{RemarkTextBox.Text.Length}/25";
    }

    private sealed record LegacyPurchaseInfo(
        PurchaseKind? Kind,
        string? Store,
        DateTime? PurchaseDate,
        LegacyEvidenceFiles Evidence);

    private enum LegacyProofType
    {
        Possession,
        Purchase,
        Payment
    }

    private sealed class LegacyEvidenceFiles
    {
        public string? PossessionPdf { get; private set; }
        public string? PossessionNative { get; private set; }
        public string? PurchasePdf { get; private set; }
        public string? PurchaseNative { get; private set; }
        public string? PaymentPdf { get; private set; }
        public string? PaymentNative { get; private set; }
        public List<string> MissingFiles { get; } = [];
        public bool HasPossessionProof => PossessionPdf is not null || PossessionNative is not null;

        public void SetSource(LegacyProofType proofType, string sourcePath)
        {
            var isPdf = string.Equals(Path.GetExtension(sourcePath), ".pdf", StringComparison.OrdinalIgnoreCase);
            switch (proofType, isPdf)
            {
                case (LegacyProofType.Possession, true) when PossessionPdf is null:
                    PossessionPdf = sourcePath;
                    break;
                case (LegacyProofType.Possession, false) when PossessionNative is null:
                    PossessionNative = sourcePath;
                    break;
                case (LegacyProofType.Purchase, true) when PurchasePdf is null:
                    PurchasePdf = sourcePath;
                    break;
                case (LegacyProofType.Purchase, false) when PurchaseNative is null:
                    PurchaseNative = sourcePath;
                    break;
                case (LegacyProofType.Payment, true) when PaymentPdf is null:
                    PaymentPdf = sourcePath;
                    break;
                case (LegacyProofType.Payment, false) when PaymentNative is null:
                    PaymentNative = sourcePath;
                    break;
            }
        }
    }
}
