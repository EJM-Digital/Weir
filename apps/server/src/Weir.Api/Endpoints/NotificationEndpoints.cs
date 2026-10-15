using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weir.Api.Http;
using Weir.Core.Auth;
using Weir.Core.Json;
using Weir.Core.Metrics;
using Weir.Core.Notifications;
using Weir.Core.Validation;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Notifications;

namespace Weir.Api.Endpoints;

/// <summary>Notification channels and <c>GET /metrics</c>.</summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<NotificationEndpointHandlers>();
        endpoints.MapV1("GET", "/suite/notification-channels", handlers.ListAsync);
        endpoints.MapV1("POST", "/suite/notification-channels", handlers.CreateAsync);
        endpoints.MapV1("PUT", "/suite/notification-channels/{channel_id}", handlers.UpdateAsync);
        endpoints.MapV1("DELETE", "/suite/notification-channels/{channel_id}", handlers.DeleteAsync);
        endpoints.MapV1("POST", "/suite/notification-channels/{channel_id}/test", handlers.TestAsync);
        return endpoints;
    }

    public static IEndpointRouteBuilder MapMetricsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        var handlers = endpoints.ServiceProvider.GetRequiredService<NotificationEndpointHandlers>();
        endpoints.MapApi("GET", "/metrics", "/metrics", handlers.MetricsAsync);
        return endpoints;
    }
}

/// <summary>Handlers for <see cref="NotificationEndpoints"/>, constructor-injected with the stores they need.</summary>
internal sealed class NotificationEndpointHandlers
{
    private const string ConfirmationExpired = "Your confirmation token expired. Refresh the page and try again.";

    private readonly NotificationChannelStore _channels;
    private readonly NotificationDispatcher _dispatcher;
    private readonly RuntimeMetricsStore _metrics;

    public NotificationEndpointHandlers(NotificationChannelStore channels, NotificationDispatcher dispatcher, RuntimeMetricsStore metrics)
    {
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    private sealed record ChannelInput(string CsrfToken, string Label, string Provider, string Url, List<string> Events, bool Enabled);

    private static ChannelInput ReadChannel(WireValue? body, ValidationIssues issues)
    {
        var model = new BodyModel(body, issues);
        var label = model.Str("label", minLength: 1, maxLength: 255);
        var provider = model.Str("provider");
        var url = model.Str("url", minLength: 1);
        var events = model.StrList("events", ["job_failed"]);
        var enabled = model.Bool("enabled", defaultValue: true);
        var csrfToken = model.Str("csrf_token");
        model.Finish(ExtraFields.Ignore);
        return new ChannelInput(csrfToken, label, provider, url, events, enabled);
    }

    public async Task<ApiResult> ListAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var uow = await request.DbAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(NotificationRules.ListOut(await _channels.ListAsync(uow).ConfigureAwait(false)));
    }

    public async Task<ApiResult> CreateAsync(ApiRequest request)
    {
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var input = ReadChannel(body, issues);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(input.CsrfToken, ConfirmationExpired);
        var uow = await request.DbAsync().ConfigureAwait(false);
        try
        {
            NotificationRules.Validate(input.Label, input.Provider, input.Url, input.Events);
        }
        catch (WireValueException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        var row = await _channels.CreateAsync(uow, input.Label, input.Provider, input.Url, input.Events, input.Enabled).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return new JsonApiResult(StatusCodes.Status201Created, NotificationRules.ChannelOut(row));
    }

    public async Task<ApiResult> UpdateAsync(ApiRequest request)
    {
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var channelId = request.PathInt("channel_id", issues);
        var input = ReadChannel(body, issues);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(input.CsrfToken, ConfirmationExpired);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await _channels.GetAsync(uow, channelId).ConfigureAwait(false)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "Notification channel not found.");
        try
        {
            NotificationRules.Validate(input.Label, input.Provider, input.Url, input.Events);
        }
        catch (WireValueException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, exception.Message);
        }

        var updated = await _channels.UpdateAsync(uow, row, input.Label, input.Provider, input.Url, input.Events, input.Enabled).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return ApiRoutes.Ok(NotificationRules.ChannelOut(updated));
    }

    public async Task<ApiResult> DeleteAsync(ApiRequest request)
    {
        await request.RequireUserAsync(UserRoles.AdminOnly).ConfigureAwait(false);
        var headerToken = request.FirstHeader("X-CSRF-Token");
        var issues = new ValidationIssues();
        var channelId = request.PathInt("channel_id", issues);
        issues.ThrowIfAny();

        request.ValidateBrowserPostOrigin();
        var secret = request.RequireSessionSecret();
        if (headerToken is null || !request.VerifyCsrf(secret, headerToken, allowAnonymous: false))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, ConfirmationExpired);
        }

        var uow = await request.DbAsync().ConfigureAwait(false);
        if (await _channels.GetAsync(uow, channelId).ConfigureAwait(false) is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "Notification channel not found.");
        }

        await _channels.DeleteAsync(uow, channelId).ConfigureAwait(false);
        await request.CommitAsync().ConfigureAwait(false);
        return new CustomApiResult(context =>
        {
            ApiResponses.NoContentJson(context);
            return Task.CompletedTask;
        });
    }

    public async Task<ApiResult> TestAsync(ApiRequest request)
    {
        var body = await request.ReadBodyAsync().ConfigureAwait(false);
        await request.RequireUserAsync(UserRoles.OperatorOrAdmin).ConfigureAwait(false);
        var issues = new ValidationIssues();
        var channelId = request.PathInt("channel_id", issues);
        var model = new BodyModel(body, issues);
        var csrfToken = model.Str("csrf_token");
        model.Finish(ExtraFields.Ignore);
        issues.ThrowIfAny();

        request.RequireConfirmationToken(csrfToken, ConfirmationExpired);
        var uow = await request.DbAsync().ConfigureAwait(false);
        var row = await _channels.GetAsync(uow, channelId).ConfigureAwait(false)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "Notification channel not found.");
        var error = await _dispatcher.TestAsync(row, request.Context.RequestAborted).ConfigureAwait(false);
        return ApiRoutes.Ok(new WireObject().Set("ok", error is null).Set("error", error));
    }

    /// <summary>Metrics access needs a matching bearer token, or an operator or admin session.</summary>
    public async Task<ApiResult> MetricsAsync(ApiRequest request)
    {
        var bearer = BearerToken(request.FirstHeader("Authorization"));
        var expected = request.Options.MetricsBearerToken;
        var tokenOk = bearer is not null && !string.IsNullOrEmpty(expected) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(bearer), Encoding.UTF8.GetBytes(expected));
        if (!tokenOk)
        {
            var uow = await request.DbAsync().ConfigureAwait(false);
            var pair = await request.Auth.LoadValidSessionAsync(uow, request.RawSessionToken).ConfigureAwait(false)
                ?? throw new ApiException(StatusCodes.Status401Unauthorized, "Not authenticated.");
            if (!UserRoles.OperatorOrAdmin.Contains(pair.User.Role))
            {
                throw new ApiException(StatusCodes.Status403Forbidden, "Forbidden.");
            }
        }

        var text = _metrics.RenderPrometheus();
        return new CustomApiResult(context => ApiResponses.WritePlainTextAsync(context, StatusCodes.Status200OK, text, "text/plain; version=0.0.4; charset=utf-8"));
    }

    private static string? BearerToken(string? headerValue)
    {
        if (string.IsNullOrEmpty(headerValue))
        {
            return null;
        }

        var space = headerValue.IndexOf(' ', StringComparison.Ordinal);
        var scheme = space < 0 ? headerValue : headerValue[..space];
        var token = space < 0 ? string.Empty : headerValue[(space + 1)..];
        return !scheme.Equals("bearer", StringComparison.OrdinalIgnoreCase) || token.Trim().Length == 0 ? null : token.Trim();
    }
}
