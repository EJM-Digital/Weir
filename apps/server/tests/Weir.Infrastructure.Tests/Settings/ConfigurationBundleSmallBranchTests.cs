using Weir.Core.Json;
using Weir.Core.Security;
using Weir.Infrastructure.Settings;

namespace Weir.Infrastructure.Tests.Settings;

/// <summary>What a backup file holds that is missing, empty or of the wrong kind, read as plainly as the pieces allow.</summary>
public sealed class ConfigurationBundleSmallBranchTests
{
    private const string Password = "a backup password";

    private static WireObject Row(int id) => new WireObject().Set("id", id);

    private static WireObject Secrets(params WireValue[] entries) =>
        new WireObject().Set("media_managers", new WireArray(entries));

    [Fact]
    public void A_connections_secrets_are_the_sealed_entry_with_its_id_and_an_entry_with_no_usable_id_is_passed_over()
    {
        var entry = new WireObject().Set("id", 7).Set("api_key", "secret");
        var secrets = Secrets(new WireObject().Set("id", "seven"), new WireObject().Set("id", 3), new WireArray([]), entry);

        Assert.Same(entry, ConfigurationBundleConnections.SecretsFor(secrets, Row(7)));
    }

    [Fact]
    public void A_connection_the_sealed_secrets_do_not_know_has_none()
    {
        var other = Secrets(new WireObject().Set("id", 3));

        Assert.Empty(ConfigurationBundleConnections.SecretsFor(other, Row(7)).Items);
        Assert.Empty(ConfigurationBundleConnections.SecretsFor(null, Row(7)).Items);
        Assert.Empty(ConfigurationBundleConnections.SecretsFor(new WireObject(), Row(7)).Items);
        Assert.Empty(ConfigurationBundleConnections.SecretsFor(other, new WireObject().Set("id", "seven")).Items);
        Assert.Empty(ConfigurationBundleConnections.SecretsFor(other, new WireObject()).Items);
    }

    [Theory]
    [InlineData("radarr", "http://a.example", "Movies", true)]
    [InlineData("radarr", "HTTP://A.EXAMPLE", "other name", true)]
    [InlineData("radarr", "http://b.example", "Movies", false)]
    [InlineData("sonarr", "http://a.example", "Movies", false)]
    public void A_bundle_row_with_an_address_is_the_same_connection_when_the_kind_and_the_address_agree(string kind, string baseUrl, string name, bool same)
    {
        var known = new ConfigurationBundleConnections.KnownConnection(1, "radarr", "http://a.example", "Movies");

        Assert.Equal(same, known.IsSameAs(kind, baseUrl, name));
    }

    [Theory]
    [InlineData("", "Movies", "movies", true)]
    [InlineData("", "Movies", "Shows", false)]
    [InlineData("http://a.example", "Movies", "Movies", false)]
    public void A_bundle_row_with_no_address_is_the_same_connection_only_when_neither_has_one_and_the_names_agree(string knownUrl, string knownName, string savedName, bool same)
    {
        var known = new ConfigurationBundleConnections.KnownConnection(1, "radarr", knownUrl, knownName);

        Assert.Equal(same, known.IsSameAs("radarr", string.Empty, savedName));
    }

    [Fact]
    public void A_workflow_row_that_is_missing_its_text_is_read_with_the_values_a_new_workflow_starts_with()
    {
        var empty = ConfigurationBundleStore.RestoreLibraryInput(new WireObject());

        Assert.Equal((string.Empty, string.Empty, string.Empty, string.Empty, string.Empty), (empty.Name, empty.MediaType, empty.WatchedFolder, empty.WorkFolder, empty.OutputFolder));

        var full = ConfigurationBundleStore.RestoreLibraryInput(new WireObject()
            .Set("name", "Movies").Set("media_type", "movie").Set("watched_folder", "/in").Set("work_folder", "/work").Set("output_folder", "/out"));

        Assert.Equal(("Movies", "movie", "/in", "/work", "/out"), (full.Name, full.MediaType, full.WatchedFolder, full.WorkFolder, full.OutputFolder));
    }

    [Fact]
    public void Locked_secrets_that_open_to_something_other_than_an_object_are_refused_in_plain_words()
    {
        var salt = BackupSecretsSeal.NewSalt();
        var sealedAway = BackupSecretsSeal.Seal("[1, 2]", BackupSecretsSeal.DeriveKey(Password, salt), salt);
        var bundle = new WireObject().Set(BackupSecretsSeal.SectionName, sealedAway);

        var refused = Assert.Throws<WireValueException>(() => ConfigurationBundleStore.OpenSecrets(bundle, Password));

        Assert.Contains("not in a form this version of Weir can read", refused.Message, StringComparison.Ordinal);
    }
}
