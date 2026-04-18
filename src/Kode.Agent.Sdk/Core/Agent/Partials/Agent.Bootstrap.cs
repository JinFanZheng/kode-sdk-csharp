using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Events;
using Kode.Agent.Sdk.Core.Files;
using Kode.Agent.Sdk.Core.Todo;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.Sdk.Core.Agent;

// One-shot setup paths invoked during CreateAsync / ResumeFromStoreAsync after the sandbox
// exists: wire the FilePool, mount TodoService/TodoManager, render each tool's GetPromptAsync
// output into the system prompt, and respond to FilePool.OnChange by enqueuing a reminder.
public sealed partial class Agent
{
    private void InitializeToolServices()
    {
        if (_sandbox == null) return;
        if (_filePool != null) return;

        var watch = _config.SandboxOptions?.WatchFiles ?? true;
        _filePool = new FilePool(
            _sandbox,
            new FilePoolOptions
            {
                Watch = watch,
                OnChange = e => HandleExternalFileChange(e.Path, e.Mtime)
            },
            _dependencies.LoggerFactory?.CreateLogger<FilePool>());
        _toolServices = new ToolServices(_filePool);
    }

    private async Task InitializeTodoAsync(CancellationToken cancellationToken)
    {
        if (_todoManager != null) return;
        if (_config.Todo?.Enabled != true) return;

        _todoService = new TodoService(_dependencies.Store, AgentId, _dependencies.LoggerFactory?.CreateLogger<TodoService>());
        await _todoService.LoadAsync(cancellationToken);

        _todoManager = new TodoManager(
            _todoService,
            _config.Todo,
            _eventBus,
            (content, category, ct) => RemindAsync(content, category, skipStandardEnding: false, cancellationToken: ct),
            _dependencies.LoggerFactory?.CreateLogger<TodoManager>());

        _todoManager.HandleStartup(cancellationToken);
    }

    private async Task InitializeToolManualAsync(CancellationToken cancellationToken)
    {
        if (_sandbox == null) return;
        if (_tools.Count == 0) return;

        var prompts = new List<(string Name, string Prompt)>();
        foreach (var tool in _tools)
        {
            var context = new ToolContext
            {
                AgentId = AgentId,
                CallId = "manual",
                Sandbox = _sandbox,
                Agent = this,
                Services = _toolServices,
                CancellationToken = cancellationToken
            };

            var prompt = await tool.GetPromptAsync(context);
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                prompts.Add((tool.Name, prompt.Trim()));
            }
        }

        if (prompts.Count == 0) return;

        var manual = RenderToolManual(prompts);
        _systemPrompt = (_systemPrompt ?? string.Empty) + manual;

        _eventBus.EmitMonitor(new ToolManualUpdatedEvent
        {
            Type = "tool_manual_updated",
            Tools = prompts.Select(p => p.Name).ToList(),
            Timestamp = NowMs()
        });
    }

    private static string RenderToolManual(IReadOnlyList<(string Name, string Prompt)> prompts)
    {
        var parts = new List<string>
        {
            "\n<tool_manual>\n"
        };
        foreach (var (name, prompt) in prompts)
        {
            parts.Add($"<tool name=\"{name}\">\n{prompt}\n</tool>\n");
        }
        parts.Add("</tool_manual>\n");
        return string.Join("\n", parts);
    }

    private void HandleExternalFileChange(string path, long mtime)
    {
        if (_sandbox == null) return;

        var rel = Path.GetRelativePath(_sandbox.WorkingDirectory, path);
        _eventBus.EmitMonitor(new FileChangedEvent
        {
            Type = "file_changed",
            Path = rel,
            Mtime = mtime
        });

        // Only enqueue — do NOT call FlushAsync here. FileSystemWatcher callbacks run on a
        // thread-pool thread; flushing would call _messages.Add() on that thread while the
        // agent's processing loop may be enumerating _messages (e.g. in ContextManager.Analyze),
        // causing "Collection was modified; enumeration operation may not execute."
        // The queue is drained at the start of every StepAsync (line ~1079), so the reminder
        // will be delivered before the next model call without any cross-thread mutation.
        var reminder = $"检测到外部修改：{rel}。请重新使用 fs_read 确认文件内容，并在必要时向用户同步。";
        // DedupKey collapses duplicate pending reminders for the same file while the
        // previous one is still queued. Filesystem watchers can fire many times for a
        // single editor save (autosave, SCM writes, mtime flaps), and without this the
        // conversation accumulates 10+ identical system-reminders back-to-back, wasting
        // context and confusing the model.
        _messageQueue.Send(reminder, new SendOptions
        {
            Kind = PendingKind.Reminder,
            Reminder = new ReminderOptions { Category = "file", SkipStandardEnding = false },
            DedupKey = $"file-change:{rel}"
        });
    }

    private sealed class ToolServices(FilePool filePool) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(FilePool)) return filePool;
            if (serviceType == typeof(IFilePool)) return filePool;
            return null;
        }
    }
}
