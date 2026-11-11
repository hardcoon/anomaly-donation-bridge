using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace AnomalyDonationBridge.App.Services;

public sealed record GitHubReleaseUpdateResult(
    string CurrentVersion,
    string LatestVersion,
    Uri ReleaseUri,
    bool IsUpdateAvailable);

public sealed class GitHubReleaseUpdateChecker
{
    private const string ReleasesEndpoint =
        "https://api.github.com/repos/hardcoon/anomaly-donation-bridge/releases?per_page=30";
    private const string ReleasePagePrefix =
        "https://github.com/hardcoon/anomaly-donation-bridge/releases/";

    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;

    public GitHubReleaseUpdateChecker() : this(SharedClient)
    {
    }

    internal GitHubReleaseUpdateChecker(HttpClient client) => _client = client;

    public static string CurrentApplicationVersion
    {
        get
        {
            var informational = typeof(GitHubReleaseUpdateChecker).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(informational)
                ? typeof(GitHubReleaseUpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "неизвестна"
                : informational.Split('+', 2)[0];
        }
    }

    public async Task<GitHubReleaseUpdateResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesEndpoint);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParsePublishedReleases(json, CurrentApplicationVersion);
    }

    internal static GitHubReleaseUpdateResult ParsePublishedReleases(string json, string currentVersion)
    {
        if (!ReleaseVersion.TryParse(currentVersion, out var current))
            throw new InvalidDataException($"Не удалось определить текущую версию программы: {currentVersion}");

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub вернул неожиданный ответ со списком релизов.");

        ReleaseVersion? latest = null;
        string? latestLabel = null;
        Uri? latestUri = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (!release.TryGetProperty("tag_name", out var tagElement) ||
                !release.TryGetProperty("html_url", out var urlElement)) continue;

            var tag = tagElement.GetString();
            var url = urlElement.GetString();
            if (!ReleaseVersion.TryParse(tag, out var candidate) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var candidateUri) ||
                !candidateUri.AbsoluteUri.StartsWith(ReleasePagePrefix, StringComparison.OrdinalIgnoreCase)) continue;

            if (latest is not null && candidate.CompareTo(latest.Value) <= 0) continue;
            latest = candidate;
            latestLabel = tag!.TrimStart('v', 'V');
            latestUri = candidateUri;
        }

        if (latest is null || latestLabel is null || latestUri is null)
            throw new InvalidDataException("В репозитории не найдено ни одного опубликованного релиза с номером версии.");

        return new GitHubReleaseUpdateResult(
            currentVersion,
            latestLabel,
            latestUri,
            latest.Value.CompareTo(current) > 0);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AnomalyDonationBridge", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private readonly record struct ReleaseVersion(Version Core, string? Prerelease) : IComparable<ReleaseVersion>
    {
        public static bool TryParse(string? value, out ReleaseVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var normalized = value.Trim().TrimStart('v', 'V').Split('+', 2)[0];
            var parts = normalized.Split('-', 2);
            if (!Version.TryParse(parts[0], out var core)) return false;
            version = new ReleaseVersion(core, parts.Length == 2 ? parts[1] : null);
            return true;
        }

        public int CompareTo(ReleaseVersion other)
        {
            var coreComparison = Core.CompareTo(other.Core);
            if (coreComparison != 0) return coreComparison;
            if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
            if (other.Prerelease is null) return -1;

            var left = Prerelease.Split('.', '-');
            var right = other.Prerelease.Split('.', '-');
            for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
            {
                if (index >= left.Length) return -1;
                if (index >= right.Length) return 1;
                var leftNumeric = int.TryParse(left[index], out var leftNumber);
                var rightNumeric = int.TryParse(right[index], out var rightNumber);
                var comparison = leftNumeric && rightNumeric
                    ? leftNumber.CompareTo(rightNumber)
                    : leftNumeric != rightNumeric
                        ? (leftNumeric ? -1 : 1)
                        : string.Compare(left[index], right[index], StringComparison.OrdinalIgnoreCase);
                if (comparison != 0) return comparison;
            }

            return 0;
        }
    }
}
