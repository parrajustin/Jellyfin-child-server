using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.ChildServer;
using MediaBrowser.Model.ChildServer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Settings and connection state of this child server's link to its parent Jellyfin server.
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
public class ChildServerController : BaseJellyfinApiController
{
    private readonly IChildServerManager _childServerManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildServerController"/> class.
    /// </summary>
    /// <param name="childServerManager">Instance of the <see cref="IChildServerManager"/> interface.</param>
    public ChildServerController(IChildServerManager childServerManager)
    {
        _childServerManager = childServerManager;
    }

    /// <summary>
    /// Gets the child server settings. Secrets are not included.
    /// </summary>
    /// <response code="200">Settings returned.</response>
    /// <returns>The settings.</returns>
    [HttpGet("Configuration")]
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ParentConnectionResult>> Connect(CancellationToken cancellationToken)
    {
        return await _childServerManager.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the current state of the link to the parent server.
    /// </summary>
    /// <response code="200">Status returned.</response>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ChildServerStatus> GetStatus()
    {
        return _childServerManager.GetStatus();
    }
}
