using System.IO;
using MediaBrowser.Controller;
using MediaBrowser.Controller.ChildServer;

namespace Jellyfin.ChildServer;

/// <summary>
/// Where the child server keeps its mirrored library and its manifest.
/// </summary>
public class ChildServerPaths
{
    private readonly IServerApplicationPaths _appPaths;
    private readonly IChildServerManager _manager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildServerPaths"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="manager">The child server manager, for the configured library location.</param>
    public ChildServerPaths(IServerApplicationPaths appPaths, IChildServerManager manager)
    {
        _appPaths = appPaths;
        _manager = manager;
    }

    /// <summary>
    /// Gets the folder that holds the child server's own files.
    /// </summary>
    public string DataRoot => Path.Combine(_appPaths.DataPath, "childserver");

    /// <summary>
    /// Gets the root folder of the mirrored library. Each parent library becomes a sub folder.
    /// </summary>
    public string LibraryRoot
    {
        get
        {
            var configured = _manager.Configuration.LibraryPath;
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(DataRoot, "library")
                : Path.GetFullPath(configured);
        }
    }

    /// <summary>
    /// Gets the path of the mirror manifest file.
    /// </summary>
    public string ManifestPath => Path.Combine(DataRoot, "mirror.json");
}
