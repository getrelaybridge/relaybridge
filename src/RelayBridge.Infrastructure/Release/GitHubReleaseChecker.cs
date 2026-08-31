// SPDX-License-Identifier: MPL-2.0

using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelayBridge.Core.Release;

namespace RelayBridge.Infrastructure.Release;

public sealed class GitHubReleaseChecker
{
    public static readonly Uri ReleasesApiUri = new(
        "https://api.github.com/repos/getrelaybridge/relaybridge/releases?per_page=100&page=1",
        UriKind.Absolute);

    public const int MaximumResponseBytes = 256 * 1024;
    public const int MaximumReleases = 100;

    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubReleaseChecker> _logger;

    public GitHubReleaseChecker(HttpClient httpClient, ILogger<GitHubReleaseChecker> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ReleaseCheckResult> CheckAsync(
        ProductSemanticVersion currentVersion,
        ReleaseChannel channel,
        DateTimeOffset checkedUtc,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "UpdateCheckStarted InstalledVersion={InstalledVersion} ReleaseChannel={ReleaseChannel}",
            currentVersion,
            channel);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd($"RelayBridge/{currentVersion}");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failure(currentVersion, channel, checkedUtc, $"Http{(int)response.StatusCode}");
            }

            var content = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var release = SelectHighestRelease(content, channel);
            var status = release is not null && release.Value.Version.CompareTo(currentVersion) > 0
                ? ReleaseCheckStatus.UpdateAvailable
                : ReleaseCheckStatus.UpToDate;
            var result = new ReleaseCheckResult(
                status,
                checkedUtc,
                currentVersion,
                channel,
                status == ReleaseCheckStatus.UpdateAvailable ? release?.Version : null,
                status == ReleaseCheckStatus.UpdateAvailable ? release?.PublishedUtc : null);

            if (status == ReleaseCheckStatus.UpdateAvailable)
            {
                _logger.LogInformation(
                    "UpdateAvailable InstalledVersion={InstalledVersion} CandidateVersion={CandidateVersion} ReleaseChannel={ReleaseChannel}",
                    currentVersion,
                    release?.Version,
                    channel);
            }
            else
            {
                _logger.LogInformation(
                    "UpdateCheckSucceeded InstalledVersion={InstalledVersion} ReleaseChannel={ReleaseChannel}",
                    currentVersion,
                    channel);
            }
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(currentVersion, channel, checkedUtc, "Timeout");
        }
        catch (HttpRequestException)
        {
            return Failure(currentVersion, channel, checkedUtc, "Network");
        }
        catch (IOException)
        {
            return Failure(currentVersion, channel, checkedUtc, "Network");
        }
        catch (InvalidDataException)
        {
            return Failure(currentVersion, channel, checkedUtc, "ResponseTooLarge");
        }
        catch (JsonException)
        {
            return Failure(currentVersion, channel, checkedUtc, "MalformedResponse");
        }
    }

    private ReleaseCheckResult Failure(
        ProductSemanticVersion currentVersion,
        ReleaseChannel channel,
        DateTimeOffset checkedUtc,
        string category)
    {
        _logger.LogWarning(
            "UpdateCheckFailed InstalledVersion={InstalledVersion} ReleaseChannel={ReleaseChannel} FailureCategory={FailureCategory}",
            currentVersion,
            channel,
            category);
        return new ReleaseCheckResult(
            ReleaseCheckStatus.CouldNotCheck,
            checkedUtc,
            currentVersion,
            channel,
            SafeFailureCategory: category);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("The release response exceeds the allowed size.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaximumResponseBytes)
                {
                    throw new InvalidDataException("The release response exceeds the allowed size.");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static CandidateRelease? SelectHighestRelease(byte[] content, ReleaseChannel channel)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The release response must be an array.");
        }

        CandidateRelease? highest = null;
        var inspected = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (++inspected > MaximumReleases)
            {
                break;
            }

            if (element.ValueKind != JsonValueKind.Object ||
                !TryReadBoolean(element, "draft", out var draft) || draft ||
                !TryReadBoolean(element, "prerelease", out var prerelease) ||
                !TryReadString(element, "tag_name", 64, out var tag) ||
                !ProductSemanticVersion.TryParseTag(tag, out var version) ||
                prerelease != version.IsPrerelease ||
                (channel == ReleaseChannel.Stable && prerelease))
            {
                continue;
            }

            DateTimeOffset? publishedUtc = null;
            if (element.TryGetProperty("published_at", out var published) &&
                published.ValueKind == JsonValueKind.String &&
                published.TryGetDateTimeOffset(out var parsedPublished))
            {
                publishedUtc = parsedPublished.ToUniversalTime();
            }

            var candidate = new CandidateRelease(version, publishedUtc);
            if (highest is null || candidate.Version.CompareTo(highest.Value.Version) > 0)
            {
                highest = candidate;
            }
        }

        return highest;
    }

    private static bool TryReadBoolean(JsonElement element, string property, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(property, out var field) ||
            (field.ValueKind != JsonValueKind.True && field.ValueKind != JsonValueKind.False))
        {
            return false;
        }

        value = field.GetBoolean();
        return true;
    }

    private static bool TryReadString(
        JsonElement element,
        string property,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var field) || field.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var parsed = field.GetString();
        if (string.IsNullOrWhiteSpace(parsed) || parsed.Length > maximumLength)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private readonly record struct CandidateRelease(
        ProductSemanticVersion Version,
        DateTimeOffset? PublishedUtc);
}
