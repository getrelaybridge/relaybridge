// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RelayBridge.Core.Release;
using RelayBridge.Host.Services;
using RelayBridge.Infrastructure.Release;
using Xunit;

namespace RelayBridge.IntegrationTests;

public sealed class GitHubReleaseCheckerTests
{
    private static readonly ProductSemanticVersion Current = ProductSemanticVersion.Parse("1.0.0-rc.1");
    private static readonly DateTimeOffset CheckedUtc = new(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Compiled_host_exposes_canonical_semantic_and_numeric_versions()
    {
        var assembly = typeof(Program).Assembly;
        var file = FileVersionInfo.GetVersionInfo(assembly.Location);
        var product = new ProductVersionService();

        Assert.Equal(new Version(1, 0, 0, 0), assembly.GetName().Version);
        Assert.Equal("1.0.0.0", file.FileVersion);
        Assert.Equal("1.0.0-rc.1", file.ProductVersion);
        Assert.Equal("1.0.0-rc.1", product.CurrentVersion.ToString());
        Assert.Equal(ReleaseChannel.Preview, product.CurrentChannel);
    }

    [Fact]
    public async Task Preview_selects_highest_valid_release_and_ignores_drafts_and_malformed_tags()
    {
        const string json = """
            [
              {"tag_name":"v9.0.0","draft":true,"prerelease":false,"published_at":"2026-08-30T09:00:00Z"},
              {"tag_name":"v1.0.0-beta","draft":false,"prerelease":true,"published_at":"2026-08-30T09:00:00Z"},
              {"tag_name":"v1.0.0-rc.2","draft":false,"prerelease":true,"published_at":"2026-08-30T09:00:00Z"},
              {"tag_name":"v0.9.2","draft":false,"prerelease":false,"published_at":"2026-08-29T09:00:00Z"}
            ]
            """;

        var result = await CheckAsync(json, ReleaseChannel.Preview);

        Assert.Equal(ReleaseCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.0.0-rc.2", result.AvailableVersion?.ToString());
        Assert.Equal(
            "https://github.com/getrelaybridge/relaybridge/releases/tag/v1.0.0-rc.2",
            result.ReleaseUri?.AbsoluteUri);
    }

    [Fact]
    public async Task Stable_ignores_release_candidates_while_preview_prefers_a_stable_same_base_release()
    {
        const string json = """
            [
              {"tag_name":"v1.1.0-rc.1","draft":false,"prerelease":true,"published_at":"2026-08-30T09:00:00Z"},
              {"tag_name":"v1.0.0","draft":false,"prerelease":false,"published_at":"2026-08-29T09:00:00Z"},
              {"tag_name":"v1.0.0-rc.12","draft":false,"prerelease":true,"published_at":"2026-08-28T09:00:00Z"}
            ]
            """;

        var stable = await CheckAsync(json, ReleaseChannel.Stable);
        var preview = await CheckAsync(json, ReleaseChannel.Preview);

        Assert.Equal("1.0.0", stable.AvailableVersion?.ToString());
        Assert.Equal("1.1.0-rc.1", preview.AvailableVersion?.ToString());
    }

    [Fact]
    public async Task Preview_prefers_stable_over_same_base_rc_and_ignores_release_supplied_url()
    {
        const string json = """
            [
              {"tag_name":"v1.0.0-rc.12","draft":false,"prerelease":true,"html_url":"https://attacker.invalid/download"},
              {"tag_name":"v1.0.0","draft":false,"prerelease":false,"html_url":"https://attacker.invalid/execute"}
            ]
            """;

        var result = await CheckAsync(json, ReleaseChannel.Preview);

        Assert.Equal("1.0.0", result.AvailableVersion?.ToString());
        Assert.Equal(
            "https://github.com/getrelaybridge/relaybridge/releases/tag/v1.0.0",
            result.ReleaseUri?.AbsoluteUri);
        Assert.DoesNotContain("attacker.invalid", result.ReleaseUri?.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"tag_name\":\"v0.9.2\",\"draft\":false,\"prerelease\":false}]")]
    [InlineData("[{\"tag_name\":\"v1.0.0-rc.1\",\"draft\":false,\"prerelease\":true}]")]
    public async Task No_release_equal_release_and_lower_release_are_up_to_date(string json)
    {
        var result = await CheckAsync(json, ReleaseChannel.Preview);

        Assert.Equal(ReleaseCheckStatus.UpToDate, result.Status);
        Assert.Null(result.AvailableVersion);
        Assert.Null(result.ReleaseUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "Http403")]
    [InlineData(HttpStatusCode.InternalServerError, "Http500")]
    public async Task Http_failures_are_safe(HttpStatusCode status, string category)
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)));
        var checker = CreateChecker(client);

        var result = await checker.CheckAsync(Current, ReleaseChannel.Preview, CheckedUtc);

        Assert.Equal(ReleaseCheckStatus.CouldNotCheck, result.Status);
        Assert.Equal(category, result.SafeFailureCategory);
    }

    [Fact]
    public async Task Malformed_json_is_a_safe_failure()
    {
        var result = await CheckAsync("not-json", ReleaseChannel.Preview);

        Assert.Equal(ReleaseCheckStatus.CouldNotCheck, result.Status);
        Assert.Equal("MalformedResponse", result.SafeFailureCategory);
    }

    [Fact]
    public async Task Oversized_response_is_rejected_before_deserialization()
    {
        var oversized = new byte[GitHubReleaseChecker.MaximumResponseBytes + 1];
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(oversized),
        }));

        var result = await CreateChecker(client).CheckAsync(Current, ReleaseChannel.Preview, CheckedUtc);

        Assert.Equal(ReleaseCheckStatus.CouldNotCheck, result.Status);
        Assert.Equal("ResponseTooLarge", result.SafeFailureCategory);
    }

    [Fact]
    public async Task Timeout_is_bounded_and_safe()
    {
        using var client = new HttpClient(new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }))
        {
            Timeout = TimeSpan.FromMilliseconds(50),
        };

        var result = await CreateChecker(client).CheckAsync(Current, ReleaseChannel.Preview, CheckedUtc);

        Assert.Equal(ReleaseCheckStatus.CouldNotCheck, result.Status);
        Assert.Equal("Timeout", result.SafeFailureCategory);
    }

    [Fact]
    public async Task Request_uses_only_fixed_public_metadata_endpoint_and_sends_no_identity_or_authentication()
    {
        HttpRequestMessage? observed = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            observed = request;
            return JsonResponse("[]");
        }));

        await CreateChecker(client).CheckAsync(Current, ReleaseChannel.Preview, CheckedUtc);

        Assert.NotNull(observed);
        Assert.Equal(GitHubReleaseChecker.ReleasesApiUri, observed!.RequestUri);
        Assert.Equal(HttpMethod.Get, observed.Method);
        Assert.Null(observed.Headers.Authorization);
        Assert.Null(observed.Content);
        Assert.Equal("RelayBridge/1.0.0-rc.1", observed.Headers.UserAgent.ToString());
        var requestText = observed.ToString();
        Assert.DoesNotContain("tenant", requestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientId", requestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sender", requestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("device", requestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hostname", requestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificate", requestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("queue", requestText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Awareness_service_makes_no_request_until_the_administrator_checks()
    {
        var requests = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return JsonResponse("[]");
        }));
        var service = new ReleaseAwarenessService(
            CreateChecker(client),
            new ProductVersionService(),
            TimeProvider.System);

        Assert.Equal(ReleaseCheckStatus.NotChecked, service.Status);
        Assert.Equal(0, requests);

        await service.CheckNowAsync();

        Assert.Equal(1, requests);
        Assert.Equal(ReleaseCheckStatus.UpToDate, service.Status);
    }

    private static async Task<ReleaseCheckResult> CheckAsync(string json, ReleaseChannel channel)
    {
        using var client = new HttpClient(new StubHandler(_ => JsonResponse(json)));
        return await CreateChecker(client).CheckAsync(Current, channel, CheckedUtc);
    }

    private static GitHubReleaseChecker CreateChecker(HttpClient client) =>
        new(client, NullLogger<GitHubReleaseChecker>.Instance);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this((request, _) => Task.FromResult(handler(request)))
        {
        }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _handler(request, cancellationToken);
    }
}
