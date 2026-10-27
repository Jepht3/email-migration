using System.IO;
using System.Text.Json;
using MailMigrator.Core.Connectivity.OAuth;
using Serilog;

namespace MailMigrator.App.Services;

/// <summary>
/// Identifiants OAuth2 de l'application LEWEBMAX (créés une fois, voir docs/OAUTH.md).
/// Lus dans oauth.json (livré avec l'application), puis oauth.local.json à côté de l'exécutable
/// ou dans %LOCALAPPDATA%\LEWEBMAX\MailMigrator, qui l'emporte (non versionné).
/// </summary>
public static class OAuthConfiguration
{
    private sealed record ProviderConfig(string? ClientId, string? ClientSecret);

    private sealed record Config(ProviderConfig? Google, ProviderConfig? Microsoft);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    public static OAuthRegistry Load(string dataFolder)
    {
        string? googleId = null, googleSecret = null, microsoftId = null;
        foreach (var path in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "oauth.json"),
                     Path.Combine(AppContext.BaseDirectory, "oauth.local.json"),
                     Path.Combine(dataFolder, "oauth.local.json"),
                 })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Options);
                googleId = Pick(config?.Google?.ClientId, googleId);
                googleSecret = Pick(config?.Google?.ClientSecret, googleSecret);
                microsoftId = Pick(config?.Microsoft?.ClientId, microsoftId);
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "Configuration OAuth illisible : {Path}", path);
            }
        }

        var providers = new List<OAuthProviderSettings>();
        if (googleId is not null) providers.Add(OAuthProviderSettings.Google(googleId, googleSecret));
        if (microsoftId is not null) providers.Add(OAuthProviderSettings.Microsoft(microsoftId));
        Log.Information("OAuth2 configuré pour : {Providers}", providers.Count == 0 ? "aucun fournisseur" : string.Join(", ", providers.Select(p => p.DisplayName)));
        return new OAuthRegistry(providers);
    }

    private static string? Pick(string? value, string? fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
