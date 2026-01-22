using System.Diagnostics;
using Kode.Agent.Boilerplate;
using Kode.Agent.Boilerplate.Middleware;
using Kode.Agent.Boilerplate.Models;
using Kode.Agent.Mcp;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Infrastructure.Sandbox;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Store.Json;
using Kode.Agent.Tools.Builtin;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

// Configure Serilog - will be configured from appsettings.json
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        "logs/kode-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
        retainedFileCountLimit: 7)
    .CreateLogger();

try
{
    Log.Information("Starting Kode.Agent Boilerplate");

    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog
    builder.Host.UseSerilog();

    // Add controllers
    builder.Services.AddControllers();

    // Add CORS
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins("http://localhost:3000", "http://localhost:5173")
                .AllowAnyMethod()
                .AllowAnyHeader()
                .WithExposedHeaders("X-Session-Id"); // 重要：暴露自定义header给前端
        });
    });

    // Configure options
    builder.Services.AddSingleton(_ =>
        BoilerplateOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath));

    // Configure agent dependencies
    builder.Services.AddSingleton<IAgentStore>(sp =>
    {
        var options = sp.GetRequiredService<BoilerplateOptions>();
        return new JsonAgentStore(options.StoreDir);
    });

    builder.Services.AddSingleton<IToolRegistry>(sp =>
    {
        var registry = new ToolRegistry();
        registry.RegisterBuiltinTools();
        return registry;
    });

    builder.Services.AddSingleton<ISandboxFactory, LocalSandboxFactory>();

    builder.Services.AddSingleton<IModelProvider>(sp =>
    {
        var options = sp.GetRequiredService<BoilerplateOptions>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var logger = sp.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("Creating Model Provider: Provider={Provider}, DefaultModel={Model}",
            options.DefaultProvider, options.DefaultModel);
        
        // Log Anthropic config
        var anthropicKey = configuration["Anthropic:ApiKey"];
        var anthropicUrl = configuration["Anthropic:BaseUrl"];
        var anthropicModel = configuration["Anthropic:ModelId"];
        logger.LogInformation("Anthropic Config: ApiKey={Key}, BaseUrl={Url}, ModelId={Model}",
            string.IsNullOrEmpty(anthropicKey) ? "EMPTY" : $"{anthropicKey[..8]}...",
            anthropicUrl ?? "NULL",
            anthropicModel ?? "NULL");

        var provider = ModelProviderFactory.CreateFromConfiguration(
            configuration, 
            options.DefaultProvider,
            options.DefaultModel);
            
        logger.LogInformation("Model Provider created successfully: {Type}", provider.GetType().Name);
        return provider;
    });

    builder.Services.AddSingleton(sp => new AgentDependencies
    {
        Store = sp.GetRequiredService<IAgentStore>(),
        ToolRegistry = sp.GetRequiredService<IToolRegistry>(),
        SandboxFactory = sp.GetRequiredService<ISandboxFactory>(),
        ModelProvider = sp.GetRequiredService<IModelProvider>(),
        LoggerFactory = sp.GetService<ILoggerFactory>()
    });

    // Add MCP client manager
    builder.Services.AddMcpClientManager();

    // Add assistant service
    builder.Services.AddSingleton<AssistantService>();

    // Add OpenTelemetry activity source
    builder.Services.AddSingleton(new ActivitySource("Kode.Agent.Boilerplate"));

    // Configure OpenTelemetry
    var otelConfig = builder.Configuration.GetSection("OpenTelemetry");
    var otelEnabled = otelConfig.GetValue<bool>("Enabled");
    if (otelEnabled)
    {
        var serviceName = otelConfig["ServiceName"] ?? "Kode.Agent.Boilerplate";
        var serviceVersion = otelConfig["ServiceVersion"] ?? "1.0.0";

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource("Kode.Agent.Boilerplate")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                var exporter = otelConfig["Exporter"]?.ToLowerInvariant();
                if (exporter == "otlp")
                {
                    var otlpEndpoint = otelConfig["OtlpEndpoint"];
                    if (!string.IsNullOrEmpty(otlpEndpoint))
                    {
                        tracing.AddOtlpExporter(options =>
                        {
                            options.Endpoint = new Uri(otlpEndpoint);
                        });
                    }
                }
                else
                {
                    tracing.AddConsoleExporter();
                }
            });

        Log.Information("OpenTelemetry enabled with exporter: {Exporter}", otelConfig["Exporter"]);
    }

    var app = builder.Build();

    // Use CORS - 必须在其他中间件之前
    app.UseCors();

    // Add request/response logging middleware
    app.UseRequestResponseLogging();

    // Load MCP tools
    var mcpManager = app.Services.GetRequiredService<McpClientManager>();
    var toolRegistry = app.Services.GetRequiredService<IToolRegistry>();
    var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
    var logger = loggerFactory.CreateLogger("Program");
    
    await LoadMcpToolsAsync(app.Configuration, mcpManager, toolRegistry, logger);

    // Map endpoints
    app.MapControllers();

    app.MapGet("/", () => Results.Json(new
    {
        name = "Kode.Agent Boilerplate",
        version = "1.0.0",
        openai_compatible = true,
        endpoints = new[]
        {
            "POST /v1/chat/completions",
            "POST /{sessionId}/v1/chat/completions",
            "GET  /health"
        },
        headers = new
        {
            X_Session_Id = "Session ID for multi-turn conversations"
        }
    }));

    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

    // OpenAI-compatible endpoints
    app.MapPost("/v1/chat/completions",
        async (HttpContext ctx, OpenAiChatCompletionRequest req, AssistantService svc) =>
            await svc.HandleChatCompletionsAsync(ctx, req));

    app.MapPost("/{sessionId}/v1/chat/completions",
        async (HttpContext ctx, OpenAiChatCompletionRequest req, AssistantService svc) =>
            await svc.HandleChatCompletionsAsync(ctx, req));

    Log.Information("Kode.Agent Boilerplate started successfully");
    Log.Information("Listening on: {Urls}", app.Configuration["Urls"]);
    Log.Information("Available endpoints:");
    Log.Information("  POST {BaseUrl}/v1/chat/completions", app.Configuration["Urls"]);
    Log.Information("  POST {BaseUrl}/{{sessionId}}/v1/chat/completions", app.Configuration["Urls"]);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

static async Task LoadMcpToolsAsync(
    IConfiguration configuration,
    McpClientManager mcpManager,
    IToolRegistry toolRegistry,
    Microsoft.Extensions.Logging.ILogger logger)
{
    var mcpServersSection = configuration.GetSection("McpServers");
    if (!mcpServersSection.Exists())
    {
        logger.LogInformation("No MCP servers configured");
        return;
    }

    var servers = mcpServersSection.GetChildren();
    foreach (var serverSection in servers)
    {
        var serverName = serverSection.Key;
        var transport = serverSection["transport"];
        var url = serverSection["url"];

        if (string.IsNullOrEmpty(transport) || string.IsNullOrEmpty(url))
        {
            logger.LogWarning("Skipping MCP server {ServerName}: missing transport or url", serverName);
            continue;
        }

        try
        {
            var transportType = transport.ToLowerInvariant() switch
            {
                "stdio" => McpTransportType.Stdio,
                "sse" => McpTransportType.Sse,
                "streamablehttp" => McpTransportType.StreamableHttp,
                _ => throw new InvalidOperationException($"Unknown transport type: {transport}")
            };
            
            var mcpConfig = new McpConfig
            {
                ServerName = serverName,
                Transport = transportType,
                Url = url
            };

            var tools = await McpToolProvider.GetToolsAsync(mcpManager, mcpConfig, logger);
            foreach (var tool in tools)
            {
                toolRegistry.Register(tool);
            }

            logger.LogInformation("Loaded {ToolCount} tools from MCP server: {ServerName}", tools.Count, serverName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load MCP server: {ServerName}", serverName);
        }
    }
}
