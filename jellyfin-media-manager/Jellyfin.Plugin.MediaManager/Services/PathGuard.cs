using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.MediaManager.Services;

/// <summary>
/// Every path that reaches the file system goes through here first. Only locations inside
/// a configured library folder are accepted, and paths that pass through symbolic links are refused.
/// </summary>
public static class PathGuard
{
    public const string PartialSuffix = ".mmpart";

    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(full);
        if (string.Equals(full, pathRoot, Comparison))
        {
            return full;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Returns the library root that contains <paramref name="fullPath"/>, or null.</summary>
    public static string? FindRoot(string fullPath, IEnumerable<string> roots)
    {
        foreach (var rawRoot in roots)
        {
            string root;
            try
            {
                root = Normalize(rawRoot);
            }
            catch (Exception)
            {
                continue;
            }

            if (string.Equals(fullPath, root, Comparison))
            {
                return root;
            }

            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, Comparison))
            {
                return root;
            }
        }

        return null;
    }

    public static bool TryResolve(
        string? input,
        IEnumerable<string> roots,
        out string full,
        out string root,
        out string error)
    {
        full = string.Empty;
        root = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input) || input.Contains('\0'))
        {
            error = "No path was supplied.";
            return false;
        }

        try
        {
            full = Normalize(input);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid path: {ex.Message}";
            return false;
        }

        var found = FindRoot(full, roots);
        if (found is null)
        {
            error = "That path is outside every library folder, so it was refused.";
            return false;
        }

        root = found;

        if (PassesThroughLink(full, root))
        {
            error = "Paths that pass through symbolic links or junctions are not allowed.";
            return false;
        }

        return true;
    }

    /// <summary>True if any existing component between the root (exclusive) and the path is a link.</summary>
    private static bool PassesThroughLink(string full, string root)
    {
        var current = full;
        while (!string.Equals(current, root, Comparison))
        {
            if (Directory.Exists(current))
            {
                if (new DirectoryInfo(current).LinkTarget is not null)
                {
                    return true;
                }
            }
            else if (System.IO.File.Exists(current))
            {
                if (new FileInfo(current).LinkTarget is not null)
                {
                    return true;
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, Comparison))
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    public static bool IsValidName(string? name, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "The name is empty.";
            return false;
        }

        if (name.Length > 255)
        {
            error = "The name is longer than 255 characters.";
            return false;
        }

        if (name is "." or "..")
        {
            error = "That name is not allowed.";
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
        {
            error = "The name contains characters that are not allowed (such as / or \\).";
            return false;
        }

        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            error = "The name cannot end with a dot or a space.";
            return false;
        }

        if (name.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Names ending in {PartialSuffix} are reserved for unfinished uploads.";
            return false;
        }

        return true;
    }

    public static bool IsAllowedExtension(string name, IEnumerable<string>? allowed)
    {
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext))
        {
            return false;
        }

        return (allowed ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim().StartsWith('.') ? a.Trim() : "." + a.Trim())
            .Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase));
    }
}
