using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Gateway;

/// <summary>
/// 将 Warning 及以上的 ILogger 日志桥接到 IDiagnosticsService，
/// 使 Diagnostics 事件流包含所有框架级错误，无需改动各模块的日志调用。
/// </summary>
[ProviderAlias("Diagnostics")]
internal sealed class DiagnosticsLoggerProvider : ILoggerProvider
{
    private readonly IDiagnosticsService _diagnosticsService;
    private readonly ICorrelationContextAccessor? _correlationContextAccessor;

    public DiagnosticsLoggerProvider(
        IDiagnosticsService diagnosticsService,
        ICorrelationContextAccessor? correlationContextAccessor = null)
    {
        _diagnosticsService = diagnosticsService ?? throw new ArgumentNullException(nameof(diagnosticsService));
        _correlationContextAccessor = correlationContextAccessor;
    }

    public ILogger CreateLogger(string categoryName) =>
        new DiagnosticsLogger(categoryName, _diagnosticsService, _correlationContextAccessor);

    public void Dispose() { }

    private sealed class DiagnosticsLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly IDiagnosticsService _diagnosticsService;
        private readonly ICorrelationContextAccessor? _correlationContextAccessor;

        public DiagnosticsLogger(
            string categoryName,
            IDiagnosticsService diagnosticsService,
            ICorrelationContextAccessor? correlationContextAccessor)
        {
            _categoryName = categoryName;
            _diagnosticsService = diagnosticsService;
            _correlationContextAccessor = correlationContextAccessor;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var level = logLevel switch
            {
                LogLevel.Warning => "warning",
                LogLevel.Error => "error",
                LogLevel.Critical => "error",
                _ => "warning"
            };

            var message = formatter(state, exception);
            if (exception is not null && !message.Contains(exception.Message, StringComparison.Ordinal))
            {
                message = $"{message} | {exception.GetBaseException().Message}";
            }

            var source = ResolveSource(_categoryName);
            var eventType = $"{source}.{logLevel.ToString().ToLowerInvariant()}";

            _diagnosticsService.Record(new DiagnosticEvent(
                Id: Guid.NewGuid().ToString("N"),
                Source: source,
                EventType: eventType,
                Level: level,
                Message: message,
                Timestamp: DateTimeOffset.UtcNow,
                CorrelationId: _correlationContextAccessor?.CorrelationId,
                Attributes: eventId.Id != 0
                    ? new Dictionary<string, string?> { ["eventId"] = eventId.Id.ToString() }
                    : null));
        }

        private static string ResolveSource(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName))
            {
                return "system";
            }

            // Microsoft.AspNetCore.* → aspnetcore
            // KodaClaw.Gateway.* → gateway
            // KodaClaw.Runtime.* → runtime
            // 其他取最后一个命名空间段
            if (categoryName.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
            {
                return "aspnetcore";
            }

            if (categoryName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            {
                return "dotnet";
            }

            var parts = categoryName.Split('.');
            if (parts.Length >= 2 && string.Equals(parts[0], "KodaClaw", StringComparison.OrdinalIgnoreCase))
            {
                return parts[1].ToLowerInvariant();
            }

            return parts[^1].ToLowerInvariant();
        }
    }
}
