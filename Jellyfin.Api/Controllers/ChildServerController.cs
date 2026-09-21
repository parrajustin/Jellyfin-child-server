using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.ChildServer;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.ChildServer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Settings, connection state and media cache of this child server's link to its parent Jellyfin server.
/// </summary>
[Authorize]
public class ChildServerController : BaseJellyfinApiController
{
    private readonly IChildServerManager _childServerManager;
    private readonly IChildServerLibrarySync _librarySync;
    private readonly IChildServerMediaCache _mediaCache;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildServerController"/> class.
    /// </summary>
    /// <param name="childServerManager">Instance of the <see cref="IChildServerManager"/> interface.</param>
    /// <param name="librarySync">Instance of the <see cref="IChildServerLibrarySync"/> interface.</param>
    /// <param name="mediaCache">Instance of the <see cref="IChildServerMediaCache"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    public ChildServerController(
        IChildServerManager childServerManager,
        IChildServerLibrarySync librarySync,
        IChildServerMediaCache mediaCache,
        ILibraryManager libraryManager)
    {
        _childServerManager = childServerManager;
        _librarySync = librarySync;
        _mediaCache = mediaCache;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets the child server settings. Secrets are not included.
    /// </summary>
    /// <response code="200">Settings returned.</response>
    /// <returns>The settings.</returns>
    [HttpGet("Configuration")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ChildServerSettings> GetConfiguration()
    {
        return _childServerManager.GetSettings();
    }

    /// <summary>
    /// Saves the child server settings. An empty password keeps the stored one.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <response code="204">Settings saved.</response>
    /// <response code="400">A value is out of range or the URL is invalid.</response>
    /// <returns>No content on success.</returns>
    [HttpPost("Configuration")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult UpdateConfiguration([FromBody, Required] ChildServerSettings settings)
    {
        try
        {
            _childServerManager.SaveSettings(settings);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        return NoContent();
    }

    /// <summary>
    /// Tries to reach a parent server and sign in with the given credentials without saving anything.
    /// </summary>
    /// <param name="settings">The connection details to try. An empty password uses the stored one when the URL and user name match.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The attempt finished; inspect the status in the body.</response>
    /// <returns>The outcome of the attempt.</returns>
    [HttpPost("TestConnection")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ParentConnectionResult>> TestConnection([FromBody, Required] ParentConnectionSettings settings, CancellationToken cancellationToken)
    {
        return await _childServerManager.TestConnectionAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs in to the configured parent server, reusing the stored access token when it still works.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The attempt finished; inspect the status in the body.</response>
    /// <returns>The outcome of the attempt.</returns>
    [HttpPost("Connect")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ParentConnectionResult>> Connect(CancellationToken cancellationToken)
    {
        return await _childServerManager.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the current state of the link to the parent server, the last sync and the media cache.
    /// </summary>
    /// <response code="200">Status returned.</response>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ChildServerStatus> GetStatus()
    {
        var status = _childServerManager.GetStatus();
        status.SyncState = _librarySync.State;
        status.LastSyncStartedUtc = _librarySync.LastSyncStartedUtc;
        status.LastSyncCompletedUtc = _librarySync.LastSyncCompletedUtc;
        status.LastSyncError = _librarySync.LastSyncError;

        var statistics = _mediaCache.GetStatistics();
        status.MirroredItemCount = statistics.MirroredItemCount;
        status.CachedItemCount = statistics.CachedItemCount;
        status.CachedBytes = statistics.CachedBytes;
        status.ActiveDownloads = statistics.ActiveDownloads;
        return status;
    }

    /// <summary>
    /// Starts mirroring the parent server's libraries in the background.
    /// </summary>
    /// <response code="202">A sync was started, or one was already running.</response>
    /// <returns>Accepted.</returns>
    [HttpPost("Sync")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult StartSync()
    {
        _librarySync.StartSyncInBackground();
        return Accepted();
    }

    /// <summary>
    /// Tells whether an item's media is stored on this device or still has to come from the parent server.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Availability returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>The availability.</returns>
    [HttpGet("Items/{itemId}/Availability")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChildItemAvailability>> GetItemAvailability([FromRoute, Required] Guid itemId, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById<BaseItem>(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var state = _mediaCache.GetState(item.Path);
        var availability = new ChildItemAvailability
        {
            IsManaged = state.IsManaged,
            IsCached = state.IsCached,
            IsDownloading = state.IsDownloading,
            DownloadedBytes = state.DownloadedBytes,
            ExpectedBytes = state.ExpectedBytes
        };

        availability.ParentReachable = state.IsManaged
            ? await _mediaCache.IsParentReachableAsync(cancellationToken).ConfigureAwait(false)
            : true;

        return availability;
    }
}
