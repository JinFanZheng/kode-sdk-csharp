using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Contracts.Workspace;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Workspace.Secrets;

/// <summary>
/// Linux credential backend.
/// Primary: <c>secret-tool</c> (GNOME Keyring / KWallet via libsecret).
/// Fallback: permission-protected JSON file at <c>{workspaceRoot}/config/.secrets</c> (chmod 600).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretStore : IPlatformKeychain
{
    private const string SecretToolBin = "secret-tool";
    private const string ServiceAttr = "service";
    private const string ServiceValue = "com.kodaclaw";
    private const string AccountAttr = "account";

    private readonly string _secretsFilePath;
    private readonly bool _useSecretTool;
    private readonly ILogger<LinuxSecretStore>? _logger;

    public LinuxSecretStore(KodaClawWorkspaceOptions options, ILogger<LinuxSecretStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _secretsFilePath = Path.Combine(
            KodaClawWorkspaceOptions.ResolveRootPathStatic(options.RootPath),
            KodaClawWorkspaceLayout.ConfigDirectory,
            ".secrets");
        _useSecretTool = DetectSecretTool();

        if (!_useSecretTool)
        {
            _logger?.LogWarning(
                "secret-tool not found or Secret Service unavailable. " +
                "Falling back to permission-protected file: {Path}. " +
                "Install libsecret-tools for OS-managed credential storage.",
                _secretsFilePath);
        }
    }

    public string StorageDisplayName => _useSecretTool
        ? "Linux Secret Service (libsecret)"
        : $"Protected File ({_secretsFilePath})";

    // -------------------------------------------------------------------------
    // IPlatformKeychain
    // -------------------------------------------------------------------------

    public Task<string?> ReadAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);
        return _useSecretTool
            ? ReadSecretToolAsync(secretRef, cancellationToken)
            : Task.FromResult(ReadFile(secretRef));
    }

    public Task WriteAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretValue);
        return _useSecretTool
            ? WriteSecretToolAsync(secretRef, secretValue.Trim(), cancellationToken)
            : Task.FromResult(WriteFile(secretRef, secretValue.Trim()));
    }

    public Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);
        return _useSecretTool
            ? DeleteSecretToolAsync(secretRef, cancellationToken)
            : Task.FromResult(DeleteFile(secretRef));
    }

    public Task<bool> ExistsAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);
        return _useSecretTool
            ? ExistsSecretToolAsync(secretRef, cancellationToken)
            : Task.FromResult(ExistsFile(secretRef));
    }

    // -------------------------------------------------------------------------
    // secret-tool backend
    // -------------------------------------------------------------------------

    private async Task<string?> ReadSecretToolAsync(SecretRef secretRef, CancellationToken cancellationToken)
    {
        var result = await RunSecretToolAsync(
            ["lookup", ServiceAttr, ServiceValue, AccountAttr, BuildAccountKey(secretRef)],
            cancellationToken);

        if (result.ExitCode == 0)
            return Normalize(result.Stdout);

        // Non-zero without error text = not found
        return string.IsNullOrWhiteSpace(result.Stderr) ? null
            : throw new InvalidOperationException($"secret-tool lookup failed: {result.Stderr}");
    }

    private async Task WriteSecretToolAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken)
    {
        var label = secretRef.DisplayName ?? $"KodaClaw — {secretRef.Scope}/{secretRef.Key}";
        var result = await RunSecretToolWithStdinAsync(
            secretValue,
            ["store", "--label", label, ServiceAttr, ServiceValue, AccountAttr, BuildAccountKey(secretRef)],
            cancellationToken);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"secret-tool store failed: {result.Stderr}");
    }

    private async Task DeleteSecretToolAsync(SecretRef secretRef, CancellationToken cancellationToken)
    {
        var result = await RunSecretToolAsync(
            ["clear", ServiceAttr, ServiceValue, AccountAttr, BuildAccountKey(secretRef)],
            cancellationToken);

        // Exit code 0 = deleted, non-zero without stderr = not found (tolerate)
        if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.Stderr))
            throw new InvalidOperationException($"secret-tool clear failed: {result.Stderr}");
    }

    private async Task<bool> ExistsSecretToolAsync(SecretRef secretRef, CancellationToken cancellationToken)
    {
        var result = await RunSecretToolAsync(
            ["lookup", ServiceAttr, ServiceValue, AccountAttr, BuildAccountKey(secretRef)],
            cancellationToken);
        return result.ExitCode == 0;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunSecretToolAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = SecretToolBin,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);
        return (proc.ExitCode, stdout, stderr);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunSecretToolWithStdinAsync(
        string stdin,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = SecretToolBin,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
        proc.Start();
        await proc.StandardInput.WriteAsync(stdin);
        proc.StandardInput.Close();
        var stdout = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);
        return (proc.ExitCode, stdout, stderr);
    }

    // -------------------------------------------------------------------------
    // File fallback backend (chmod 600)
    // -------------------------------------------------------------------------

    private string? ReadFile(SecretRef secretRef)
    {
        var dict = LoadSecretsFile();
        return dict.TryGetValue(BuildAccountKey(secretRef), out var val) ? val : null;
    }

    private object WriteFile(SecretRef secretRef, string secretValue)
    {
        var dict = LoadSecretsFile();
        dict[BuildAccountKey(secretRef)] = secretValue;
        SaveSecretsFile(dict);
        return null!;
    }

    private object DeleteFile(SecretRef secretRef)
    {
        var dict = LoadSecretsFile();
        if (dict.Remove(BuildAccountKey(secretRef)))
            SaveSecretsFile(dict);
        return null!;
    }

    private bool ExistsFile(SecretRef secretRef)
        => LoadSecretsFile().ContainsKey(BuildAccountKey(secretRef));

    private Dictionary<string, string> LoadSecretsFile()
    {
        if (!File.Exists(_secretsFilePath))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var json = File.ReadAllText(_secretsFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load secrets file from {Path}", _secretsFilePath);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void SaveSecretsFile(Dictionary<string, string> dict)
    {
        var dir = Path.GetDirectoryName(_secretsFilePath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(_secretsFilePath, json);

        // Restrict permissions to owner read/write only (equivalent to chmod 600)
        try
        {
            File.SetUnixFileMode(_secretsFilePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not set chmod 600 on secrets file {Path}", _secretsFilePath);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string BuildAccountKey(SecretRef secretRef)
        => $"{secretRef.Scope}/{secretRef.Key}";

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static bool DetectSecretTool()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = SecretToolBin,
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            // secret-tool detection is best-effort; missing binary is expected on many systems
            return false;
        }
    }
}
