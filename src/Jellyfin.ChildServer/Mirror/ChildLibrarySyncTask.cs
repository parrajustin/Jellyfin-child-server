using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.ChildServer;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.ChildServer.Mirror;

/// <summary>
/// Scheduled task that mirrors the parent server's libraries.
/// </summary>
public class ChildLibrarySyncTask : IScheduledTask
{
    /// <summary>
    /// The task key.
    /// </summary>
    public const string TaskKey = "ChildServerSync";

    private readonly IChildServerManager _manager;
    private readonly IChildServerLibrarySync _sync;
    private readonly ILogger<ChildLibrarySyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildLibrarySyncTask"/> class.
    /// </summary>
    /// <param name="manager">The child server manager.</param>
    /// <param name="sync">The library sync.</param>
    /// <param name="logger">The logger.</param>
    public ChildLibrarySyncTask(IChildServerManager manager, IChildServerLibrarySync sync, ILogger<ChildLibrarySyncTask> logger)
    {
        _manager = manager;
        _sync = sync;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync parent server library";

    /// <inheritdoc />
    public string Description => "Mirrors the movies and shows of the parent Jellyfin server into this child server.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var hours = Math.Clamp(_manager.Configuration.SyncIntervalHours, 1, 168);
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };

        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(hours).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_manager.IsConfigured)
        {
            _logger.LogInformation("No parent server is configured; nothing to sync");
            progress.Report(100);
            return;
        }

        await _sync.SyncAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}
