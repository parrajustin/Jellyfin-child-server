using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MediaBrowser.Model.Dto;

namespace Jellyfin.ChildServer.Mirror;

/// <summary>
/// Turns parent items into local folder and file names that Jellyfin's own resolvers understand.
/// </summary>
public static class MirrorPathBuilder
{
    private const int MaxNameLength = 120;

    private static readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3g2", ".3gp", ".asf", ".avi", ".divx", ".flv", ".m2ts", ".m2v", ".m4v", ".mkv", ".mk3d", ".mov", ".mp4", ".mpe",
        ".mpeg", ".mpg", ".mts", ".mxf", ".ogm", ".ogv", ".rm", ".rmvb", ".ts", ".vob", ".webm", ".wmv", ".wtv"
    };

    private static readonly Dictionary<string, string> _containerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["matroska"] = "mkv",
        ["mov"] = "mp4",
        ["mp4"] = "mp4",
        ["m4v"] = "m4v",
        ["avi"] = "avi",
        ["mpegts"] = "ts",
        ["ts"] = "ts",
        ["mpeg"] = "mpg",
        ["mpg"] = "mpg",
        ["asf"] = "wmv",
        ["wmv"] = "wmv",
        ["flv"] = "flv",
        ["ogg"] = "ogv",
        ["webm"] = "webm",
        ["3gp"] = "3gp",
        ["rm"] = "rm",
        ["mkv"] = "mkv"
    };

    private static readonly char[] _invalidNameChars = Path.GetInvalidFileNameChars()
        .Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
        .Distinct()
        .ToArray();

    /// <summary>
    /// Makes a display name safe to use as a folder or file name.
    /// </summary>
    /// <param name="name">The display name.</param>
    /// <param name="fallback">What to use when the name is empty.</param>
    /// <returns>The safe name.</returns>
    public static string SanitizeName(string? name, string fallback = "Untitled")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var builder = new StringBuilder(name.Length);
        var lastWasSpace = false;
        foreach (var c in name.Trim())
        {
            var replacement = Array.IndexOf(_invalidNameChars, c) >= 0 || char.IsControl(c) ? ' ' : c;
            if (char.IsWhiteSpace(replacement))
            {
                if (lastWasSpace)
                {
                    continue;
                }

                lastWasSpace = true;
                builder.Append(' ');
            }
            else
            {
                lastWasSpace = false;
                builder.Append(replacement);
            }
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        if (result.Length > MaxNameLength)
        {
            result = result.Substring(0, MaxNameLength).TrimEnd();
        }

        return result.Length == 0 ? fallback : result;
    }

    /// <summary>
    /// Picks the file extension for a mirrored video: the parent's own extension when known, else one derived from the container.
    /// </summary>
    /// <param name="item">The parent item.</param>
    /// <returns>The extension without the dot.</returns>
    public static string GetExtension(BaseItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!string.IsNullOrEmpty(item.Path))
        {
            var extension = Path.GetExtension(item.Path);
            if (_videoExtensions.Contains(extension))
            {
                return extension.Substring(1);
            }
        }

        var container = item.Container;
        if (string.IsNullOrEmpty(container))
        {
            container = item.MediaSources?.FirstOrDefault()?.Container;
        }

        if (!string.IsNullOrEmpty(container))
        {
            var first = container.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(first))
            {
                if (_containerExtensions.TryGetValue(first, out var mapped))
                {
                    return mapped;
                }

                if (_videoExtensions.Contains("." + first))
                {
                    return first;
                }
            }
        }

        return "mkv";
    }

    /// <summary>
    /// Builds the folder name of a movie or series: <c>Name (Year)</c>.
    /// </summary>
    /// <param name="item">The parent item.</param>
    /// <returns>The folder name.</returns>
    public static string TitledFolderName(BaseItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var name = SanitizeName(item.Name);
        if (item.ProductionYear is > 0 && !name.EndsWith(')'))
        {
            return name + " (" + item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture) + ")";
        }

        return name;
    }

    /// <summary>
    /// Builds the folder name of a season.
    /// </summary>
    /// <param name="seasonNumber">The season number, when known.</param>
    /// <param name="seasonName">The season name, when known.</param>
    /// <returns>The folder name.</returns>
    public static string SeasonFolderName(int? seasonNumber, string? seasonName)
    {
        if (seasonNumber == 0)
        {
            return "Specials";
        }

        if (seasonNumber is > 0)
        {
            return "Season " + seasonNumber.Value.ToString("00", CultureInfo.InvariantCulture);
        }

        return SanitizeName(seasonName, "Season 01");
    }

    /// <summary>
    /// Builds the file name (without extension) of an episode: <c>Series S02E01</c>.
    /// </summary>
    /// <param name="seriesName">The series name.</param>
    /// <param name="episode">The episode.</param>
    /// <returns>The file name without extension.</returns>
    public static string EpisodeFileName(string? seriesName, BaseItemDto episode)
    {
        ArgumentNullException.ThrowIfNull(episode);

        var series = SanitizeName(seriesName, "Series");
        var season = episode.ParentIndexNumber;
        var number = episode.IndexNumber;
        if (season.HasValue && number.HasValue)
        {
            var name = series + " S" + season.Value.ToString("00", CultureInfo.InvariantCulture) + "E" + number.Value.ToString("00", CultureInfo.InvariantCulture);
            if (episode.IndexNumberEnd is > 0 && episode.IndexNumberEnd > number)
            {
                name += "-E" + episode.IndexNumberEnd.Value.ToString("00", CultureInfo.InvariantCulture);
            }

            return name;
        }

        return series + " - " + SanitizeName(episode.Name, "Episode");
    }

    /// <summary>
    /// Maps an image content type to a file extension.
    /// </summary>
    /// <param name="contentType">The content type.</param>
    /// <returns>The extension without the dot.</returns>
    public static string ImageExtension(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return "jpg";
        }

        if (contentType.Contains("png", StringComparison.OrdinalIgnoreCase))
        {
            return "png";
        }

        if (contentType.Contains("webp", StringComparison.OrdinalIgnoreCase))
        {
            return "webp";
        }

        if (contentType.Contains("gif", StringComparison.OrdinalIgnoreCase))
        {
            return "gif";
        }

        return "jpg";
    }
}
