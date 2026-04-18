using Kode.Agent.Sdk.Core.Context;
using System.Text.Json;

namespace Kode.Agent.Sdk.Core.Agent;

// Template → AgentConfig merge helpers. Pure static, no agent state touched:
// these run during CreateAsync before the instance exists in its final shape.
public sealed partial class Agent
{
    private static AgentConfig ApplyTemplateConfig(AgentConfig config, AgentDependencies dependencies)
    {
        if (dependencies.TemplateRegistry == null) return config;
        if (string.IsNullOrWhiteSpace(config.TemplateId)) return config;
        if (!dependencies.TemplateRegistry.TryGet(config.TemplateId, out var template) || template == null) return config;

        var merged = config;

        if (string.IsNullOrWhiteSpace(merged.SystemPrompt))
        {
            merged = merged with { SystemPrompt = template.SystemPrompt };
        }

        if (string.IsNullOrWhiteSpace(merged.Model) && !string.IsNullOrWhiteSpace(template.Model))
        {
            merged = merged with { Model = template.Model };
        }

        if (merged.Tools == null)
        {
            if (template.Tools.AllowAll)
            {
                merged = merged with { Tools = ["*"] };
            }
            else if (template.Tools.AllowedTools != null)
            {
                merged = merged with { Tools = template.Tools.AllowedTools.ToArray() };
            }
        }

        if (merged.Permissions == null && template.Permission != null)
        {
            merged = merged with
            {
                Permissions = new Kode.Agent.Sdk.Core.Types.PermissionConfig
                {
                    Mode = template.Permission.Mode,
                    AllowTools = template.Permission.AllowTools,
                    RequireApprovalTools = template.Permission.RequireApprovalTools,
                    DenyTools = template.Permission.DenyTools,
                    Metadata = template.Permission.Metadata?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase)
                }
            };
        }

        if (merged.SandboxOptions == null && template.Sandbox != null)
        {
            merged = merged with { SandboxOptions = ConvertSandboxOptions(template.Sandbox) };
        }

        if (template.Runtime?.Metadata != null)
        {
            if (template.Runtime.Metadata.TryGetValue("maxToolConcurrency", out var maxConc) &&
                maxConc.ValueKind == System.Text.Json.JsonValueKind.Number &&
                maxConc.TryGetInt32(out var value) &&
                value > 0 &&
                merged.MaxToolConcurrency == 3)
            {
                merged = merged with { MaxToolConcurrency = value };
            }

            if (template.Runtime.Metadata.TryGetValue("toolTimeoutMs", out var timeoutMs) &&
                timeoutMs.ValueKind == System.Text.Json.JsonValueKind.Number &&
                timeoutMs.TryGetInt32(out var ms) &&
                ms > 0 &&
                merged.ToolTimeout == TimeSpan.FromMinutes(10))
            {
                merged = merged with { ToolTimeout = TimeSpan.FromMilliseconds(ms) };
            }

            if (merged.Context == null &&
                template.Runtime.Metadata.TryGetValue("context", out var contextMeta) &&
                contextMeta.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                try
                {
                    var ctx = contextMeta.Deserialize<ContextManagerOptions>();
                    if (ctx != null)
                    {
                        merged = merged with { Context = ctx };
                    }
                }
                catch
                {
                    // ignore invalid metadata.context
                }
            }
        }

        if (merged.ExposeThinking is null && template.Runtime != null)
        {
            merged = merged with { ExposeThinking = template.Runtime.ExposeThinking };
        }

        if (merged.SubAgents == null && template.Runtime?.SubAgents != null)
        {
            merged = merged with { SubAgents = template.Runtime.SubAgents };
        }

        if (merged.Todo == null && template.Runtime?.Todo != null)
        {
            merged = merged with { Todo = template.Runtime.Todo };
        }

        return merged;
    }

    private static SandboxOptions ConvertSandboxOptions(IReadOnlyDictionary<string, System.Text.Json.JsonElement> sandbox)
    {
        var options = new SandboxOptions();

        if (sandbox.TryGetValue("workDir", out var workDir) && workDir.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            options = options with { WorkingDirectory = workDir.GetString() };
        }

        if (sandbox.TryGetValue("enforceBoundary", out var enforceBoundary) &&
            (enforceBoundary.ValueKind == System.Text.Json.JsonValueKind.True || enforceBoundary.ValueKind == System.Text.Json.JsonValueKind.False))
        {
            options = options with { EnforceBoundary = enforceBoundary.GetBoolean() };
        }

        if (sandbox.TryGetValue("allowPaths", out var allowPaths) && allowPaths.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var p in allowPaths.EnumerateArray())
            {
                if (p.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = p.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        list.Add(s);
                    }
                }
            }
            options = options with { AllowPaths = list };
        }

        if (sandbox.TryGetValue("watchFiles", out var watchFiles) &&
            (watchFiles.ValueKind == System.Text.Json.JsonValueKind.True || watchFiles.ValueKind == System.Text.Json.JsonValueKind.False))
        {
            options = options with { WatchFiles = watchFiles.GetBoolean() };
        }

        if (sandbox.TryGetValue("useDocker", out var useDocker) &&
            (useDocker.ValueKind == System.Text.Json.JsonValueKind.True || useDocker.ValueKind == System.Text.Json.JsonValueKind.False))
        {
            options = options with { UseDocker = useDocker.GetBoolean() };
        }

        if (sandbox.TryGetValue("dockerImage", out var dockerImage) && dockerImage.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            options = options with { DockerImage = dockerImage.GetString() };
        }

        if (sandbox.TryGetValue("dockerNetworkMode", out var dockerNetworkMode) && dockerNetworkMode.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            options = options with { DockerNetworkMode = dockerNetworkMode.GetString() };
        }

        if (sandbox.TryGetValue("sandboxStateDirectory", out var sandboxStateDirectory) && sandboxStateDirectory.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            options = options with { SandboxStateDirectory = sandboxStateDirectory.GetString() };
        }

        return options;
    }

    private static Kode.Agent.Sdk.Core.Types.PermissionConfig ConvertPermissionConfig(Kode.Agent.Sdk.Core.Templates.PermissionConfig config)
    {
        return new Kode.Agent.Sdk.Core.Types.PermissionConfig
        {
            Mode = config.Mode,
            AllowTools = config.AllowTools,
            RequireApprovalTools = config.RequireApprovalTools,
            DenyTools = config.DenyTools,
            Metadata = config.Metadata?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase)
        };
    }
}
