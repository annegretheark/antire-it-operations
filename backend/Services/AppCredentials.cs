using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;

namespace backend.Services;

// Appens egen innlogging mot Microsoft Graph og Azure (2.1), i prioritert rekkefølge:
//   1. MicrosoftGraph:UseFederatedManagedIdentity = true → appens managed identity
//      logger inn som app-registreringen (federated identity credential). Ingen
//      hemmelighet; rettighetene til app-registreringen gjelder, også i andre tenanter.
//      Brukertildelt identitet: Azure:ManagedIdentityClientId.
//   2. MicrosoftGraph:UseManagedIdentity = true  → managed identity direkte (bare egen tenant)
//   3. MicrosoftGraph:CertificateThumbprint     → sertifikat fra Windows-sertifikatlageret
//      (CurrentUser\My, deretter LocalMachine\My)
//   4. MicrosoftGraph:CertificatePath (+ CertificatePassword) → sertifikat fra .pfx-fil
//   5. MicrosoftGraph:ClientSecret              → klienthemmelighet (bare til overgangen)
// Én innlogging (med token-cache) per tenant.
public class AppCredentials
{
    private readonly string _clientId;
    private readonly Func<string, TokenCredential> _create;
    private readonly ConcurrentDictionary<string, TokenCredential> _byTenant = new();

    // On-behalf-of: innlogget persons token byttes mot et token for samme person.
    // Null når innloggingen ikke kan brukes til det (managed identity direkte).
    private readonly Func<string, string, TokenCredential>? _createOnBehalfOf;

    // federated_managed_identity | managed_identity | certificate | secret, til visning og logg
    public string Kind { get; }

    public AppCredentials(IConfiguration configuration)
    {
        _clientId = configuration["MicrosoftGraph:ClientId"]
            ?? throw new InvalidOperationException("MicrosoftGraph:ClientId mangler");

        var thumbprint = configuration["MicrosoftGraph:CertificateThumbprint"];
        var path = configuration["MicrosoftGraph:CertificatePath"];
        var secret = configuration["MicrosoftGraph:ClientSecret"];

        var identityClientId = configuration["Azure:ManagedIdentityClientId"];

        if (configuration.GetValue("MicrosoftGraph:UseFederatedManagedIdentity", false))
        {
            Kind = "federated_managed_identity";
            var identity = ManagedIdentity(identityClientId);

            // Token fra identiteten brukes som bevis (client assertion) for app-registreringen
            Func<CancellationToken, Task<string>> assertion = async cancellationToken =>
                (await identity.GetTokenAsync(
                    new TokenRequestContext(["api://AzureADTokenExchange/.default"]), cancellationToken)).Token;

            _create = tenantId => new ClientAssertionCredential(tenantId, _clientId, assertion);
            _createOnBehalfOf = (tenantId, userToken) =>
                new OnBehalfOfCredential(tenantId, _clientId, assertion, userToken);
        }
        else if (configuration.GetValue("MicrosoftGraph:UseManagedIdentity", false))
        {
            Kind = "managed_identity";
            var identity = ManagedIdentity(identityClientId);
            _create = _ => identity;
        }
        else if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            Kind = "certificate";
            var certificate = FindInStore(thumbprint);
            _create = tenantId => new ClientCertificateCredential(tenantId, _clientId, certificate);
            _createOnBehalfOf = (tenantId, userToken) =>
                new OnBehalfOfCredential(tenantId, _clientId, certificate, userToken);
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            Kind = "certificate";
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                path, configuration["MicrosoftGraph:CertificatePassword"]);
            _create = tenantId => new ClientCertificateCredential(tenantId, _clientId, certificate);
            _createOnBehalfOf = (tenantId, userToken) =>
                new OnBehalfOfCredential(tenantId, _clientId, certificate, userToken);
        }
        else if (!string.IsNullOrWhiteSpace(secret))
        {
            Kind = "secret";
            _create = tenantId => new ClientSecretCredential(tenantId, _clientId, secret);
            _createOnBehalfOf = (tenantId, userToken) =>
                new OnBehalfOfCredential(tenantId, _clientId, secret, userToken);
        }
        else
        {
            throw new InvalidOperationException(
                "Appen har ingen innlogging mot Graph og Azure. Sett MicrosoftGraph:CertificateThumbprint " +
                "(se scripts/New-AppCertificate.ps1), MicrosoftGraph:UseManagedIdentity eller MicrosoftGraph:ClientSecret.");
        }
    }

    public TokenCredential For(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("Tenant mangler for innlogging mot Graph og Azure.");

        return _byTenant.GetOrAdd(tenantId.ToLowerInvariant(), _create);
    }

    // Innlogging på vegne av personen som sendte userToken (tokenet til API-et).
    // Graph sjekker da personens egne roller i Entra. Lages per forespørsel.
    public TokenCredential OnBehalfOf(string tenantId, string userToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("Tenant mangler for innlogging mot Graph.");
        if (string.IsNullOrWhiteSpace(userToken))
            throw new InvalidOperationException("Innlogget brukers token mangler.");

        return (_createOnBehalfOf ?? throw new InvalidOperationException(
            "Handlinger med personens egne rettigheter krever sertifikat eller federated managed identity " +
            "(MicrosoftGraph:UseManagedIdentity støtter ikke on-behalf-of)."))(tenantId, userToken);
    }

    private static ManagedIdentityCredential ManagedIdentity(string? clientId) =>
        new(string.IsNullOrWhiteSpace(clientId)
            ? ManagedIdentityId.SystemAssigned
            : ManagedIdentityId.FromUserAssignedClientId(clientId));

    private static X509Certificate2 FindInStore(string thumbprint)
    {
        var normalized = thumbprint.Replace(" ", "").Trim();

        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);

            var match = store.Certificates
                .Find(X509FindType.FindByThumbprint, normalized, validOnly: false)
                .FirstOrDefault(c => c.HasPrivateKey);

            if (match != null)
                return match;
        }

        throw new InvalidOperationException(
            $"Fant ikke sertifikatet {normalized} med privat nøkkel i CurrentUser\\My eller LocalMachine\\My.");
    }
}
