namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Thrown when stored provider credentials cannot be refreshed and no fallback credential is
/// configured.
/// </summary>
public sealed class ProviderCredentialUnavailableException : InvalidOperationException
{
    /// <summary>Gets the provider whose credential could not be resolved.</summary>
    public string ProviderName { get; }

    /// <summary>
    /// Initializes an actionable credential-resolution failure while preserving the refresh error.
    /// </summary>
    public ProviderCredentialUnavailableException(string providerName, Exception innerException)
        : base(
            $"Credentials for provider '{providerName}' could not be refreshed. Retry after the provider recovers or re-authenticate the provider.",
            innerException)
    {
        ProviderName = providerName;
    }
}
