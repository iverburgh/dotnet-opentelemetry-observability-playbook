using Azure.Core;
using Azure.Identity;

namespace Observability.AzureMonitor;

/// <summary>
/// Turns the <c>APPLICATIONINSIGHTS_AUTHENTICATION_STRING</c> setting into a credential, so the exporter signs
/// in with a managed identity and the resource can disable local (instrumentation key) authentication.
/// </summary>
public static class AzureMonitorCredential
{
    public const string SettingName = "APPLICATIONINSIGHTS_AUTHENTICATION_STRING";

    /// <summary>
    /// <c>null</c> for an empty value (key-based ingestion); <c>Authorization=AAD</c> for the system-assigned
    /// identity; <c>Authorization=AAD;ClientId=&lt;guid&gt;</c> for a user-assigned identity.
    /// </summary>
    /// <exception cref="FormatException">The value is not one of those forms.</exception>
    public static TokenCredential? Create(string? authenticationString)
    {
        if (string.IsNullOrWhiteSpace(authenticationString))
        {
            return null;
        }

        string? authorization = null;
        string? clientId = null;
        foreach (var part in authenticationString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
            {
                throw new FormatException($"'{part}' in {SettingName} is not a key=value pair.");
            }

            if (pair[0].Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                authorization = pair[1];
            }
            else if (pair[0].Equals("ClientId", StringComparison.OrdinalIgnoreCase))
            {
                clientId = pair[1];
            }
            else
            {
                throw new FormatException($"{SettingName} has an unknown key '{pair[0]}'.");
            }
        }

        if (!string.Equals(authorization, "AAD", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"{SettingName} must contain Authorization=AAD.");
        }

        if (clientId is null)
        {
            return new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        }

        return Guid.TryParse(clientId, out _)
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))
            : throw new FormatException($"ClientId in {SettingName} must be a GUID.");
    }
}
