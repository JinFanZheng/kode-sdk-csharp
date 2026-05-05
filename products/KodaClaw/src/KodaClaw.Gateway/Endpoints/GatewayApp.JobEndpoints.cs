using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Jobs;
using KodaClaw.Contracts.System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapJobEndpoints(WebApplication app)
    {
        var jobs = app.MapGroup("/api/jobs");

        // GET /api/jobs
        jobs.MapGet(string.Empty, async (
            HttpContext context,
            IConfiguration configuration,
            IJobRepository jobRepository,
            IDiagnosticsService diagnosticsService,
            [FromQuery] string? status,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            JobStatus? statusFilter = null;
            if (status != null)
            {
                if (status.Equals("pending", StringComparison.OrdinalIgnoreCase))
                    statusFilter = JobStatus.Pending;
                else if (status.Equals("running", StringComparison.OrdinalIgnoreCase))
                    statusFilter = JobStatus.Running;
                else if (status.Equals("completed", StringComparison.OrdinalIgnoreCase))
                    statusFilter = JobStatus.Completed;
                else if (status.Equals("failed", StringComparison.OrdinalIgnoreCase))
                    statusFilter = JobStatus.Failed;
                else if (status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
                    statusFilter = JobStatus.Cancelled;
                else if (status.Equals("paused", StringComparison.OrdinalIgnoreCase))
                    statusFilter = JobStatus.Paused;
                else
                {
                    RecordDiagnosticEvent(
                        diagnosticsService, context,
                        source: "gateway.jobs",
                        eventType: "gateway.jobs.invalid_request",
                        level: "warning",
                        message: "Job list received an invalid status filter.");
                    return Results.BadRequest(new ErrorResponse(
                        Code: "validation.job_status_invalid",
                        Message: "Job status filter is invalid."));
                }
            }

            var items = await jobRepository.ListAsync(statusFilter, cancellationToken);
            var responseItems = items.Select(j => new JobListItem(
                j.Id,
                j.Name,
                j.Type.ToString().ToLowerInvariant(),
                j.Status.ToString().ToLowerInvariant(),
                j.NextRunAt,
                j.RunCount)).ToArray();

            return Results.Ok(new JobListResponse(responseItems));
        });

        // GET /api/jobs/{id}
        jobs.MapGet("/{id}", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IJobRepository jobRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var job = await jobRepository.GetByIdAsync(id, cancellationToken);
            if (job is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService, context,
                    source: "gateway.jobs",
                    eventType: "gateway.jobs.not_found",
                    level: "warning",
                    message: "Requested job was not found.",
                    attributes: new Dictionary<string, string?> { ["jobId"] = id });
                return Results.NotFound(new ErrorResponse(
                    Code: "job.not_found",
                    Message: "Job was not found."));
            }

            var response = new JobStatusResponse(
                job.Id,
                job.Name,
                job.Type.ToString().ToLowerInvariant(),
                job.Prompt,
                job.Status.ToString().ToLowerInvariant(),
                job.Cron,
                job.NextRunAt,
                job.Channels,
                job.DeliveryMode.ToString().ToLowerInvariant(),
                job.TimeoutMinutes,
                job.MaxRetries,
                job.RunCount,
                job.CreatedAt,
                job.UpdatedAt);

            return Results.Ok(response);
        });

        // GET /api/jobs/{id}/result
        jobs.MapGet("/{id}/result", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IJobRepository jobRepository,
            IDiagnosticsService diagnosticsService,
            [FromQuery] string? runId,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var job = await jobRepository.GetByIdAsync(id, cancellationToken);
            if (job is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService, context,
                    source: "gateway.jobs",
                    eventType: "gateway.jobs.not_found",
                    level: "warning",
                    message: "Job result requested for missing job.",
                    attributes: new Dictionary<string, string?> { ["jobId"] = id });
                return Results.NotFound(new ErrorResponse(
                    Code: "job.not_found",
                    Message: "Job was not found."));
            }

            JobRunRecord? run = null;
            if (runId != null)
                run = job.Runs.FirstOrDefault(r => r.RunId == runId);
            else
                run = job.Runs.LastOrDefault(r => r.CompletedAt != null);

            if (run?.Result != null)
                return Results.Ok(new JobResultResponse(job.Id, true, null, run.Result));

            var message = run == null ? "No runs yet." : "Run not completed.";
            return Results.Ok(new JobResultResponse(job.Id, false, message, null));
        });

        // POST /api/jobs/create
        jobs.MapPost("/create", async (
            HttpContext context,
            CreateJobRequest request,
            IConfiguration configuration,
            IJobRepository jobRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (!TryParseJobType(request.Type, out var jobType))
            {
                RecordDiagnosticEvent(
                    diagnosticsService, context,
                    source: "gateway.jobs",
                    eventType: "gateway.jobs.invalid_request",
                    level: "warning",
                    message: "Job create received an invalid type.");
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.job_type_invalid",
                    Message: "Job type must be one-shot, recurring, or self-driven."));
            }

            DateTimeOffset? nextRunAt = null;
            if (request.NextRunAt != null)
            {
                if (!DateTimeOffset.TryParse(request.NextRunAt, out var parsed))
                {
                    return Results.BadRequest(new ErrorResponse(
                        Code: "validation.next_run_at_invalid",
                        Message: "nextRunAt must be a valid ISO 8601 datetime."));
                }
                nextRunAt = parsed;
            }

            var now = DateTimeOffset.UtcNow;
            var job = new JobDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = request.Name,
                Type = jobType,
                Prompt = request.Prompt,
                Status = JobStatus.Pending,
                Cron = request.Cron,
                NextRunAt = nextRunAt,
                FallbackIntervalMinutes = request.FallbackIntervalMinutes,
                Channels = request.Channels ?? [],
                DeliveryMode = TryParseDeliveryMode(request.DeliveryMode),
                TimeoutMinutes = request.TimeoutMinutes ?? 10,
                MaxRetries = request.MaxRetries ?? 1,
                CreatedAt = now,
                UpdatedAt = now,
            };

            var errors = job.Validate(now);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.job_invalid",
                    Message: string.Join("; ", errors)));
            }

            var created = await jobRepository.CreateAsync(job, cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService, context,
                source: "gateway.jobs",
                eventType: "gateway.jobs.created",
                level: "info",
                message: "Job created.",
                attributes: new Dictionary<string, string?>
                {
                    ["jobId"] = created.Id,
                    ["jobType"] = created.Type.ToString(),
                });

            return Results.Ok(new JobCreateResponse(
                created.Id,
                created.Name,
                created.Type.ToString().ToLowerInvariant(),
                created.Status.ToString().ToLowerInvariant()));
        });

        // POST /api/jobs/{id}/cancel
        jobs.MapPost("/{id}/cancel", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IJobRepository jobRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var job = await jobRepository.GetByIdAsync(id, cancellationToken);
            if (job is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService, context,
                    source: "gateway.jobs",
                    eventType: "gateway.jobs.not_found",
                    level: "warning",
                    message: "Job cancel targeted a missing job.",
                    attributes: new Dictionary<string, string?> { ["jobId"] = id });
                return Results.NotFound(new ErrorResponse(
                    Code: "job.not_found",
                    Message: "Job was not found."));
            }

            var previousStatus = job.Status.ToString().ToLowerInvariant();
            var updated = job with { Status = JobStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
            await jobRepository.UpdateAsync(updated, cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService, context,
                source: "gateway.jobs",
                eventType: "gateway.jobs.cancelled",
                level: "info",
                message: "Job cancelled.",
                attributes: new Dictionary<string, string?>
                {
                    ["jobId"] = id,
                    ["previousStatus"] = previousStatus,
                });

            return Results.Ok(new JobCancelResponse(previousStatus, "cancelled"));
        });

        // POST /api/jobs/{id}/update
        jobs.MapPost("/{id}/update", async (
            HttpContext context,
            string id,
            UpdateJobRequest request,
            IConfiguration configuration,
            IJobRepository jobRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var job = await jobRepository.GetByIdAsync(id, cancellationToken);
            if (job is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService, context,
                    source: "gateway.jobs",
                    eventType: "gateway.jobs.not_found",
                    level: "warning",
                    message: "Job update targeted a missing job.",
                    attributes: new Dictionary<string, string?> { ["jobId"] = id });
                return Results.NotFound(new ErrorResponse(
                    Code: "job.not_found",
                    Message: "Job was not found."));
            }

            var updatedFields = new List<string>();
            var updated = job;

            if (request.Prompt != null) { updated = updated with { Prompt = request.Prompt }; updatedFields.Add("prompt"); }
            if (request.Cron != null) { updated = updated with { Cron = request.Cron }; updatedFields.Add("cron"); }
            if (request.TimeoutMinutes.HasValue) { updated = updated with { TimeoutMinutes = request.TimeoutMinutes.Value }; updatedFields.Add("timeoutMinutes"); }
            if (request.Channels != null) { updated = updated with { Channels = request.Channels }; updatedFields.Add("channels"); }
            if (request.DeliveryMode != null) { updated = updated with { DeliveryMode = TryParseDeliveryMode(request.DeliveryMode) }; updatedFields.Add("deliveryMode"); }

            if (updatedFields.Count == 0)
                return Results.Ok(new JobUpdateResponse([]));

            updated = updated with { UpdatedAt = DateTimeOffset.UtcNow };
            await jobRepository.UpdateAsync(updated, cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService, context,
                source: "gateway.jobs",
                eventType: "gateway.jobs.updated",
                level: "info",
                message: "Job updated.",
                attributes: new Dictionary<string, string?>
                {
                    ["jobId"] = id,
                    ["updatedFields"] = string.Join(",", updatedFields),
                });

            return Results.Ok(new JobUpdateResponse(updatedFields));
        });

        // POST /api/jobs/{id}/reschedule
        jobs.MapPost("/{id}/reschedule", async (
            HttpContext context,
            string id,
            RescheduleJobRequest request,
            IConfiguration configuration,
            IJobRepository jobRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var job = await jobRepository.GetByIdAsync(id, cancellationToken);
            if (job is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService, context,
                    source: "gateway.jobs",
                    eventType: "gateway.jobs.not_found",
                    level: "warning",
                    message: "Job reschedule targeted a missing job.",
                    attributes: new Dictionary<string, string?> { ["jobId"] = id });
                return Results.NotFound(new ErrorResponse(
                    Code: "job.not_found",
                    Message: "Job was not found."));
            }

            if (job.Type != JobType.SelfDriven)
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.reschedule_not_allowed",
                    Message: "仅 self-driven Job 可调用 reschedule。"));
            }

            if (!DateTimeOffset.TryParse(request.NextRunAt, out var nextRunAt))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.next_run_at_invalid",
                    Message: "nextRunAt must be a valid ISO 8601 datetime."));
            }

            var now = DateTimeOffset.UtcNow;
            if (nextRunAt < now.AddMinutes(5))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.next_run_at_too_soon",
                    Message: "nextRunAt must be at least 5 minutes in the future."));
            }

            if (nextRunAt > now.AddDays(30))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.next_run_at_too_far",
                    Message: "nextRunAt must be within 30 days."));
            }

            var updated = job with { NextRunAt = nextRunAt, UpdatedAt = now };
            await jobRepository.UpdateAsync(updated, cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService, context,
                source: "gateway.jobs",
                eventType: "gateway.jobs.rescheduled",
                level: "info",
                message: "Job rescheduled.",
                attributes: new Dictionary<string, string?>
                {
                    ["jobId"] = id,
                    ["nextRunAt"] = nextRunAt.ToString("O"),
                });

            return Results.Ok(new JobRescheduleApiResponse(id, nextRunAt.ToString("O")));
        });

        // POST /api/jobs/{id}/pause
        jobs.MapPost("/{id}/pause", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IJobRepository jobRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var job = await jobRepository.GetByIdAsync(id, cancellationToken);
            if (job is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService, context,
                    source: "gateway.jobs",
                    eventType: "gateway.jobs.not_found",
                    level: "warning",
                    message: "Job pause targeted a missing job.",
                    attributes: new Dictionary<string, string?> { ["jobId"] = id });
                return Results.NotFound(new ErrorResponse(
                    Code: "job.not_found",
                    Message: "Job was not found."));
            }

            if (job.Status != JobStatus.Pending)
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.pause_not_allowed",
                    Message: $"只能暂停 pending 状态的 Job，当前状态为 {job.Status.ToString().ToLowerInvariant()}。"));
            }

            var previousStatus = job.Status.ToString().ToLowerInvariant();
            var updated = job with { Status = JobStatus.Paused, UpdatedAt = DateTimeOffset.UtcNow };
            await jobRepository.UpdateAsync(updated, cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService, context,
                source: "gateway.jobs",
                eventType: "gateway.jobs.paused",
                level: "info",
                message: "Job paused.",
                attributes: new Dictionary<string, string?>
                {
                    ["jobId"] = id,
                    ["previousStatus"] = previousStatus,
                });

            return Results.Ok(new JobPauseResumeApiResponse(id, previousStatus, "paused"));
        });

        // POST /api/jobs/{id}/resume
        jobs.MapPost("/{id}/resume", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IJobRepository jobRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var job = await jobRepository.GetByIdAsync(id, cancellationToken);
            if (job is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService, context,
                    source: "gateway.jobs",
                    eventType: "gateway.jobs.not_found",
                    level: "warning",
                    message: "Job resume targeted a missing job.",
                    attributes: new Dictionary<string, string?> { ["jobId"] = id });
                return Results.NotFound(new ErrorResponse(
                    Code: "job.not_found",
                    Message: "Job was not found."));
            }

            if (job.Status != JobStatus.Paused)
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.resume_not_allowed",
                    Message: $"只能 resume paused 状态的 Job，当前状态为 {job.Status.ToString().ToLowerInvariant()}。"));
            }

            var previousStatus = job.Status.ToString().ToLowerInvariant();
            var updated = job with { Status = JobStatus.Pending, UpdatedAt = DateTimeOffset.UtcNow };
            await jobRepository.UpdateAsync(updated, cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService, context,
                source: "gateway.jobs",
                eventType: "gateway.jobs.resumed",
                level: "info",
                message: "Job resumed.",
                attributes: new Dictionary<string, string?>
                {
                    ["jobId"] = id,
                    ["previousStatus"] = previousStatus,
                });

            return Results.Ok(new JobPauseResumeApiResponse(id, previousStatus, "pending"));
        });

        // POST /api/jobs/{id}/delete
        jobs.MapPost("/{id}/delete", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IJobRepository jobRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            await jobRepository.DeleteAsync(id, cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService, context,
                source: "gateway.jobs",
                eventType: "gateway.jobs.deleted",
                level: "info",
                message: "Job deleted.",
                attributes: new Dictionary<string, string?> { ["jobId"] = id });

            return Results.Ok(new JobDeleteResponse(true));
        });
    }

    private static bool TryParseJobType(string type, out JobType jobType)
    {
        if (type.Equals("one-shot", StringComparison.OrdinalIgnoreCase))
        { jobType = JobType.OneShot; return true; }
        if (type.Equals("recurring", StringComparison.OrdinalIgnoreCase))
        { jobType = JobType.Recurring; return true; }
        if (type.Equals("self-driven", StringComparison.OrdinalIgnoreCase))
        { jobType = JobType.SelfDriven; return true; }
        jobType = default;
        return false;
    }

    private static JobDeliveryMode TryParseDeliveryMode(string? mode)
    {
        if (mode == null) return JobDeliveryMode.None;
        if (mode.Equals("auto", StringComparison.OrdinalIgnoreCase)) return JobDeliveryMode.Auto;
        if (mode.Equals("approval", StringComparison.OrdinalIgnoreCase)) return JobDeliveryMode.Approval;
        return JobDeliveryMode.None;
    }

    // ── API DTOs ─────────────────────────────────────────────────────────────────

    private sealed record JobListItem(
        string Id,
        string Name,
        string Type,
        string Status,
        DateTimeOffset? NextRunAt,
        int RunCount);

    private sealed record JobListResponse(IReadOnlyList<JobListItem> Items);

    private sealed record JobStatusResponse(
        string Id,
        string Name,
        string Type,
        string Prompt,
        string Status,
        string? Cron,
        DateTimeOffset? NextRunAt,
        IReadOnlyList<string> Channels,
        string DeliveryMode,
        int TimeoutMinutes,
        int MaxRetries,
        int RunCount,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record JobResultResponse(
        string JobId,
        bool HasResult,
        string? Message,
        string? Result);

    private sealed record JobCreateResponse(
        string Id,
        string Name,
        string Type,
        string Status);

    private sealed record JobCancelResponse(
        string PreviousStatus,
        string NewStatus);

    private sealed record JobUpdateResponse(
        IReadOnlyList<string> UpdatedFields);

    private sealed record JobDeleteResponse(
        bool Deleted);

    private sealed record CreateJobRequest
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public string Prompt { get; init; } = "";
        public string? Cron { get; init; }
        public string? NextRunAt { get; init; }
        public int? FallbackIntervalMinutes { get; init; }
        public IReadOnlyList<string>? Channels { get; init; }
        public string? DeliveryMode { get; init; }
        public int? TimeoutMinutes { get; init; }
        public int? MaxRetries { get; init; }
    }

    private sealed record RescheduleJobRequest
    {
        public string NextRunAt { get; init; } = "";
    }

    private sealed record JobRescheduleApiResponse(
        string JobId,
        string NextRunAt);

    private sealed record JobPauseResumeApiResponse(
        string JobId,
        string PreviousStatus,
        string NewStatus);

    private sealed record UpdateJobRequest
    {
        public string? Prompt { get; init; }
        public string? Cron { get; init; }
        public int? TimeoutMinutes { get; init; }
        public IReadOnlyList<string>? Channels { get; init; }
        public string? DeliveryMode { get; init; }
    }
}
