using System.Net;
using Microsoft.AspNetCore.Http;
using Weir.Api.Http;
using Weir.Api.Tests.Platform;
using Weir.Core.Json;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.Http;

/// <summary>How the server answers an error, and how it refuses a request body it cannot read.</summary>
public sealed class ApiResponsesTests
{
    private const string WebhookPath = "/api/v1/intake/webhook/sonarr";

    [Fact]
    public void An_error_with_only_a_message_is_a_server_error_that_carries_the_message_as_its_detail()
    {
        var plain = new ApiException("Something went wrong.");
        var wrapped = new ApiException("Outer.", new InvalidOperationException("inner"));
        var empty = new ApiException();

        Assert.Equal((StatusCodes.Status500InternalServerError, "Something went wrong."), (plain.StatusCode, plain.Detail));
        Assert.Equal(("Outer.", "inner"), (wrapped.Detail, wrapped.InnerException!.Message));
        Assert.Equal((StatusCodes.Status500InternalServerError, string.Empty), (empty.StatusCode, empty.Detail));
    }

    [Fact]
    public async Task An_error_with_a_code_and_extra_members_is_answered_with_all_of_them_and_its_headers()
    {
        var exception = new ApiException(StatusCodes.Status409Conflict, "Still in use.", new Dictionary<string, string> { ["Retry-After"] = "3" })
        {
            Code = "in_use",
            Details = new WireObject().Set("workflows", new WireArray([WireValue.Of("Films")])),
        };
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await ApiResponses.WriteDetailAsync(context, exception);

        context.Response.Body.Position = 0;
        var body = WireJsonParser.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal("3", context.Response.Headers["Retry-After"].ToString());
        Assert.Equal("Still in use.", ((WireString)((WireObject)body).Get("detail")!).Value);
        Assert.Equal("in_use", ((WireString)((WireObject)body).Get("code")!).Value);
        Assert.NotNull(((WireObject)body).Get("workflows"));
    }

    [Fact]
    public async Task A_text_answer_is_sent_with_its_exact_length_and_the_content_type_asked_for()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await ApiResponses.WritePlainTextAsync(context, StatusCodes.Status418ImATeapot, "café", "text/csv");

        Assert.Equal(StatusCodes.Status418ImATeapot, context.Response.StatusCode);
        Assert.Equal("text/csv", context.Response.ContentType);
        Assert.Equal(5, context.Response.ContentLength);
    }

    [Fact]
    public void A_no_content_answer_has_the_json_media_type_and_no_body()
    {
        var context = new DefaultHttpContext();

        ApiResponses.NoContentJson(context);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
    }

    [Fact]
    public async Task A_body_whose_bytes_are_not_valid_text_is_refused_with_400_and_says_so()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        using var content = new ByteArrayContent([.. "{\"a\":\""u8, 0xC3, 0x28, .. "\"}"u8]);
        content.Headers.ContentType = new("application/json");

        using var response = await new ApiTestClient(server).SendAsync(HttpMethod.Post, WebhookPath, content: content);

        Assert.Equal((HttpStatusCode.BadRequest, "There was an error parsing the body"), (response.StatusCode, await Detail(response)));
    }

    [Fact]
    public async Task A_body_sent_in_chunks_is_refused_with_413_as_soon_as_it_passes_the_limit_whatever_it_claimed()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        using var chunked = new StreamContent(new UnseekableStream(ApiRequestBody.MaxBodyBytes + 1));
        chunked.Headers.ContentType = new("application/json");

        using var response = await new ApiTestClient(server).SendAsync(HttpMethod.Post, WebhookPath, content: chunked);

        Assert.Equal(
            (HttpStatusCode.RequestEntityTooLarge, "The request body is larger than Weir accepts."),
            (response.StatusCode, await Detail(response)));
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("Application/JSON; charset=utf-8", true)]
    [InlineData("application/problem+json", true)]
    [InlineData("application/xml", false)]
    [InlineData("text/json", false)]
    [InlineData("application", false)]
    [InlineData("application/json/extra", false)]
    [InlineData("", false)]
    public void Only_application_json_and_its_plus_json_types_count_as_json(string contentType, bool expected)
    {
        Assert.Equal(expected, ApiRequestBody.IsJsonContentType(contentType));
    }

    /// <summary>A stream that cannot seek or say how long it is, so the client sends it in chunks with no <c>Content-Length</c>.</summary>
    private sealed class UnseekableStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, _remaining);
            Array.Fill(buffer, (byte)' ', offset, take);
            _remaining -= take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
