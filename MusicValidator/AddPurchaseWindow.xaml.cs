using System.Globalization;
using System.Windows;

namespace MusicValidator;

public partial class AddPurchaseWindow : Window
{
    internal PurchaseRecord? Purchase { get; private set; }

    internal AddPurchaseWindow()
    {
        InitializeComponent();
        PurchaseDatePicker.SelectedDate = DateTime.Today;
        PriceTextBox.Text = 0m.ToString("0.00", CultureInfo.CurrentCulture);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var store = StoreTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(store))
        {
            ShowInputError("Enter the store where the songs were purchased.");
            StoreTextBox.Focus();
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

        Purchase = new PurchaseRecord
        {
            PurchaseDate = purchaseDate.Date,
            Store = store,
            Price = price,
            Songs = []
        };
        DialogResult = true;
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
