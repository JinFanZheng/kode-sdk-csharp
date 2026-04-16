using Kode.Agent.Sdk.Diagnostics;
using KodaClaw.Automation;
using KodaClaw.BrowserHub;
using KodaClaw.BrowserHub.Tools;
using Kode.Agent.Sdk.Core.Abstractions;
using KodaClaw.ChannelHub;
using KodaClaw.Contracts;
using KodaClaw.ControlPlane;
using KodaClaw.Gateway.Channels;
using KodaClaw.Gateway;
using KodaClaw.Gateway.Plugins;
using KodaClaw.McpHub;
using KodaClaw.ModelHub;
using KodaClaw.PluginHost;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Diagnostics;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

public static partial class GatewayApp
{
    private static void ConfigureGatewayServices(
        WebApplicationBuilder builder,
        RuntimeConfigurationSnapshot runtimeBootstrap,
        IReadOnlySet<string> configuredCorsOrigins,
        Action<IServiceCollection>? configureServices)
    {
        var workspaceRoot = KodaClawWorkspaceOptions.ResolveRootPathStatic(
            builder.Configuration["KODACLAW_WORKSPACE_ROOT"]
            ?? builder.Configuration["Workspace:RootPath"]);

        // OT-4A: resolve OTLP endpoint from environment (empty = OTel export disabled).
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
                           ?? builder.Configuration["OpenTelemetry:OtlpEndpoint"];
        var otlpEnabled = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Host.UseSerilog((ctx, cfg) =>
        {
            var logDir = Path.Combine(workspaceRoot, "logs");
            Directory.CreateDirectory(logDir);
            cfg.ReadFrom.Configuration(ctx.Configuration)
               .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
               .WriteTo.File(
                   Path.Combine(logDir, "gateway-.log"),
                   rollingInterval: RollingInterval.Day,
                   retainedFileCountLimit: 7,
                   outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
               .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
               .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning);

            // OT-3C: bridge Serilog structured logs to OTel LogsProvider when OTLP is configured.
            if (otlpEnabled)
            {
                cfg.WriteTo.OpenTelemetry(opts =>
                {
                    opts.Endpoint = otlpEndpoint!;
                    opts.Protocol = OtlpProtocol.Grpc;
                    opts.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = "kodaclaw-gateway",
                        ["service.version"] = "1.0.0",
                    };
                });
            }
        });

        // OT-4A: configure OpenTelemetry Traces + Metrics.
        var otelBuilder = builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService("kodaclaw-gateway", serviceVersion: "1.0.0")
                .AddAttributes([new("deployment.environment", builder.Environment.EnvironmentName)]));

        otelBuilder.WithTracing(tracing =>
        {
            tracing
                .AddSource(KodeAgentActivitySource.SourceName)   // SDK agent spans
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();

            if (otlpEnabled)
                tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
        });

        otelBuilder.WithMetrics(metrics =>
        {
            metrics
                .AddMeter(KodeAgentMetrics.MeterName)            // SDK agent metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation();

            if (otlpEnabled)
                metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
        });

        builder.Services.AddKodaClawControlPlane(workspaceRoot);
        builder.Services.AddCors(options =>
        {
            options.AddPolicy(GatewayCorsPolicyName, policy =>
            {
                policy
                    .SetIsOriginAllowed(origin => IsAllowedCorsOrigin(origin, configuredCorsOrigins))
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .WithExposedHeaders(CorrelationHeaderName);
            });
        });
        builder.Services.AddKodaClawWorkspace(options =>
        {
            options.RootPath = builder.Configuration["KODACLAW_WORKSPACE_ROOT"]
                ?? builder.Configuration["Workspace:RootPath"];
        });
        builder.Services.AddSingleton<GatewayAuthTokenAccessor>();
        builder.Services.AddSingleton<ModelPresetService>();
        builder.Services.AddSingleton<PersonaPresetService>();
        builder.Services.AddSingleton<ModelConnectionTestService>();
        builder.Services.AddSingleton<OnboardingStateService>();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<SecretMigrationReportService>();
        builder.Services.AddSingleton<WorkspaceBackupService>();
        builder.Services.AddSingleton<WorkspaceRepairService>();
        builder.Services.AddSingleton<SessionRetentionService>();
        builder.Services.AddHostedService<SessionRetentionHostedService>();
        builder.Services.AddSingleton<SandboxRiskOverviewService>();
        builder.Services.AddSingleton<UpdateStateService>();
        builder.Services.AddSingleton<DiagnosticBundleService>();
        builder.Services.AddSingleton<ChannelInboundGatewayService>();

        if (IsStartupRepairEnabled(builder.Configuration))
        {
            builder.Services.AddHostedService<StartupRepairHostedService>();
        }
        // Legacy ModelEndpoint → ProviderAccount + AccountModel migration must run FIRST:
        // once config/accounts/ exists, ConfigBootstrapWriter and ModelRegistrySeedService
        // below will early-out on ListAccountsAsync().Count > 0 and leave the migrated data
        // intact. Running it after either of them would hide the user's old endpoints
        // because the migration early-outs when config/accounts/ already exists.
        builder.Services.AddHostedService(sp => new ModelEndpointMigrationHostedService(
            workspaceRoot,
            sp.GetRequiredService<IProviderAccountRepository>(),
            sp.GetService<IDiagnosticsService>(),
            sp.GetService<ILogger<ModelEndpointMigrationHostedService>>()));

        // ConfigBootstrapWriter + ConfigBootstrapService must be registered BEFORE
        // ModelRegistrySeedService so the keychain-backed endpoint is written first.
        builder.Services.AddSingleton<ConfigBootstrapWriter>();
        builder.Services.AddHostedService<ConfigBootstrapService>();
        builder.Services.AddHostedService<ModelRegistrySeedService>();
        builder.Services.AddSingleton<ChannelConnectorHostedService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<ChannelConnectorHostedService>());
        builder.Services.AddSingleton<IChannelConnectorRegistry>(
            provider => provider.GetRequiredService<ChannelConnectorHostedService>());

        builder.Services.AddKodaClawJsonStore(workspaceRoot);
        builder.Services.AddKodaClawAutomation(options => options.Enabled = true);
        builder.Services.AddKodaClawChannelHub();
        builder.Services.AddModelRegistry();
        builder.Services.AddKodaClawMcpHub();
        builder.Services.AddKodaClawPluginHost();
        builder.Services.AddKodaClawBrowserHub();
        builder.Services.AddSingleton<IPluginGatewayService, PluginGatewayService>();
        builder.Services.AddSingleton<IRuntimeConfigurationResolver, GatewayRuntimeConfigurationResolver>();
        builder.Services.AddKodaClawRuntime(options =>
        {
            options.DefaultModel = runtimeBootstrap.DefaultModel;
            options.OpenAIApiKey = runtimeBootstrap.OpenAIApiKey;
            options.OpenAIBaseUrl = runtimeBootstrap.OpenAIBaseUrl;
            options.AnthropicApiKey = runtimeBootstrap.AnthropicApiKey;
            options.AnthropicBaseUrl = runtimeBootstrap.AnthropicBaseUrl;
        });

        // MetricsBridgeService bridges SDK Meter events to IDiagnosticsService.
        builder.Services.AddHostedService<MetricsBridgeService>();

        configureServices?.Invoke(builder.Services);
    }

    private static void ConfigureGatewayMiddleware(WebApplication app)
    {
        // Register BrowserHub tools (Runtime can't reference BrowserHub due to circular dep).
        var browserHubService = app.Services.GetService<KodaClaw.BrowserHub.IBrowserHubService>();
        var toolRegistry = app.Services.GetRequiredService<IToolRegistry>();
        if (browserHubService is not null)
        {
            toolRegistry.Register("browser_action",
                _ => new BrowserActionTool(browserHubService));
        }

        var diagnosticsService = app.Services.GetRequiredService<IDiagnosticsService>();
        var correlationContextAccessor = app.Services.GetRequiredService<ICorrelationContextAccessor>();
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        loggerFactory.AddProvider(new DiagnosticsLoggerProvider(diagnosticsService, correlationContextAccessor));


        app.Use(async (context, next) =>
        {
            var correlationId = GetOrCreateCorrelationId(context);
            var correlationContextAccessor = context.RequestServices.GetRequiredService<ICorrelationContextAccessor>();
            correlationContextAccessor.CorrelationId = correlationId;
            context.Items[CorrelationHeaderName] = correlationId;
            context.Response.Headers[CorrelationHeaderName] = correlationId;

            try
            {
                await next();
            }
            catch (Exception ex)
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Gateway");
                if (ex is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
                {
                    logger.LogDebug("Client disconnected: {Method} {Path}", context.Request.Method, context.Request.Path);
                    return;
                }
                logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
                
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new 
                    { 
                        error = "Internal server error",
                        detail = ex.Message,
                        correlationId = context.Items[CorrelationHeaderName]?.ToString()
                    });
                }
            }
            finally
            {
                correlationContextAccessor.CorrelationId = null;
            }
        });

        app.UseCors(GatewayCorsPolicyName);

        // Serve static web assets from wwwroot/ when present (Docker mode: built web UI is copied there).
        app.UseStaticFiles();

        // Setup Wizard guard: redirect browser navigation requests to /setup when the provider
        // account repository is empty (i.e., no API key has been configured yet).
        // Skips: /api/* (API), /healthz (Docker probe), /setup* (the wizard itself), static assets.
        app.Use(async (context, next) =>
        {
            if (context.Request.Method == HttpMethods.Get)
            {
                var path = context.Request.Path.Value ?? string.Empty;
                var skip = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase)
                    || System.IO.Path.HasExtension(path);

                if (!skip)
                {
                    var accountRepo = context.RequestServices.GetRequiredService<IProviderAccountRepository>();
                    var accounts = await accountRepo.ListAccountsAsync(context.RequestAborted);
                    if (accounts.Count == 0)
                    {
                        context.Response.Redirect("/setup");
                        return;
                    }
                }
            }

            await next();
        });

        app.UseWebSockets();
    }

    private static void MapGatewayEndpoints(WebApplication app)
    {
        MapSystemEndpoints(app);
        MapChatAndDiagnosticsEndpoints(app);
        MapProviderAccountEndpoints(app);
        MapSettingsEndpoints(app);
        MapAutomationEndpoints(app);
        MapPluginEndpoints(app);
        MapCanvasEndpoints(app);
        MapSkillsEndpoints(app);
        MapApprovalEndpoints(app);
        MapSessionEndpoints(app);
        MapInboxEndpoints(app);
        MapChannelEndpoints(app);
        MapWorkspaceEndpoints(app);
        MapWorkspaceGitEndpoints(app);
        MapMcpServersEndpoints(app);
        MapMemoryEndpoints(app);
        MapMediaEndpoints(app);
        MapAutomationNotificationEndpoints(app);
        MapWeChatAuthEndpoints(app);
        MapSystemEventsEndpoints(app);
        MapRootEndpoint(app);
        MapBrowserEndpoints(app);
        MapSetupEndpoints(app);

        // SPA fallback: serve index.html for any non-API path (enables client-side routing).
        // Only active when wwwroot/index.html exists (i.e., Docker mode with bundled web UI).
        app.MapFallbackToFile("index.html");
    }

    private static bool IsStartupRepairEnabled(IConfiguration configuration)
    {
        return !string.Equals(
            configuration["KODACLAW_STARTUP_REPAIR_ENABLED"]
                ?? configuration["Gateway:StartupRepairEnabled"],
            "false",
            StringComparison.OrdinalIgnoreCase);
    }
}
