using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.ChildServer;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers
{
    public sealed class ChildServerControllerTests : IClassFixture<JellyfinApplicationFactory>
    {
        private readonly JellyfinApplicationFactory _factory;
        private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;
        private static string? _accessToken;

        public ChildServerControllerTests(JellyfinApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task GetConfiguration_Unauthenticated_Unauthorized()
        {
            var client = _factory.CreateClient();

            var response = await client.GetAsync("/ChildServer/Configuration", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task GetConfiguration_AsAdministrator_ReturnsDefaults()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

            var response = await client.GetAsync("/ChildServer/Configuration", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var settings = await response.Content.ReadFromJsonAsync<ChildServerSettings>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(settings);
            Assert.Null(settings.Password);
            Assert.False(settings.HasPassword);
            Assert.Equal(4, settings.PrefetchEpisodeCount);
        }

        [Fact]
        public async Task GetStatus_AsAdministrator_ReportsAnUnconnectedChild()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

            var response = await client.GetAsync("/ChildServer/Status", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var status = await response.Content.ReadFromJsonAsync<ChildServerStatus>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(status);

            // Other tests in this class may have saved settings, but nothing in here can ever sign in or sync.
            Assert.False(status.IsAuthenticated);
            Assert.Equal(ChildSyncState.Idle, status.SyncState);
            Assert.Equal(0, status.MirroredItemCount);
            Assert.Equal(0, status.CachedItemCount);
            Assert.Equal(0, status.ActiveDownloads);
        }

        [Fact]
        public async Task TestConnection_UnreachableParent_ReportsConnectionFailed()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

            var settings = new ParentConnectionSettings
            {
                Url = "http://127.0.0.1:9",
                Username = "alice",
                Password = "secret"
            };

            var response = await client.PostAsJsonAsync("/ChildServer/TestConnection", settings, _jsonOptions, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<ParentConnectionResult>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            Assert.Equal(ParentConnectionStatus.ConnectionFailed, result.Status);
            Assert.False(result.IsSuccess);
            Assert.Contains("127.0.0.1", result.Message, System.StringComparison.Ordinal);
        }

        [Fact]
        public async Task UpdateConfiguration_InvalidUrl_BadRequest()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

            var settings = new ChildServerSettings
            {
                ParentUrl = "ftp://parent",
                Username = "alice"
            };

            var response = await client.PostAsJsonAsync("/ChildServer/Configuration", settings, _jsonOptions, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task UpdateConfiguration_Valid_RoundTripsWithoutSecrets()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

            var settings = new ChildServerSettings
            {
                ParentUrl = "http://parent.example:8096",
                Username = "alice",
                Password = "secret",
                PrefetchEpisodeCount = 3,
                CustomHeaders = new[] { new ParentRequestHeader { Name = "CF-Access-Client-Id", Value = "id" } }
            };

            var saveResponse = await client.PostAsJsonAsync("/ChildServer/Configuration", settings, _jsonOptions, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, saveResponse.StatusCode);

            var readResponse = await client.GetAsync("/ChildServer/Configuration", TestContext.Current.CancellationToken);
            var saved = await readResponse.Content.ReadFromJsonAsync<ChildServerSettings>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(saved);
            Assert.Equal("http://parent.example:8096/", saved.ParentUrl);
            Assert.Equal("alice", saved.Username);
            Assert.Null(saved.Password);
            Assert.True(saved.HasPassword);
            Assert.Equal(3, saved.PrefetchEpisodeCount);
            var header = Assert.Single(saved.CustomHeaders);
            Assert.Equal("CF-Access-Client-Id", header.Name);

            // Leave the shared server unconfigured for the other tests in this class.
            var resetResponse = await client.PostAsJsonAsync("/ChildServer/Configuration", new ChildServerSettings(), _jsonOptions, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, resetResponse.StatusCode);
        }
    }
}
