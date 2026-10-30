namespace HaloBar.Services;

/// <summary>
/// A one-line description of a photo or video for the island when it's dropped on the shelf:
/// "1920×1080 · 2:31 · 54 MB". Read through Windows' own property system, the same as Explorer's
/// Details pane, so any format Windows understands works.
/// </summary>
internal static class MediaInfo
{
    private static readonly string[] Photos = [".jpg", ".jpeg", ".png", ".webp", ".heic", ".gif", ".bmp", ".tif", ".tiff", ".avif"];
    private static readonly string[] Videos = [".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".m4v"];

    public static bool IsMedia(string path) =>
        Photos.Contains(Path.GetExtension(path).ToLowerInvariant()) || Videos.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Null for anything that isn't a photo or video, or can't be read.</summary>
    public static async Task<string?> DescribeAsync(string path)
    {
        if (!IsMedia(path) || !File.Exists(path)) return null;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var size = DownloadService.Size(new FileInfo(path).Length);
            if (Videos.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                var video = await file.Properties.GetVideoPropertiesAsync();
                var length = video.Duration.TotalHours >= 1 ? video.Duration.ToString(@"h\:mm\:ss") : video.Duration.ToString(@"m\:ss");
                return $"{video.Width}×{video.Height} · {length} · {size}";
            }
            var photo = await file.Properties.GetImagePropertiesAsync();
            return $"{photo.Width}×{photo.Height} · {size}";
        }
        catch (Exception ex)
        {
            Log.Write($"Media info for {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }
}
