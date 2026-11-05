using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaManager.Configuration;

/// <summary>
/// Plugin settings, stored by Jellyfin as XML in the plugin configuration folder.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public static readonly string[] DefaultExtensions =
    {
        // video
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg",
        // audio
        ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".alac",
        // subtitles and lyrics
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".sup", ".vtt", ".lrc",
        // artwork and metadata
        ".jpg", ".jpeg", ".png", ".webp", ".tbn", ".nfo"
    };

    public PluginConfiguration()
    {
        MaxUploadSizeMb = 0;
        AllowedExtensions = DefaultExtensions;
        AutoScanAfterUpload = true;
        AllowDelete = true;
        DebugLogging = false;
    }

    /// <summary>Gets or sets the largest file accepted, in MB. 0 means no limit.</summary>
    public int MaxUploadSizeMb { get; set; }

    /// <summary>Gets or sets the file extensions that may be uploaded or renamed to (include the dot).</summary>
    public string[] AllowedExtensions { get; set; }

    /// <summary>Gets or sets a value indicating whether a library scan is queued after an upload batch finishes.</summary>
    public bool AutoScanAfterUpload { get; set; }

    /// <summary>Gets or sets a value indicating whether files and folders can be deleted from the UI.</summary>
    public bool AllowDelete { get; set; }

    /// <summary>Gets or sets a value indicating whether every operation is written to the Jellyfin log.</summary>
    public bool DebugLogging { get; set; }
}
