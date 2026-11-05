using System.Globalization;
using System.IO;
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

    internal AddPurchaseWindow(IEnumerable<string> previousStores, PurchaseRecord? purchaseToEdit = null)
    {
        InitializeComponent();
        _existingPurchase = purchaseToEdit;
        StoreComboBox.ItemsSource = previousStores
            .Where(store => !string.IsNullOrWhiteSpace(store))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        PurchaseDatePicker.SelectedDate = DateTime.Today;
        PriceTextBox.Text = 0m.ToString("0.00", CultureInfo.CurrentCulture);
        if (_existingPurchase is not null)
        {
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

    private void PaymentMethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProofVisibility();
    }

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
        var store = StoreComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(store))
        {
            ShowInputError("Enter the store where the songs were purchased.");
            StoreComboBox.Focus();
            return;
        }

        if (PurchaseDatePicker.SelectedDate is not DateTime purchaseDate)
        {
            ShowInputError("Select the purchase date.");
            PurchaseDatePicker.Focus();
            return;
        }

        if (!decimal.TryParse(
                PriceTextBox.Text,
                NumberStyles.Currency,
                CultureInfo.CurrentCulture,
                out var price)
            || price < 0)
        {
            ShowInputError("Enter a valid, non-negative purchase price.");
            PriceTextBox.Focus();
            return;
        }

        if (!Enum.TryParse<PurchaseKind>(
                (PurchaseKindComboBox.SelectedItem as ComboBoxItem)?.Tag as string,
                out var purchaseKind))
        {
            ShowInputError("Select whether this was a physical or digital purchase.");
            PurchaseKindComboBox.Focus();
            return;
        }

        if (!Enum.TryParse<PaymentMethod>(
                (PaymentMethodComboBox.SelectedItem as ComboBoxItem)?.Tag as string,
                out var paymentMethod))
        {
            ShowInputError("Select whether the payment was cash or digital.");
            PaymentMethodComboBox.Focus();
            return;
        }

        var existingPurchase = _existingPurchase;
        Purchase = new PurchaseRecord
        {
            Id = existingPurchase?.Id ?? Guid.NewGuid(),
            PurchaseDate = purchaseDate.Date,
            Store = store,
            Remark = RemarkTextBox.Text.Trim(),
            Price = price,
            Kind = purchaseKind,
            PaymentMethod = paymentMethod,
            ManuallyValidated = ManualValidationCheckBox.IsChecked == true,
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
        StoreComboBox.Text = purchase.Store;
        PriceTextBox.Text = purchase.Price.ToString("0.00", CultureInfo.CurrentCulture);
        RemarkTextBox.Text = purchase.Remark;
        SelectComboItem(PurchaseKindComboBox, purchase.Kind?.ToString());
        SelectComboItem(PaymentMethodComboBox, purchase.PaymentMethod?.ToString());
        ManualValidationCheckBox.IsChecked = purchase.ManuallyValidated;

        PurchaseDatePicker.IsEnabled = false;
        StoreComboBox.IsEnabled = false;
        PriceTextBox.IsEnabled = false;
        RemarkTextBox.IsEnabled = false;
        PurchaseKindComboBox.IsEnabled = !purchase.Kind.HasValue;
        PaymentMethodComboBox.IsEnabled = !purchase.PaymentMethod.HasValue;

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
        PossessionProofFileText.Text = FormatProofFileNames(
            _possessionProofPdfSourcePath ?? _existingPurchase?.PossessionProofPdfFile ?? _existingPurchase?.PossessionProofFile,
            _possessionProofNativeSourcePath ?? _existingPurchase?.PossessionProofNativeFile);
    }

    private void UpdatePaymentProofFileText()
    {
        PaymentProofFileText.Text = FormatProofFileNames(
            _paymentProofPdfSourcePath ?? _existingPurchase?.PaymentProofPdfFile ?? _existingPurchase?.PaymentProofFile,
            _paymentProofNativeSourcePath ?? _existingPurchase?.PaymentProofNativeFile);
    }

    private void UpdatePurchaseProofFileText()
    {
        PurchaseProofFileText.Text = FormatProofFileNames(
            _purchaseProofPdfSourcePath ?? _existingPurchase?.PurchaseProofPdfFile ?? _existingPurchase?.PurchaseProofFile,
            _purchaseProofNativeSourcePath ?? _existingPurchase?.PurchaseProofNativeFile);
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
        PossessionProofPanel.Visibility = purchaseKind == nameof(PurchaseKind.Physical)
            ? Visibility.Visible
            : Visibility.Collapsed;
        PaymentProofPanel.Visibility = paymentMethod == nameof(PaymentMethod.Digital)
            ? Visibility.Visible
            : Visibility.Collapsed;
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
}
