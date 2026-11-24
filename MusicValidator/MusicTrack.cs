using System.Globalization;
using System.IO;
using System.Security;

namespace MusicValidator;

internal sealed record MusicTrack(
    uint? TrackNumber,
    string Title,
    string Artist,
    string Album,
    string Duration,
    string FilePath,
    string? MetadataError)
{
    public string Status { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string FolderOrderKey { get; init; } = string.Empty;
    public bool IsValidated => string.Equals(Status, "Validated", StringComparison.OrdinalIgnoreCase);

    public string Details => MetadataError is null
        ? FilePath
        : $"{FilePath}{Environment.NewLine}Metadata could not be read: {MetadataError}";
}

internal static class MusicTrackReader
{
    public static MusicTrack Read(string filePath)
    {
        try
        {
            using var audioFile = TagLib.File.Create(filePath);
            var title = string.IsNullOrWhiteSpace(audioFile.Tag.Title)
                ? Path.GetFileNameWithoutExtension(filePath)
                : audioFile.Tag.Title.Trim();
            var artist = string.Join(
                ", ",
                audioFile.Tag.Performers.Where(performer => !string.IsNullOrWhiteSpace(performer)));
            var album = audioFile.Tag.Album?.Trim() ?? string.Empty;
            var duration = FormatDuration(audioFile.Properties.Duration);
            uint? trackNumber = audioFile.Tag.Track is > 0 ? audioFile.Tag.Track : null;

            var track = new MusicTrack(trackNumber, title, artist, album, duration, filePath, null);
            return track with { Status = CheckTrack(track) };
        }
        catch (TagLib.CorruptFileException exception)
        {
            return CreateUnreadableTrack(filePath, exception);
        }
        catch (TagLib.UnsupportedFormatException exception)
        {
            return CreateUnreadableTrack(filePath, exception);
        }
        catch (IOException exception)
        {
            return CreateUnreadableTrack(filePath, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return CreateUnreadableTrack(filePath, exception);
        }
        catch (SecurityException exception)
        {
            return CreateUnreadableTrack(filePath, exception);
        }
    }

    private static MusicTrack CreateUnreadableTrack(string filePath, Exception exception)
    {
        var track = new MusicTrack(
            null,
            Path.GetFileNameWithoutExtension(filePath),
            string.Empty,
            string.Empty,
            string.Empty,
            filePath,
            exception.Message);
        return track with { Status = CheckTrack(track) };
    }

    private static string CheckTrack(MusicTrack track)
    {
        return "Unvalidated";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return string.Empty;
        }

        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
