using System.Windows;

namespace MusicValidator;

public partial class AssignPurchaseWindow : Window
{
    internal PurchaseRecord? Purchase { get; private set; }
    internal bool IsNewPurchase { get; private set; }

    internal AssignPurchaseWindow(IEnumerable<PurchaseRecord> purchases, IReadOnlyList<PurchaseSong> songs)
    {
        InitializeComponent();
        PurchaseComboBox.ItemsSource = purchases.ToList();
        if (PurchaseComboBox.Items.Count > 0)
        {
            PurchaseComboBox.SelectedIndex = 0;
        }

        SongsListBox.ItemsSource = songs;
        SongCountText.Text = $"{songs.Count:N0} songs selected";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (PurchaseComboBox.SelectedItem is not PurchaseRecord purchase)
        {
            MessageBox.Show(
                this,
                "Choose a purchase or create a new one.",
                "No purchase selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Purchase = purchase;
        DialogResult = true;
    }

    private void CreatePurchase_Click(object sender, RoutedEventArgs e)
    {
        var previousStores = PurchaseComboBox.ItemsSource
            .OfType<PurchaseRecord>()
            .Select(purchase => purchase.Store);
        var dialog = new AddPurchaseWindow(previousStores) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Purchase is PurchaseRecord purchase)
        {
            Purchase = purchase;
            IsNewPurchase = true;
            DialogResult = true;
        }
    }
}
