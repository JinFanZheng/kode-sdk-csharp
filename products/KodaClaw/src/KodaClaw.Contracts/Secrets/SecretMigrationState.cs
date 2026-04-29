using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Secrets;

[JsonConverter(typeof(JsonStringEnumConverter<SecretMigrationState>))]
public enum SecretMigrationState
{
    Migrated,
    LegacyFallback,
    Missing,
}
