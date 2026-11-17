using Microsoft.Extensions.Configuration;
using backend.Services;

namespace backend.Tests;

// Hvilken innlogging appen bruker mot Graph og Azure (2.1)
public class AppCredentialsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values
                .Append(("MicrosoftGraph:ClientId", "00000000-0000-0000-0000-000000000002"))
                .ToDictionary(v => v.Item1, v => (string?)v.Item2))
            .Build();

    [Fact]
    public void Secret_is_used_when_nothing_else_is_configured()
    {
        var credentials = new AppCredentials(Config(("MicrosoftGraph:ClientSecret", "hemmelig")));

        Assert.Equal("secret", credentials.Kind);
        Assert.Same(credentials.For("ABC"), credentials.For("abc"));
    }

    [Fact]
    public void Managed_identity_wins_over_everything()
    {
        var credentials = new AppCredentials(Config(
            ("MicrosoftGraph:UseManagedIdentity", "true"),
            ("MicrosoftGraph:CertificateThumbprint", "0000"),
            ("MicrosoftGraph:ClientSecret", "hemmelig")));

        Assert.Equal("managed_identity", credentials.Kind);
    }

    [Fact]
    public void Certificate_wins_over_secret_and_must_exist()
    {
        // Sertifikatet foretrekkes; finnes det ikke, er det en tydelig feil
        // (ikke et stille fall tilbake til hemmeligheten)
        var error = Assert.Throws<InvalidOperationException>(() => new AppCredentials(Config(
            ("MicrosoftGraph:CertificateThumbprint", "00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF 00 11 22 33"),
            ("MicrosoftGraph:ClientSecret", "hemmelig"))));

        Assert.Contains("Fant ikke sertifikatet 00112233", error.Message);
    }

    [Fact]
    public void Missing_login_is_explained()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new AppCredentials(Config()));

        Assert.Contains("New-AppCertificate.ps1", error.Message);
    }

    [Fact]
    public void Tenant_is_required()
    {
        var credentials = new AppCredentials(Config(("MicrosoftGraph:ClientSecret", "hemmelig")));

        Assert.Throws<InvalidOperationException>(() => credentials.For(""));
    }
}
