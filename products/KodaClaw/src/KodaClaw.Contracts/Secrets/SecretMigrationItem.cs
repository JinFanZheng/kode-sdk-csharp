namespace KodaClaw.Contracts.Secrets;

public sealed record SecretMigrationItem(
    string Id,
    string Kind,
    string DisplayName,
    SecretMigrationState State,
    string Location,
    string Field,
    string? ConfiguredSecretRef = null,
    bool SecretRefExists = false,
    string? LegacySource = null,
    bool LegacySourceAvailable = false,
    string? Notes = null);
