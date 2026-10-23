using System.Globalization;
using System.Numerics;
using Weir.Core.Json;
using Weir.Core.Time;

namespace Weir.Core.Updates;

/// <summary>One asset of a GitHub release; <paramref name="ApiUrl"/> is null for an asset taken from the release feed, which has no API address.</summary>
public sealed record GitHubReleaseAsset(string Name, string? ApiUrl, string BrowserDownloadUrl, long SizeBytes, string? ContentType);

/// <summary>A GitHub release.</summary>
public sealed record GitHubReleaseRecord(
    string TagName,
    string Version,
    string? ReleaseName,
    string? HtmlUrl,
    Timestamp? PublishedAt,
    bool Draft,
    bool Prerelease,
    IReadOnlyList<GitHubReleaseAsset> Assets)
{
    public GitHubReleaseAsset? AssetNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var wanted = name.Trim().ToLowerInvariant();
        return Assets.FirstOrDefault(asset => string.Equals(asset.Name.Trim().ToLowerInvariant(), wanted, StringComparison.Ordinal));
    }

    public GitHubReleaseAsset? WindowsInstallerAsset() =>
        AssetNamed(ReleaseCatalog.WindowsInstallerAssetName) ?? AssetNamed(ReleaseCatalog.LegacyWindowsInstallerAssetName);
}

/// <summary>GitHub is limiting requests from this network until <paramref name="ResetsAt"/>; <paramref name="LastKnown"/> is the newest release Weir had before that, if any.</summary>
public sealed record ReleaseRateLimit(DateTimeOffset ResetsAt, GitHubReleaseRecord? LastKnown);

/// <summary>
/// A failed GitHub release request; <see cref="ReleaseFetchException.StatusCode"/> is set when GitHub answered with an HTTP
/// error, and <see cref="ReleaseFetchException.RateLimit"/> when that error was GitHub limiting the network.
/// </summary>
public sealed class ReleaseFetchException : Exception
{
    public ReleaseFetchException()
    {
    }

    public ReleaseFetchException(string message)
        : base(message)
    {
    }

    public ReleaseFetchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ReleaseFetchException(int statusCode)
        : base($"GitHub answered HTTP {statusCode}.")
    {
        StatusCode = statusCode;
    }

    public ReleaseFetchException(int statusCode, ReleaseRateLimit rateLimit)
        : this(statusCode)
    {
        RateLimit = rateLimit;
    }

    public int? StatusCode { get; }

    public ReleaseRateLimit? RateLimit { get; }
}

/// <summary>Weir's GitHub releases: where they live, how versions compare, and reading the release API's payload.</summary>
public static class ReleaseCatalog
{
    /// <summary>
    /// The API's release list, asked only when <see cref="ReleasesFeedUrl"/> cannot be read, as every call counts against an
    /// unauthenticated allowance of 60 an hour for the whole network. The list, not <c>/releases/latest</c>: that endpoint
    /// never returns a pre-release, so it would find nothing while only pre-releases are published.
    /// </summary>
    public const string ReleasesUrl = ProjectLocation.ApiRepositoryUrl + "/releases?per_page=30";

    /// <summary>The public release feed, where the newest release is looked up first: reading it does not count against the API allowance.</summary>
    public const string ReleasesFeedUrl = ProjectLocation.RepositoryUrl + "/releases.atom";
    public const string WindowsInstallerAssetName = "Weir-win-Setup.exe";
    public const string LegacyWindowsInstallerAssetName = "WeirSetup.exe";

    public static string? NormalizeReleaseVersion(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var text = raw.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var stripped = text.StartsWith('v') ? text[1..] : text;
        return stripped.Length == 0 ? null : stripped;
    }

    /// <summary>Where a file attached to the release tagged <paramref name="tag"/> is downloaded from, which does not count against the API allowance either.</summary>
    public static string DownloadUrl(string tag, string assetName) =>
        $"{ProjectLocation.RepositoryUrl}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";

    public static string TagUrl(string tag) => $"{ProjectLocation.RepositoryUrl}/releases/tag/{Uri.EscapeDataString(tag)}";

    public static string TagForVersion(string version)
    {
        var normalized = NormalizeReleaseVersion(version) ?? throw new WireValueException("Release version is missing.");
        return "v" + normalized;
    }

    /// <summary>Reads the release list API's JSON into releases. Throws <see cref="WireValueException"/> for an unusable payload.</summary>
    public static IReadOnlyList<GitHubReleaseRecord> CoerceReleaseListPayload(WireValue payload)
    {
        if (payload is not WireArray list)
        {
            throw new WireValueException("Release API returned an unexpected response.");
        }

        return [.. list.Items.Select(CoerceReleasePayload)];
    }

    /// <summary>Reads the release API's JSON into a <see cref="GitHubReleaseRecord"/>. Throws <see cref="WireValueException"/> for an unusable payload.</summary>
    public static GitHubReleaseRecord CoerceReleasePayload(WireValue payload)
    {
        if (payload is not WireObject dict)
        {
            throw new WireValueException("Release API returned an unexpected response.");
        }

        var tagName = OrText(dict.Get("tag_name")).Trim();
        var version = NormalizeReleaseVersion(tagName) ?? throw new WireValueException("Release metadata is missing a valid tag name.");
        Timestamp? publishedAt = null;
        if (dict.Get("published_at") is WireString published && published.Value.Trim().Length > 0)
        {
            if (!Timestamp.TryFromIsoFormat(published.Value.Trim().Replace("Z", "+00:00", StringComparison.Ordinal), out var parsed))
            {
                throw new WireValueException($"Invalid isoformat string: {WireStrings.Repr(published.Value.Trim())}");
            }

            publishedAt = parsed;
        }

        var assets = new List<GitHubReleaseAsset>();
        if (dict.Get("assets") is WireArray list)
        {
            foreach (var item in list.Items.OfType<WireObject>())
            {
                var name = OrText(item.Get("name")).Trim();
                var apiUrl = OrText(item.Get("url")).Trim();
                if (name.Length == 0 || apiUrl.Length == 0)
                {
                    throw new WireValueException("Release asset metadata is incomplete.");
                }

                var contentType = OrText(item.Get("content_type")).Trim();
                var size = item.Get("size") is { } sizeValue && sizeValue.IsTruthy ? WireConvert.ToInt(sizeValue) : BigInteger.Zero;
                assets.Add(new GitHubReleaseAsset(
                    name, apiUrl, OrText(item.Get("browser_download_url")).Trim(),
                    size > long.MaxValue ? long.MaxValue : (long)size,
                    contentType.Length == 0 ? null : contentType));
            }
        }

        var releaseName = OrText(dict.Get("name")).Trim();
        var htmlUrl = OrText(dict.Get("html_url")).Trim();
        return new GitHubReleaseRecord(
            tagName, version, releaseName.Length == 0 ? null : releaseName, htmlUrl.Length == 0 ? null : htmlUrl,
            publishedAt, dict.Get("draft")?.IsTruthy ?? false, dict.Get("prerelease")?.IsTruthy ?? false, assets);
    }

    private static string OrText(WireValue? value) => value is null || !value.IsTruthy ? string.Empty : WireConvert.Str(value);
}

/// <summary>The update status and update-settings payloads, and the tray's settings and state files.</summary>
public static class UpdateStatus
{
    public const string RateLimitedStatus = "rate_limited";

    /// <summary>The status of a Windows install whose owner has update checking switched off: Weir asks GitHub nothing.</summary>
    public const string OffStatus = "off";
    public static readonly IReadOnlyList<string> Modes = ["Auto", "DownloadOnly", "NotifyOnly"];

    public static WireObject Unavailable(string currentVersion, string installType, string status, string summary) =>
        Base(currentVersion, installType, status, summary)
            .Set("latest_version", (string?)null)
            .Set("latest_name", (string?)null)
            .Set("published_at", (string?)null)
            .Set("release_url", (string?)null)
            .Set("windows_installer_url", (string?)null)
            .Set("docker_image", installType == "docker" ? ProjectLocation.DockerImage : null)
            .Set("docker_tag", (string?)null)
            .Set("docker_update_command", (string?)null)
            .Set("in_app_upgrade_supported", installType == "windows")
            .Set("in_app_upgrade_summary", (string?)null);

    private static bool IsNewer(string currentVersion, string releaseVersion) =>
        SemanticVersion.TryParse(currentVersion, out var current)
        && SemanticVersion.TryParse(releaseVersion, out var latest)
        && latest > current;

    /// <summary>The update status once a release was fetched.</summary>
    public static WireObject FromRelease(string currentVersion, string installType, GitHubReleaseRecord release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var updateAvailable = IsNewer(currentVersion, release.Version);
        string status;
        string summary;
        if (release.Version.Length == 0)
        {
            status = "unavailable";
            summary = "Could not read the latest release version.";
        }
        else if (updateAvailable)
        {
            status = "update_available";
            summary = $"Weir {release.Version} is available.";
        }
        else
        {
            status = "up_to_date";
            summary = $"This install is already on Weir {currentVersion}.";
        }

        var installer = release.WindowsInstallerAsset();
        var dockerTag = release.Version.Length == 0 ? null : release.Version;
        return Base(currentVersion, installType, status, summary)
            .Set("latest_version", release.Version)
            .Set("latest_name", release.ReleaseName ?? release.Version)
            .Set("published_at", release.PublishedAt?.ToWireText())
            .Set("release_url", release.HtmlUrl)
            .Set("windows_installer_url", installer?.BrowserDownloadUrl)
            .Set("docker_image", installType == "docker" ? ProjectLocation.DockerImage : null)
            .Set("docker_tag", dockerTag)
            .Set("docker_update_command", installType == "docker" && dockerTag is not null ? "docker compose pull && docker compose up -d" : null)
            .Set("in_app_upgrade_supported", installType == "windows")
            .Set("in_app_upgrade_summary", installType == "windows" ? "Updates are managed by the Weir desktop app via Velopack." : null);
    }

    /// <summary>
    /// GitHub is limiting update checks from this network until <paramref name="retryAt"/>. The newest release Weir knew
    /// before that stays in the answer, and <paramref name="summary"/> says when the next check is. <c>known_update_available</c>
    /// says whether that release is newer than this install, so an update Weir already knew of stays on offer.
    /// </summary>
    public static WireObject RateLimited(string currentVersion, string installType, GitHubReleaseRecord? lastKnown, DateTimeOffset retryAt, string summary)
    {
        var known = lastKnown is null ? Unavailable(currentVersion, installType, RateLimitedStatus, summary) : FromRelease(currentVersion, installType, lastKnown);
        var updateKnown = lastKnown is not null && IsNewer(currentVersion, lastKnown.Version);
        return known
            .Set("status", RateLimitedStatus)
            .Set("summary", summary)
            .Set("known_update_available", updateKnown)
            .Set("retry_at", Timestamp.FromDateTimeOffset(retryAt.ToUniversalTime()).ToWireText());
    }

    /// <summary>The sentence for a limit that lifts at <paramref name="retryAt"/>, with the time on the clock of <paramref name="timezoneName"/>.</summary>
    public static string RateLimitedSummary(DateTimeOffset retryAt, string? timezoneName) =>
        $"GitHub is limiting update checks from your network right now. Weir will check again at {ClockText(retryAt, timezoneName)}.";

    /// <summary>A clock time as the web writes it, "11:35 pm".</summary>
    public static string ClockText(DateTimeOffset at, string? timezoneName) =>
        TimeZones.ToLocal(at, timezoneName).ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();

    /// <summary>The status when update checking is off: nothing was asked of GitHub, and the summary says how to look once or turn it on.</summary>
    public static WireObject Off(string currentVersion, string installType) =>
        Unavailable(currentVersion, installType, OffStatus, "Update checking is off, so Weir is not looking for new versions. Press Check now to look once, or turn it on below.");

    public static WireObject UpdateSettingsOut(string mode, bool checkOnStartup, long checkIntervalMinutes, bool checkForUpdates) => new WireObject()
        .Set("mode", mode)
        .Set("check_on_startup", checkOnStartup)
        .Set("check_interval_minutes", checkIntervalMinutes)
        .Set("check_for_updates", checkForUpdates);

    /// <summary>A new install: nobody has chosen yet, and checking is off until they do.</summary>
    public static readonly WireObject DefaultUpdateSettings = UpdateSettingsOut("Auto", true, 60, checkForUpdates: false);

    /// <summary>A file that cannot be read: what was chosen is not known, so nothing installs and nothing is asked of GitHub.</summary>
    public static readonly WireObject UnreadableUpdateSettings = UpdateSettingsOut("NotifyOnly", true, 60, checkForUpdates: false);

    /// <summary>
    /// Reads <c>update-settings.json</c>'s text; <see langword="null"/> when it cannot be read and the
    /// notify-only fallback applies. Throws <see cref="WireTypeException"/> for valid JSON that is not an
    /// object.
    /// </summary>
    public static WireObject? ParseUpdateSettings(string text)
    {
        WireValue raw;
        try
        {
            raw = WireJsonParser.Parse(text);
        }
        catch (WireJsonDecodeException)
        {
            return null;
        }

        if (raw is not WireObject dict)
        {
            throw new WireTypeException("update-settings.json must hold a JSON object.");
        }

        var mode = WireConvert.Str(dict.Get("mode") ?? new WireString(string.Empty)).Trim();
        if (!Modes.Contains(mode, StringComparer.Ordinal))
        {
            return null;
        }

        var checkOnStartup = (dict.Get("checkOnStartup") ?? WireBool.True).IsTruthy;

        // A file saved before this choice existed was saved by an install that was checking, so it keeps checking.
        var checkForUpdates = (dict.Get("checkForUpdates") ?? WireBool.True).IsTruthy;
        BigInteger interval;
        try
        {
            interval = WireConvert.ToInt(dict.Get("checkIntervalMinutes") ?? new WireInteger(60));
        }
        catch (Exception exception) when (exception is WireValueException or WireTypeException)
        {
            return null;
        }

        return interval < 1 || interval > 10080 ? null : UpdateSettingsOut(mode, checkOnStartup, (long)interval, checkForUpdates);
    }

    /// <summary>The <c>update-settings.json</c> the tray reads, as JSON indented by two spaces.</summary>
    public static string SerializeUpdateSettings(string mode, bool checkOnStartup, long checkIntervalMinutes, bool checkForUpdates) =>
        WireJsonWriter.Dumps(
            new WireObject()
                .Set("mode", mode)
                .Set("checkOnStartup", checkOnStartup)
                .Set("checkIntervalMinutes", checkIntervalMinutes)
                .Set("checkForUpdates", checkForUpdates),
            WireJsonFormat.Indented);

    /// <summary>
    /// The <c>work-state.json</c> the tray reads to learn whether Weir is idle: whether file work is running or could
    /// start now, and the UTC moment that was last checked.
    /// </summary>
    public static string SerializeWorkState(bool busy, DateTimeOffset checkedAt) =>
        WireJsonWriter.Dumps(
            new WireObject().Set("busy", busy).Set("checkedAt", checkedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            WireJsonFormat.Compact);

    /// <summary>
    /// What the tray is doing about updates: <c>idle</c> (an update it found and did not download is named in
    /// <c>pending_version</c>), <c>checking</c>, <c>downloading</c>, <c>downloaded</c> (waiting to be applied) or <c>failed</c>.
    /// </summary>
    public const string StateIdle = "idle";
    public const string StateChecking = "checking";
    public const string StateDownloading = "downloading";
    public const string StateDownloaded = "downloaded";
    public const string StateFailed = "failed";

    private static readonly IReadOnlyList<string> ActiveOrFailedStates = [StateChecking, StateDownloading, StateFailed];

    /// <summary>
    /// Reads the tray's update state file: where the tray is with an update, the version it found or downloaded and, after a
    /// failure, why. A file that cannot be read says nothing is happening. <c>downloaded</c> settles whether the update is
    /// downloaded; the named state can only add what the tray is doing otherwise.
    /// </summary>
    public static WireObject ParseUpdateState(string? text)
    {
        var fallback = StateOut(false, null, StateIdle, null);
        if (text is null)
        {
            return fallback;
        }

        try
        {
            if (WireJsonParser.Parse(text) is not WireObject dict)
            {
                return fallback;
            }

            var version = dict.Get("version");
            string? pending = null;
            if (version is not null && version.IsTruthy)
            {
                if (version is not WireString s)
                {
                    return fallback;
                }

                pending = s.Value;
            }

            var downloaded = (dict.Get("downloaded") ?? WireBool.False).IsTruthy;
            var state = dict.Get("state") is WireString named && ActiveOrFailedStates.Contains(named.Value, StringComparer.Ordinal) ? named.Value : StateIdle;
            var failure = state == StateFailed && dict.Get("failure") is WireString reason && reason.Value.Length > 0 ? reason.Value : null;
            return StateOut(downloaded, pending, downloaded ? StateDownloaded : state, failure, LastCheckedAt(dict));
        }
        catch (WireJsonDecodeException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// <paramref name="state"/> as read, but saying the tray is at <paramref name="step"/>: <see cref="StateChecking"/> or
    /// <see cref="StateDownloading"/> when a person has asked for it and the tray has not yet taken it up, or
    /// <see cref="StateIdle"/> when the tray that said it was at one has gone.
    /// </summary>
    public static WireObject WithStep(WireObject state, string step)
    {
        ArgumentNullException.ThrowIfNull(state);
        return StateOut(false, state.Get("pending_version") is WireString version && version.Value.Length > 0 ? version.Value : null, step, null, (state.Get("last_checked_at") as WireString)?.Value);
    }

    // When the tray last looked, in the form the rest of the API uses; left out when the tray has not looked or wrote a time that cannot be read.
    private static string? LastCheckedAt(WireObject dict) =>
        dict.Get("checkedAt") is WireString { Value: var text }
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? Timestamp.FromDateTimeOffset(at.ToUniversalTime()).ToWireText()
            : null;

    private static WireObject StateOut(bool downloaded, string? pendingVersion, string state, string? failure, string? lastCheckedAt = null)
    {
        var shown = new WireObject()
            .Set("downloaded", downloaded)
            .Set("pending_version", pendingVersion)
            .Set("state", state)
            .Set("failure", failure);
        return lastCheckedAt is null ? shown : shown.Set("last_checked_at", lastCheckedAt);
    }

    private static WireObject Base(string currentVersion, string installType, string status, string summary) => new WireObject()
        .Set("current_version", currentVersion)
        .Set("install_type", installType)
        .Set("status", status)
        .Set("summary", summary);
}
