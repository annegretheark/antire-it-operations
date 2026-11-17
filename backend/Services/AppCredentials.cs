using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;

namespace backend.Services;

// Appens egen innlogging mot Microsoft Graph og Azure (2.1), i prioritert rekkefølge:
//   1. MicrosoftGraph:UseManagedIdentity = true  → managed identity (når appen kjører i Azure)
//   2. MicrosoftGraph:CertificateThumbprint     → sertifikat fra Windows-sertifikatlageret
//      (CurrentUser\My, deretter LocalMachine\My)
//   3. MicrosoftGraph:CertificatePath (+ CertificatePassword) → sertifikat fra .pfx-fil
//   4. MicrosoftGraph:ClientSecret              → klienthemmelighet (bare til overgangen)
// Én innlogging (med token-cache) per tenant.
public class AppCredentials
{
    private readonly string _clientId;
    private readonly Func<string, TokenCredential> _create;
    private readonly ConcurrentDictionary<string, TokenCredential> _byTenant = new();

    // certificate | managed_identity | secret, til visning og logg
    public string Kind { get; }

    public AppCredentials(IConfiguration configuration)
    {
        _clientId = configuration["MicrosoftGraph:ClientId"]
            ?? throw new InvalidOperationException("MicrosoftGraph:ClientId mangler");

        var thumbprint = configuration["MicrosoftGraph:CertificateThumbprint"];
        var path = configuration["MicrosoftGraph:CertificatePath"];
        var secret = configuration["MicrosoftGraph:ClientSecret"];

        if (configuration.GetValue("MicrosoftGraph:UseManagedIdentity", false))
        {
            Kind = "managed_identity";
            var identity = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
            _create = _ => identity;
        }
        else if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            Kind = "certificate";
            var certificate = FindInStore(thumbprint);
            _create = tenantId => new ClientCertificateCredential(tenantId, _clientId, certificate);
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            Kind = "certificate";
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                path, configuration["MicrosoftGraph:CertificatePassword"]);
            _create = tenantId => new ClientCertificateCredential(tenantId, _clientId, certificate);
        }
        else if (!string.IsNullOrWhiteSpace(secret))
        {
            Kind = "secret";
            _create = tenantId => new ClientSecretCredential(tenantId, _clientId, secret);
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
