using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json.Serialization;

namespace KodaClaw.Cli.Commands;

public static class JobCommands
{
    public static Command Build()
    {
        var jobCmd = new Command("job", "Job management");
        jobCmd.AddCommand(BuildListCommand());
        jobCmd.AddCommand(BuildStatusCommand());
        jobCmd.AddCommand(BuildResultCommand());
        jobCmd.AddCommand(BuildCreateCommand());
        jobCmd.AddCommand(BuildCancelCommand());
        jobCmd.AddCommand(BuildUpdateCommand());
        jobCmd.AddCommand(BuildDeleteCommand());
        jobCmd.AddCommand(BuildRescheduleCommand());
        jobCmd.AddCommand(BuildPauseCommand());
        jobCmd.AddCommand(BuildResumeCommand());
        return jobCmd;
    }

    private static Command BuildListCommand()
    {
        var statusOpt = new Option<string?>("--status", "Filter by status: pending | cancelled");
        var jsonOpt = new Option<bool>("--json", "Output as JSON");
        var cmd = new Command("list", "List all jobs") { statusOpt, jsonOpt };

        cmd.SetHandler(async (string? status, bool json) =>
        {
            using var client = new HttpGatewayClient(HttpGatewayClient.ResolveGatewayUrl(), HttpGatewayClient.ResolveToken());
            try
            {
                var query = status != null ? $"?status={status}" : "";
                var result = await client.GetAsync<JobListResponse>($"api/jobs{query}");
                var items = result?.Items ?? [];

                if (json)
                {
                    OutputFormatter.WriteJson(items.Select(j => new
                    {
                        id = j.Id,
                        name = j.Name,
                        type = j.Type,
                        status = j.Status,
                        nextRunAt = j.NextRunAt,
                        runCount = j.RunCount,
                    }));
                }
                else
                {
                    if (items.Count == 0) { Console.WriteLine("No jobs found."); return; }
                    OutputFormatter.WriteTable(
                        items.Select(j => new[]
                        {
                            j.Id, j.Name, j.Type, j.Status,
                            j.NextRunAt ?? "-",
                            j.RunCount.ToString(),
                        }),
                        ["ID", "Name", "Type", "Status", "NextRun", "Runs"]);
                }
            }
            catch (HttpRequestException ex) { OutputFormatter.WriteError($"Gateway error: {ex.Message}"); Environment.Exit(1); }
        }, statusOpt, jsonOpt);

        return cmd;
    }

    private static Command BuildStatusCommand()
    {
        var idArg = new Argument<string>("id", "Job ID");
        var jsonOpt = new Option<bool>("--json", "Output as JSON");
        var cmd = new Command("status", "Show full job details") { idArg, jsonOpt };

        cmd.SetHandler(async (string id, bool json) =>
        {
            using var client = new HttpGatewayClient(HttpGatewayClient.ResolveGatewayUrl(), HttpGatewayClient.ResolveToken());
            try
            {
                var result = await client.GetAsync<JobStatusResponse>($"api/jobs/{id}");
                if (json)
                {
                    OutputFormatter.WriteJson(new
                    {
                        id = result!.Id,
                        name = result.Name,
                        type = result.Type,
                        prompt = result.Prompt,
                        status = result.Status,
                        cron = result.Cron,
                        nextRunAt = result.NextRunAt,
                        channels = result.Channels,
                        deliveryMode = result.DeliveryMode,
                        timeoutMinutes = result.TimeoutMinutes,
                        maxRetries = result.MaxRetries,
                        runCount = result.RunCount,
                        createdAt = result.CreatedAt,
                        updatedAt = result.UpdatedAt,
                    });
                }
                else
                {
                    Console.WriteLine($"ID:         {result!.Id}");
                    Console.WriteLine($"Name:       {result.Name}");
                    Console.WriteLine($"Type:       {result.Type}");
                    Console.WriteLine($"Status:     {result.Status}");
                    Console.WriteLine($"Prompt:     {Truncate(result.Prompt, 80)}");
                    Console.WriteLine($"Cron:       {result.Cron ?? "-"}");
                    Console.WriteLine($"NextRun:    {result.NextRunAt ?? "-"}");
                    Console.WriteLine($"Channels:   {(result.Channels.Count > 0 ? string.Join(", ", result.Channels) : "-")}");
                    Console.WriteLine($"Delivery:   {result.DeliveryMode}");
                    Console.WriteLine($"Timeout:    {result.TimeoutMinutes}min");
                    Console.WriteLine($"MaxRetries: {result.MaxRetries}");
                    Console.WriteLine($"Runs:       {result.RunCount}");
                    Console.WriteLine($"Created:    {result.CreatedAt}");
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            { OutputFormatter.WriteError($"Job '{id}' not found"); Environment.Exit(3); }
            catch (HttpRequestException ex)
            { OutputFormatter.WriteError($"Gateway error: {ex.Message}"); Environment.Exit(1); }
        }, idArg, jsonOpt);

        return cmd;
    }

    private static Command BuildResultCommand()
    {
        var idArg = new Argument<string>("id", "Job ID");
        var runIdOpt = new Option<string?>("--run-id", "Specific run ID");
        var jsonOpt = new Option<bool>("--json", "Output as JSON");
        var cmd = new Command("result", "Show job run result") { idArg, runIdOpt, jsonOpt };

        cmd.SetHandler(async (string id, string? runId, bool json) =>
        {
            using var client = new HttpGatewayClient(HttpGatewayClient.ResolveGatewayUrl(), HttpGatewayClient.ResolveToken());
            try
            {
                var query = runId != null ? $"?runId={runId}" : "";
                var result = await client.GetAsync<JobResultResponse>($"api/jobs/{id}/result{query}");
                if (json)
                    OutputFormatter.WriteJson(new { jobId = id, hasResult = result!.HasResult, message = result.Message });
                else
                    Console.WriteLine(result!.HasResult ? $"Result: {result.Result}" : result.Message ?? "No result yet.");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            { OutputFormatter.WriteError($"Job '{id}' not found"); Environment.Exit(3); }
            catch (HttpRequestException ex)
            { OutputFormatter.WriteError($"Gateway error: {ex.Message}"); Environment.Exit(1); }
        }, idArg, runIdOpt, jsonOpt);

        return cmd;
    }

    private static Command BuildCancelCommand()
    {
        var idArg = new Argument<string>("id", "Job ID");
        var jsonOpt = new Option<bool>("--json", "Output as JSON");
        var cmd = new Command("cancel", "Cancel a job") { idArg, jsonOpt };

        cmd.SetHandler(async (string id, bool json) =>
        {
            using var client = new HttpGatewayClient(HttpGatewayClient.ResolveGatewayUrl(), HttpGatewayClient.ResolveToken());
            try
            {
                var result = await client.PostAsync<JobCancelResponse>($"api/jobs/{id}/cancel");
                if (json)
                    OutputFormatter.WriteJson(new { jobId = id, previousStatus = result!.PreviousStatus, newStatus = result.NewStatus });
                else
                    OutputFormatter.WriteSuccess($"Job '{id}' cancelled (was {result!.PreviousStatus})");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            { OutputFormatter.WriteError($"Job '{id}' not found"); Environment.Exit(3); }
            catch (HttpRequestException ex)
            { OutputFormatter.WriteError($"Gateway error: {ex.Message}"); Environment.Exit(1); }
        }, idArg, jsonOpt);

        return cmd;
    }

    private static Command BuildUpdateCommand()
    {
        var idArg = new Argument<string>("id", "Job ID");
        var promptOpt = new Option<string?>("--prompt", "New prompt text");
        var cronOpt = new Option<string?>("--cron", "New cron expression");
        var timeoutOpt = new Option<int?>("--timeout", "New timeout in minutes");
        var channelsOpt = new Option<string?>("--channels", "Comma-separated channel BindingIds");
        var deliveryModeOpt = new Option<string?>("--delivery-mode", "auto | approval | none");
        var jsonOpt = new Option<bool>("--json", "Output as JSON");
        var cmd = new Command("update", "Update a job") { idArg, promptOpt, cronOpt, timeoutOpt, channelsOpt, deliveryModeOpt, jsonOpt };

        cmd.SetHandler(async (string id, string? prompt, string? cron, int? timeout, string? channels, string? deliveryMode, bool json) =>
        {
            using var client = new HttpGatewayClient(HttpGatewayClient.ResolveGatewayUrl(), HttpGatewayClient.ResolveToken());
            try
            {
                var body = new Dictionary<string, object?>();
                if (prompt != null) body["prompt"] = prompt;
                if (cron != null) body["cron"] = cron;
                if (timeout.HasValue) body["timeoutMinutes"] = timeout.Value;
                if (channels != null) body["channels"] = channels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (deliveryMode != null) body["deliveryMode"] = deliveryMode;

                var result = await client.PostAsync<JobUpdateResponse>($"api/jobs/{id}/update", body);
                if (json)
                    OutputFormatter.WriteJson(new { jobId = id, updatedFields = result!.UpdatedFields });
                else
                    OutputFormatter.WriteSuccess($"Job '{id}' updated (fields: {string.Join(", ", result!.UpdatedFields)})");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            { OutputFormatter.WriteError($"Job '{id}' not found"); Environment.Exit(3); }
            catch (HttpRequestException ex)
            { OutputFormatter.WriteError($"Gateway error: {ex.Message}"); Environment.Exit(1); }
        }, idArg, promptOpt, cronOpt, timeoutOpt, channelsOpt, deliveryModeOpt, jsonOpt);

        return cmd;
    }

    private static Command BuildCreateCommand()
    {
        var nameOpt = new Option<string>("--name", "Job name") { IsRequired = true };
        var typeOpt = new Option<string>("--type", "Job type: one-shot | recurring | self-driven") { IsRequired = true };
        var promptOpt = new Option<string>("--prompt", "Job prompt") { IsRequired = true };
        var cronOpt = new Option<string?>("--cron", "Cron expression (required for recurring)");
        var nextRunAtOpt = new Option<string?>("--next-run-at", "Next run time ISO 8601 (required for one-shot/self-driven)");
        var fallbackIntervalOpt = new Option<int?>("--fallback-interval", "Fallback interval in minutes (self-driven only)");
        var channelsOpt = new Option<string?>("--channels", "Comma-separated channel BindingIds");
        var deliveryModeOpt = new Option<string?>("--delivery-mode", "auto | approval | none");
        var timeoutOpt = new Option<int?>("--timeout", "Timeout in minutes (1-1440)");
        var maxRetriesOpt = new Option<int?>("--max-retries", "Max retries (0-5)");
        var jsonOpt = new Option<bool>("--json", "Output as JSON");
        var cmd = new Command("create", "Create a new job") { nameOpt, typeOpt, promptOpt, cronOpt, nextRunAtOpt, fallbackIntervalOpt, channelsOpt, deliveryModeOpt, timeoutOpt, maxRetriesOpt, jsonOpt };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var name = ctx.ParseResult.GetValueForOption(nameOpt)!;
            var type = ctx.ParseResult.GetValueForOption(typeOpt)!;
            var prompt = ctx.ParseResult.GetValueForOption(promptOpt)!;
            var cron = ctx.ParseResult.GetValueForOption(cronOpt);
            var nextRunAt = ctx.ParseResult.GetValueForOption(nextRunAtOpt);
            var fallbackInterval = ctx.ParseResult.GetValueForOption(fallbackIntervalOpt);
            var channels = ctx.ParseResult.GetValueForOption(channelsOpt);
            var deliveryMode = ctx.ParseResult.GetValueForOption(deliveryModeOpt);
            var timeout = ctx.ParseResult.GetValueForOption(timeoutOpt);
            var maxRetries = ctx.ParseResult.GetValueForOption(maxRetriesOpt);
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);

            using var client = new HttpGatewayClient(HttpGatewayClient.ResolveGatewayUrl(), HttpGatewayClient.ResolveToken());
            try
            {
                var body = new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["type"] = type,
                    ["prompt"] = prompt,
                };
                if (cron != null) body["cron"] = cron;
                if (nextRunAt != null) body["nextRunAt"] = nextRunAt;
                if (fallbackInterval.HasValue) body["fallbackIntervalMinutes"] = fallbackInterval.Value;
                if (channels != null) body["channels"] = channels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (deliveryMode != null) body["deliveryMode"] = deliveryMode;
                if (timeout.HasValue) body["timeoutMinutes"] = timeout.Value;
                if (maxRetries.HasValue) body["maxRetries"] = maxRetries.Value;

                var result = await client.PostAsync<JobCreateResponse>("api/jobs/create", body);
                if (json)
                    OutputFormatter.WriteJson(new { jobId = result!.Id, name = result.Name, type = result.Type, status = result.Status });
                else
                    OutputFormatter.WriteSuccess($"Job '{result!.Id}' created (name: {result.Name}, type: {result.Type})");
            }
            catch (HttpRequestException ex)
            { OutputFormatter.WriteError($"Gateway error: {ex.Message}"); Environment.Exit(1); }
        });

        return cmd;
    }

    private static Command BuildDeleteCommand()
    {
        var idArg = new Argument<string>("id", "Job ID");
        var jsonOpt = new Option<bool>("--json", "Output as JSON");
        var cmd = new Command("delete", "Delete a cancelled job") { idArg, jsonOpt };

        cmd.SetHandler(async (string id, bool json) =>
        {
            using var client = new HttpGatewayClient(HttpGatewayClient.ResolveGatewayUrl(), HttpGatewayClient.ResolveToken());
            try
            {
                var result = await client.PostAsync<JobDeleteResponse>($"api/jobs/{id}/delete");
                if (json)
                    OutputFormatter.WriteJson(new { jobId = id, deleted = result!.Deleted });
                else
                    OutputFormatter.WriteSuccess($"Job '{id}' deleted");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            { OutputFormatter.WriteError($"Job '{id}' not found"); Environment.Exit(3); }
            catch (HttpRequestException ex)
            { OutputFormatter.WriteError($"Gateway error: {ex.Message}"); Environment.Exit(1); }
        }, idArg, jsonOpt);

        return cmd;
    }

    private static Command BuildRescheduleCommand()
    {
        var idArg = new Argument<string>("id", "Job ID");
        var nextRunAtOpt = new Option<string>("--next-run-at", "Next run time ISO 8601") { IsRequired = true };
        var jsonOpt = new Option<bool>("--json", "Output as JSON");
        var cmd = new Command("reschedule", "Reschedule a self-driven job") { idArg, nextRunAtOpt, jsonOpt };

        cmd.SetHandler(async (string id, string nextRunAt, bool json) =>
        {
            using var client = new HttpGatewayClient(HttpGatewayClient.ResolveGatewayUrl(), HttpGatewayClient.ResolveToken());
            try
            {
                var body = new Dictionary<string, object?> { ["nextRunAt"] = nextRunAt };
                var result = await client.PostAsync<JobRescheduleResponse>($"api/jobs/{id}/reschedule", body);
                if (json)
                    OutputFormatter.WriteJson(new { jobId = id, nextRunAt = result!.NextRunAt });
                else
                    OutputFormatter.WriteSuccess($"Job '{id}' rescheduled to {result!.NextRunAt}");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            { OutputFormatter.WriteError($"Job '{id}' not found"); Environment.Exit(3); }
            catch (HttpRequestException ex)
            { OutputFormatter.WriteError($"Gateway error: {ex.Message}"); Environment.Exit(1); }
        }, idArg, nextRunAtOpt, jsonOpt);

        return cmd;
    }

    private static Command BuildPauseCommand()
    {
        var idArg = new Argument<string>("id", "Job ID");
        var jsonOpt = new Option<bool>("--json", "Output as JSON");
        var cmd = new Command("pause", "Pause a job") { idArg, jsonOpt };

        cmd.SetHandler(async (string id, bool json) =>
        {
            using var client = new HttpGatewayClient(HttpGatewayClient.ResolveGatewayUrl(), HttpGatewayClient.ResolveToken());
            try
            {
                var result = await client.PostAsync<JobPauseResumeResponse>($"api/jobs/{id}/pause");
                if (json)
                    OutputFormatter.WriteJson(new { jobId = id, previousStatus = result!.PreviousStatus, newStatus = result.NewStatus });
                else
                    OutputFormatter.WriteSuccess($"Job '{id}' paused (was {result!.PreviousStatus})");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            { OutputFormatter.WriteError($"Job '{id}' not found"); Environment.Exit(3); }
            catch (HttpRequestException ex)
            { OutputFormatter.WriteError($"Gateway error: {ex.Message}"); Environment.Exit(1); }
        }, idArg, jsonOpt);

        return cmd;
    }

    private static Command BuildResumeCommand()
    {
        var idArg = new Argument<string>("id", "Job ID");
        var jsonOpt = new Option<bool>("--json", "Output as JSON");
        var cmd = new Command("resume", "Resume a paused job") { idArg, jsonOpt };

        cmd.SetHandler(async (string id, bool json) =>
        {
            using var client = new HttpGatewayClient(HttpGatewayClient.ResolveGatewayUrl(), HttpGatewayClient.ResolveToken());
            try
            {
                var result = await client.PostAsync<JobPauseResumeResponse>($"api/jobs/{id}/resume");
                if (json)
                    OutputFormatter.WriteJson(new { jobId = id, previousStatus = result!.PreviousStatus, newStatus = result.NewStatus });
                else
                    OutputFormatter.WriteSuccess($"Job '{id}' resumed (was {result!.PreviousStatus})");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            { OutputFormatter.WriteError($"Job '{id}' not found"); Environment.Exit(3); }
            catch (HttpRequestException ex)
            { OutputFormatter.WriteError($"Gateway error: {ex.Message}"); Environment.Exit(1); }
        }, idArg, jsonOpt);

        return cmd;
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private record JobListResponse
    {
        [JsonPropertyName("items")] public IReadOnlyList<JobItem> Items { get; init; } = [];
    }

    private record JobItem
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("status")] public string Status { get; init; } = "";
        [JsonPropertyName("nextRunAt")] public string? NextRunAt { get; init; }
        [JsonPropertyName("runCount")] public int RunCount { get; init; }
    }

    private record JobStatusResponse
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("prompt")] public string Prompt { get; init; } = "";
        [JsonPropertyName("status")] public string Status { get; init; } = "";
        [JsonPropertyName("cron")] public string? Cron { get; init; }
        [JsonPropertyName("nextRunAt")] public string? NextRunAt { get; init; }
        [JsonPropertyName("channels")] public IReadOnlyList<string> Channels { get; init; } = [];
        [JsonPropertyName("deliveryMode")] public string DeliveryMode { get; init; } = "";
        [JsonPropertyName("timeoutMinutes")] public int TimeoutMinutes { get; init; }
        [JsonPropertyName("maxRetries")] public int MaxRetries { get; init; }
        [JsonPropertyName("runCount")] public int RunCount { get; init; }
        [JsonPropertyName("createdAt")] public string CreatedAt { get; init; } = "";
        [JsonPropertyName("updatedAt")] public string UpdatedAt { get; init; } = "";
    }

    private record JobResultResponse
    {
        [JsonPropertyName("jobId")] public string JobId { get; init; } = "";
        [JsonPropertyName("hasResult")] public bool HasResult { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
        [JsonPropertyName("result")] public string? Result { get; init; }
    }

    private record JobCancelResponse
    {
        [JsonPropertyName("previousStatus")] public string PreviousStatus { get; init; } = "";
        [JsonPropertyName("newStatus")] public string NewStatus { get; init; } = "";
    }

    private record JobUpdateResponse
    {
        [JsonPropertyName("updatedFields")] public IReadOnlyList<string> UpdatedFields { get; init; } = [];
    }

    private record JobCreateResponse
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("status")] public string Status { get; init; } = "";
    }

    private record JobDeleteResponse
    {
        [JsonPropertyName("deleted")] public bool Deleted { get; init; }
    }

    private record JobRescheduleResponse
    {
        [JsonPropertyName("jobId")] public string JobId { get; init; } = "";
        [JsonPropertyName("nextRunAt")] public string NextRunAt { get; init; } = "";
    }

    private record JobPauseResumeResponse
    {
        [JsonPropertyName("jobId")] public string JobId { get; init; } = "";
        [JsonPropertyName("previousStatus")] public string PreviousStatus { get; init; } = "";
        [JsonPropertyName("newStatus")] public string NewStatus { get; init; } = "";
    }
}
