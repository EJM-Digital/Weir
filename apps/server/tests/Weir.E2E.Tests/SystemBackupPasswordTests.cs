using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>
/// System › Backups › the backup password. Setting it asks for the password twice and locks the secrets in the backups made from then on;
/// restoring such a backup asks for the password, stays open on a wrong one and changes nothing until the right one is typed.
/// </summary>
public sealed class SystemBackupPasswordTests(E2EServer server) : E2ETestBase(server)
{
    private const string Settings = "/api/v1/suite/settings";
    private const string Backups = "/api/v1/suite/configuration-backups";
    private const string Bundle = "/api/v1/suite/configuration-bundle";
    private const string PasswordRoute = "/api/v1/suite/backup-password";
    private const string BackupPassword = "an e2e backup password";

    private static ILocator Dialog(IPage page) => page.GetByTestId("restore-configuration-dialog");

    private static ILocator Restore(IPage page) => page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Restore the backup taken") });

    private static async Task SaveLogRetentionAsync(SystemSession system, int logDays)
    {
        var saved = await system.Admin.PutWithCsrfAsync(Settings, new JsonObject { ["app_timezone"] = "UTC", ["log_retention_days"] = logDays });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.ToString());
    }

    [E2EFact]
    public async Task Setting_the_backup_password_locks_the_next_backup_and_restoring_it_needs_that_password()
    {
        await using var system = await SystemSession.OpenAsync(await NewPageAsync(), "Backups");
        var (page, requests) = (system.Page, system.Requests);
        await Expect(page.GetByTestId("backup-password-words")).ToContainTextAsync("No backup password is set, so backups leave out");
        Assert.False((bool)(await system.ReadAsync(Backups))["password_set"]!);
        await SaveLogRetentionAsync(system, logDays: 45);

        // Two different passwords send nothing; the same one twice sets it.
        var mark = requests.Mark();
        await page.Locator("#backup-password").FillAsync(BackupPassword);
        await page.Locator("#backup-password-confirm").FillAsync("something else entirely");
        await page.GetByRole(AriaRole.Button, new() { Name = "Set backup password", Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("backup-password-error")).ToContainTextAsync("The two backup passwords do not match.");
        requests.AssertNothingSentSince(mark);

        await page.Locator("#backup-password-confirm").FillAsync(BackupPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Set backup password", Exact = true }).ClickAsync();

        await Expect(page.GetByText("Backup password saved. Backups from now on use it.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByTestId("backup-password-words")).ToContainTextAsync("Restoring one asks for it.");
        var put = Assert.Single(requests.Since(mark), request => request.Method == "PUT");
        Assert.Equal(PasswordRoute, put.PathAndQuery);
        Assert.Equal(["confirm_password", "csrf_token", "password"], JsonNode.Parse(put.Body!)!.AsObject().Select(field => field.Key).Order(StringComparer.Ordinal));
        Assert.True((bool)(await system.ReadAsync(Backups))["password_set"]!);

        // The backup made now holds its secrets locked, and the password is nowhere in the file.
        await page.GetByRole(AriaRole.Button, new() { Name = "Back up now", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Backup created.", new() { Exact = true })).ToBeVisibleAsync();
        var listed = await system.ReadAsync(Backups);
        var item = Assert.Single(listed["items"]!.AsArray())!.AsObject();
        Assert.True((bool)item["secrets_sealed"]!);
        var file = await File.ReadAllTextAsync(Path.Join((string)listed["directory"]!, (string)item["file_name"]!));
        Assert.Contains("\"sealed_secrets\"", file, StringComparison.Ordinal);
        Assert.DoesNotContain(BackupPassword, file, StringComparison.Ordinal);

        // Restoring it asks for the password; a wrong one is refused in the dialog, which stays open, and changes nothing.
        await SaveLogRetentionAsync(system, logDays: 60);
        await page.ReloadAsync();
        await Restore(page).ClickAsync();
        await Expect(Dialog(page)).ToContainTextAsync("locked with a backup password. Enter the password it was made with.");
        var replace = Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Replace settings", Exact = true });
        await Expect(replace).ToBeDisabledAsync();
        await Dialog(page).GetByLabel("Backup password").FillAsync("not the backup password");
        await replace.ClickAsync();
        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("That is not the backup password this backup was made with. Nothing was changed.");
        await Expect(Dialog(page)).ToBeVisibleAsync();
        Assert.Equal(60, (int)(await system.ReadAsync(Settings))["log_retention_days"]!);

        var replaceMark = requests.Mark();
        await Dialog(page).GetByLabel("Backup password").FillAsync(BackupPassword);
        await replace.ClickAsync();

        await Expect(page.GetByText("Configuration restored.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Dialog(page)).ToHaveCountAsync(0);
        var sent = Assert.Single(requests.Since(replaceMark), request => request.Method == "PUT");
        Assert.Equal(Bundle, sent.PathAndQuery);
        Assert.Equal(["bundle", "csrf_token", "password"], JsonNode.Parse(sent.Body!)!.AsObject().Select(field => field.Key).Order(StringComparer.Ordinal));
        Assert.Equal(45, (int)(await system.ReadAsync(Settings))["log_retention_days"]!);
    }
}
