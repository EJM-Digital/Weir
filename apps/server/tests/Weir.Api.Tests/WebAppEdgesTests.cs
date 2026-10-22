using System.Net;
using Weir.Api.Web;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests;

/// <summary>The built web app at its edges: which requests for a file the compressed-file layer leaves alone, where the app's front page is looked for, and the metrics bearer token in every shape a caller can send it.</summary>
public sealed class WebAppEdgesTests
{
    private static readonly (string, string)[] WithWebDist = [("WEIR_WEB_DIST", "{home}/web")];

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task Only_a_read_of_an_asset_is_answered_from_the_built_app(string method)
    {
        await using var server = await WeirTestServer.StartAsync(WithWebDist, prepareHome: WeirTestServer.WriteWebDist);

        using var response = await server.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/assets/app-abc123.js"));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_request_for_part_of_an_asset_gets_that_part_of_the_file_itself()
    {
        await using var server = await WeirTestServer.StartAsync(WithWebDist, prepareHome: WeirTestServer.WriteWebDist);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/assets/app-abc123.js");
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-3");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");

        using var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public void The_front_page_is_only_found_when_the_app_folder_and_its_index_exist()
    {
        var folder = Directory.CreateTempSubdirectory("weir-web-dist-").FullName;
        try
        {
            Assert.Null(new WebDist(null).IndexFile);
            Assert.Null(new WebDist(Path.Join(folder, "missing")).IndexFile);
            Assert.Null(new WebDist(folder).IndexFile);
            File.WriteAllText(Path.Join(folder, "index.html"), "<!doctype html>");
            Assert.Equal(Path.Join(folder, "index.html"), new WebDist(folder).IndexFile);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("gzip;q", "gzip", false)]
    [InlineData("gzip;level=9", "gzip", true)]
    public void Accept_encoding_options_without_a_weight_or_with_other_names(string header, string encoding, bool expected) =>
        Assert.Equal(expected, CompressedStaticAssetsMiddleware.AcceptsEncoding(header, encoding));

    [Theory]
    [InlineData("text/plain", "text/plain; charset=utf-8")]
    [InlineData("text/plain; charset=utf-16", "text/plain; charset=utf-16")]
    [InlineData("image/png", "image/png")]
    public void Text_types_are_given_a_charset_unless_they_have_one(string contentType, string expected) =>
        Assert.Equal(expected, WebContentTypes.WithCharset(contentType));

    [Theory]
    [InlineData("Bearer")]
    [InlineData("Bearer   ")]
    [InlineData("Basic s3cret-metrics-token")]
    [InlineData("Bearer not-the-token")]
    public async Task A_metrics_request_whose_authorization_is_not_the_token_falls_back_to_a_signed_in_operator(string authorization)
    {
        await using var server = await StartServerAsync(("WEIR_METRICS_BEARER_TOKEN", "s3cret-metrics-token"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_metrics_request_with_the_token_is_answered_whatever_the_case_of_the_scheme()
    {
        await using var server = await StartServerAsync(("WEIR_METRICS_BEARER_TOKEN", "s3cret-metrics-token"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.TryAddWithoutValidation("Authorization", "bEaReR s3cret-metrics-token");
        using var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
