using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaManager.Configuration;
using Jellyfin.Plugin.MediaManager.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaManager.Api;

public sealed class CreateFolderRequest
{
    public string Path { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class RenameRequest
{
    public string Path { get; set; } = string.Empty;

    public string NewName { get; set; } = string.Empty;
}

public sealed class MoveRequest
{
    public string Path { get; set; } = string.Empty;

    public string Destination { get; set; } = string.Empty;
}

public sealed class DeleteRequest
{
    public string Path { get; set; } = string.Empty;

    public bool Recursive { get; set; }
}

/// <summary>
/// Admin-only API behind the Media Manager dashboard page. Base route: /MediaManager.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("MediaManager")]
public class MediaManagerController : ControllerBase
{
    private const long MaxChunkBytes = 64L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> UploadLocks = new();

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MediaManagerController> _logger;

    public MediaManagerController(ILibraryManager libraryManager, ILogger<MediaManagerController> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private string Who => User.FindFirst("Jellyfin-User")?.Value ?? "unknown-user";

    private List<string> LibraryRoots() =>
        _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct()
            .ToList();

    private void Info(string message, params object?[] args)
    {
        _logger.LogInformation("[MediaManager] " + message, args);
    }

    private void Debug(string message, params object?[] args)
    {
        if (Config.DebugLogging)
        {
            _logger.LogInformation("[MediaManager][debug] " + message, args);
        }
    }

    private ObjectResult Fail(int status, string message, string? code = null)
    {
        _logger.LogWarning("[MediaManager] {User}: {Status} {Message}", Who, status, message);
        return StatusCode(status, new { error = message, code });
    }

    private bool Resolve(string? path, out string full, out string root, out ObjectResult? failure)
    {
        failure = null;
        if (PathGuard.TryResolve(path, LibraryRoots(), out full, out root, out var error))
        {
            return true;
        }

        failure = Fail(400, error);
        return false;
    }

    // ------------------------------------------------------------------ info

    [HttpGet("Info")]
    public ActionResult GetInfo()
    {
        var libraries = _libraryManager.GetVirtualFolders()
            .Select(f => new
            {
                name = f.Name,
                collectionType = f.CollectionType?.ToString(),
                locations = (f.Locations ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray()
            })
            .ToList();

        var cfg = Config;
        Debug("{User} loaded info ({Count} libraries)", Who, libraries.Count);

        return Ok(new
        {
            libraries,
            settings = new
            {
                autoScan = cfg.AutoScanAfterUpload,
                allowDelete = cfg.AllowDelete,
                maxUploadSizeMb = cfg.MaxUploadSizeMb,
                allowedExtensions = cfg.AllowedExtensions,
                partialSuffix = PathGuard.PartialSuffix
            }
        });
    }

    // ---------------------------------------------------------------- browse

    [HttpGet("Browse")]
    public ActionResult Browse([FromQuery] string path)
    {
        if (!Resolve(path, out var full, out var root, out var failure))
        {
            return failure!;
        }

        if (!Directory.Exists(full))
        {
            return Fail(404, "That folder does not exist.");
        }

        try
        {
            var entries = new List<FileSystemInfo>();
            foreach (var e in new DirectoryInfo(full).EnumerateFileSystemInfos())
            {
                if (e.LinkTarget is not null)
                {
                    continue; // links are never shown or touched
                }

                entries.Add(e);
            }

            var result = entries
                .OrderByDescending(e => e is DirectoryInfo)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new
                {
                    name = e.Name,
                    isDirectory = e is DirectoryInfo,
                    size = e is FileInfo fi ? (long?)fi.Length : null,
                    modified = e.LastWriteTimeUtc,
                    partial = e.Name.EndsWith(PathGuard.PartialSuffix, StringComparison.OrdinalIgnoreCase)
                })
                .ToList();

            var parent = string.Equals(full, root, PathGuard.Comparison) ? null : Path.GetDirectoryName(full);
            Debug("{User} browsed {Path} ({Count} entries)", Who, full, result.Count);

            return Ok(new { path = full, root, parent, entries = result });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail(403, $"Jellyfin is not allowed to read that folder: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Fail(500, $"Could not read that folder: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- upload

    /// <summary>
    /// Receives one chunk of a file. Chunks go to "name.mmpart" and are renamed to "name" when the last
    /// byte arrives. The response always says how many bytes the server holds, so the client can resume.
    /// </summary>
    [HttpPost("Upload")]
    [RequestSizeLimit(MaxChunkBytes)]
    public async Task<ActionResult> Upload(
        [FromQuery] string path,
        [FromQuery] string name,
        [FromQuery] long offset,
        [FromQuery] long total,
        [FromQuery] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (!Resolve(path, out var dir, out _, out var failure))
        {
            return failure!;
        }

        if (!Directory.Exists(dir))
        {
            return Fail(404, "The target folder does not exist.");
        }

        if (!PathGuard.IsValidName(name, out var nameError))
        {
            return Fail(400, nameError);
        }

        var cfg = Config;
        if (!PathGuard.IsAllowedExtension(name, cfg.AllowedExtensions))
        {
            return Fail(400, $"Files of type '{Path.GetExtension(name)}' are not on the allowed list (see plugin settings).");
        }

        if (total <= 0 || offset < 0 || offset >= total)
        {
            return Fail(400, $"Bad range: offset {offset} of total {total}. Empty files are not supported.");
        }

        if (cfg.MaxUploadSizeMb > 0 && total > cfg.MaxUploadSizeMb * 1024L * 1024L)
        {
            return Fail(413, $"That file is larger than the {cfg.MaxUploadSizeMb} MB limit set in the plugin settings.");
        }

        var target = Path.Combine(dir, name);
        var partial = target + PathGuard.PartialSuffix;

        var gate = UploadLocks.GetOrAdd(partial, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var complete = false;
        try
        {
            if (offset == 0)
            {
                if (Directory.Exists(target))
                {
                    return Fail(409, "A folder with that name already exists here.");
                }

                if (System.IO.File.Exists(target))
                {
                    if (new FileInfo(target).LinkTarget is not null)
                    {
                        return Fail(400, "The existing file is a symbolic link, so it was not touched.");
                    }

                    if (!overwrite)
                    {
                        return Fail(409, $"'{name}' already exists in this folder.", "exists");
                    }
                }
            }

            FileStream fs;
            if (offset == 0)
            {
                fs = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                Info("{User} started upload {File} ({Total} bytes) into {Dir}", Who, name, total, dir);
            }
            else
            {
                if (!System.IO.File.Exists(partial))
                {
                    return StatusCode(409, new { error = "The unfinished upload was not found on the server.", expected = 0L });
                }

                fs = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.None, 81920, true);
                if (fs.Length != offset)
                {
                    var have = fs.Length;
                    await fs.DisposeAsync().ConfigureAwait(false);
                    return StatusCode(409, new { error = $"Server holds {have} bytes but the chunk starts at {offset}.", expected = have });
                }

                fs.Seek(0, SeekOrigin.End);
            }

            long written = 0;
            var remaining = total - offset;
            await using (fs.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await Request.Body.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > remaining)
                    {
                        return Fail(400, "The chunk contained more data than the declared file size.");
                    }

                    await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            var received = offset + written;
            Debug("{User} chunk for {File}: {Received}/{Total}", Who, name, received, total);

            if (received < total)
            {
                return Ok(new { complete = false, received });
            }

            System.IO.File.Move(partial, target, true);
            complete = true;
            Info("{User} finished upload {File} ({Total} bytes) into {Dir}", Who, name, total, dir);
            return Ok(new { complete = true, received, path = target });
        }
        catch (OperationCanceledException)
        {
            Debug("{User} upload of {File} interrupted at the client", Who, name);
            return StatusCode(499, new { error = "The upload was interrupted." });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail(403, $"Jellyfin does not have write permission for that folder: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Fail(500, $"Disk error while saving '{name}': {ex.Message}");
        }
        finally
        {
            gate.Release();
            if (complete)
            {
                UploadLocks.TryRemove(partial, out _);
            }
        }
    }

    /// <summary>Discards an unfinished upload.</summary>
    [HttpDelete("Upload")]
    public ActionResult CancelUpload([FromQuery] string path, [FromQuery] string name)
    {
        if (!Resolve(path, out var dir, out _, out var failure))
        {
            return failure!;
        }

        if (!PathGuard.IsValidName(name, out var nameError))
        {
            return Fail(400, nameError);
        }

        var partial = Path.Combine(dir, name) + PathGuard.PartialSuffix;
        try
        {
            if (System.IO.File.Exists(partial))
            {
                System.IO.File.Delete(partial);
                Info("{User} discarded unfinished upload {File}", Who, name);
            }

            UploadLocks.TryRemove(partial, out _);
            return NoContent();
        }
        catch (IOException ex)
        {
            return Fail(500, $"Could not remove the unfinished upload: {ex.Message}");
        }
    }

    // ------------------------------------------------------- file operations

    [HttpPost("Folder")]
    public ActionResult CreateFolder([FromBody] CreateFolderRequest request)
    {
        if (!Resolve(request.Path, out var dir, out _, out var failure))
        {
            return failure!;
        }

        if (!Directory.Exists(dir))
        {
            return Fail(404, "The parent folder does not exist.");
        }

        if (!PathGuard.IsValidName(request.Name, out var nameError))
        {
            return Fail(400, nameError);
        }

        var target = Path.Combine(dir, request.Name);
        if (Directory.Exists(target) || System.IO.File.Exists(target))
        {
            return Fail(409, $"'{request.Name}' already exists here.");
        }

        try
        {
            Directory.CreateDirectory(target);
            Info("{User} created folder {Path}", Who, target);
            return Ok(new { path = target });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(500, $"Could not create the folder: {ex.Message}");
        }
    }

    [HttpPost("Rename")]
    public ActionResult Rename([FromBody] RenameRequest request)
    {
        if (!Resolve(request.Path, out var full, out var root, out var failure))
        {
            return failure!;
        }

        if (string.Equals(full, root, PathGuard.Comparison))
        {
            return Fail(400, "A library root folder cannot be renamed.");
        }

        var isDir = Directory.Exists(full);
        if (!isDir && !System.IO.File.Exists(full))
        {
            return Fail(404, "That item no longer exists.");
        }

        if (!PathGuard.IsValidName(request.NewName, out var nameError))
        {
            return Fail(400, nameError);
        }

        if (!isDir && !PathGuard.IsAllowedExtension(request.NewName, Config.AllowedExtensions))
        {
            return Fail(400, $"Files cannot be renamed to '{Path.GetExtension(request.NewName)}' because it is not on the allowed list.");
        }

        var target = Path.Combine(Path.GetDirectoryName(full)!, request.NewName);
        if (Directory.Exists(target) || System.IO.File.Exists(target))
        {
            return Fail(409, $"'{request.NewName}' already exists here.");
        }

        try
        {
            if (isDir)
            {
                Directory.Move(full, target);
            }
            else
            {
                System.IO.File.Move(full, target);
            }

            Info("{User} renamed {From} to {To}", Who, full, target);
            return Ok(new { path = target });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(500, $"Rename failed: {ex.Message}");
        }
    }

    [HttpPost("Move")]
    public ActionResult Move([FromBody] MoveRequest request)
    {
        if (!Resolve(request.Path, out var full, out var root, out var failure))
        {
            return failure!;
        }

        if (!Resolve(request.Destination, out var destination, out _, out failure))
        {
            return failure!;
        }

        if (string.Equals(full, root, PathGuard.Comparison))
        {
            return Fail(400, "A library root folder cannot be moved.");
        }

        var isDir = Directory.Exists(full);
        if (!isDir && !System.IO.File.Exists(full))
        {
            return Fail(404, "That item no longer exists.");
        }

        if (!Directory.Exists(destination))
        {
            return Fail(404, "The destination folder does not exist.");
        }

        var separator = Path.DirectorySeparatorChar;
        if (isDir && (string.Equals(destination, full, PathGuard.Comparison)
            || destination.StartsWith(full + separator, PathGuard.Comparison)))
        {
            return Fail(400, "A folder cannot be moved into itself.");
        }

        var target = Path.Combine(destination, Path.GetFileName(full));
        if (Directory.Exists(target) || System.IO.File.Exists(target))
        {
            return Fail(409, $"'{Path.GetFileName(full)}' already exists in the destination.");
        }

        try
        {
            if (isDir)
            {
                Directory.Move(full, target);
            }
            else
            {
                System.IO.File.Move(full, target);
            }

            Info("{User} moved {From} to {To}", Who, full, target);
            return Ok(new { path = target });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(500, $"Move failed (folders cannot be moved between different drives): {ex.Message}");
        }
    }

    [HttpPost("Delete")]
    public ActionResult Delete([FromBody] DeleteRequest request)
    {
        if (!Config.AllowDelete)
        {
            return Fail(403, "Deleting is turned off in the plugin settings.");
        }

        if (!Resolve(request.Path, out var full, out var root, out var failure))
        {
            return failure!;
        }

        if (string.Equals(full, root, PathGuard.Comparison))
        {
            return Fail(400, "A library root folder cannot be deleted.");
        }

        try
        {
            if (Directory.Exists(full))
            {
                var hasContent = Directory.EnumerateFileSystemEntries(full).Any();
                if (hasContent && !request.Recursive)
                {
                    return Fail(409, "That folder is not empty.", "not-empty");
                }

                Directory.Delete(full, request.Recursive);
            }
            else if (System.IO.File.Exists(full))
            {
                System.IO.File.Delete(full);
            }
            else
            {
                return Fail(404, "That item no longer exists.");
            }

            _logger.LogWarning("[MediaManager] {User} deleted {Path}", Who, full);
            return NoContent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(500, $"Delete failed: {ex.Message}");
        }
    }

    [HttpPost("Scan")]
    public ActionResult Scan()
    {
        _libraryManager.QueueLibraryScan();
        Info("{User} queued a library scan", Who);
        return NoContent();
    }
}
