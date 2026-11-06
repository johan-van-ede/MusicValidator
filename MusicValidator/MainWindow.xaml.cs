using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace MusicValidator;

public partial class MainWindow : Window
{
    private static readonly Regex NumberTokenRegex = new(@"\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ObservableCollection<MusicTrack> _musicTracks = [];
    private readonly ObservableCollection<PurchaseRecord> _purchases = [];
    private readonly ICollectionView _musicView;
    private readonly HashSet<string> _assignedSongPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _validatedSongPaths = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings _settings = new();
    private ListSortDirection _folderPathSortDirection = ListSortDirection.Ascending;
    private int _validatedTrackCount;
    private string? _selectedFolder;

    public MainWindow()
    {
        InitializeComponent();
        _musicView = CollectionViewSource.GetDefaultView(_musicTracks);
        SortMusicByFolderPath(_folderPathSortDirection);
        MusicGrid.ItemsSource = _musicView;
        PurchasesGrid.ItemsSource = _purchases;
        UpdateMusicCount();
        UpdateValidationMetric();

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
        var dialog = new AddPurchaseWindow(_purchases.Select(purchase => purchase.Store)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Purchase is not PurchaseRecord purchase)
        {
            return;
        }

        var updatedPurchases = _purchases.Prepend(purchase).ToList();
        if (!TrySavePurchases(updatedPurchases, purchase))
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

    private void RecheckPurchases_Click(object sender, RoutedEventArgs e)
    {
        PurchaseChecker.CheckAll(_settings.PurchaseStorageFolder, _purchases);
        RebuildAssignedSongPaths();
        PurchasesGrid.Items.Refresh();

        var attentionCount = _purchases.Count(purchase => !purchase.IsValidated);
        StatusText.Text = attentionCount == 0
            ? $"All {_purchases.Count:N0} purchases passed their checks."
            : $"{attentionCount:N0} of {_purchases.Count:N0} purchases need attention.";
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

        if (!TryDeletePurchase(purchase))
        {
            return;
        }

        _purchases.Remove(purchase);
        RebuildAssignedSongPaths();
        UpdatePurchaseCount();
        StatusText.Text = $"Purchase from {purchase.Store} removed.";
    }

    private void PurchaseDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PurchaseRecord purchase })
        {
            new PurchaseDetailsWindow(
                purchase,
                SavePurchaseSongs,
                _settings.PurchaseStorageFolder) { Owner = this }.ShowDialog();
        }
    }

    private void PurchaseValidationStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PurchaseRecord purchase })
        {
            return;
        }

        var dialog = new AddPurchaseWindow(_purchases.Select(item => item.Store), purchase) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Purchase is not PurchaseRecord updatedPurchase)
        {
            return;
        }

        var purchaseIndex = _purchases.IndexOf(purchase);
        if (purchaseIndex < 0)
        {
            return;
        }

        var updatedPurchases = _purchases
            .Select(item => ReferenceEquals(item, purchase) ? updatedPurchase : item)
            .ToList();
        if (!TrySavePurchases(updatedPurchases, updatedPurchase))
        {
            return;
        }

        _purchases[purchaseIndex] = updatedPurchase;
        PurchasesGrid.SelectedItem = updatedPurchase;
        RebuildAssignedSongPaths();
        UpdatePurchaseCount();
        PurchasesGrid.Items.Refresh();
        StatusText.Text = $"{purchase.Store}: {updatedPurchase.ValidationStatus}.";
    }

    private void MusicGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AssignToPurchaseButton.IsEnabled = MusicGrid.SelectedItems.Count > 0;
    }

    private void ToggleFolderPathSort_Click(object sender, RoutedEventArgs e)
    {
        var direction = _folderPathSortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        SortMusicByFolderPath(direction);
    }

    private void AssignToPurchase_Click(object sender, RoutedEventArgs e)
    {
        AssignTracksToPurchase(MusicGrid.SelectedItems.OfType<MusicTrack>().ToList());
    }

    private void TrackStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MusicTrack track })
        {
            AssignTracksToPurchase([track]);
        }
    }

    private void AssignTracksToPurchase(IReadOnlyList<MusicTrack> selectedTracks)
    {
        var songs = selectedTracks
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

        if (!TrySavePurchases(purchasesToSave, purchase))
        {
            purchase.Songs = originalSongs;
            return;
        }

        if (isNewPurchase)
        {
            _purchases.Insert(0, purchase);
        }

        RebuildAssignedSongPaths();
        UpdatePurchaseCount();
        PurchasesGrid.Items.Refresh();
        UpdateValidationMetric();
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
        _validatedTrackCount = 0;
        UpdateMusicCount();
        UpdateValidationMetric();
        MusicGrid.UnselectAll();
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
                var displayedTrack = track;
                if (_validatedSongPaths.Contains(track.FilePath))
                {
                    displayedTrack = track with { Status = "Validated" };
                }
                else if (_assignedSongPaths.Contains(track.FilePath))
                {
                    displayedTrack = track with { Status = "Assigned" };
                }

                _musicTracks.Add(displayedTrack);
                if (displayedTrack.IsValidated)
                {
                    _validatedTrackCount++;
                }

                UpdateMusicCount();
                UpdateValidationMetric();
            }

            await producer;

            var failedMetadata = _musicTracks.Count(track => track.MetadataError is not null);
            StatusText.Text = failedMetadata == 0
                ? "Scan complete."
                : $"Scan complete; metadata could not be read for {failedMetadata:N0} tracks.";
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
                var relativePath = Path.GetRelativePath(folderPath, filePath);
                var track = MusicTrackReader.Read(filePath) with
                {
                    RelativePath = relativePath,
                    FolderOrderKey = CreateFolderOrderKey(relativePath)
                };
                await writer.WriteAsync(track, cancellationToken);
            });
    }

    private void SortMusicByFolderPath(ListSortDirection direction)
    {
        _folderPathSortDirection = direction;
        _musicView.SortDescriptions.Clear();
        _musicView.SortDescriptions.Add(
            new SortDescription(nameof(MusicTrack.FolderOrderKey), direction));
        _musicView.Refresh();
        PathSortButton.ToolTip =
            $"Sort by relative folder path ({direction.ToString().ToLowerInvariant()}); click to reverse";
    }

    private void UpdateMusicCount()
    {
        MusicCount.Text = $"{_musicTracks.Count:N0} tracks";
    }

    private void UpdateValidationMetric()
    {
        var validationRate = _musicTracks.Count == 0
            ? 0
            : _validatedTrackCount / (double)_musicTracks.Count;
        ValidationMetricText.Text = $"Validated {validationRate:P0}";
    }

    private static string CreateFolderOrderKey(string relativePath)
    {
        var normalizedPath = relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .ToUpperInvariant();
        return NumberTokenRegex.Replace(normalizedPath, match =>
        {
            var significantDigits = match.Value.TrimStart('0');
            if (significantDigits.Length == 0)
            {
                significantDigits = "0";
            }

            return $"{significantDigits.Length:D10}:{significantDigits}:{match.Value.Length:D10}";
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
        catch (InvalidDataException exception)
        {
            ShowPurchaseLoadError(exception);
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
        var purchaseList = purchases.ToList();
        PurchaseChecker.CheckAll(_settings.PurchaseStorageFolder, purchaseList);

        _purchases.Clear();
        foreach (var purchase in purchaseList)
        {
            _purchases.Add(purchase);
        }

        RebuildAssignedSongPaths();
        UpdatePurchaseCount();
    }

    private void RebuildAssignedSongPaths()
    {
        _assignedSongPaths.Clear();
        _validatedSongPaths.Clear();
        foreach (var purchase in _purchases)
        {
            foreach (var song in purchase.Songs)
            {
                _assignedSongPaths.Add(song.FilePath);
                if (purchase.IsValidated)
                {
                    _validatedSongPaths.Add(song.FilePath);
                }
            }
        }

        for (var index = 0; index < _musicTracks.Count; index++)
        {
            var track = _musicTracks[index];
            var status = _validatedSongPaths.Contains(track.FilePath)
                ? "Validated"
                : _assignedSongPaths.Contains(track.FilePath) ? "Assigned" : "Unvalidated";
            if (!string.Equals(track.Status, status, StringComparison.Ordinal))
            {
                _musicTracks[index] = track with { Status = status };
            }
        }

        _validatedTrackCount = _musicTracks.Count(track => track.IsValidated);
        UpdateValidationMetric();
    }

    private void ClearMusicFolder()
    {
        _selectedFolder = null;
        _musicTracks.Clear();
        _validatedTrackCount = 0;
        SortMusicByFolderPath(ListSortDirection.Ascending);
        UpdateMusicCount();
        UpdateValidationMetric();
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

    private void ShowPurchaseReportError(Exception exception)
    {
        MessageBox.Show(
            this,
            $"The purchase was saved, but its PDF report could not be generated. The report may not reflect the latest changes.{Environment.NewLine}{exception.Message}",
            "Purchase report could not be generated",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private bool TryDeletePurchase(PurchaseRecord purchase)
    {
        try
        {
            PurchaseStore.Delete(_settings.PurchaseStorageFolder, purchase);
            return true;
        }
        catch (InvalidDataException exception)
        {
            ShowPurchaseSaveError(exception);
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

    private bool TrySavePurchases(IEnumerable<PurchaseRecord> purchases, PurchaseRecord? reportPurchase = null)
    {
        try
        {
            var purchaseList = purchases.ToList();
            PurchaseStore.Save(_settings.PurchaseStorageFolder, purchaseList);
            PurchaseChecker.CheckAll(_settings.PurchaseStorageFolder, purchaseList);
        }
        catch (InvalidDataException exception)
        {
            ShowPurchaseSaveError(exception);
            return false;
        }
        catch (IOException exception)
        {
            ShowPurchaseSaveError(exception);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowPurchaseSaveError(exception);
            return false;
        }
        catch (SecurityException exception)
        {
            ShowPurchaseSaveError(exception);
            return false;
        }

        if (reportPurchase is null)
        {
            return true;
        }

        try
        {
            PurchaseReportGenerator.Save(_settings.PurchaseStorageFolder, reportPurchase);
        }
        catch (InvalidDataException exception)
        {
            ShowPurchaseReportError(exception);
        }
        catch (IOException exception)
        {
            ShowPurchaseReportError(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowPurchaseReportError(exception);
        }
        catch (SecurityException exception)
        {
            ShowPurchaseReportError(exception);
        }

        return true;
    }

    private bool SavePurchaseSongs(PurchaseRecord purchase, IReadOnlyList<PurchaseSong> songs)
    {
        var originalSongs = purchase.Songs;
        purchase.Songs = songs.ToList();
        if (!TrySavePurchases(_purchases, purchase))
        {
            purchase.Songs = originalSongs;
            return false;
        }

        RebuildAssignedSongPaths();
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
