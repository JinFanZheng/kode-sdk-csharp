using System.Diagnostics;
using KodaClaw.Contracts.Secrets;

namespace KodaClaw.Workspace.Secrets;

public sealed class MacOsKeychainCommandRunner : IPlatformKeychain
{
    public string StorageDisplayName => "macOS Keychain";

    public async Task<string?> ReadAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);
        var result = await RunSecurityAsync(
            ["find-generic-password", "-s", BuildServiceName(secretRef), "-a", secretRef.Key, "-w"],
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            return Normalize(result.StandardOutput);
        }

        if (IsNotFound(result))
        {
            return null;
        }

        throw new InvalidOperationException($"macOS Keychain read failed: {result.StandardError}");
    }

    public async Task WriteAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretValue);

        var arguments = new List<string>
        {
            "add-generic-password",
            "-U",
            "-s", BuildServiceName(secretRef),
            "-a", secretRef.Key,
            "-w", secretValue,
        };

        if (!string.IsNullOrWhiteSpace(secretRef.DisplayName))
        {
            arguments.Add("-l");
            arguments.Add(secretRef.DisplayName!);
        }

        var result = await RunSecurityAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"macOS Keychain write failed: {result.StandardError}");
        }
    }

    public async Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);
        var result = await RunSecurityAsync(
            ["delete-generic-password", "-s", BuildServiceName(secretRef), "-a", secretRef.Key],
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == 0 || IsNotFound(result))
        {
            return;
        }

        throw new InvalidOperationException($"macOS Keychain delete failed: {result.StandardError}");
    }

    public async Task<bool> ExistsAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);
        var result = await RunSecurityAsync(
            ["find-generic-password", "-s", BuildServiceName(secretRef), "-a", secretRef.Key],
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private static string BuildServiceName(SecretRef secretRef)
    {
        return $"com.kodaclaw.{secretRef.Scope}";
    }

    private static string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static bool IsNotFound(SecurityCommandResult result)
    {
        return result.ExitCode != 0 &&
            (result.StandardError.Contains("could not be found", StringComparison.OrdinalIgnoreCase) ||
             result.StandardError.Contains("The specified item could not be found", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<SecurityCommandResult> RunSecurityAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start macOS security command.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new SecurityCommandResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private sealed record SecurityCommandResult(int ExitCode, string StandardOutput, string StandardError);
}
