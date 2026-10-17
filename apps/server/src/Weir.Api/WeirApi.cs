using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Weir.Api.Endpoints;
using Weir.Api.Http;
using Weir.Api.Web;
using Weir.Core.Configuration;
using Weir.Core.Metrics;
using Weir.Infrastructure;
using Weir.Infrastructure.Activity;
using Weir.Infrastructure.Auth;
using Weir.Infrastructure.Http;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.LibraryMode;
using Weir.Infrastructure.MediaManagers;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Scheduling;
using Weir.Infrastructure.Settings;
using Weir.Infrastructure.SystemReadings;

namespace Weir.Api;

/// <summary>Registers the HTTP surface and builds its middleware pipeline.</summary>
public static class WeirApi
{
    public static IServiceCollection AddWeirApi(this IServiceCollection services, WeirOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddWeirPlatform(options);
        services.AddWeirSystemStats(options);
        services.AddWeirSystemOverview(options);
        services.AddSingleton<ServerLifecycle>();
        services.AddSingleton(WebApp.Resolve(options.WebDist));
        services.AddSingleton<IOperatorAuthentication, SessionOperatorAuthentication>();
        services.AddSingleton<RouteTable>();
        services.AddSingleton<WeirOpenApiDocumentCache>();
        services.TryAddSingleton<RuntimeMetricsStore>();
        services.AddSingleton<IJobQueueMetrics, RuntimeJobQueueMetrics>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<AuthRateLimiters>();
        services.AddSingleton<SetupCodeGate>();
        services.AddSingleton<ConfigurationBackups>();
        // The processing.progress SSE stream (#745 part 5): wraps LiveProgressStore so the one caller that
        // frames it takes a single dependency instead of holding the store only to forward it.
        services.TryAddSingleton<ActivityProgressFrames>();
        services.TryAddSingleton<ConnectionActivityFrames>();
        services.TryAddSingleton<LiveStreamFrames>();
        services.TryAddSingleton<SystemStatsFrames>();

        // Endpoint handler groups: one instance per group, constructor-injected with the stores it needs.
        services.AddSingleton<AuthAccountEndpointHandlers>();
        services.AddSingleton<AuthSessionEndpointHandlers>();
        services.AddSingleton<AuthSessionLifecycleEndpointHandlers>();
        services.AddSingleton<ActivityEndpointHandlers>();
        services.AddSingleton<SystemStatsEndpointHandlers>();
        services.AddSingleton<NotificationEndpointHandlers>();
        services.AddSingleton<SuiteConfigurationEndpointHandlers>();
        services.AddSingleton<SuiteDiagnosticsEndpointHandlers>();
        services.AddSingleton<SuiteFileEndpointHandlers>();
        services.AddSingleton<SuiteUpdateEndpointHandlers>();
        services.AddSingleton<SuiteNetworkAccessEndpointHandlers>();
        services.AddSingleton<SuiteOperationalHistoryEndpointHandlers>();
        services.AddSingleton<SuitePauseEndpointHandlers>();
        services.AddSingleton<MediaManagerConnectionsEndpointHandlers>();
        services.AddSingleton<MediaManagerIntakeEndpointHandlers>();
        services.AddSingleton<UpdateFiles>();
        services.AddSingleton<IReleaseCatalogClient>(provider => new GitHubReleaseCatalogClient(
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<GitHubReleaseCatalogClient>>(),
            handler: null,
            feedUrl: provider.GetRequiredService<WeirOptions>().ReleasesFeedUrl));
        services.AddSingleton<IExternalJsonPoster, ExternalJsonPoster>();
        services.AddSingleton<NotificationDispatcher>();
        // Registered before AddWeirJobs, whose TryAdd would otherwise install the version that sends nothing.
        services.AddSingleton<IJobNotifications, WebhookJobNotifications>();
        services.AddWeirMediaManagers(options);
        services.AddWeirDownloadClients();
        services.AddSingleton<DownloadClientConnectionsEndpointHandlers>();
        services.AddWeirProcessingApis();
        services.AddWeirProcessingFailureFollowUps(options);
        services.AddWeirLibraryMode(options);

        // Scheduled work, hosted with the jobs (AddWeirJobs) by PeriodicTaskService.
        services.AddSingleton<SessionCleanupTask>();
        services.AddSingleton<LogRetentionTask>();
        services.AddSingleton<ConfigurationBackupTask>();
        services.AddSingleton<ActivityLatestPollTask>();
        services.AddSingleton<IPeriodicTask>(provider => provider.GetRequiredService<SessionCleanupTask>());
        services.AddSingleton<IPeriodicTask>(provider => provider.GetRequiredService<LogRetentionTask>());
        services.AddSingleton<IPeriodicTask>(provider => provider.GetRequiredService<ConfigurationBackupTask>());
        services.AddSingleton<IPeriodicTask>(provider => provider.GetRequiredService<ActivityLatestPollTask>());
        services.AddSingleton<IPeriodicTask, WorkStateTask>();
        services.AddSingleton<IPeriodicTask, SuitePauseExpiryTask>();
        services.AddSingleton<IPeriodicTask, MetricsChangeTask>();
        services.AddSingleton<IPeriodicTask, UpdateCheckTask>();
        // Hears the tray's answers (a downloaded update, the firewall prompt) so open screens show them at once.
        services.AddHostedService<TrayHandOffWatcher>();
        // Tells the tray what it shows (the pause, managers and folders not answering) and does the pause it asks for.
        services.AddHostedService<TrayStatusWriter>();
        services.AddHostedService<TrayPauseRequestWatcher>();
        // Saves the copy of Weir's data the tray asks for just before it applies an update.
        services.AddHostedService<TrayUpdateBackupWatcher>();
        // Says in the log when requests wait for a thread to start work on, which nothing else shows.
        services.AddHostedService<ThreadPoolWatch>();
        // Knows when the whole process could not run (the computer asleep, a machine saved or a container frozen), so work that comes
        // late for that reason is told as information and a scheduler that fell behind while Weir ran is still a warning.
        services.AddSingleton<ProcessPulse>();
        services.AddHostedService(provider => provider.GetRequiredService<ProcessPulse>());
        services.AddWeirResponseCompression();
        services.AddRouting();
        return services;
    }

    /// <summary>
    /// The middleware, outermost first: the server's error response, forwarded headers (trusted proxies only),
    /// the Host allow-list, compressed assets, CORS (when origins are configured), the trusted-proxy scheme,
    /// HEAD-as-GET, the X-Requested-With check, request context, security headers, the busy-database answer, response compression, then
    /// routes, the static mount and the 404 handler.
    /// </summary>
    public static WebApplication UseWeirApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetRequiredService<WeirOptions>();
        var webDist = app.Services.GetRequiredService<WebDist>();
        app.UseMiddleware<ServerErrorMiddleware>();
        app.UseTrustedForwardedHeaders(options);
        // Before everything else that could answer a request: a rejected Host never reaches static
        // assets, CORS or routing.
        app.UseMiddleware<HostAllowListMiddleware>();
        if (webDist.MountedAtStartup)
        {
            app.UseMiddleware<CompressedStaticAssetsMiddleware>();
        }

        if (options.CorsOrigins.Count > 0)
        {
            app.UseMiddleware<CorsMiddleware>();
        }

        app.UseMiddleware<TrustedProxySchemeMiddleware>();
        app.UseMiddleware<HeadMirrorsGetMiddleware>();
        app.UseMiddleware<XRequestedWithMiddleware>();
        app.UseMiddleware<RequestContextMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<DatabaseBusyMiddleware>();
        app.UseMiddleware<MethodNotAllowedBodyMiddleware>();
        app.UseWeirResponseCompression();
        app.UseRouting();
        // Static files run between routing and endpoints so matched routes win over files, and the 404
        // handler runs only after both.
        // That ordering needs explicit UseEndpoints instead of top-level route registration.
#pragma warning disable ASP0014
        app.UseWebAppStaticFiles(webDist);
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapSystemEndpoints();
            endpoints.MapSystemStatsEndpoints();
            endpoints.MapSystemOverviewEndpoints();
            endpoints.MapSystemLogEndpoints();
            endpoints.MapOpenApiEndpoint();
            endpoints.MapMetricsEndpoint();
            endpoints.MapAuthEndpoints();
            endpoints.MapSuiteEndpoints();
            endpoints.MapSuiteFileEndpoints();
            endpoints.MapReconciliationEndpoints();
            endpoints.MapMediaManagerConnectionsEndpoints();
            endpoints.MapMediaManagerIntakeEndpoints();
            endpoints.MapDownloadClientConnectionsEndpoints();
            endpoints.MapNotificationEndpoints();
            endpoints.MapActivityEndpoints();
            endpoints.MapWeirProcessingApis();
            var routes = endpoints.ServiceProvider.GetRequiredService<RouteTable>();
            routes.Add("/", [HttpMethods.Get], "/");
            routes.Add("/index.html", [HttpMethods.Get], "/index.html");
            endpoints.MapWebAppIndex();
        });
#pragma warning restore ASP0014
        app.Run(WebApp.HandleUnmatchedAsync);
        return app;
    }
}
