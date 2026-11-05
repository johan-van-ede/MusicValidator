using System.Globalization;
using System.Security;
using System.Windows;
using Microsoft.Win32;

namespace MusicValidator;

public partial class PurchaseDetailsWindow : Window
{
    private readonly PurchaseRecord _purchase;
    private readonly Func<PurchaseRecord, IReadOnlyList<PurchaseSong>, bool> _saveSongChanges;

    internal PurchaseDetailsWindow(
        PurchaseRecord purchase,
        Func<PurchaseRecord, IReadOnlyList<PurchaseSong>, bool> saveSongChanges)
    {
        InitializeComponent();
        _purchase = purchase;
        _saveSongChanges = saveSongChanges;
        UpdatePurchaseView();
    }

    private async void AddSongs_Click(object sender, RoutedEventArgs e)
    {
        var patterns = string.Join(
            ";",
            AppSettings.SupportedAudioExtensions.Select(extension => $"*{extension}"));
        var dialog = new OpenFileDialog
        {
            Title = "Select songs to add to this purchase",
            Filter = $"Supported audio files|{patterns}",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var selectedFilePaths = dialog.FileNames;
        AddSongsButton.IsEnabled = false;
        SongReadStatus.Text = "Reading song metadata...";
        try
        {
            var newSongs = await Task.Run(() =>
            {
                var songs = new PurchaseSong[selectedFilePaths.Length];
                Parallel.For(
                    0,
                    selectedFilePaths.Length,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4) },
                    index => songs[index] = PurchaseSong.FromTrack(MusicTrackReader.Read(selectedFilePaths[index])));
                return songs;
            });

            var existingPaths = _purchase.Songs
                .Select(song => song.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var songsToAdd = newSongs.Where(song => existingPaths.Add(song.FilePath)).ToList();
            if (songsToAdd.Count == 0)
            {
                SongReadStatus.Text = "All selected songs are already in this purchase.";
                return;
            }

            var updatedSongs = _purchase.Songs.Concat(songsToAdd).ToList();
            if (!_saveSongChanges(_purchase, updatedSongs))
            {
                SongReadStatus.Text = "Could not save the selected songs.";
                return;
            }

            _purchase.Songs = updatedSongs;
            UpdatePurchaseView();
            var unreadableMetadata = songsToAdd.Count(song => song.MetadataError is not null);
            SongReadStatus.Text = unreadableMetadata == 0
                ? $"{songsToAdd.Count:N0} songs added."
                : $"{songsToAdd.Count:N0} songs added; metadata could not be read for {unreadableMetadata:N0}. Hover those rows for details.";
        }
        catch (AggregateException exception)
        {
            SongReadStatus.Text = "Some selected files could not be read.";
            MessageBox.Show(
                this,
                $"Could not read the selected songs.{Environment.NewLine}{exception.GetBaseException().Message}",
                "Songs could not be read",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            AddSongsButton.IsEnabled = true;
        }
    }

    private void UpdatePurchaseView()
    {
        PurchaseSummaryText.Text =
            $"{_purchase.PurchaseDate.ToString("d", CultureInfo.CurrentCulture)}  |  {_purchase.Store}  |  {_purchase.Price.ToString("C", CultureInfo.CurrentCulture)}  |  {_purchase.SongCount:N0} songs";
        SongsGrid.ItemsSource = _purchase.Songs;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
