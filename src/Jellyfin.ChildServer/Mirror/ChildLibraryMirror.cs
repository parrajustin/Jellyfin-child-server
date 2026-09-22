using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.ChildServer.Cache;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.ChildServer;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.ChildServer;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.ChildServer.Mirror;

/// <summary>
/// Mirrors the parent server's libraries: one placeholder file per video, NFO sidecars and small images,
/// registered as libraries on this server so the normal scanner and web client show them.
/// </summary>
public sealed class ChildLibraryMirror : IChildServerLibrarySync, IDisposable
{
    private const string RefreshLibraryTaskKey = "RefreshLibrary";
    private const int MaxTreeDepth = 24;
    private const int PosterWidth = 600;
    private const int BackdropWidth = 1280;
    private const int ThumbWidth = 640;

    private static readonly BaseItemKind[] _movieKinds = { BaseItemKind.Movie };
    private static readonly BaseItemKind[] _seriesKinds = { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode };
    private static readonly string[] _sidecarImageExtensions = { ".jpg", ".png", ".webp", ".gif" };

    private readonly ChildServerManager _manager;
    private readonly ParentLibraryReader _reader;
    private readonly MirrorManifest _manifest;
    private readonly ChildServerPaths _paths;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<ChildLibraryMirror> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Lock _stateLock = new();
    private Task? _currentSync;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildLibraryMirror"/> class.
    /// </summary>
    /// <param name="manager">The child server manager.</param>
    /// <param name="reader">The parent library reader.</param>
    /// <param name="manifest">The mirror manifest.</param>
    /// <param name="paths">The child server paths.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="taskManager">The task manager, used to run the library scan.</param>
    /// <param name="logger">The logger.</param>
    public ChildLibraryMirror(
        ChildServerManager manager,
        ParentLibraryReader reader,
        MirrorManifest manifest,
        ChildServerPaths paths,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        ILogger<ChildLibraryMirror> logger)
    {
        _manager = manager;
        _reader = reader;
        _manifest = manifest;
        _paths = paths;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public ChildSyncState State { get; private set; }

    /// <inheritdoc />
    public DateTime? LastSyncStartedUtc { get; private set; }

    /// <inheritdoc />
    public DateTime? LastSyncCompletedUtc { get; private set; }

    /// <inheritdoc />
    public string? LastSyncError { get; private set; }

    /// <inheritdoc />
    public Task SyncAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        Task sync;
        lock (_stateLock)
        {
            if (_currentSync is { IsCompleted: false })
            {
                sync = _currentSync;
            }
            else
            {
                sync = RunSyncAsync(progress, cancellationToken);
                _currentSync = sync;
            }
        }

        return sync;
    }

    /// <inheritdoc />
    public bool StartSyncInBackground()
    {
        lock (_stateLock)
        {
            if (_currentSync is { IsCompleted: false })
            {
                return false;
            }

            _currentSync = Task.Run(() => RunSyncAsync(new Progress<double>(), CancellationToken.None));
            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _syncLock.Dispose();
    }

    /// <summary>
    /// Whether a parent library holds videos and is therefore mirrored. Music, books, photos, collections,
    /// playlists and live TV are not.
    /// </summary>
    private static bool IsMirrored(CollectionType? collectionType)
        => collectionType is null
            or CollectionType.unknown
            or CollectionType.folders
            or CollectionType.movies
            or CollectionType.tvshows
            or CollectionType.homevideos
            or CollectionType.musicvideos;

    /// <summary>
    /// Movie and show libraries are laid out by naming convention; every other video library keeps the parent's folders.
    /// </summary>
    private static bool UsesFlatLayout(CollectionType? collectionType)
        => collectionType is CollectionType.movies or CollectionType.tvshows;

    private static CollectionTypeOptions? MapCollectionType(CollectionType? collectionType)
        => collectionType switch
        {
            CollectionType.movies => CollectionTypeOptions.movies,
            CollectionType.tvshows => CollectionTypeOptions.tvshows,
            CollectionType.homevideos => CollectionTypeOptions.homevideos,
            CollectionType.musicvideos => CollectionTypeOptions.musicvideos,
            _ => null
        };

    private static LibraryOptions CreateLibraryOptions(string path)
    {
        var typeOptions = new[] { "Movie", "Series", "Season", "Episode", "Video" }
            .Select(type => new TypeOptions
            {
                Type = type,
                MetadataFetchers = Array.Empty<string>(),
                MetadataFetcherOrder = Array.Empty<string>(),
                ImageFetchers = Array.Empty<string>(),
                ImageFetcherOrder = Array.Empty<string>()
            })
            .ToArray();

        return new LibraryOptions
        {
            PathInfos = new[] { new MediaPathInfo(path) },
            SaveLocalMetadata = false,
            EnableRealtimeMonitor = false,
            EnableChapterImageExtraction = false,
            ExtractChapterImagesDuringLibraryScan = false,
            EnableTrickplayImageExtraction = false,
            ExtractTrickplayImagesDuringLibraryScan = false,
            EnableLUFSScan = false,
            EnableEmbeddedTitles = false,
            EnableEmbeddedEpisodeInfos = false,
            SkipSubtitlesIfEmbeddedSubtitlesPresent = true,
            AutomaticRefreshIntervalDays = 0,
            TypeOptions = typeOptions
        };
    }

    private static string GetNfoText(BaseItemDto item)
        => item.Type switch
        {
            BaseItemKind.Movie => NfoWriter.Movie(item),
            BaseItemKind.Series => NfoWriter.Series(item),
            BaseItemKind.Season => NfoWriter.Season(item),
            BaseItemKind.Episode => NfoWriter.Episode(item),
            _ => NfoWriter.Movie(item)
        };

    private static async Task WriteTextIfChangedAsync(string path, string text, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existing, text, StringComparison.Ordinal))
            {
                return;
            }
        }

        await File.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
    }

    private static bool SidecarImageExists(string pathWithoutExtension)
        => _sidecarImageExtensions.Any(extension => File.Exists(pathWithoutExtension + extension));

    private static void DeleteSidecars(string mediaPath)
    {
        var withoutExtension = Path.Combine(Path.GetDirectoryName(mediaPath) ?? string.Empty, Path.GetFileNameWithoutExtension(mediaPath));
        TryDelete(withoutExtension + ".nfo");
        foreach (var extension in _sidecarImageExtensions)
        {
            TryDelete(withoutExtension + "-thumb" + extension);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Left behind on purpose; the next sync tries again.
        }
    }

    private static void DeleteEmptyDirectories(string start, string stopAt)
    {
        var current = start;
        while (!string.IsNullOrEmpty(current)
            && !string.Equals(MirrorManifest.NormalizePath(current), MirrorManifest.NormalizePath(stopAt), StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(current))
        {
            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                return;
            }

            try
            {
                Directory.Delete(current);
            }
            catch (IOException)
            {
                return;
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }
    }

    private async Task RunSyncAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        State = ChildSyncState.Running;
        LastSyncStartedUtc = DateTime.UtcNow;
        LastSyncError = null;
        try
        {
            await MirrorAllAsync(progress, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Parent library sync finished; {Count} items mirrored", _manifest.Count);
        }
        catch (Exception ex)
        {
            LastSyncError = ex.Message;
            _logger.LogError(ex, "Parent library sync failed");
            throw;
        }
        finally
        {
            State = ChildSyncState.Idle;
            LastSyncCompletedUtc = DateTime.UtcNow;
            _syncLock.Release();
        }
    }

    private async Task MirrorAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_manager.IsConfigured)
        {
            throw new ParentServerUnavailableException("Configure the parent server URL and user name first.");
        }

        var session = await _manager.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        var views = await _reader.GetViewsAsync(session, cancellationToken).ConfigureAwait(false);
        var supported = views.Where(v => IsMirrored(v.CollectionType)).ToList();
        _logger.LogInformation("Parent server has {Total} libraries, {Supported} of them hold videos and are mirrored", views.Count, supported.Count);

        var libraryRoot = _paths.LibraryRoot;
        Directory.CreateDirectory(libraryRoot);

        var seenPaths = new HashSet<string>(MirrorManifest.PathComparer);
        var seenViews = new HashSet<string>(StringComparer.Ordinal);
        var registered = false;
        var index = 0;
        foreach (var view in supported)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var viewName = MirrorPathBuilder.SanitizeName(view.Name, "Library");
            var viewRoot = Path.Combine(libraryRoot, viewName);
            Directory.CreateDirectory(viewRoot);
            seenViews.Add(viewName);

            MirrorPlan plan;
            if (UsesFlatLayout(view.CollectionType))
            {
                var kinds = view.CollectionType == CollectionType.tvshows ? _seriesKinds : _movieKinds;
                var items = await _reader.GetViewItemsAsync(session, view.Id, kinds, cancellationToken).ConfigureAwait(false);
                plan = BuildPlan(viewName, viewRoot, items);
            }
            else
            {
                plan = new MirrorPlan();
                await AddTreeAsync(session, plan, viewName, viewRoot, view.Id, 0, cancellationToken).ConfigureAwait(false);
            }

            await ApplyPlanAsync(session, plan, seenPaths, cancellationToken).ConfigureAwait(false);

            registered |= await EnsureLibraryAsync(viewName, viewRoot, view.CollectionType, cancellationToken).ConfigureAwait(false);
            index++;
            progress.Report(80.0 * index / Math.Max(1, supported.Count));
        }

        RemoveVanished(seenPaths, seenViews, libraryRoot);
        _manifest.Save();
        progress.Report(85);

        await ScanLibraryAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    private static string Disambiguate(string name, BaseItemDto item, HashSet<string> usedNames)
    {
        // Two parent items can share a title (the same movie in two qualities, say); keep both by tagging the
        // second with a bit of its parent id. The NFO carries the real title, so nothing changes for viewers.
        if (usedNames.Add(name))
        {
            return name;
        }

        var tagged = string.Concat(name, " [", item.Id.ToString("N", CultureInfo.InvariantCulture).AsSpan(0, 8), "]");
        usedNames.Add(tagged);
        return tagged;
    }

    private MirrorPlan BuildPlan(string viewName, string viewRoot, IReadOnlyList<BaseItemDto> items)
    {
        var plan = new MirrorPlan();
        var seriesById = items.Where(i => i.Type == BaseItemKind.Series).ToDictionary(i => i.Id, i => i);
        var seasonsById = items.Where(i => i.Type == BaseItemKind.Season).ToDictionary(i => i.Id, i => i);
        var usedMovieFolders = new HashSet<string>(MirrorManifest.PathComparer);
        var usedEpisodeFiles = new HashSet<string>(MirrorManifest.PathComparer);

        foreach (var item in items)
        {
            switch (item.Type)
            {
                case BaseItemKind.Movie:
                {
                    var folderName = Disambiguate(MirrorPathBuilder.TitledFolderName(item), item, usedMovieFolders);
                    var folder = Path.Combine(viewRoot, folderName);
                    var file = Path.Combine(folder, folderName + "." + MirrorPathBuilder.GetExtension(item));
                    plan.Folders.Add(new FolderPlan(folder, item, Path.Combine(folder, "movie.nfo"), Path.Combine(folder, "poster"), Path.Combine(folder, "fanart")));
                    plan.Videos.Add(new VideoPlan(file, item, viewName, Path.Combine(folder, folderName + ".nfo"), null));
                    break;
                }

                case BaseItemKind.Episode:
                {
                    seriesById.TryGetValue(item.SeriesId ?? Guid.Empty, out var series);
                    seasonsById.TryGetValue(item.SeasonId ?? Guid.Empty, out var season);
                    var seriesName = series?.Name ?? item.SeriesName;
                    var seriesFolder = Path.Combine(viewRoot, series is null ? MirrorPathBuilder.SanitizeName(seriesName, "Series") : MirrorPathBuilder.TitledFolderName(series));
                    var seasonNumber = season?.IndexNumber ?? item.ParentIndexNumber;
                    var seasonFolder = Path.Combine(seriesFolder, MirrorPathBuilder.SeasonFolderName(seasonNumber, season?.Name ?? item.SeasonName));
                    var baseName = MirrorPathBuilder.EpisodeFileName(seriesName, item);
                    var fileName = usedEpisodeFiles.Add(Path.Combine(seasonFolder, baseName))
                        ? baseName
                        : string.Concat(baseName, " [", item.Id.ToString("N", CultureInfo.InvariantCulture).AsSpan(0, 8), "]");
                    var file = Path.Combine(seasonFolder, fileName + "." + MirrorPathBuilder.GetExtension(item));

                    if (series is not null)
                    {
                        plan.AddFolderOnce(new FolderPlan(seriesFolder, series, Path.Combine(seriesFolder, "tvshow.nfo"), Path.Combine(seriesFolder, "poster"), Path.Combine(seriesFolder, "fanart")));
                    }

                    if (season is not null)
                    {
                        var seasonImageName = seasonNumber == 0 ? "season-specials-poster" : "season" + (seasonNumber ?? 1).ToString("00", CultureInfo.InvariantCulture) + "-poster";
                        plan.AddFolderOnce(new FolderPlan(seasonFolder, season, Path.Combine(seasonFolder, "season.nfo"), Path.Combine(seriesFolder, seasonImageName), null));
                    }

                    plan.Videos.Add(new VideoPlan(file, item, viewName, Path.Combine(seasonFolder, fileName + ".nfo"), Path.Combine(seasonFolder, fileName + "-thumb")));
                    break;
                }

                default:
                    break;
            }
        }

        return plan;
    }

    /// <summary>
    /// Mirrors a folder of the parent as it is: sub folders keep their names, shows and seasons get their
    /// sidecars, and every video becomes a placeholder file. Used for home video, mixed and untyped libraries.
    /// </summary>
    private async Task AddTreeAsync(ParentSession session, MirrorPlan plan, string viewName, string folder, Guid parentId, int depth, CancellationToken cancellationToken)
    {
        if (depth > MaxTreeDepth)
        {
            _logger.LogWarning("Skipping {Folder}: the parent library is nested deeper than {Depth} levels", folder, MaxTreeDepth);
            return;
        }

        var children = await _reader.GetChildrenAsync(session, parentId, cancellationToken).ConfigureAwait(false);
        var usedNames = new HashSet<string>(MirrorManifest.PathComparer);
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.IsFolder == true)
            {
                var folderName = Disambiguate(
                    child.Type == BaseItemKind.Series ? MirrorPathBuilder.TitledFolderName(child) : MirrorPathBuilder.SanitizeName(child.Name, "Folder"),
                    child,
                    usedNames);
                var path = Path.Combine(folder, folderName);
                switch (child.Type)
                {
                    case BaseItemKind.Series:
                        plan.AddFolderOnce(new FolderPlan(path, child, Path.Combine(path, "tvshow.nfo"), Path.Combine(path, "poster"), Path.Combine(path, "fanart")));
                        break;
                    case BaseItemKind.Season:
                    {
                        var seasonNumber = child.IndexNumber;
                        var seasonImageName = seasonNumber == 0 ? "season-specials-poster" : "season" + (seasonNumber ?? 1).ToString("00", CultureInfo.InvariantCulture) + "-poster";
                        plan.AddFolderOnce(new FolderPlan(path, child, Path.Combine(path, "season.nfo"), Path.Combine(folder, seasonImageName), null));
                        break;
                    }

                    default:
                        plan.Directories.Add(path);
                        break;
                }

                await AddTreeAsync(session, plan, viewName, path, child.Id, depth + 1, cancellationToken).ConfigureAwait(false);
            }
            else if (child.MediaType == MediaType.Video)
            {
                var baseName = child.Type == BaseItemKind.Episode
                    ? MirrorPathBuilder.EpisodeFileName(child.SeriesName, child)
                    : MirrorPathBuilder.SanitizeName(child.Name, "Video");
                baseName = Disambiguate(baseName, child, usedNames);
                var file = Path.Combine(folder, baseName + "." + MirrorPathBuilder.GetExtension(child));
                plan.Videos.Add(new VideoPlan(file, child, viewName, Path.Combine(folder, baseName + ".nfo"), Path.Combine(folder, baseName + "-thumb")));
            }
        }
    }

    private async Task ApplyPlanAsync(ParentSession session, MirrorPlan plan, HashSet<string> seenPaths, CancellationToken cancellationToken)
    {
        foreach (var directory in plan.Directories)
        {
            Directory.CreateDirectory(directory);
        }

        foreach (var folder in plan.Folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(folder.Path);
            await WriteTextIfChangedAsync(folder.NfoPath, GetNfoText(folder.Item), cancellationToken).ConfigureAwait(false);
            await FetchImageAsync(session, folder.Item, ImageType.Primary, null, PosterWidth, folder.PosterPathWithoutExtension, cancellationToken).ConfigureAwait(false);
            if (folder.BackdropPathWithoutExtension is not null)
            {
                await FetchImageAsync(session, folder.Item, ImageType.Backdrop, 0, BackdropWidth, folder.BackdropPathWithoutExtension, cancellationToken).ConfigureAwait(false);
            }
        }

        var now = DateTime.UtcNow;
        foreach (var video in plan.Videos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = video.Item;
            var mediaSource = item.MediaSources?.FirstOrDefault();
            var normalizedPath = MirrorManifest.NormalizePath(video.Path);
            if (!seenPaths.Add(normalizedPath))
            {
                _logger.LogWarning("Two parent items map to the same local file {Path}; keeping the first ({Name})", normalizedPath, item.Name);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(normalizedPath)!);
            var existing = _manifest.Get(normalizedPath);
            var entry = existing ?? new MirrorEntry { Path = normalizedPath };
            var sourceChanged = existing is not null
                && (!string.Equals(existing.ParentItemId, item.Id.ToString("N", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    || (mediaSource?.Size is > 0 && existing.ExpectedSize > 0 && existing.ExpectedSize != mediaSource.Size.Value));

            entry.View = video.ViewName;
            entry.ParentItemId = item.Id.ToString("N", CultureInfo.InvariantCulture);
            entry.ParentMediaSourceId = mediaSource?.Id;
            entry.Kind = item.Type.ToString();
            entry.Name = item.Name ?? string.Empty;
            entry.ExpectedSize = mediaSource?.Size ?? 0;
            entry.Container = mediaSource?.Container ?? item.Container;
            entry.RunTimeTicks = mediaSource?.RunTimeTicks ?? item.RunTimeTicks;
            entry.Bitrate = mediaSource?.Bitrate;
            entry.MediaStreams = mediaSource?.MediaStreams?.ToArray() ?? Array.Empty<MediaStream>();
            entry.Etag = item.Etag;
            entry.LastSeenUtc = now;

            if (sourceChanged || !File.Exists(normalizedPath))
            {
                // A new item, or the parent's file changed: start over with an empty placeholder.
                using (File.Create(normalizedPath))
                {
                }

                TryDelete(normalizedPath + ".jfpart");
                entry.IsCached = false;
            }
            else if (entry.IsCached && !ChildMediaCache.GetCachedBytes(entry).HasValue)
            {
                entry.IsCached = false;
            }

            _manifest.Upsert(entry);
            await WriteTextIfChangedAsync(video.NfoPath, GetNfoText(item), cancellationToken).ConfigureAwait(false);
            if (video.ThumbPathWithoutExtension is not null)
            {
                await FetchImageAsync(session, item, ImageType.Primary, null, ThumbWidth, video.ThumbPathWithoutExtension, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FetchImageAsync(ParentSession session, BaseItemDto item, ImageType imageType, int? imageIndex, int maxWidth, string pathWithoutExtension, CancellationToken cancellationToken)
    {
        if (SidecarImageExists(pathWithoutExtension))
        {
            return;
        }

        var hasImage = imageType == ImageType.Backdrop
            ? item.BackdropImageTags is { Length: > 0 }
            : item.ImageTags is not null && item.ImageTags.ContainsKey(imageType);
        if (!hasImage)
        {
            return;
        }

        try
        {
            var image = await _reader.GetImageAsync(session, item.Id, imageType, imageIndex, maxWidth, cancellationToken).ConfigureAwait(false);
            if (image is null)
            {
                return;
            }

            await File.WriteAllBytesAsync(pathWithoutExtension + "." + MirrorPathBuilder.ImageExtension(image.ContentType), image.Bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (ParentServerException ex)
        {
            _logger.LogWarning("Could not fetch the {ImageType} image of {Name} from the parent: {Message}", imageType, item.Name, ex.Message);
        }
    }

    private void RemoveVanished(HashSet<string> seenPaths, HashSet<string> seenViews, string libraryRoot)
    {
        foreach (var entry in _manifest.GetAll())
        {
            if (seenPaths.Contains(entry.Path))
            {
                continue;
            }

            if (!seenViews.Contains(entry.View))
            {
                // The whole library was not reachable this time (for example a view the parent no longer exposes);
                // keep the entry so a temporary parent hiccup does not wipe the mirror.
                continue;
            }

            _logger.LogInformation("Removing {Name}, which is no longer on the parent server", entry.Name);
            TryDelete(entry.Path);
            TryDelete(entry.Path + ".jfpart");
            DeleteSidecars(entry.Path);
            _manifest.Remove(entry.Path);
            DeleteEmptyDirectories(Path.GetDirectoryName(entry.Path) ?? string.Empty, libraryRoot);
        }
    }

    private async Task<bool> EnsureLibraryAsync(string name, string path, CollectionType? collectionType, CancellationToken cancellationToken)
    {
        var normalized = MirrorManifest.NormalizePath(path);
        var existing = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(v => v.Locations.Any(l => string.Equals(MirrorManifest.NormalizePath(l), normalized, StringComparison.OrdinalIgnoreCase)));
        if (existing is not null)
        {
            return false;
        }

        _logger.LogInformation("Registering mirrored library {Name} at {Path}", name, path);
        await _libraryManager.AddVirtualFolder(name, MapCollectionType(collectionType), CreateLibraryOptions(path), false).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }

    private async Task ScanLibraryAsync(CancellationToken cancellationToken)
    {
        var worker = _taskManager.ScheduledTasks.FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, RefreshLibraryTaskKey, StringComparison.Ordinal));
        if (worker is null)
        {
            _logger.LogWarning("The library scan task was not found; mirrored items appear after the next scan");
            return;
        }

        // Wait for a scan that is already running instead of starting a second one.
        while (worker.State != TaskState.Idle)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        await _taskManager.Execute(worker, new TaskOptions()).ConfigureAwait(false);
        while (worker.State != TaskState.Idle)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record FolderPlan(string Path, BaseItemDto Item, string NfoPath, string PosterPathWithoutExtension, string? BackdropPathWithoutExtension);

    private sealed record VideoPlan(string Path, BaseItemDto Item, string ViewName, string NfoPath, string? ThumbPathWithoutExtension);

    private sealed class MirrorPlan
    {
        private readonly HashSet<string> _folderPaths = new(MirrorManifest.PathComparer);

        /// <summary>
        /// Gets plain folders to create, with no sidecars.
        /// </summary>
        public List<string> Directories { get; } = new();

        public List<FolderPlan> Folders { get; } = new();

        public List<VideoPlan> Videos { get; } = new();

        public void AddFolderOnce(FolderPlan folder)
        {
            if (_folderPaths.Add(folder.Path))
            {
                Folders.Add(folder);
            }
        }
    }
}
