namespace KodaClaw.Contracts;

/// <summary>
/// The result of capability-matching resolution: an Account + Model pair
/// ready for the runtime to construct a provider instance.
/// </summary>
public sealed record ResolvedModel(
    ProviderAccount Account,
    AccountModel Model);
