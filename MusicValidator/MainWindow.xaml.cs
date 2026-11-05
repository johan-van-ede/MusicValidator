using System.Collections.ObjectModel;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MusicValidator;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MusicTrack> _musicTracks = [];
    private readonly ObservableCollection<PurchaseRecord> _purchases = [];
    private AppSettings _settings = new();
    private string? _selectedFolder;

    public MainWindow()
    {
        InitializeComponent();
        MusicGrid.ItemsSource = _musicTracks;
        PurchasesGrid.ItemsSource = _purchases;

        try
        {
            _settings = SettingsStore.Load();
        }
        catch (JsonException exception)
        {
            ShowSettingsLoadError(exception);
        }
        catch (IOException exception)
        {
            ShowSettingsLoadError(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowSettingsLoadError(exception);
        }
        catch (SecurityException exception)
        {
            ShowSettingsLoadError(exception);
        }

        UpdatePurchaseFolderText();
        LoadPurchases();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        if (!string.IsNullOrWhiteSpace(_settings.MusicFolderPath))
        {
            await ScanFolderAsync(_settings.MusicFolderPath);
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a music folder"
        };

        if (dialog.ShowDialog(this) == true)
        {
            var updatedSettings = _settings.Copy();
            updatedSettings.MusicFolderPath = dialog.FolderName;
            _settings = updatedSettings;
            TrySaveSettings(updatedSettings);
            await ScanFolderAsync(dialog.FolderName);
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var updatedSettings = dialog.Settings;
        var purchaseFolderChanged = !string.Equals(
            updatedSettings.PurchaseStorageFolder,
            _settings.PurchaseStorageFolder,
            StringComparison.OrdinalIgnoreCase);
        List<PurchaseRecord>? updatedPurchases = null;
        if (purchaseFolderChanged)
        {
            if (!TryLoadPurchases(updatedSettings.PurchaseStorageFolder, out var loadedPurchases))
            {
                return;
            }

            updatedPurchases = loadedPurchases;
        }

        if (!TrySaveSettings(updatedSettings))
        {
            return;
        }

        _settings = updatedSettings;
        UpdatePurchaseFolderText();
        if (updatedPurchases is not null)
        {
            ReplacePurchases(updatedPurchases);
        }

        if (string.IsNullOrWhiteSpace(_settings.MusicFolderPath))
        {
            ClearMusicFolder();
        }
        else
        {
            await ScanFolderAsync(_settings.MusicFolderPath);
        }
    }

    private void AddPurchase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddPurchaseWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Purchase is not PurchaseRecord purchase)
        {
            return;
        }

        var updatedPurchases = _purchases.Prepend(purchase).ToList();
        if (!TrySavePurchases(updatedPurchases))
        {
            return;
        }

        _purchases.Insert(0, purchase);
        UpdatePurchaseCount();
        StatusText.Text = $"Purchase from {purchase.Store} saved.";
    }

    private void PurchasesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemovePurchaseButton.IsEnabled = PurchasesGrid.SelectedItem is PurchaseRecord;
    }

    private void RemovePurchase_Click(object sender, RoutedEventArgs e)
    {
        if (PurchasesGrid.SelectedItem is not PurchaseRecord purchase)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            $"Remove the purchase from {purchase.Store} on {purchase.PurchaseDate:d}?{Environment.NewLine}Its song associations will be removed, but the audio files will not be deleted.",
            "Remove purchase",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var updatedPurchases = _purchases
            .Where(item => item.Id != purchase.Id)
            .ToList();
        if (!TrySavePurchases(updatedPurchases))
        {
            return;
        }

        _purchases.Remove(purchase);
        UpdatePurchaseCount();
        StatusText.Text = $"Purchase from {purchase.Store} removed.";
    }

    private void PurchaseDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PurchaseRecord purchase })
        {
            new PurchaseDetailsWindow(purchase, SavePurchaseSongs) { Owner = this }.ShowDialog();
        }
    }

    private void MusicGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AssignToPurchaseButton.IsEnabled = MusicGrid.SelectedItems.Count > 0;
    }

    private void AssignToPurchase_Click(object sender, RoutedEventArgs e)
    {
        var songs = MusicGrid.SelectedItems
            .OfType<MusicTrack>()
            .Select(PurchaseSong.FromTrack)
            .ToList();
        if (songs.Count == 0)
        {
            return;
        }

        var dialog = new AssignPurchaseWindow(_purchases, songs) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Purchase is not PurchaseRecord purchase)
        {
            return;
        }

        var originalSongs = purchase.Songs;
        var existingPaths = originalSongs
            .Select(song => song.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newSongs = songs.Where(song => existingPaths.Add(song.FilePath)).ToList();

        if (newSongs.Count == 0)
        {
            MessageBox.Show(
                this,
                "All selected songs are already assigned to this purchase.",
                "No new songs",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        purchase.Songs = originalSongs.Concat(newSongs).ToList();
        var isNewPurchase = dialog.IsNewPurchase;
        var purchasesToSave = isNewPurchase
            ? _purchases.Prepend(purchase).ToList()
            : _purchases.ToList();

        if (!TrySavePurchases(purchasesToSave))
        {
            purchase.Songs = originalSongs;
            return;
        }

        if (isNewPurchase)
        {
            _purchases.Insert(0, purchase);
        }

        UpdatePurchaseCount();
        PurchasesGrid.Items.Refresh();
        StatusText.Text = $"{newSongs.Count:N0} songs assigned to {purchase.Store}.";
    }

    private void SelectPurchaseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose purchase storage folder"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!TryLoadPurchases(dialog.FolderName, out var purchases))
        {
            return;
        }

        var updatedSettings = _settings.Copy();
        updatedSettings.PurchaseStorageFolder = dialog.FolderName;
        if (!TrySaveSettings(updatedSettings))
        {
            return;
        }

        _settings = updatedSettings;
        UpdatePurchaseFolderText();
        ReplacePurchases(purchases);
        StatusText.Text = $"Loaded {_purchases.Count:N0} purchases from the selected folder.";
    }

    private async Task ScanFolderAsync(string folderPath)
    {
        _selectedFolder = folderPath;
        _musicTracks.Clear();
        MusicCount.Text = "0 tracks";
        MusicFolderText.Text = folderPath;
        MusicFolderText.ToolTip = folderPath;
        OpenFolderButton.IsEnabled = false;
        SettingsButton.IsEnabled = false;
        ScanProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Scanning files and reading metadata...";

        try
        {
            var settings = _settings.Copy();
            var channel = Channel.CreateBounded<MusicTrack>(new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            var producer = Task.Run(async () =>
            {
                try
                {
                    await ReadFolderAsync(folderPath, settings, channel.Writer);
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            });

            await foreach (var track in channel.Reader.ReadAllAsync())
            {
                _musicTracks.Add(track);
                MusicCount.Text = $"{_musicTracks.Count:N0} tracks";
                if (_musicTracks.Count % 10 == 0)
                {
                    StatusText.Text = $"{_musicTracks.Count:N0} audio files loaded; scanning continues...";
                }
            }

            await producer;

            var failedMetadata = _musicTracks.Count(track => track.MetadataError is not null);
            StatusText.Text = failedMetadata == 0
                ? $"{_musicTracks.Count:N0} audio files loaded."
                : $"{_musicTracks.Count:N0} audio files loaded; metadata could not be read for {failedMetadata:N0}. Hover a row for details.";
        }
        catch (SecurityException exception)
        {
            ShowScanError(folderPath, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowScanError(folderPath, exception);
        }
        catch (IOException exception)
        {
            ShowScanError(folderPath, exception);
        }
        finally
        {
            ScanProgress.Visibility = Visibility.Collapsed;
            OpenFolderButton.IsEnabled = true;
            SettingsButton.IsEnabled = true;
        }
    }

    private static async Task ReadFolderAsync(
        string folderPath,
        AppSettings settings,
        ChannelWriter<MusicTrack> writer)
    {
        var extensions = settings.AudioExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filePaths = Directory
            .EnumerateFiles(
                folderPath,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = settings.IncludeSubfolders,
                    ReturnSpecialDirectories = false
                })
            .Where(path => extensions.Contains(Path.GetExtension(path)));

        await Parallel.ForEachAsync(
            filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4) },
            async (filePath, cancellationToken) =>
            {
                var track = MusicTrackReader.Read(filePath);
                await writer.WriteAsync(track, cancellationToken);
            });
    }

    private void ShowScanError(string folderPath, Exception exception)
    {
        StatusText.Text = "Folder scan failed.";
        MessageBox.Show(
            this,
            $"Could not scan \"{folderPath}\".{Environment.NewLine}{exception.Message}",
            "Folder scan failed",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void ShowSettingsLoadError(Exception exception)
    {
        MessageBox.Show(
            this,
            $"Could not load application settings. Default scan options will be used.{Environment.NewLine}{exception.Message}",
            "Settings could not be loaded",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ShowSettingsSaveError(Exception exception)
    {
        MessageBox.Show(
            this,
            $"Could not save application settings.{Environment.NewLine}{exception.Message}",
            "Settings could not be saved",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void LoadPurchases()
    {
        if (TryLoadPurchases(_settings.PurchaseStorageFolder, out var purchases))
        {
            ReplacePurchases(purchases);
        }
    }

    private bool TryLoadPurchases(string folderPath, out List<PurchaseRecord> purchases)
    {
        try
        {
            purchases = PurchaseStore.Load(folderPath);
            return true;
        }
        catch (JsonException exception)
        {
            ShowPurchaseLoadError(exception);
        }
        catch (IOException exception)
        {
            ShowPurchaseLoadError(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowPurchaseLoadError(exception);
        }
        catch (SecurityException exception)
        {
            ShowPurchaseLoadError(exception);
        }

        purchases = [];
        return false;
    }

    private bool TrySaveSettings(AppSettings settings)
    {
        try
        {
            SettingsStore.Save(settings);
            return true;
        }
        catch (IOException exception)
        {
            ShowSettingsSaveError(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowSettingsSaveError(exception);
        }
        catch (SecurityException exception)
        {
            ShowSettingsSaveError(exception);
        }

        return false;
    }

    private void ReplacePurchases(IEnumerable<PurchaseRecord> purchases)
    {
        _purchases.Clear();
        foreach (var purchase in purchases)
        {
            _purchases.Add(purchase);
        }

        UpdatePurchaseCount();
    }

    private void ClearMusicFolder()
    {
        _selectedFolder = null;
        _musicTracks.Clear();
        MusicCount.Text = "0 tracks";
        MusicFolderText.Text = "No folder selected yet.";
        MusicFolderText.ToolTip = null;
        StatusText.Text = "Choose a folder to display its audio files.";
        MusicGrid.UnselectAll();
    }

    private void ShowPurchaseLoadError(Exception exception)
    {
        MessageBox.Show(
            this,
            $"Could not load saved purchases.{Environment.NewLine}{exception.Message}",
            "Purchases could not be loaded",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ShowPurchaseSaveError(Exception exception)
    {
        MessageBox.Show(
            this,
            $"Could not save this purchase.{Environment.NewLine}{exception.Message}",
            "Purchase could not be saved",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private bool TrySavePurchases(IEnumerable<PurchaseRecord> purchases)
    {
        try
        {
            PurchaseStore.Save(_settings.PurchaseStorageFolder, purchases);
            return true;
        }
        catch (IOException exception)
        {
            ShowPurchaseSaveError(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowPurchaseSaveError(exception);
        }
        catch (SecurityException exception)
        {
            ShowPurchaseSaveError(exception);
        }

        return false;
    }

    private bool SavePurchaseSongs(PurchaseRecord purchase, IReadOnlyList<PurchaseSong> songs)
    {
        var originalSongs = purchase.Songs;
        purchase.Songs = songs.ToList();
        if (!TrySavePurchases(_purchases))
        {
            purchase.Songs = originalSongs;
            return false;
        }

        UpdatePurchaseCount();
        PurchasesGrid.Items.Refresh();
        return true;
    }

    private void UpdatePurchaseCount()
    {
        PurchaseCount.Text = $"{_purchases.Count:N0} purchases";
    }

    private void UpdatePurchaseFolderText()
    {
        PurchaseFolderText.Text = _settings.PurchaseStorageFolder;
        PurchaseFolderText.ToolTip = _settings.PurchaseStorageFolder;
    }
}
