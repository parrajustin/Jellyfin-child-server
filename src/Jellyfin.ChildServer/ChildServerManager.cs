using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.ChildServer;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.ChildServer;
using Microsoft.Extensions.Logging;

namespace Jellyfin.ChildServer;

/// <summary>
/// Owns the link between this child server and its parent: settings, sign in state and connection checks.
/// </summary>
public class ChildServerManager : IChildServerManager
{
    /// <summary>
    /// The key of the configuration store, which lands in <c>childserver.xml</c>.
    /// </summary>
    public const string ConfigurationKey = "childserver";

    private const int MaxPrefetchEpisodes = 50;
    private const int MaxDownloadWaitSeconds = 86400;
    private const int MaxSyncIntervalHours = 168;

    private readonly IServerConfigurationManager _configurationManager;
    private readonly ParentServerClient _client;
    private readonly ILogger<ChildServerManager> _logger;
    private readonly Lock _statusLock = new();

    private ParentConnectionResult? _lastAttempt;
    private DateTime? _lastAttemptUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildServerManager"/> class.
    /// </summary>
    /// <param name="configurationManager">The configuration manager that persists <see cref="ChildServerConfiguration"/>.</param>
    /// <param name="client">The parent server client.</param>
    /// <param name="logger">The logger.</param>
    public ChildServerManager(IServerConfigurationManager configurationManager, ParentServerClient client, ILogger<ChildServerManager> logger)
    {
        _configurationManager = configurationManager;
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public ChildServerConfiguration Configuration => _configurationManager.GetConfiguration<ChildServerConfiguration>(ConfigurationKey);

    /// <inheritdoc />
    public bool IsConfigured => IsConfiguredInternal(Configuration);

    /// <inheritdoc />
    public ChildServerSettings GetSettings()
    {
        var config = Configuration;
        return new ChildServerSettings
        {
            ParentUrl = config.ParentUrl,
            Username = config.Username,
            Password = null,
            HasPassword = !string.IsNullOrEmpty(config.Password),
            CustomHeaders = CloneHeaders(config.CustomHeaders),
            PrefetchEpisodeCount = config.PrefetchEpisodeCount,
            MaxCacheSizeMb = config.MaxCacheSizeMb,
            DownloadWaitTimeoutSeconds = config.DownloadWaitTimeoutSeconds,
            SyncIntervalHours = config.SyncIntervalHours
        };
    }

    /// <inheritdoc />
    public void SaveSettings(ChildServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? parentUrl = null;
        if (!string.IsNullOrWhiteSpace(settings.ParentUrl))
        {
            parentUrl = ParentServerClient.TryParseBaseUrl(settings.ParentUrl)?.ToString()
                ?? throw new ArgumentException("The parent server URL must be an absolute http or https URL, for example http://192.168.1.10:8096.", nameof(settings));
        }

        if (settings.PrefetchEpisodeCount < 0 || settings.PrefetchEpisodeCount > MaxPrefetchEpisodes)
        {
            throw new ArgumentException("The number of episodes to prefetch must be between 0 and " + MaxPrefetchEpisodes.ToString(CultureInfo.InvariantCulture) + ".", nameof(settings));
        }

        if (settings.MaxCacheSizeMb < 0)
        {
            throw new ArgumentException("The cache size cannot be negative.", nameof(settings));
        }

        if (settings.DownloadWaitTimeoutSeconds < 0 || settings.DownloadWaitTimeoutSeconds > MaxDownloadWaitSeconds)
        {
            throw new ArgumentException("The download wait timeout must be between 0 and " + MaxDownloadWaitSeconds.ToString(CultureInfo.InvariantCulture) + " seconds.", nameof(settings));
        }

        if (settings.SyncIntervalHours < 1 || settings.SyncIntervalHours > MaxSyncIntervalHours)
        {
            throw new ArgumentException("The sync interval must be between 1 and " + MaxSyncIntervalHours.ToString(CultureInfo.InvariantCulture) + " hours.", nameof(settings));
        }

        var headers = CleanHeaders(settings.CustomHeaders);
        var username = string.IsNullOrWhiteSpace(settings.Username) ? null : settings.Username.Trim();

        var config = Configuration;
        var credentialsChanged = !string.Equals(parentUrl, config.ParentUrl, StringComparison.Ordinal)
            || !string.Equals(username, config.Username, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(settings.Password);

        config.ParentUrl = parentUrl;
        config.Username = username;
        if (!string.IsNullOrEmpty(settings.Password))
        {
            config.Password = settings.Password;
        }

        config.CustomHeaders = headers;
        config.PrefetchEpisodeCount = settings.PrefetchEpisodeCount;
        config.MaxCacheSizeMb = settings.MaxCacheSizeMb;
        config.DownloadWaitTimeoutSeconds = settings.DownloadWaitTimeoutSeconds;
        config.SyncIntervalHours = settings.SyncIntervalHours;

        if (credentialsChanged)
        {
            config.AccessToken = null;
            config.ParentUserId = null;
            config.ParentServerId = null;
            config.ParentServerName = null;
        }

        _configurationManager.SaveConfiguration(ConfigurationKey, config);
        _logger.LogInformation("Child server settings saved; parent {ParentUrl}, credentials changed: {CredentialsChanged}", parentUrl ?? "(none)", credentialsChanged);
    }

    /// <inheritdoc />
    public ChildServerStatus GetStatus()
    {
        var config = Configuration;
        var status = new ChildServerStatus
        {
            IsConfigured = IsConfiguredInternal(config),
            IsAuthenticated = !string.IsNullOrEmpty(config.AccessToken),
            ParentServerName = config.ParentServerName,
            ParentUserId = config.ParentUserId
        };

        lock (_statusLock)
        {
            if (_lastAttempt is not null)
            {
                status.LastConnectionStatus = _lastAttempt.Status;
                status.LastConnectionMessage = _lastAttempt.Message;
                status.LastConnectionAttemptUtc = _lastAttemptUtc;
                status.ParentServerVersion = _lastAttempt.ServerVersion;
            }
        }

        return status;
    }

    /// <inheritdoc />
    public async Task<ParentConnectionResult> TestConnectionAsync(ParentConnectionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var password = settings.Password;
        if (string.IsNullOrEmpty(password))
        {
            // The form does not echo the stored password back, so an empty field means "use what is saved".
            var config = Configuration;
            var sameAccount = string.Equals(ParentServerClient.TryParseBaseUrl(settings.Url)?.ToString(), config.ParentUrl, StringComparison.Ordinal)
                && string.Equals(settings.Username?.Trim(), config.Username, StringComparison.Ordinal);
            password = sameAccount ? config.Password : null;
        }

        var (result, _) = await ProbeAndSignInAsync(settings.Url, settings.Username, password, settings.CustomHeaders, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async Task<ParentConnectionResult> ConnectAsync(CancellationToken cancellationToken)
    {
        var config = Configuration;
        if (!IsConfiguredInternal(config))
        {
            return RecordAttempt(new ParentConnectionResult
            {
                Status = ParentConnectionStatus.NotConfigured,
                Message = "Configure the parent server URL and user name first."
            });
        }

        ParentConnectionResult? result = null;
        if (!string.IsNullOrEmpty(config.AccessToken))
        {
            var endpoint = new ParentEndpoint(ParentServerClient.TryParseBaseUrl(config.ParentUrl)!, config.CustomHeaders);
            try
            {
                var info = await _client.GetSystemInfoAsync(endpoint, config.AccessToken, cancellationToken).ConfigureAwait(false);
                result = new ParentConnectionResult
                {
                    Status = ParentConnectionStatus.Success,
                    Message = "Connected to " + info.ServerName + " (" + info.Version + ") with the stored sign in.",
                    ServerName = info.ServerName,
                    ServerVersion = info.Version,
                    ServerId = info.Id,
                    UserId = config.ParentUserId
                };
            }
            catch (ParentServerException ex) when (ex.Status == ParentConnectionStatus.InvalidCredentials)
            {
                _logger.LogInformation("The stored parent access token was rejected; signing in again");
            }
            catch (ParentServerException ex)
            {
                _logger.LogWarning(ex, "Checking the stored parent sign in failed");
                result = FromException(ex);
            }
        }

        if (result is null)
        {
            var (signIn, accessToken) = await ProbeAndSignInAsync(config.ParentUrl, config.Username, config.Password, config.CustomHeaders, cancellationToken).ConfigureAwait(false);
            result = signIn;

            if (signIn.IsSuccess)
            {
                config.AccessToken = accessToken;
                config.ParentUserId = signIn.UserId;
                config.ParentServerId = signIn.ServerId;
                config.ParentServerName = signIn.ServerName;
                _configurationManager.SaveConfiguration(ConfigurationKey, config);
            }
        }

        return RecordAttempt(result);
    }

    private static bool IsConfiguredInternal(ChildServerConfiguration config)
        => ParentServerClient.TryParseBaseUrl(config.ParentUrl) is not null && !string.IsNullOrWhiteSpace(config.Username);

    private static ParentRequestHeader[] CloneHeaders(IEnumerable<ParentRequestHeader>? headers)
        => headers?.Select(h => new ParentRequestHeader { Name = h.Name, Value = h.Value }).ToArray() ?? Array.Empty<ParentRequestHeader>();

    private static ParentRequestHeader[] CleanHeaders(IEnumerable<ParentRequestHeader>? headers)
    {
        if (headers is null)
        {
            return Array.Empty<ParentRequestHeader>();
        }

        var cleaned = new List<ParentRequestHeader>();
        foreach (var header in headers)
        {
            var name = header.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == ':'))
            {
                throw new ArgumentException("The header name '" + name + "' contains characters that are not allowed in an HTTP header name.", nameof(headers));
            }

            var value = header.Value ?? string.Empty;
            if (value.Any(c => c == '\r' || c == '\n'))
            {
                throw new ArgumentException("The value of header '" + name + "' cannot span multiple lines.", nameof(headers));
            }

            cleaned.Add(new ParentRequestHeader { Name = name, Value = value.Trim() });
        }

        return cleaned.ToArray();
    }

    private static ParentConnectionResult FromException(ParentServerException ex)
        => new()
        {
            Status = ex.Status,
            Message = ex.Message
        };

    private ParentConnectionResult RecordAttempt(ParentConnectionResult result)
    {
        lock (_statusLock)
        {
            _lastAttempt = result;
            _lastAttemptUtc = DateTime.UtcNow;
        }

        return result;
    }

    private async Task<(ParentConnectionResult Result, string? AccessToken)> ProbeAndSignInAsync(
        string? url,
        string? username,
        string? password,
        IReadOnlyList<ParentRequestHeader>? headers,
        CancellationToken cancellationToken)
    {
        var baseUrl = ParentServerClient.TryParseBaseUrl(url);
        if (baseUrl is null)
        {
            return (new ParentConnectionResult
            {
                Status = ParentConnectionStatus.ConnectionFailed,
                Message = "The parent server URL must be an absolute http or https URL, for example http://192.168.1.10:8096."
            }, null);
        }

        username = username?.Trim();
        if (string.IsNullOrEmpty(username))
        {
            return (new ParentConnectionResult
            {
                Status = ParentConnectionStatus.InvalidCredentials,
                Message = "Enter the user name of an account on the parent server."
            }, null);
        }

        var endpoint = new ParentEndpoint(baseUrl, headers ?? Array.Empty<ParentRequestHeader>());
        string? serverName = null;
        string? serverVersion = null;
        string? serverId = null;
        try
        {
            var info = await _client.GetPublicSystemInfoAsync(endpoint, cancellationToken).ConfigureAwait(false);
            serverName = info.ServerName;
            serverVersion = info.Version;
            serverId = info.Id;

            var auth = await _client.AuthenticateAsync(endpoint, username, password ?? string.Empty, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(auth.AccessToken))
            {
                throw new ParentServerException(ParentConnectionStatus.InvalidResponse, "The parent server signed in but did not return an access token.");
            }

            var userId = auth.User?.Id.ToString("N", CultureInfo.InvariantCulture);
            var userName = auth.User?.Name ?? username;
            _logger.LogInformation("Signed in to parent server {ServerName} ({ServerVersion}) as {UserName}", serverName, serverVersion, userName);

            return (new ParentConnectionResult
            {
                Status = ParentConnectionStatus.Success,
                Message = "Signed in to " + serverName + " (" + serverVersion + ") as " + userName + ".",
                ServerName = serverName,
                ServerVersion = serverVersion,
                ServerId = serverId,
                UserId = userId
            }, auth.AccessToken);
        }
        catch (ParentServerException ex)
        {
            _logger.LogWarning("Connecting to parent server {BaseUrl} failed: {Status} {Message}", baseUrl, ex.Status, ex.Message);
            var result = FromException(ex);
            result.ServerName = serverName;
            result.ServerVersion = serverVersion;
            result.ServerId = serverId;
            return (result, null);
        }
    }
}
