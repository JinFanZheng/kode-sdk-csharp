using Kode.Agent.Mcp;
using Kode.Agent.WebApiAssistant;
using Kode.Agent.WebApiAssistant.Extensions;
using Kode.Agent.WebApiAssistant.OpenAI;
using Kode.Agent.WebApiAssistant.Services;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Infrastructure.Sandbox;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Store.Json;
using Kode.Agent.Tools.Builtin;
using Serilog;
using Serilog.Events;

EnvLoader.Load();

// 配置 Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        "logs/kode-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
        retainedFileCountLimit: 7)
    .CreateLogger();

try
{
    Log.Information("Starting Kode.Agent WebApi Assistant");

    var builder = WebApplication.CreateBuilder(args);

    // 使用 Serilog
    builder.Host.UseSerilog();

    // 添加控制器支持
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    // builder.Services.AddSwaggerGen();

    builder.Services.AddSingleton(_ =>
        AssistantOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath));
    builder.Services.AddSingleton<IAgentStore>(sp =>
    {
        var options = sp.GetRequiredService<AssistantOptions>();
        return new JsonAgentStore(options.StoreDir);
    });
    builder.Services.AddSingleton<IToolRegistry>(sp =>
    {
        var registry = new ToolRegistry();
        registry.RegisterBuiltinTools();
        registry.RegisterPlatformTools(sp);
        return registry;
    });
    builder.Services.AddSingleton<ISandboxFactory, LocalSandboxFactory>();
    builder.Services.AddSingleton<IModelProvider>(sp =>
    {
        var options = sp.GetRequiredService<AssistantOptions>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        return ModelProviderFactory.CreateFromConfiguration(configuration, options.DefaultProvider,
            options.DefaultModel);
    });
    builder.Services.AddSingleton(sp => new AgentDependencies
    {
        Store = sp.GetRequiredService<IAgentStore>(),
        ToolRegistry = sp.GetRequiredService<IToolRegistry>(),
        SandboxFactory = sp.GetRequiredService<ISandboxFactory>(),
        ModelProvider = sp.GetRequiredService<IModelProvider>(),
        LoggerFactory = sp.GetService<ILoggerFactory>()
    });
    builder.Services.AddSingleton<AssistantService>();

    // 添加 MCP 服务
    builder.Services.AddMcpClientManager();
    builder.Services.AddSingleton<McpServersLoader>();

    // // 添加任务调度器
    // builder.Services.AddHostedService<Kode.Agent.WebApiAssistant.Scheduler.TaskScheduler>();

    // 添加调度器演示服务（会自动注册示例任务）
    // builder.Services.AddSingleton<Kode.Agent.WebApiAssistant.Scheduler.SchedulerDemoService>();

    // 添加核心服务（包括 UserService 和 SessionService）
    builder.Services.AddCoreServices();

    // 添加 HttpClient 支持（用于 Notify 工具）
    builder.Services.AddHttpClient("Notify", client => { client.Timeout = TimeSpan.FromSeconds(30); });

    // 添加平台特定工具服务（email, notify, time）
    builder.Services.AddPlatformTools(builder.Configuration);

    var app = builder.Build();

    // 启用 Swagger
    // app.UseSwagger();
    // app.UseSwaggerUI();

    // 映射控制器路由
    app.MapControllers();

    app.MapGet("/", () => Results.Json(new
    {
        name = "Kode.Agent WebApi Assistant",
        openai_compatible = true,
        endpoints = new[]
        {
            "POST /v1/chat/completions",
            "GET  /healthz"
        }
    }));

    app.MapGet("/healthz", () => Results.Ok(new {ok = true}));

    app.MapPost("/v1/chat/completions",
        async (HttpContext httpContext, OpenAiChatCompletionRequest request, AssistantService service) =>
            await service.HandleChatCompletionsAsync(httpContext, request));

    Log.Information("Kode.Agent WebApi Assistant started successfully");
    Log.Information("Available endpoints: http://localhost:5123/v1/chat/completions");
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