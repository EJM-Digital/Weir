using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Weir.LiveAudit;

// The public HTTP contract, bootstrap and sign-in, and the authenticated read-only API surface.
internal sealed partial class LiveAudit
{
    private static readonly string[] ReadSurfacePaths =
    [
        "/api/v1/activity/recent?limit=5",
        "/api/v1/auth/bootstrap/status",
        "/api/v1/auth/csrf",
        "/api/v1/auth/me",
        "/api/v1/auth/session",
        "/api/v1/auth/sessions",
        "/api/v1/media-managers/capabilities",
        "/api/v1/media-managers/connections",
        "/api/v1/media-managers/connections/999999",
        "/api/v1/pause",
        "/api/v1/processing/files?limit=5",
        "/api/v1/processing/files/999999/log",
        "/api/v1/processing/files/999999/log/download",
        "/api/v1/processing/files/999999/why-held",
        "/api/v1/processing/hardware",
        "/api/v1/processing/jobs/inspection?limit=5",
        "/api/v1/processing/libraries",
        "/api/v1/processing/libraries/999999",
        "/api/v1/processing/libraries/discover/999999",
        "/api/v1/processing/libraries/discover/999999/drift",
        "/api/v1/processing/maintenance",
        "/api/v1/processing/metadata-provider",
        "/api/v1/processing/operator-settings",
        "/api/v1/processing/overview-stats",
        "/api/v1/processing/rule-sets",
        "/api/v1/processing/runtime-settings",
        "/api/v1/suite/configuration-backups",
        "/api/v1/suite/configuration-backups/999999/download",
        "/api/v1/suite/configuration-bundle",
        "/api/v1/suite/logs?limit=5",
        "/api/v1/suite/metrics",
        "/api/v1/suite/notification-channels",
        "/api/v1/suite/security-overview",
        "/api/v1/suite/settings",
        "/api/v1/suite/update-settings",
        "/api/v1/suite/update-state",
        "/api/v1/suite/update-status",
        "/api/v1/system/directories",
        "/api/v1/system/media-tools",
        "/api/v1/system/readiness",
        "/api/v1/system/reconciliation",
    ];

    private async Task PublicContractAsync()
    {
        var health = await GetAsync("/health");
        AssertCommonHeaders(health, "/health");
        var healthBody = await JsonBodyAsync(health);
        Require(healthBody["status"]?.GetValue<string>() == "ok", "health status is not ok");
        Record("public health endpoint and headers");

        var readiness = await GetAsync("/ready");
        AssertCommonHeaders(readiness, "/ready");
        var readinessBody = await JsonBodyAsync(readiness);
        Require(readinessBody["ready"]?.GetValue<bool>() == true, "readiness is not true");
        Require(readinessBody["status"]?.GetValue<string>() == "ready", "readiness status is wrong");
        Require(
            readinessBody.Select(pair => pair.Key).All(key => key is "ready" or "status"),
            "public readiness leaks detailed dependency state");
        Record("public readiness endpoint is minimal and truthful");

        var openapi = await GetAsync("/openapi.json");
        Require(!LowerHeaders(openapi).ContainsKey("server"), "/openapi.json exposes a Server header");
        var openapiBody = await JsonBodyAsync(openapi);
        Require(openapiBody.ContainsKey("openapi"), "OpenAPI document is missing its version");
        Require(
            (openapiBody["info"]?["version"]?.ToString() ?? "").Trim() == config.ExpectedVersion,
            $"packaged server does not report version {config.ExpectedVersion}");
        Record("OpenAPI document and packaged version");

        var index = await GetAsync("/");
        var indexHeaders = LowerHeaders(index);
        Require(!indexHeaders.ContainsKey("server"), "index exposes a Server header");
        Require(
            indexHeaders.GetValueOrDefault("cache-control", "").Contains("no-store", StringComparison.OrdinalIgnoreCase),
            "index is not marked no-store");
        var html = await index.TextAsync();
        var assets = AssetReference().Matches(html);
        Require(assets.Count > 0, "index did not reference a built asset");
        var asset = await GetAsync(
            assets[0].Groups[1].Value,
            new Dictionary<string, string> { ["Accept-Encoding"] = "gzip, br" });
        var assetHeaders = LowerHeaders(asset);
        Require(!assetHeaders.ContainsKey("server"), "static asset exposes a Server header");
        Require(
            assetHeaders.GetValueOrDefault("cache-control", "").Contains("immutable", StringComparison.OrdinalIgnoreCase),
            "hashed static asset is not immutable-cacheable");
        if (assetHeaders.GetValueOrDefault("content-encoding", "").Length > 0)
        {
            Require(
                assetHeaders.GetValueOrDefault("vary", "").Contains("accept-encoding", StringComparison.OrdinalIgnoreCase),
                "compressed static asset is missing Vary: Accept-Encoding");
        }

        Record("static asset compression/cache contract");
    }

    private async Task BootstrapAndSignInAsync()
    {
        await page.GotoAsync(config.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await SettleAsync();

        var wizardSkip = page.GetByTestId("setup-wizard-skip");
        var shellReady = page.GetByTestId("shell-ready");
        var wizardOrShell = wizardSkip.Or(shellReady).First;

        var setupUser = page.GetByTestId("setup-username");
        if (await setupUser.CountAsync() > 0)
        {
            await VisibleAsync(setupUser, "first-time setup form is visible");
            await setupUser.FillAsync(config.AuditUser);
            await page.GetByTestId("setup-password").FillAsync(config.AuditPassword);
            await page.GetByTestId("setup-confirm-password").FillAsync(config.AuditPassword);
            var setupCode = page.GetByTestId("setup-code");
            if (await setupCode.CountAsync() > 0)
            {
                await VisibleAsync(setupCode, "setup code field is visible for this non-loopback peer");
                Require(
                    config.DockerContainer.Length > 0,
                    "the target asked for a setup code but WEIR_LIVE_E2E_DOCKER_CONTAINER is not set");
                await setupCode.FillAsync(await DockerSetupCode.ReadAsync(config));
            }

            await ClickAsync(page.GetByTestId("setup-submit"), "first-time setup submit");
            // Setup waits on the recovery codes until they are marked as saved.
            await VisibleAsync(page.GetByTestId("setup-codes"), "recovery codes shown after bootstrap");
            await page.GetByTestId("recovery-codes-saved").CheckAsync();
            await ClickAsync(page.GetByTestId("recovery-codes-continue"), "recovery codes saved");
            // Bootstrap signs the new admin in directly (#704): the browser briefly lands on "/" before
            // RequireSetupWizard's own client-side redirect to the wizard settles, so a visible element - the
            // wizard or the already-signed-in shell, whichever this install ends up on - is what to wait for, not
            // a URL string that can be read mid-redirect.
            await VisibleAsync(wizardOrShell, "setup wizard or the signed-in shell after bootstrap");
        }

        var loginUser = page.GetByTestId("login-username");
        if (await loginUser.CountAsync() > 0)
        {
            await VisibleAsync(loginUser, "login form is visible");
            await loginUser.FillAsync(config.AuditUser);
            await page.GetByTestId("login-password").FillAsync(config.AuditPassword);
            await ClickAsync(page.GetByTestId("login-submit"), "login submit");
            await VisibleAsync(wizardOrShell, "setup wizard or the signed-in shell after login");
        }

        if (await wizardSkip.CountAsync() > 0 && await wizardSkip.IsVisibleAsync())
        {
            await VisibleAsync(
                page.GetByText("How do your downloads reach Weir?"),
                "setup wizard asks how downloads reach Weir first");
            Require(
                await page.Locator("input[name='setup-wizard-source']").CountAsync() == 4,
                "setup wizard does not offer Deluno, Sonarr / Radarr, a download client and Neither");
            await VisibleAsync(
                page.GetByText("Skip sign-in for devices on your home network?"),
                "setup wizard asks whether devices on the home network skip sign-in");
            Require(
                await page.Locator("input[name='setup-wizard-home-network']").CountAsync() == 2
                && await page.Locator("input[name='setup-wizard-home-network']:checked").CountAsync() == 0,
                "setup wizard does not offer Yes and No for skipping sign-in, unanswered");
            await ClickAsync(
                page.GetByRole(AriaRole.Radio, new PageGetByRoleOptions { Name = "Neither" }),
                "choose to pick the folders yourself in the setup wizard");
            await VisibleAsync(
                page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Movies watched folder" }),
                "setup wizard shows the typed-folder form for Neither");
            await ClickAsync(wizardSkip, "skip setup wizard after exercising its entry path");
        }

        await VisibleAsync(shellReady, "authenticated application shell");
        Record("bootstrap, login, and setup-wizard state");
    }

    /// <summary>Touches every non-streaming documented GET route without mutating data.</summary>
    private async Task AuthenticatedReadSurfaceAsync()
    {
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "application/json",
            ["X-Requested-With"] = "XMLHttpRequest",
        };
        foreach (var path in ReadSurfacePaths)
        {
            // Run protected reads in the signed-in page itself. Playwright's separate APIRequestContext does not
            // reliably inherit the browser session after the first-user bootstrap redirect on packaged Windows
            // builds, which made this audit report a false 401 even while the authenticated shell was visibly
            // loaded.
            var expectedNotFound = path.Contains("999999", StringComparison.Ordinal)
                || path.Contains("00000000-0000-4000-8000-000000000000", StringComparison.Ordinal);
            if (expectedNotFound)
            {
                diagnostics.BeginExpectedNotFound(new Uri(new Uri(config.BaseUrl + "/"), path.TrimStart('/')).AbsoluteUri);
            }

            int status;
            try
            {
                status = await page.EvaluateAsync<int>(
                    """
                    async ({ path, headers }) => {
                      const response = await fetch(path, {
                        method: "GET",
                        credentials: "same-origin",
                        headers,
                      });
                      await response.arrayBuffer();
                      return response.status;
                    }
                    """,
                    new Dictionary<string, object?> { ["path"] = path, ["headers"] = headers });
            }
            finally
            {
                diagnostics.EndExpectedNotFound();
            }

            Require(status is 200 or 204 or 404, $"GET {path} returned unexpected HTTP {status}");
            httpChecks.Add($"GET {path} -> {status}");
        }

        // The SSE route is intentionally excluded from the finite request loop; the Activity browser step opens
        // and closes it as a real client.
        Record($"authenticated read-only API surface ({ReadSurfacePaths.Length} routes; Activity SSE exercised in browser)");
    }

    private static async Task<JsonObject> JsonBodyAsync(IAPIResponse response)
    {
        var element = await response.JsonAsync() ?? throw new AuditFailure("response had no JSON body");
        return JsonNode.Parse(element.GetRawText())?.AsObject()
            ?? throw new AuditFailure("response body was not a JSON object");
    }

    [GeneratedRegex("""(?:src|href)=["']([^"']*assets/[^"']+)["']""")]
    private static partial Regex AssetReference();
}
