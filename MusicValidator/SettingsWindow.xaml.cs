using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MusicValidator;

public partial class SettingsWindow : Window
{
    internal AppSettings Settings { get; private set; }

    internal SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Settings = settings.Copy();
        MusicFolderTextBox.Text = string.Join(Environment.NewLine, Settings.MusicFolderPaths);
        PurchaseFolderTextBox.Text = Settings.PurchaseStorageFolder;
        IncludeSubfoldersCheckBox.IsChecked = Settings.IncludeSubfolders;

        foreach (var extension in AppSettings.SupportedAudioExtensions)
        {
            AudioFormatsPanel.Children.Add(new CheckBox
            {
                Content = extension.TrimStart('.').ToUpperInvariant(),
                Tag = extension,
                IsChecked = Settings.AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 0, 18, 10),
                MinWidth = 68,
                Foreground = System.Windows.Media.Brushes.Gainsboro
            });
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void BrowseMusicFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select music folders",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            MusicFolderTextBox.Text = string.Join(Environment.NewLine, dialog.FolderNames);
        }
    }

    private void BrowsePurchaseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select purchase folder" };
        if (dialog.ShowDialog(this) == true)
        {
            PurchaseFolderTextBox.Text = dialog.FolderName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var extensions = AudioFormatsPanel.Children
            .OfType<CheckBox>()
            .Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => (string)checkBox.Tag)
            .ToList();

        if (extensions.Count == 0)
        {
            MessageBox.Show(
                this,
                "Select at least one audio file type.",
                "No file types selected",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Settings = new AppSettings
        {
            IncludeSubfolders = IncludeSubfoldersCheckBox.IsChecked == true,
            AudioExtensions = extensions,
            MusicFolderPaths = MusicFolderTextBox.Text
                .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            MusicFolderPath = MusicFolderTextBox.Text
                .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(),
            PurchaseStorageFolder = PurchaseFolderTextBox.Text
        };
        DialogResult = true;
    }
}
