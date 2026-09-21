using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.ChildServer.Mirror;

/// <summary>
/// The list of media files mirrored from the parent server, kept in memory and persisted as JSON.
/// </summary>
public sealed class MirrorManifest
{
    private static readonly StringComparer _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<string> _filePathProvider;
    private readonly ILogger<MirrorManifest> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, MirrorEntry> _entries = new(_pathComparer);
    private string? _loadedFrom;

    /// <summary>
    /// Initializes a new instance of the <see cref="MirrorManifest"/> class.
    /// </summary>
    /// <param name="filePathProvider">Returns the path of the manifest file.</param>
    /// <param name="logger">The logger.</param>
    public MirrorManifest(Func<string> filePathProvider, ILogger<MirrorManifest> logger)
    {
        _filePathProvider = filePathProvider;
        _logger = logger;
    }

    /// <summary>
    /// Gets the comparer used for local paths.
    /// </summary>
    public static StringComparer PathComparer => _pathComparer;

    /// <summary>
    /// Gets the number of entries.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Normalizes a path so that lookups match regardless of separators or trailing slashes.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The normalized absolute path.</returns>
    public static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>
    /// Gets the entry for a path.
    /// </summary>
    /// <param name="path">The local path.</param>
    /// <returns>The entry, or null.</returns>
    public MirrorEntry? Get(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = NormalizePath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }

        lock (_lock)
        {
            EnsureLoaded();
            return _entries.TryGetValue(normalized, out var entry) ? entry : null;
        }
    }

    /// <summary>
    /// Gets a snapshot of every entry.
    /// </summary>
    /// <returns>The entries.</returns>
    public IReadOnlyList<MirrorEntry> GetAll()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _entries.Values.ToList();
        }
    }

    /// <summary>
    /// Adds or replaces an entry.
    /// </summary>
    /// <param name="entry">The entry; its path is normalized.</param>
    public void Upsert(MirrorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Path = NormalizePath(entry.Path);
        lock (_lock)
        {
            EnsureLoaded();
            _entries[entry.Path] = entry;
        }
    }

    /// <summary>
    /// Removes an entry.
    /// </summary>
    /// <param name="path">The local path.</param>
    /// <returns>True when an entry was removed.</returns>
    public bool Remove(string path)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _entries.Remove(NormalizePath(path));
        }
    }

    /// <summary>
    /// Writes the manifest to disk atomically.
    /// </summary>
    public void Save()
    {
        lock (_lock)
        {
            EnsureLoaded();
            var filePath = _filePathProvider();
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new ManifestDocument
            {
                Entries = _entries.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToList()
            };

            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(document, _jsonOptions));
            File.Move(tempPath, filePath, overwrite: true);
            _loadedFrom = filePath;
        }
    }

    private void EnsureLoaded()
    {
        var filePath = _filePathProvider();
        if (string.Equals(_loadedFrom, filePath, StringComparison.Ordinal))
        {
            return;
        }

        _entries.Clear();
        _loadedFrom = filePath;
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<ManifestDocument>(File.ReadAllText(filePath), _jsonOptions);
            foreach (var entry in document?.Entries ?? Enumerable.Empty<MirrorEntry>())
            {
                if (string.IsNullOrEmpty(entry.Path))
                {
                    continue;
                }

                entry.Path = NormalizePath(entry.Path);
                _entries[entry.Path] = entry;
            }

            _logger.LogInformation("Loaded {Count} mirrored items from {Path}", _entries.Count, filePath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The mirror manifest at {Path} could not be read; starting with an empty one", filePath);
            _entries.Clear();
        }
    }

    private sealed class ManifestDocument
    {
        public int Version { get; set; } = 1;

        public IReadOnlyList<MirrorEntry> Entries { get; set; } = Array.Empty<MirrorEntry>();
    }
}
