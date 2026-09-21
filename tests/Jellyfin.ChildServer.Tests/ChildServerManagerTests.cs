using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.ChildServer;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.ChildServer.Tests;

public sealed class ChildServerManagerTests : IDisposable
{
    private const string ParentUrl = "http://parent.example:8096";
    private const string PublicInfoJson = "{\"ServerName\":\"Parent\",\"Version\":\"12.1.0\",\"Id\":\"parent-id\",\"StartupWizardCompleted\":true}";
    private const string SystemInfoJson = "{\"ServerName\":\"Parent\",\"Version\":\"12.1.0\",\"Id\":\"parent-id\"}";
    private static readonly Guid _parentUserId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly string _authJson = "{\"AccessToken\":\"token-1\",\"ServerId\":\"parent-id\",\"User\":{\"Id\":\"" + _parentUserId.ToString("D") + "\",\"Name\":\"alice\"}}";

    private readonly StubHttpMessageHandler _handler = new();
    private readonly ChildServerConfiguration _config = new();
    private readonly Mock<IServerConfigurationManager> _configurationManager = new();
    private object? _savedConfiguration;

    public ChildServerManagerTests()
    {
        _configurationManager.Setup(c => c.GetConfiguration(ChildServerManager.ConfigurationKey)).Returns(_config);
        _configurationManager
            .Setup(c => c.SaveConfiguration(ChildServerManager.ConfigurationKey, It.IsAny<object>()))
            .Callback<string, object>((_, value) => _savedConfiguration = value);
    }

    [Theory]
    [InlineData("192.168.1.10:8096", "http://192.168.1.10:8096/")]
    [InlineData("https://media.example.com/jellyfin", "https://media.example.com/jellyfin/")]
    [InlineData("  http://parent:8096/  ", "http://parent:8096/")]
    [InlineData("http://parent:8096/?x=1#frag", "http://parent:8096/")]
    public void TryParseBaseUrl_ValidInput_Normalizes(string input, string expected)
    {
        Assert.Equal(expected, ParentServerClient.TryParseBaseUrl(input)?.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://parent:8096")]
    [InlineData("not a url")]
    public void TryParseBaseUrl_InvalidInput_ReturnsNull(string? input)
    {
        Assert.Null(ParentServerClient.TryParseBaseUrl(input));
    }

    [Fact]
    public async Task TestConnection_ValidCredentials_ReturnsSuccessWithServerDetails()
    {
        MapHealthyParent();
        var manager = CreateManager();

        var result = await manager.TestConnectionAsync(Settings(password: "secret"), TestContext.Current.CancellationToken);

        Assert.Equal(ParentConnectionStatus.Success, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Equal("Parent", result.ServerName);
        Assert.Equal("12.1.0", result.ServerVersion);
        Assert.Equal("parent-id", result.ServerId);
        Assert.Equal(_parentUserId.ToString("N"), result.UserId);
        Assert.Contains("alice", result.Message, StringComparison.Ordinal);

        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, _handler.Requests[0].Method);
        Assert.Equal("/System/Info/Public", _handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, _handler.Requests[1].Method);
        Assert.Equal("/Users/AuthenticateByName", _handler.Requests[1].RequestUri!.AbsolutePath);

        // Nothing is persisted by a test.
        Assert.Null(_savedConfiguration);
    }

    [Fact]
    public async Task TestConnection_IdentifiesItselfWithJellyfinAuthorizationHeader()
    {
        MapHealthyParent();
        var manager = CreateManager();

        await manager.TestConnectionAsync(Settings(password: "secret"), TestContext.Current.CancellationToken);

        var authorization = _handler.Requests[0].Headers.Authorization;
        Assert.NotNull(authorization);
        Assert.Equal("MediaBrowser", authorization.Scheme);
        Assert.Contains("Client=\"Jellyfin%20Child%20Server\"", authorization.Parameter, StringComparison.Ordinal);
        Assert.Contains("DeviceId=\"child-system-id\"", authorization.Parameter, StringComparison.Ordinal);
        Assert.Contains("Device=\"Child%20Box\"", authorization.Parameter, StringComparison.Ordinal);
        Assert.Contains("Version=\"12.1.0\"", authorization.Parameter, StringComparison.Ordinal);
        Assert.DoesNotContain("Token=", authorization.Parameter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnection_WrongPassword_ReturnsInvalidCredentials()
    {
        _handler.MapJson("/System/Info/Public", HttpStatusCode.OK, PublicInfoJson);
        _handler.Map("/Users/AuthenticateByName", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var manager = CreateManager();

        var result = await manager.TestConnectionAsync(Settings(password: "wrong"), TestContext.Current.CancellationToken);

        Assert.Equal(ParentConnectionStatus.InvalidCredentials, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Contains("user name or password", result.Message, StringComparison.Ordinal);
        Assert.Equal("Parent", result.ServerName);
    }

    [Fact]
    public async Task TestConnection_ServerUnreachable_ReturnsConnectionFailed()
    {
        _handler.FailWith = new HttpRequestException("No such host is known.");
        var manager = CreateManager();

        var result = await manager.TestConnectionAsync(Settings(password: "secret"), TestContext.Current.CancellationToken);

        Assert.Equal(ParentConnectionStatus.ConnectionFailed, result.Status);
        Assert.Contains("parent.example", result.Message, StringComparison.Ordinal);
        Assert.Contains("No such host is known.", result.Message, StringComparison.Ordinal);
        Assert.Null(result.ServerName);
    }

    [Fact]
    public async Task TestConnection_NotAJellyfinServer_ReturnsInvalidResponse()
    {
        _handler.Map("/System/Info/Public", _ => StubHttpMessageHandler.Html(HttpStatusCode.OK, "<html><body>Sign in</body></html>"));
        var manager = CreateManager();

        var result = await manager.TestConnectionAsync(Settings(password: "secret"), TestContext.Current.CancellationToken);

        Assert.Equal(ParentConnectionStatus.InvalidResponse, result.Status);
        Assert.Contains("text/html", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnection_GatewayRefuses_ReturnsAccessDenied()
    {
        _handler.Map("/System/Info/Public", _ => StubHttpMessageHandler.Html(HttpStatusCode.Forbidden, "forbidden"));
        var manager = CreateManager();

        var result = await manager.TestConnectionAsync(Settings(password: "secret"), TestContext.Current.CancellationToken);

        Assert.Equal(ParentConnectionStatus.AccessDenied, result.Status);
        Assert.Contains("403", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnection_CustomHeaders_AreSentOnEveryRequest()
    {
        MapHealthyParent();
        var manager = CreateManager();
        var settings = Settings(password: "secret");
        settings.CustomHeaders = new[]
        {
            new ParentRequestHeader { Name = "CF-Access-Client-Id", Value = "client-id" },
            new ParentRequestHeader { Name = "CF-Access-Client-Secret", Value = "client-secret" }
        };

        var result = await manager.TestConnectionAsync(settings, TestContext.Current.CancellationToken);

        Assert.Equal(ParentConnectionStatus.Success, result.Status);
        Assert.All(_handler.Requests, request =>
        {
            Assert.Equal("client-id", request.Headers.GetValues("CF-Access-Client-Id").Single());
            Assert.Equal("client-secret", request.Headers.GetValues("CF-Access-Client-Secret").Single());
        });
    }

    [Fact]
    public async Task TestConnection_InvalidUrl_FailsWithoutRequests()
    {
        var manager = CreateManager();
        var settings = Settings(password: "secret");
        settings.Url = "ftp://parent";

        var result = await manager.TestConnectionAsync(settings, TestContext.Current.CancellationToken);

        Assert.Equal(ParentConnectionStatus.ConnectionFailed, result.Status);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task TestConnection_EmptyPassword_UsesStoredPasswordForSameAccount()
    {
        ConfigureParent(accessToken: null);
        string? sentPassword = null;
        _handler.MapJson("/System/Info/Public", HttpStatusCode.OK, PublicInfoJson);
        _handler.Map("/Users/AuthenticateByName", request =>
        {
            sentPassword = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, _authJson);
        });
        var manager = CreateManager();

        var result = await manager.TestConnectionAsync(Settings(password: null), TestContext.Current.CancellationToken);

        Assert.Equal(ParentConnectionStatus.Success, result.Status);
        Assert.Contains("stored-secret", sentPassword, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_NotConfigured_ReturnsNotConfigured()
    {
        var manager = CreateManager();

        var result = await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ParentConnectionStatus.NotConfigured, result.Status);
        Assert.Empty(_handler.Requests);
        Assert.Equal(ParentConnectionStatus.NotConfigured, manager.GetStatus().LastConnectionStatus);
    }

    [Fact]
    public async Task Connect_SignsInAndPersistsToken()
    {
        ConfigureParent(accessToken: null);
        MapHealthyParent();
        var manager = CreateManager();

        var result = await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ParentConnectionStatus.Success, result.Status);
        var saved = Assert.IsType<ChildServerConfiguration>(_savedConfiguration);
        Assert.Equal("token-1", saved.AccessToken);
        Assert.Equal(_parentUserId.ToString("N"), saved.ParentUserId);
        Assert.Equal("parent-id", saved.ParentServerId);
        Assert.Equal("Parent", saved.ParentServerName);

        var status = manager.GetStatus();
        Assert.True(status.IsConfigured);
        Assert.True(status.IsAuthenticated);
        Assert.Equal("Parent", status.ParentServerName);
        Assert.Equal(ParentConnectionStatus.Success, status.LastConnectionStatus);
        Assert.NotNull(status.LastConnectionAttemptUtc);
    }

    [Fact]
    public async Task Connect_StoredTokenStillValid_DoesNotSignInAgain()
    {
        ConfigureParent(accessToken: "token-0");
        _handler.MapJson("/System/Info", HttpStatusCode.OK, SystemInfoJson);
        var manager = CreateManager();

        var result = await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ParentConnectionStatus.Success, result.Status);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("/System/Info", request.RequestUri!.AbsolutePath);
        Assert.Contains("Token=\"token-0\"", request.Headers.Authorization!.Parameter, StringComparison.Ordinal);
        Assert.Null(_savedConfiguration);
    }

    [Fact]
    public async Task Connect_StoredTokenRejected_SignsInAgain()
    {
        ConfigureParent(accessToken: "token-0");
        _handler.Map("/System/Info", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        MapHealthyParent();
        var manager = CreateManager();

        var result = await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ParentConnectionStatus.Success, result.Status);
        var saved = Assert.IsType<ChildServerConfiguration>(_savedConfiguration);
        Assert.Equal("token-1", saved.AccessToken);
        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task Connect_ParentDown_ReportsConnectionFailedAndKeepsToken()
    {
        ConfigureParent(accessToken: "token-0");
        _handler.FailWith = new HttpRequestException("Connection refused");
        var manager = CreateManager();

        var result = await manager.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ParentConnectionStatus.ConnectionFailed, result.Status);
        Assert.Null(_savedConfiguration);
        Assert.Equal("token-0", _config.AccessToken);
        Assert.Equal(ParentConnectionStatus.ConnectionFailed, manager.GetStatus().LastConnectionStatus);
    }

    [Fact]
    public void SaveSettings_KeepsPasswordWhenEmptyAndDropsTokenWhenAccountChanges()
    {
        ConfigureParent(accessToken: "token-0");
        var manager = CreateManager();

        manager.SaveSettings(new ChildServerSettings
        {
            ParentUrl = ParentUrl,
            Username = "alice",
            Password = string.Empty,
            PrefetchEpisodeCount = 2
        });

        Assert.Equal("stored-secret", _config.Password);
        Assert.Equal("token-0", _config.AccessToken);
        Assert.Equal(2, _config.PrefetchEpisodeCount);

        manager.SaveSettings(new ChildServerSettings
        {
            ParentUrl = "http://other.example:8096",
            Username = "alice",
            Password = null
        });

        Assert.Equal("http://other.example:8096/", _config.ParentUrl);
        Assert.Null(_config.AccessToken);
        Assert.Null(_config.ParentUserId);
        Assert.Equal("stored-secret", _config.Password);
        Assert.Same(_config, _savedConfiguration);
    }

    [Fact]
    public void SaveSettings_NewPassword_ReplacesPasswordAndDropsToken()
    {
        ConfigureParent(accessToken: "token-0");
        var manager = CreateManager();

        manager.SaveSettings(new ChildServerSettings { ParentUrl = ParentUrl, Username = "alice", Password = "new-secret" });

        Assert.Equal("new-secret", _config.Password);
        Assert.Null(_config.AccessToken);
    }

    [Fact]
    public void SaveSettings_CleansHeaders()
    {
        var manager = CreateManager();

        manager.SaveSettings(new ChildServerSettings
        {
            CustomHeaders = new[]
            {
                new ParentRequestHeader { Name = "  CF-Access-Client-Id ", Value = " id " },
                new ParentRequestHeader { Name = string.Empty, Value = "ignored" }
            }
        });

        var header = Assert.Single(_config.CustomHeaders);
        Assert.Equal("CF-Access-Client-Id", header.Name);
        Assert.Equal("id", header.Value);
    }

    [Theory]
    [InlineData("ftp://parent", 4, 100, 60, 6)]
    [InlineData(ParentUrl, -1, 100, 60, 6)]
    [InlineData(ParentUrl, 4, -1, 60, 6)]
    [InlineData(ParentUrl, 4, 100, -5, 6)]
    [InlineData(ParentUrl, 4, 100, 60, 0)]
    public void SaveSettings_RejectsBadValues(string url, int prefetch, int cacheMb, int waitSeconds, int syncHours)
    {
        var manager = CreateManager();
        var settings = new ChildServerSettings
        {
            ParentUrl = url,
            Username = "alice",
            PrefetchEpisodeCount = prefetch,
            MaxCacheSizeMb = cacheMb,
            DownloadWaitTimeoutSeconds = waitSeconds,
            SyncIntervalHours = syncHours
        };

        Assert.Throws<ArgumentException>(() => manager.SaveSettings(settings));
        Assert.Null(_savedConfiguration);
    }

    [Fact]
    public void SaveSettings_RejectsInvalidHeaderName()
    {
        var manager = CreateManager();
        var settings = new ChildServerSettings
        {
            CustomHeaders = new[] { new ParentRequestHeader { Name = "Bad Header:", Value = "x" } }
        };

        Assert.Throws<ArgumentException>(() => manager.SaveSettings(settings));
    }

    [Fact]
    public void GetSettings_NeverReturnsSecrets()
    {
        ConfigureParent(accessToken: "token-0");
        var manager = CreateManager();

        var settings = manager.GetSettings();

        Assert.Equal(ParentUrl + "/", settings.ParentUrl);
        Assert.Equal("alice", settings.Username);
        Assert.Null(settings.Password);
        Assert.True(settings.HasPassword);
        Assert.True(manager.IsConfigured);
    }

    public void Dispose()
    {
        _handler.Dispose();
    }

    private static ParentConnectionSettings Settings(string? password)
        => new()
        {
            Url = ParentUrl,
            Username = "alice",
            Password = password
        };

    private void MapHealthyParent()
    {
        _handler.MapJson("/System/Info/Public", HttpStatusCode.OK, PublicInfoJson);
        _handler.MapJson("/Users/AuthenticateByName", HttpStatusCode.OK, _authJson);
    }

    private void ConfigureParent(string? accessToken)
    {
        _config.ParentUrl = ParentUrl + "/";
        _config.Username = "alice";
        _config.Password = "stored-secret";
        _config.AccessToken = accessToken;
        _config.ParentUserId = accessToken is null ? null : _parentUserId.ToString("N");
    }

    private ChildServerManager CreateManager()
    {
        var appHost = new Mock<IServerApplicationHost>();
        appHost.SetupGet(h => h.SystemId).Returns("child-system-id");
        appHost.SetupGet(h => h.FriendlyName).Returns("Child Box");
        appHost.SetupGet(h => h.ApplicationVersionString).Returns("12.1.0");

        var client = new ParentServerClient(new StubHttpClientFactory(_handler), appHost.Object, NullLogger<ParentServerClient>.Instance);
        return new ChildServerManager(_configurationManager.Object, client, NullLogger<ChildServerManager>.Instance);
    }
}
