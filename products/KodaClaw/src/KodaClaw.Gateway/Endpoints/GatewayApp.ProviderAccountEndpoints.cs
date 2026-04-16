using KodaClaw.Contracts;
using KodaClaw.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapProviderAccountEndpoints(WebApplication app)
    {
        // ── Presets (unchanged) ─────────────────────────────────

        var presets = app.MapGroup("/api/models/presets");

        presets.MapGet(string.Empty, (
            HttpContext context,
            IConfiguration configuration,
            ModelPresetService modelPresetService,
            IDiagnosticsService diagnosticsService) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var allPresets = modelPresetService.GetAll();
            return Results.Ok(allPresets);
        });

        presets.MapGet("/{presetId}", (
            HttpContext context,
            string presetId,
            IConfiguration configuration,
            ModelPresetService modelPresetService,
            IDiagnosticsService diagnosticsService) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var preset = modelPresetService.GetById(presetId);
            if (preset is null)
                return Results.NotFound(new ErrorResponse(
                    Code: "model_preset.not_found",
                    Message: "Model preset was not found."));

            return Results.Ok(preset);
        });

        // ── Connection test ─────────────────────────────────────

        var modelsGroup = app.MapGroup("/api/models");

        modelsGroup.MapPost("/test-connection", async (
            HttpContext context,
            ModelConnectionTestRequest request,
            IConfiguration configuration,
            ModelConnectionTestService modelConnectionTestService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var result = await modelConnectionTestService.TestAsync(request, cancellationToken);
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.models",
                eventType: "gateway.models.connection_tested",
                level: result.Ok ? "info" : "warning",
                message: result.Ok ? "Model connection test succeeded." : $"Model connection test failed: {result.Error}",
                attributes: new Dictionary<string, string?>
                {
                    ["ok"] = result.Ok.ToString(),
                    ["latencyMs"] = result.LatencyMs.ToString(),
                    ["modelId"] = result.ModelId,
                    ["error"] = result.Error,
                });

            return Results.Ok(result);
        });

        // ── Provider Accounts ───────────────────────────────────

        var accounts = app.MapGroup("/api/provider-accounts");

        accounts.MapGet(string.Empty, async (
            HttpContext context,
            IConfiguration configuration,
            IProviderAccountRepository providerAccountRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var allAccounts = await providerAccountRepository.ListAccountsAsync(cancellationToken);
            var responses = new List<ProviderAccountResponse>(allAccounts.Count);
            foreach (var a in allAccounts)
            {
                var models = await providerAccountRepository.ListModelsAsync(a.Id, cancellationToken);
                responses.Add(MapToAccountResponse(a, models));
            }

            return Results.Ok(responses);
        });

        accounts.MapGet("/{id}", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IProviderAccountRepository providerAccountRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var account = await providerAccountRepository.GetAccountByIdAsync(id, cancellationToken);
            if (account is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.provider_accounts",
                    eventType: "gateway.provider_accounts.not_found",
                    level: "warning",
                    message: "Requested provider account was not found.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["accountId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "provider_account.not_found",
                    Message: "Provider account was not found."));
            }

            var models = await providerAccountRepository.ListModelsAsync(id, cancellationToken);
            return Results.Ok(MapToAccountResponse(account, models));
        });

        accounts.MapPost(string.Empty, async (
            HttpContext context,
            CreateProviderAccountRequest request,
            IConfiguration configuration,
            IProviderAccountRepository providerAccountRepository,
            ISecretStore secretStore,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "provider_account.invalid_request",
                    Message: "DisplayName is required."));
            }

            var now = DateTimeOffset.UtcNow;
            var accountId = $"acct-{Guid.NewGuid():N}";

            string? resolvedSecretRef = null;
            if (!string.IsNullOrWhiteSpace(request.ApiKeyValue))
            {
                var secretRef = new SecretRef("keychain", "accounts", accountId);
                await secretStore.UpsertAsync(secretRef, request.ApiKeyValue, cancellationToken);
                resolvedSecretRef = secretRef.ToReferenceString();
            }

            var account = new ProviderAccount(
                Id: accountId,
                DisplayName: request.DisplayName,
                ProviderKind: request.ProviderKind,
                BaseUrl: request.BaseUrl,
                ApiKeySecretRef: resolvedSecretRef,
                ApiKeyEnvironmentVariable: request.ApiKeyEnvironmentVariable,
                AccessMode: request.AccessMode,
                Enabled: true,
                CreatedAt: now,
                UpdatedAt: now,
                CustomHeaders: request.CustomHeaders);

            await providerAccountRepository.AddAccountAsync(account, cancellationToken);

            // Determine if this is the first account (for auto-default logic)
            var existingAccounts = await providerAccountRepository.ListAccountsAsync(cancellationToken);
            var isFirstAccount = existingAccounts.Count == 1; // just the one we added
            var allModels = isFirstAccount
                ? await providerAccountRepository.ListAllModelsAsync(cancellationToken)
                : [];
            var isFirstModel = isFirstAccount && allModels.Count == 0;

            var createdModels = new List<AccountModel>();
            if (request.Models is { Count: > 0 })
            {
                for (var i = 0; i < request.Models.Count; i++)
                {
                    var mr = request.Models[i];
                    var modelEntryId = $"model-{Guid.NewGuid():N}";
                    var shouldBeGlobalDefault = (isFirstModel && i == 0) || mr.IsGlobalDefault;

                    var model = new AccountModel(
                        Id: modelEntryId,
                        AccountId: accountId,
                        DisplayName: mr.DisplayName,
                        ModelId: mr.ModelId,
                        Capabilities: mr.Capabilities,
                        IsDefaultForAccount: mr.IsDefaultForAccount || (i == 0),
                        IsGlobalDefault: shouldBeGlobalDefault,
                        Enabled: true,
                        CreatedAt: now,
                        UpdatedAt: now,
                        ContextWindowSize: mr.ContextWindowSize,
                        MaxOutputTokens: mr.MaxOutputTokens,
                        IsReasoning: mr.IsReasoning,
                        SupportsToolCalling: mr.SupportsToolCalling,
                        Pricing: mr.Pricing);

                    await providerAccountRepository.AddModelAsync(model, cancellationToken);

                    if (shouldBeGlobalDefault)
                    {
                        await providerAccountRepository.SetGlobalDefaultAsync(modelEntryId, now, cancellationToken);
                    }

                    createdModels.Add(model);
                }
            }

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.provider_accounts",
                eventType: "gateway.provider_accounts.created",
                level: "info",
                message: "Created provider account.",
                attributes: new Dictionary<string, string?>
                {
                    ["accountId"] = accountId,
                    ["providerKind"] = account.ProviderKind.ToString(),
                    ["modelCount"] = createdModels.Count.ToString(),
                });

            var response = MapToAccountResponse(account, createdModels);
            return Results.Created($"/api/provider-accounts/{accountId}", response);
        });

        accounts.MapPut("/{id}", async (
            HttpContext context,
            string id,
            UpdateProviderAccountRequest request,
            IConfiguration configuration,
            IProviderAccountRepository providerAccountRepository,
            ISecretStore secretStore,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var existing = await providerAccountRepository.GetAccountByIdAsync(id, cancellationToken);
            if (existing is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.provider_accounts",
                    eventType: "gateway.provider_accounts.not_found",
                    level: "warning",
                    message: "Provider account update targeted a missing account.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["accountId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "provider_account.not_found",
                    Message: "Provider account was not found."));
            }

            var resolvedSecretRef = existing.ApiKeySecretRef;
            if (!string.IsNullOrWhiteSpace(request.ApiKeyValue))
            {
                var secretRef = new SecretRef("keychain", "accounts", id);
                await secretStore.UpsertAsync(secretRef, request.ApiKeyValue, cancellationToken);
                resolvedSecretRef = secretRef.ToReferenceString();
            }

            var updated = existing with
            {
                DisplayName = request.DisplayName ?? existing.DisplayName,
                BaseUrl = request.BaseUrl ?? existing.BaseUrl,
                ApiKeySecretRef = resolvedSecretRef,
                ApiKeyEnvironmentVariable = request.ApiKeyEnvironmentVariable ?? existing.ApiKeyEnvironmentVariable,
                Enabled = request.Enabled ?? existing.Enabled,
                CustomHeaders = request.CustomHeaders ?? existing.CustomHeaders,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            var persisted = await providerAccountRepository.UpdateAccountAsync(updated, cancellationToken);
            if (!persisted)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "provider_account.not_found",
                    Message: "Provider account was not found."));
            }

            var reloaded = await providerAccountRepository.GetAccountByIdAsync(id, cancellationToken) ?? updated;
            var models = await providerAccountRepository.ListModelsAsync(id, cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.provider_accounts",
                eventType: "gateway.provider_accounts.updated",
                level: "info",
                message: "Updated provider account.",
                attributes: new Dictionary<string, string?>
                {
                    ["accountId"] = reloaded.Id,
                    ["providerKind"] = reloaded.ProviderKind.ToString(),
                });

            return Results.Ok(MapToAccountResponse(reloaded, models));
        });

        accounts.MapDelete("/{id}", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IProviderAccountRepository providerAccountRepository,
            ISecretStore secretStore,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var existing = await providerAccountRepository.GetAccountByIdAsync(id, cancellationToken);
            if (existing is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.provider_accounts",
                    eventType: "gateway.provider_accounts.not_found",
                    level: "warning",
                    message: "Provider account delete targeted a missing account.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["accountId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "provider_account.not_found",
                    Message: "Provider account was not found."));
            }

            // Clean up secret if stored in keychain
            if (!string.IsNullOrWhiteSpace(existing.ApiKeySecretRef) &&
                SecretRef.TryParse(existing.ApiKeySecretRef, out var secretRef))
            {
                await secretStore.DeleteAsync(secretRef, cancellationToken);
            }

            // DeleteAccountAsync cascades to models
            var deleted = await providerAccountRepository.DeleteAccountAsync(id, cancellationToken);
            if (!deleted)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "provider_account.not_found",
                    Message: "Provider account was not found."));
            }

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.provider_accounts",
                eventType: "gateway.provider_accounts.deleted",
                level: "info",
                message: "Deleted provider account.",
                attributes: new Dictionary<string, string?>
                {
                    ["accountId"] = id,
                });

            return Results.NoContent();
        });

        // ── Account Models ──────────────────────────────────────

        accounts.MapPost("/{accountId}/models", async (
            HttpContext context,
            string accountId,
            CreateAccountModelRequest request,
            IConfiguration configuration,
            IProviderAccountRepository providerAccountRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var account = await providerAccountRepository.GetAccountByIdAsync(accountId, cancellationToken);
            if (account is null)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "provider_account.not_found",
                    Message: "Provider account was not found."));
            }

            if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.ModelId))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "account_model.invalid_request",
                    Message: "DisplayName and ModelId are required."));
            }

            var now = DateTimeOffset.UtcNow;
            var modelEntryId = $"model-{Guid.NewGuid():N}";

            var model = new AccountModel(
                Id: modelEntryId,
                AccountId: accountId,
                DisplayName: request.DisplayName,
                ModelId: request.ModelId,
                Capabilities: request.Capabilities,
                IsDefaultForAccount: request.IsDefaultForAccount,
                IsGlobalDefault: request.IsGlobalDefault,
                Enabled: true,
                CreatedAt: now,
                UpdatedAt: now,
                ContextWindowSize: request.ContextWindowSize,
                MaxOutputTokens: request.MaxOutputTokens,
                IsReasoning: request.IsReasoning,
                SupportsToolCalling: request.SupportsToolCalling,
                Pricing: request.Pricing);

            await providerAccountRepository.AddModelAsync(model, cancellationToken);

            if (request.IsGlobalDefault)
            {
                await providerAccountRepository.SetGlobalDefaultAsync(modelEntryId, now, cancellationToken);
            }

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.provider_accounts",
                eventType: "gateway.provider_accounts.model_added",
                level: "info",
                message: "Added model to provider account.",
                attributes: new Dictionary<string, string?>
                {
                    ["accountId"] = accountId,
                    ["modelEntryId"] = modelEntryId,
                    ["modelId"] = model.ModelId,
                });

            return Results.Created(
                $"/api/provider-accounts/{accountId}/models/{modelEntryId}",
                MapToModelResponse(model));
        });

        accounts.MapPut("/{accountId}/models/{modelId}", async (
            HttpContext context,
            string accountId,
            string modelId,
            UpdateAccountModelRequest request,
            IConfiguration configuration,
            IProviderAccountRepository providerAccountRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var account = await providerAccountRepository.GetAccountByIdAsync(accountId, cancellationToken);
            if (account is null)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "provider_account.not_found",
                    Message: "Provider account was not found."));
            }

            var existing = await providerAccountRepository.GetModelByIdAsync(modelId, cancellationToken);
            if (existing is null || existing.AccountId != accountId)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.provider_accounts",
                    eventType: "gateway.provider_accounts.model_not_found",
                    level: "warning",
                    message: "Account model update targeted a missing model.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["accountId"] = accountId,
                        ["modelEntryId"] = modelId,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "account_model.not_found",
                    Message: "Account model was not found."));
            }

            var updated = existing with
            {
                DisplayName = request.DisplayName ?? existing.DisplayName,
                ModelId = request.ModelId ?? existing.ModelId,
                Capabilities = request.Capabilities ?? existing.Capabilities,
                ContextWindowSize = request.ContextWindowSize ?? existing.ContextWindowSize,
                MaxOutputTokens = request.MaxOutputTokens ?? existing.MaxOutputTokens,
                IsReasoning = request.IsReasoning ?? existing.IsReasoning,
                SupportsToolCalling = request.SupportsToolCalling ?? existing.SupportsToolCalling,
                Enabled = request.Enabled ?? existing.Enabled,
                Pricing = request.Pricing ?? existing.Pricing,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            var persisted = await providerAccountRepository.UpdateModelAsync(updated, cancellationToken);
            if (!persisted)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "account_model.not_found",
                    Message: "Account model was not found."));
            }

            var reloaded = await providerAccountRepository.GetModelByIdAsync(modelId, cancellationToken) ?? updated;

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.provider_accounts",
                eventType: "gateway.provider_accounts.model_updated",
                level: "info",
                message: "Updated account model.",
                attributes: new Dictionary<string, string?>
                {
                    ["accountId"] = accountId,
                    ["modelEntryId"] = reloaded.Id,
                    ["modelId"] = reloaded.ModelId,
                });

            return Results.Ok(MapToModelResponse(reloaded));
        });

        accounts.MapDelete("/{accountId}/models/{modelId}", async (
            HttpContext context,
            string accountId,
            string modelId,
            IConfiguration configuration,
            IProviderAccountRepository providerAccountRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var existing = await providerAccountRepository.GetModelByIdAsync(modelId, cancellationToken);
            if (existing is null || existing.AccountId != accountId)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.provider_accounts",
                    eventType: "gateway.provider_accounts.model_not_found",
                    level: "warning",
                    message: "Account model delete targeted a missing model.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["accountId"] = accountId,
                        ["modelEntryId"] = modelId,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "account_model.not_found",
                    Message: "Account model was not found."));
            }

            // If this was the global default, try to reassign
            if (existing.IsGlobalDefault)
            {
                var allModels = await providerAccountRepository.ListAllModelsAsync(cancellationToken);
                var nextDefault = allModels.FirstOrDefault(m => m.Id != modelId && m.Enabled);
                if (nextDefault is not null)
                {
                    await providerAccountRepository.SetGlobalDefaultAsync(
                        nextDefault.Id, DateTimeOffset.UtcNow, cancellationToken);
                }
            }

            var deleted = await providerAccountRepository.DeleteModelAsync(modelId, cancellationToken);
            if (!deleted)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "account_model.not_found",
                    Message: "Account model was not found."));
            }

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.provider_accounts",
                eventType: "gateway.provider_accounts.model_deleted",
                level: "info",
                message: "Deleted account model.",
                attributes: new Dictionary<string, string?>
                {
                    ["accountId"] = accountId,
                    ["modelEntryId"] = modelId,
                });

            return Results.NoContent();
        });

        accounts.MapPost("/{accountId}/models/{modelId}/default", async (
            HttpContext context,
            string accountId,
            string modelId,
            IConfiguration configuration,
            IProviderAccountRepository providerAccountRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var model = await providerAccountRepository.GetModelByIdAsync(modelId, cancellationToken);
            if (model is null || model.AccountId != accountId)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.provider_accounts",
                    eventType: "gateway.provider_accounts.model_not_found",
                    level: "warning",
                    message: "Set-default targeted a missing model.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["accountId"] = accountId,
                        ["modelEntryId"] = modelId,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "account_model.not_found",
                    Message: "Account model was not found."));
            }

            if (!model.Enabled)
            {
                return Results.Conflict(new ErrorResponse(
                    Code: "account_model.default_must_be_enabled",
                    Message: "Only enabled models can become the global default."));
            }

            var updated = await providerAccountRepository.SetGlobalDefaultAsync(
                modelId, DateTimeOffset.UtcNow, cancellationToken);
            if (!updated)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "account_model.not_found",
                    Message: "Account model was not found."));
            }

            var reloaded = await providerAccountRepository.GetModelByIdAsync(modelId, cancellationToken);
            if (reloaded is null)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "account_model.not_found",
                    Message: "Account model was not found."));
            }

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.provider_accounts",
                eventType: "gateway.provider_accounts.default_set",
                level: "info",
                message: "Updated global default model.",
                attributes: new Dictionary<string, string?>
                {
                    ["accountId"] = accountId,
                    ["modelEntryId"] = reloaded.Id,
                    ["modelId"] = reloaded.ModelId,
                });

            return Results.Ok(MapToModelResponse(reloaded));
        });

        // ── Compatibility: GET /api/models → flat model list ────

        modelsGroup.MapGet(string.Empty, async (
            HttpContext context,
            IConfiguration configuration,
            IProviderAccountRepository providerAccountRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var allModels = await providerAccountRepository.ListAllModelsAsync(cancellationToken);
            var responses = allModels.Select(MapToModelResponse).ToList();
            return Results.Ok(responses);
        });
    }

    // ── Mapping helpers ─────────────────────────────────────────

    private static ProviderAccountResponse MapToAccountResponse(
        ProviderAccount account,
        IEnumerable<AccountModel> models)
    {
        return new ProviderAccountResponse(
            Id: account.Id,
            DisplayName: account.DisplayName,
            ProviderKind: account.ProviderKind,
            BaseUrl: account.BaseUrl,
            AccessMode: account.AccessMode,
            Enabled: account.Enabled,
            HasApiKey: !string.IsNullOrWhiteSpace(account.ApiKeySecretRef)
                       || !string.IsNullOrWhiteSpace(account.ApiKeyEnvironmentVariable),
            CreatedAt: account.CreatedAt,
            UpdatedAt: account.UpdatedAt,
            Models: models.Select(MapToModelResponse).ToList(),
            ApiKeyEnvironmentVariable: account.ApiKeyEnvironmentVariable,
            CustomHeaders: account.CustomHeaders);
    }

    private static AccountModelResponse MapToModelResponse(AccountModel model)
    {
        return new AccountModelResponse(
            Id: model.Id,
            AccountId: model.AccountId,
            DisplayName: model.DisplayName,
            ModelId: model.ModelId,
            Capabilities: model.Capabilities,
            IsDefaultForAccount: model.IsDefaultForAccount,
            IsGlobalDefault: model.IsGlobalDefault,
            Enabled: model.Enabled,
            ContextWindowSize: model.ContextWindowSize,
            MaxOutputTokens: model.MaxOutputTokens,
            IsReasoning: model.IsReasoning,
            SupportsToolCalling: model.SupportsToolCalling,
            Pricing: model.Pricing);
    }
}
