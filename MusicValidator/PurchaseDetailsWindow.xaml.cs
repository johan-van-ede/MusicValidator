using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MusicValidator;

public partial class PurchaseDetailsWindow : Window
{
    private readonly PurchaseRecord _purchase;
    private readonly string _purchaseStorageFolder;
    private readonly Func<PurchaseRecord, IReadOnlyList<PurchaseSong>, bool> _saveSongChanges;

    internal PurchaseDetailsWindow(
        PurchaseRecord purchase,
        Func<PurchaseRecord, IReadOnlyList<PurchaseSong>, bool> saveSongChanges,
        string purchaseStorageFolder)
    {
        InitializeComponent();
        _purchase = purchase;
        _saveSongChanges = saveSongChanges;
        _purchaseStorageFolder = purchaseStorageFolder;
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
        PurchaseSummaryText.Text = _purchase.Details;
        SongsGrid.ItemsSource = _purchase.Songs;
        EvidencePanel.Children.Clear();
        AddEvidenceGroup(
            "Open proof of ownership",
            _purchase.PossessionProofPdfFile ?? _purchase.PossessionProofFile,
            _purchase.PossessionProofNativeFile);
        AddEvidenceGroup(
            "Open proof of payment",
            _purchase.PaymentProofPdfFile ?? _purchase.PaymentProofFile,
            _purchase.PaymentProofNativeFile);
        AddEvidenceGroup(
            "Open proof of purchase",
            _purchase.PurchaseProofPdfFile ?? _purchase.PurchaseProofFile,
            _purchase.PurchaseProofNativeFile);
        EvidencePanel.Visibility = EvidencePanel.Children.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void AddEvidenceGroup(string label, string? pdfPath, string? nativePath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) && string.IsNullOrWhiteSpace(nativePath))
        {
            return;
        }

        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 16, 8)
        };
        group.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush")
        });
        if (!string.IsNullOrWhiteSpace(pdfPath))
        {
            group.Children.Add(CreateEvidenceButton("PDF", pdfPath));
        }

        if (!string.IsNullOrWhiteSpace(nativePath))
        {
            group.Children.Add(CreateEvidenceButton("File", nativePath));
        }

        EvidencePanel.Children.Add(group);
    }

    private Button CreateEvidenceButton(string label, string relativePath)
    {
        var button = new Button
        {
            Content = label,
            Tag = relativePath,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8, 5, 8, 5)
        };
        button.Click += OpenEvidence_Click;
        return button;
    }

    private void OpenEvidence_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string relativePath })
        {
            return;
        }

        try
        {
            var filePath = PurchaseStore.GetEvidenceFilePath(
                _purchaseStorageFolder,
                _purchase,
                relativePath);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("The selected evidence file could not be found.", filePath);
            }

            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (InvalidDataException exception)
        {
            ShowEvidenceOpenError(exception);
        }
        catch (IOException exception)
        {
            ShowEvidenceOpenError(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowEvidenceOpenError(exception);
        }
        catch (SecurityException exception)
        {
            ShowEvidenceOpenError(exception);
        }
        catch (Win32Exception exception)
        {
            ShowEvidenceOpenError(exception);
        }
    }

    private void ShowEvidenceOpenError(Exception exception)
    {
        MessageBox.Show(
            this,
            $"Could not open the evidence file.{Environment.NewLine}{exception.Message}",
            "Evidence could not be opened",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
