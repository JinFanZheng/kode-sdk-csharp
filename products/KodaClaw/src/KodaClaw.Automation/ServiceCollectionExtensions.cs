using KodaClaw.Automation.Scheduler;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Jobs;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Settings;
using KodaClaw.Contracts.Timers;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Automation;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKodaClawAutomation(
        this IServiceCollection services,
        Action<AutomationSchedulerOptions>? configureScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var schedulerOptions = new AutomationSchedulerOptions();
        configureScheduler?.Invoke(schedulerOptions);
        if (schedulerOptions.PollInterval <= TimeSpan.Zero)
        {
            schedulerOptions.PollInterval = TimeSpan.FromMinutes(1);
        }

        if (schedulerOptions.FailureRetryDelay <= TimeSpan.Zero)
        {
            schedulerOptions.FailureRetryDelay = TimeSpan.FromMinutes(15);
        }

        services.TryAddSingleton(schedulerOptions);
        services.TryAddSingleton<IAutomationClock, SystemAutomationClock>();
        services.TryAddSingleton<IOneShotTimerService>(provider =>
        {
            var timerRepository = provider.GetService<IOneShotTimerRepository>();
            var sessionService = provider.GetService<IAutomationSessionService>();
            var inboxRepository = provider.GetService<IInboxRepository>();
            if (timerRepository is null || sessionService is null || inboxRepository is null)
            {
                return new DisabledOneShotTimerService();
            }

            return new OneShotTimerService(
                timerRepository,
                sessionService,
                inboxRepository,
                provider.GetRequiredService<IAutomationClock>(),
                provider.GetService<IDiagnosticsService>(),
                provider.GetService<ILogger<OneShotTimerService>>());
        });

        services.TryAddSingleton<IAutomationScheduler>(provider =>
        {
            var sessionService = provider.GetService<IAutomationSessionService>();
            var inboxRepository = provider.GetService<IInboxRepository>();
            if (sessionService is null || inboxRepository is null)
            {
                return new DisabledAutomationScheduler();
            }

            return new AutomationScheduler(
                provider.GetRequiredService<IAutomationDefinitionRepository>(),
                provider.GetRequiredService<IAutomationRunRepository>(),
                sessionService,
                inboxRepository,
                provider.GetRequiredService<IAutomationClock>(),
                provider.GetRequiredService<AutomationSchedulerOptions>(),
                provider.GetService<ISettingsRepository>(),
                provider.GetService<IAutomationNotificationService>(),
                provider.GetService<IMemoryConsolidationService>(),
                provider.GetService<IOneShotTimerService>(),
                provider.GetService<ICorrelationContextAccessor>(),
                provider.GetService<IDiagnosticsService>(),
                provider.GetService<ILogger<AutomationScheduler>>(),
                provider.GetService<IHostApplicationLifetime>(),
                provider.GetService<IAutomationChannelMessageLinkRepository>());
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AutomationSchedulerHostedService>());

        // JobScheduler registrations (Phase 1b-1/1b-2)
        services.TryAddSingleton<JobSchedulerOptions>();
        services.TryAddSingleton(provider => new JobScheduler(
            provider.GetRequiredService<IJobRepository>(),
            provider.GetRequiredService<IAutomationSessionService>(),
            provider.GetRequiredService<JobSchedulerOptions>(),
            provider.GetService<IAutomationNotificationService>(),
            provider.GetService<ILogger<JobScheduler>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobSchedulerHostedService>());

        return services;
    }

    private sealed class DisabledOneShotTimerService : IOneShotTimerService
    {
        public Task TickAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class DisabledAutomationScheduler : IAutomationScheduler
    {
        public Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }

        public Task<int> TickAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }

        public Task<string?> TriggerDefinitionAsync(string definitionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }
    }
}
