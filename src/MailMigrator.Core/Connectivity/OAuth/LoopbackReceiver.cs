using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MailMigrator.Core.Connectivity.OAuth;

/// <summary>
/// Petit serveur HTTP local (127.0.0.1, port libre) qui reçoit le retour du navigateur après
/// la connexion : « http://127.0.0.1:PORT/?code=…&amp;state=… ». Aucun droit administrateur requis.
/// </summary>
public sealed class LoopbackReceiver : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public LoopbackReceiver(string host)
    {
        _listener.Start();
        RedirectUri = $"http://{host}:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
    }

    public string RedirectUri { get; }

    /// <summary>Attend le retour du navigateur ; renvoie le code d'autorisation.</summary>
    public async Task<string> WaitForCodeAsync(string expectedState, CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? "";
            var target = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
            var query = ParseQuery(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");

            // Requêtes parasites (favicon…) : on ignore et on continue d'attendre.
            if (!query.ContainsKey("code") && !query.ContainsKey("error"))
            {
                await RespondAsync(stream, 404, "").ConfigureAwait(false);
                continue;
            }

            if (query.TryGetValue("error", out var error))
            {
                await RespondAsync(stream, 200, Page("Connexion annulée", "Vous pouvez fermer cet onglet et revenir à LEWEBMAX Mail Migrator.")).ConfigureAwait(false);
                throw new OAuthException(error == "access_denied" ? "Connexion refusée ou annulée dans le navigateur." : $"Erreur d'autorisation : {error}");
            }
            if (query.GetValueOrDefault("state") != expectedState)
            {
                await RespondAsync(stream, 400, Page("Requête invalide", "Le jeton de sécurité ne correspond pas.")).ConfigureAwait(false);
                throw new OAuthException("Réponse d'autorisation invalide (state différent) : connexion interrompue par sécurité.");
            }
            await RespondAsync(stream, 200, Page("Connexion réussie ✔", "Vous pouvez fermer cet onglet et revenir à LEWEBMAX Mail Migrator.")).ConfigureAwait(false);
            return query["code"];
        }
    }

    internal static Dictionary<string, string> ParseQuery(string query) => query
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");

    private static string Page(string title, string text) => $$"""
        <!doctype html><html lang="fr"><head><meta charset="utf-8"><title>{{title}}</title>
        <style>body{font-family:"Segoe UI",sans-serif;display:grid;place-items:center;height:100vh;margin:0;background:#f6f7f9;color:#0f172a}
        div{background:#fff;padding:32px 40px;border-radius:12px;box-shadow:0 2px 16px #0001;text-align:center}h1{font-size:22px}</style>
        </head><body><div><h1>{{title}}</h1><p>{{text}}</p></div></body></html>
        """;

    private static async Task RespondAsync(NetworkStream stream, int status, string html)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    public void Dispose() => _listener.Stop();
}
