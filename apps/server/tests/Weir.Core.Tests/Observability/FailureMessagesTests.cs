using System.Net.Sockets;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Core.Observability;

namespace Weir.Core.Tests.Observability;

/// <summary>How a failure is sorted into a kind and worded for the person reading Activity.</summary>
public sealed class FailureMessagesTests
{
    private static FailureSubject Subject(string message, ExceptionCategory category = ExceptionCategory.Other, string type = "RuntimeError") =>
        new(type, message, category);

    [Theory]
    [InlineData(FailureKind.Auth, "auth")]
    [InlineData(FailureKind.Credential, "credential")]
    [InlineData(FailureKind.Network, "network")]
    [InlineData(FailureKind.Filesystem, "filesystem")]
    [InlineData(FailureKind.RateLimit, "rate_limit")]
    [InlineData(FailureKind.Validation, "validation")]
    [InlineData(FailureKind.NotFound, "not_found")]
    [InlineData(FailureKind.Internal, "internal")]
    public void Each_kind_has_one_stored_name(FailureKind kind, string name) => Assert.Equal(name, FailureMessages.KindText(kind));

    [Theory]
    [InlineData("The rate limit was hit", FailureKind.RateLimit)]
    [InlineData("RateLimit exceeded", FailureKind.RateLimit)]
    [InlineData("rate-limit reached", FailureKind.RateLimit)]
    [InlineData("HTTP 429", FailureKind.RateLimit)]
    [InlineData("the credential is wrong", FailureKind.Credential)]
    [InlineData("bad API key", FailureKind.Credential)]
    [InlineData("token expired", FailureKind.Credential)]
    [InlineData("the secret is missing", FailureKind.Credential)]
    [InlineData("Unauthorized", FailureKind.Auth)]
    [InlineData("Forbidden", FailureKind.Auth)]
    [InlineData("HTTP 401", FailureKind.Auth)]
    [InlineData("HTTP 403", FailureKind.Auth)]
    [InlineData("Not Found", FailureKind.NotFound)]
    [InlineData("HTTP 404", FailureKind.NotFound)]
    [InlineData("something broke", FailureKind.Internal)]
    public void Words_in_the_failure_decide_its_kind(string message, FailureKind kind) =>
        Assert.Equal(kind, FailureMessages.Classify(Subject(message)));

    [Fact]
    public void A_rate_limit_outranks_a_credential_and_a_credential_outranks_a_refusal()
    {
        Assert.Equal(FailureKind.RateLimit, FailureMessages.Classify(Subject("429 for this token")));
        Assert.Equal(FailureKind.Credential, FailureMessages.Classify(Subject("403: the api key was refused")));
    }

    [Fact]
    public void The_family_decides_when_no_word_does_and_a_file_problem_outranks_not_found()
    {
        Assert.Equal(FailureKind.Filesystem, FailureMessages.Classify(Subject("could not open", ExceptionCategory.Filesystem)));
        Assert.Equal(FailureKind.Filesystem, FailureMessages.Classify(Subject("file not found", ExceptionCategory.Filesystem)));
        Assert.Equal(FailureKind.Network, FailureMessages.Classify(Subject("connection reset", ExceptionCategory.NetworkOrOs)));
        Assert.Equal(FailureKind.Validation, FailureMessages.Classify(Subject("bad value", ExceptionCategory.Validation)));
        Assert.Equal(FailureKind.NotFound, FailureMessages.Classify(Subject("404", ExceptionCategory.NetworkOrOs)));
        Assert.Throws<ArgumentNullException>(() => FailureMessages.Classify(null!));
    }

    [Fact]
    public void The_type_name_is_searched_as_well_as_the_message()
    {
        Assert.Equal(FailureKind.Auth, FailureMessages.Classify(Subject("denied", type: "UnauthorizedAccessException")));
    }

    [Theory]
    [InlineData(FailureKind.RateLimit, null, "The provider temporarily limited requests.")]
    [InlineData(FailureKind.RateLimit, "sonarr", "The provider from Sonarr temporarily limited requests.")]
    [InlineData(FailureKind.Credential, "radarr", "Weir could not use the saved credentials from Radarr.")]
    [InlineData(FailureKind.Auth, "", "The service rejected the credentials or permission level.")]
    [InlineData(FailureKind.Network, "deluno", "Weir could not reach the service from Deluno over the network.")]
    [InlineData(FailureKind.Filesystem, "sonarr", "Weir could not use a file or folder it needed.")]
    [InlineData(FailureKind.Validation, null, "The saved settings or job payload did not pass validation.")]
    [InlineData(FailureKind.NotFound, "tmdb", "The item or endpoint from TMDb was not found.")]
    [InlineData(FailureKind.Internal, null, "The job hit an unexpected error.")]
    public void Each_kind_says_why_naming_the_provider_when_there_is_one(FailureKind kind, string? provider, string why) =>
        Assert.Equal(why, FailureMessages.WhyForKind(kind, provider));

    [Theory]
    [InlineData(FailureKind.Credential, "sonarr", false, "Re-enter the Sonarr credentials and run the connection test again.")]
    [InlineData(FailureKind.Auth, null, false, "Re-enter the provider credentials and run the connection test again.")]
    [InlineData(FailureKind.Network, "OpenSubtitles", false, "Check the OpenSubtitles address, network access, and that the service is running.")]
    [InlineData(FailureKind.RateLimit, null, true, "Wait for the provider limit to reset, or reduce how often this workflow runs.")]
    [InlineData(FailureKind.Filesystem, null, false, "Check that the file or folder still exists and that Weir can read and write it.")]
    [InlineData(FailureKind.Validation, null, false, "Review the saved settings for this workflow and save them again.")]
    [InlineData(FailureKind.NotFound, null, false, "Refresh the source library and run the workflow again.")]
    public void Each_kind_that_a_person_can_act_on_says_what_to_do(FailureKind kind, string? provider, bool recoverable, string action) =>
        Assert.Equal(action, FailureMessages.NextActionForKind(kind, provider, recoverable));

    [Fact]
    public void A_missing_item_that_weir_carries_on_without_and_an_unexpected_error_have_nothing_to_do()
    {
        Assert.Null(FailureMessages.NextActionForKind(FailureKind.NotFound, null, recoverable: true));
        Assert.Null(FailureMessages.NextActionForKind(FailureKind.Internal, "sonarr", recoverable: false));
    }

    [Fact]
    public void A_failure_that_stops_the_job_says_so_and_names_the_provider()
    {
        var failure = FailureMessages.FromException("Processing", "connection test", Subject("The api key was rejected; api_key=hunter2"), provider: "sonarr");

        Assert.Equal(FailureKind.Credential, failure.Kind);
        Assert.False(failure.Recoverable);
        Assert.Equal(
            "Processing connection test for Sonarr failed: Weir could not use the saved credentials from Sonarr. This job is marked failed so it does not look successful.",
            failure.Message);
        Assert.Equal("RuntimeError: The api key was rejected; api_key=[redacted]", failure.TechnicalDetail);
        Assert.Equal("Re-enter the Sonarr credentials and run the connection test again.", failure.NextAction);
    }

    [Fact]
    public void A_failure_weir_carries_on_past_says_it_was_skipped()
    {
        var failure = FailureMessages.FromException("Processing", "subtitle search", Subject("HTTP 429"), recoverable: true);

        Assert.Equal(
            "Processing subtitle search skipped and continued: The provider temporarily limited requests. Weir will continue with the next available provider.",
            failure.Message);
    }

    [Fact]
    public void A_reason_and_a_continuation_given_by_the_caller_replace_the_standard_wording()
    {
        var failure = FailureMessages.FromException(
            "Library",
            "scan",
            Subject("boom"),
            continuation: "The scan will start again at midnight.",
            reason: "The library folder is offline.");

        Assert.Equal("The library folder is offline.", failure.Why);
        Assert.Equal("The scan will start again at midnight.", failure.WhatHappensNext);
        Assert.Equal("Library scan failed: The library folder is offline. The scan will start again at midnight.", failure.Message);
    }

    [Fact]
    public void The_technical_detail_is_cut_to_a_thousand_characters_and_a_failure_must_be_given()
    {
        var failure = FailureMessages.FromException("Library", "scan", Subject(new string('x', 3000)));

        Assert.Equal(1000, failure.TechnicalDetail!.Length);
        Assert.Throws<ArgumentNullException>(() => FailureMessages.FromException("Library", "scan", null!));
    }

    [Fact]
    public void A_failure_is_written_out_with_its_next_action_and_technical_detail_only_when_it_has_them()
    {
        var full = FailureMessages.FromException("Processing", "connection test", Subject("token rejected"), provider: "radarr").AsDict();

        Assert.Equal("credential", Assert.IsType<WireString>(full["failure_kind"]).Value);
        Assert.Equal("Processing connection test", Assert.IsType<WireString>(full["what_failed"]).Value);
        Assert.False(Assert.IsType<WireBool>(full["recoverable"]).Value);
        Assert.Equal("Re-enter the Radarr credentials and run the connection test again.", Assert.IsType<WireString>(full["next_action"]).Value);
        Assert.Contains("technical_detail", full.Keys);

        var bare = new OperatorFailure("Library", "scan", FailureKind.Internal, true, "message", "why", "next", NextAction: null, TechnicalDetail: string.Empty).AsDict();

        Assert.DoesNotContain("next_action", bare.Keys);
        Assert.DoesNotContain("technical_detail", bare.Keys);
        Assert.Equal("message", Assert.IsType<WireString>(bare["user_message"]).Value);
        Assert.Equal("why", Assert.IsType<WireString>(bare["why"]).Value);
        Assert.Equal("next", Assert.IsType<WireString>(bare["what_happens_next"]).Value);
    }

    [Fact]
    public void A_refusal_weir_made_itself_is_recorded_as_a_runtime_error()
    {
        var subject = FailureMessages.RuntimeError("no space");

        Assert.Equal(new FailureSubject("RuntimeError", "no space", ExceptionCategory.Other), subject);
    }

    public static TheoryData<Exception, string, ExceptionCategory> DotNetFailures() => new()
    {
        { new AlreadyRecordedFailureException("shown already"), AlreadyRecordedFailureException.FailureTypeName, ExceptionCategory.Other },
        { new FileNotFoundException("gone"), "FileNotFoundError", ExceptionCategory.Filesystem },
        { new DirectoryNotFoundException("gone"), "FileNotFoundError", ExceptionCategory.Filesystem },
        { new UnauthorizedAccessException("denied"), "PermissionError", ExceptionCategory.Filesystem },
        { new TimeoutException("slow"), "TimeoutError", ExceptionCategory.NetworkOrOs },
        { new HttpRequestException("refused"), "ConnectionError", ExceptionCategory.NetworkOrOs },
        { new SocketException(), "ConnectionError", ExceptionCategory.NetworkOrOs },
        { new IOException("pipe"), "OSError", ExceptionCategory.NetworkOrOs },
        { new WireTypeException("wrong type"), "TypeError", ExceptionCategory.Validation },
        { new ArgumentException("bad"), "ValueError", ExceptionCategory.Validation },
        { new FormatException("bad"), "ValueError", ExceptionCategory.Validation },
        { new InvalidCastException("bad"), "ValueError", ExceptionCategory.Validation },
        { new WireValueException("bad"), "ValueError", ExceptionCategory.Validation },
        { new InvalidOperationException("odd"), "InvalidOperationException", ExceptionCategory.Other },
    };

    [Theory]
    [MemberData(nameof(DotNetFailures))]
    public void A_dotnet_exception_is_recorded_under_the_stable_name_and_family_stored_details_use(Exception exception, string typeName, ExceptionCategory category)
    {
        var subject = FailureMessages.FromDotNet(exception);

        Assert.Equal(typeName, subject.TypeName);
        Assert.Equal(exception.Message, subject.Message);
        Assert.Equal(category, subject.Category);
    }

    [Fact]
    public void An_exception_must_be_given()
    {
        Assert.Throws<ArgumentNullException>(() => FailureMessages.FromDotNet(null!));
    }
}
