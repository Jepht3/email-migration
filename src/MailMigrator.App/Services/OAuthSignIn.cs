using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailMigrator.Core.Connectivity;

namespace MailMigrator.App.Services;

/// <summary>Real system-browser authorization with PKCE and a loopback-only callback.</summary>
public sealed class OAuthSignIn
{
    public const string GoogleClientId = "964453715684-2dtedi03u7rfjclfb5ocmi1dp30qfq4i.apps.googleusercontent.com";
    public const string MicrosoftClientId = "3f13b650-2c82-4164-8822-8045317d7212";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<OAuthCredentials> ConnectAsync(OAuthProvider provider, string email, CancellationToken cancellationToken)
    {
        if (App.IsDemo) throw new InvalidOperationException("La connexion OAuth est disponible dans le logiciel réel, hors démonstration.");
        var credentials = new OAuthCredentials
        {
            Provider = provider,
            ClientId = provider == OAuthProvider.Google ? GoogleClientId : MicrosoftClientId,
            ClientSecret = provider == OAuthProvider.Google ? ReadGoogleClientSecret() : null
        };
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = provider == OAuthProvider.Google ? $"http://127.0.0.1:{port}/" : $"http://localhost:{port}/";
            var authorization = provider == OAuthProvider.Google
                ? "https://accounts.google.com/o/oauth2/v2/auth"
                : "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
            var query = new Dictionary<string, string>
            {
                ["client_id"] = credentials.ClientId, ["redirect_uri"] = redirect,
                ["response_type"] = "code", ["scope"] = OAuthEndpoints.Scope(provider),
                ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
                ["login_hint"] = email, ["prompt"] = provider == OAuthProvider.Google ? "consent" : "select_account"
            };
            if (provider == OAuthProvider.Google) query["access_type"] = "offline";
            Process.Start(new ProcessStartInfo(authorization + "?" + Encode(query)) { UseShellExecute = true });
            var code = await ReceiveCodeAsync(listener, new Uri(redirect), state, timeout.Token);
            var form = new Dictionary<string, string>
            {
                ["client_id"] = credentials.ClientId, ["code"] = code, ["redirect_uri"] = redirect,
                ["grant_type"] = "authorization_code", ["code_verifier"] = verifier
            };
            if (credentials.ClientSecret is not null) form["client_secret"] = credentials.ClientSecret;
            using var body = new FormUrlEncodedContent(form);
            using var response = await Http.PostAsync(OAuthEndpoints.Token(provider), body, timeout.Token);
            await OAuthEndpoints.ApplyResponseAsync(response, credentials, timeout.Token);
            if (credentials.RefreshToken.Length == 0)
                throw new InvalidOperationException("Le fournisseur n’a pas accordé de connexion durable. Reconnectez la boîte en autorisant l’accès hors connexion.");
            return credentials;
        }
        finally { listener.Stop(); }
    }

    private static string ReadGoogleClientSecret()
    {
        // Desktop client configuration is supplied alongside the build. This value is
        // not a user token; Google desktop clients cannot keep it confidential.
        var path = Path.Combine(AppContext.BaseDirectory, "google-desktop-client.json");
        if (!File.Exists(path))
            throw new InvalidOperationException("La configuration Google de cette installation est incomplète. Le fichier du client OAuth doit être intégré à la distribution.");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var installed = json.RootElement.GetProperty("installed");
        if (installed.GetProperty("client_id").GetString() != GoogleClientId)
            throw new InvalidOperationException("La configuration Google ne correspond pas à LEWEBMAX Mail Migrator.");
        return installed.GetProperty("client_secret").GetString()
            ?? throw new InvalidOperationException("La configuration du client Google est incomplète.");
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Encode(Dictionary<string, string> values) => string.Join("&", values.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    private static async Task<string> ReceiveCodeAsync(TcpListener listener, Uri redirect, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var length = 0;
            while (length < buffer.Length)
            {
                int read;
                try { read = await stream.ReadAsync(buffer.AsMemory(length), requestTimeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                if (read == 0) break;
                length += read;
                if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }
            var first = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ');
            var valid = first.Length == 3 && first[0] == "GET" && first[1].StartsWith("/?", StringComparison.Ordinal);
            var values = valid ? ParseQuery(new Uri(redirect, first[1]).Query) : new Dictionary<string, string>();
            valid = valid && values.TryGetValue("state", out var received) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received), Encoding.UTF8.GetBytes(state));
            var success = valid && values.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code) && !values.ContainsKey("error");
            var message = success ? "Autorisation reçue. Revenez dans LEWEBMAX Mail Migrator pour vérifier la connexion IMAP." : "Connexion non autorisée. Revenez dans LEWEBMAX Mail Migrator.";
            var payload = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"fr\"><meta charset=\"utf-8\"><title>LEWEBMAX</title><p>" + message + "</p></html>");
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {payload.Length}\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, requestTimeout.Token);
            await stream.WriteAsync(payload, requestTimeout.Token);
            if (success) return values["code"];
            if (valid && values.ContainsKey("error")) throw new InvalidOperationException("La connexion a été refusée ou annulée auprès du fournisseur.");
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;
            var key = Uri.UnescapeDataString(parts[0]);
            if (!result.TryAdd(key, Uri.UnescapeDataString(parts[1].Replace('+', ' ')))) return [];
        }
        return result;
    }
}
