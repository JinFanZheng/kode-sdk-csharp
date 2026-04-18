using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Tools.Builtin.FileSystem;
using Kode.Agent.Tools.Builtin.History;
using Kode.Agent.Tools.Builtin.Shell;
using Kode.Agent.Tools.Builtin.Skills;
using Kode.Agent.Tools.Builtin.Todo;
using Microsoft.Extensions.DependencyInjection;

namespace Kode.Agent.Tools.Builtin;

/// <summary>
/// Extension methods for registering built-in tools.
/// </summary>
public static class ServiceCollectionExtensions
{
    // Tools registered across all three entry points (DI, registry, toolkit).
    private static readonly Type[] CoreBuiltinToolTypes =
    [
        // File system
        typeof(FsReadTool),
        typeof(FsWriteTool),
        typeof(FsGlobTool),
        typeof(FsGrepTool),
        typeof(FsEditTool),
        typeof(FsRmTool),
        typeof(FsListTool),
        // Shell
        typeof(BashRunTool),
        typeof(BashKillTool),
        typeof(BashLogsTool),
        // Todo
        typeof(TodoReadTool),
        typeof(TodoWriteTool),
    ];

    // Extra tools registered only via IToolRegistry (Skills, History).
    private static readonly Type[] ExtraRegistryToolTypes =
    [
        typeof(SkillListTool),
        typeof(SkillActivateTool),
        typeof(SkillResourceTool),
        typeof(HistorySearchTool),
    ];

    /// <summary>
    /// Adds all built-in tools to the service collection.
    /// </summary>
    public static IServiceCollection AddBuiltinTools(this IServiceCollection services)
    {
        foreach (var toolType in CoreBuiltinToolTypes)
            services.AddSingleton(typeof(ITool), toolType);
        return services;
    }

    /// <summary>
    /// Registers all built-in tools with the tool registry.
    /// </summary>
    public static IToolRegistry RegisterBuiltinTools(this IToolRegistry registry)
    {
        foreach (var toolType in CoreBuiltinToolTypes)
            registry.Register((ITool)Activator.CreateInstance(toolType)!);
        foreach (var toolType in ExtraRegistryToolTypes)
            registry.Register((ITool)Activator.CreateInstance(toolType)!);
        return registry;
    }

    internal static IReadOnlyList<Type> CoreToolTypes => CoreBuiltinToolTypes;
}

/// <summary>
/// Toolkit containing all built-in tools.
/// </summary>
public sealed class BuiltinToolKit : ToolKit
{
    protected override void RegisterTools()
    {
        foreach (var toolType in ServiceCollectionExtensions.CoreToolTypes)
            RegisterTool((ITool)Activator.CreateInstance(toolType)!);
    }
}
