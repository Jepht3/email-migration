using System.Globalization;
using System.Text;
using MailMigrator.Core.Formatting;
using MailMigrator.Core.Models;

namespace MailMigrator.Core.Reporting;

/// <summary>
/// Exports du rapport à envoyer au client : CSV (Excel, séparateur « ; ») et HTML autonome
/// reprenant le design de l'application, propre à l'impression et lisible sur mobile.
/// </summary>
public static class ReportExporter
{
    /// <summary>Couleurs d'identité des tâches (mêmes teintes que l'application).</summary>
    private static readonly string[] JobColors = ["#2563EB", "#7C3AED", "#0D9488", "#EA580C", "#DB2777"];

    public static string StatusText(MessageStatus status) => status switch
    {
        MessageStatus.Copied => "Copié",
        MessageStatus.Skipped => "Déjà présent",
        MessageStatus.Failed => "Échec",
        _ => "En attente",
    };

    public static string StatusText(JobStatus status) => status switch
    {
        JobStatus.Completed => "Migration terminée",
        JobStatus.CompletedWithErrors => "Migration terminée avec des erreurs",
        JobStatus.Failed => "La migration a échoué",
        JobStatus.Cancelled => "Migration annulée",
        _ => "Migration en cours",
    };

    /// <summary>Nom de fichier proposé : « Rapport migration contact@client.fr 2026-10-27 1630 ».</summary>
    public static string SuggestedFileName(MigrationHistoryEntry entry, string extension)
    {
        var name = $"Rapport migration {entry.SourceEmail} {entry.StartedAt.LocalDateTime:yyyy-MM-dd HHmm}";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name + extension;
    }

    // ------------------------------------------------------------------ CSV

    /// <summary>Un message par ligne ; UTF-8 avec BOM pour qu'Excel affiche correctement les accents.</summary>
    public static async Task WriteCsvAsync(JobReport report, Stream output, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        await writer.WriteLineAsync("Dossier;Date;Expéditeur;Sujet;Taille (octets);Statut;Erreur");
        foreach (var m in report.Messages)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(';',
                Csv(m.Folder),
                Csv(m.Date.LocalDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)),
                Csv(m.Sender),
                Csv(m.Subject),
                m.Size.ToString(CultureInfo.InvariantCulture),
                Csv(StatusText(m.Status)),
                Csv(m.Error ?? "")));
        }
    }

    /// <summary>Échappement CSV + neutralisation des formules (=, +, -, @) pour éviter toute injection dans Excel.</summary>
    internal static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    // ------------------------------------------------------------------ HTML

    public static string BuildHtml(JobReport report)
    {
        var e = report.Summary;
        var color = JobColors[((e.ColorIndex % JobColors.Length) + JobColors.Length) % JobColors.Length];
        var finished = e.Status.IsFinished();
        var ok = finished && e.Failed == 0 && e.Status == JobStatus.Completed;
        var badge = !finished ? ("#0284C7", "#E0F2FE", "En cours") : ok ? ("#15803D", "#DCFCE7", "Réussie") : ("#B45309", "#FEF3C7", "À vérifier");
        var failures = report.Messages.Where(m => m.Status == MessageStatus.Failed).ToList();
        var gaps = report.Folders.Sum(f => f.Gap);
        // Échappe seulement les caractères dangereux : les accents restent lisibles dans le fichier.
        static string H(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        var started = e.StartedAt.LocalDateTime.ToString("dd/MM/yyyy 'à' HH:mm", Format.French);
        var failedCss = e.Failed > 0 ? " err" : "";

        var html = new StringBuilder();
        html.Append($$"""
            <!doctype html>
            <html lang="fr">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Rapport de migration — {{H(e.SourceEmail)}}</title>
            <style>
            :root{--accent:{{color}};--fg:#0f172a;--muted:#526075;--line:#e5e7eb;--bg:#f6f7f9;--card:#fff;--ok:#16a34a;--warn:#b45309;--err:#dc2626}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}
            main{max-width:1100px;margin:0 auto;padding:32px 16px}
            header{display:flex;align-items:center;gap:16px;margin-bottom:24px}
            .logo{width:44px;height:44px;flex:none}
            .brand{font-weight:600;font-size:16px}.brand small{display:block;color:var(--muted);font-weight:400;font-size:12px}
            .card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:20px 24px;margin-bottom:16px;box-shadow:0 2px 12px #0f172a0a}
            .hero{border-top:4px solid var(--accent)}
            h1{font:600 26px "Segoe UI Variable Display","Segoe UI",sans-serif;margin:0 0 6px}h2{font-size:17px;margin:0 0 12px}
            .badge{display:inline-block;padding:2px 10px;border-radius:999px;font-size:12px;font-weight:600;vertical-align:middle;margin-left:8px}
            .muted{color:var(--muted)}.num{font-variant-numeric:tabular-nums}
            .tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin-bottom:16px}
            .tile{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px 16px}
            .tile b{display:block;font:600 24px "Segoe UI Variable Display","Segoe UI",sans-serif}.tile span{color:var(--muted);font-size:12px}
            .table-wrap{overflow-x:auto}table{width:100%;border-collapse:collapse;font-size:13px}
            th{text-align:left;color:var(--muted);font-size:12px;font-weight:600;border-bottom:1px solid var(--line);padding:8px}
            td{border-bottom:1px solid var(--line);padding:8px;vertical-align:top}td.r,th.r{text-align:right}
            tr.gap td{background:#fef3c7}.ok{color:var(--ok);font-weight:600}.warn{color:var(--warn);font-weight:600}.err{color:var(--err)}
            footer{color:var(--muted);font-size:12px;text-align:center;margin-top:24px}
            @media print{
              body{background:#fff} main{padding:0} .card,.tile{box-shadow:none;break-inside:avoid} tr{break-inside:avoid}
            }
            </style>
            </head>
            <body><main>
            <header>
              <svg class="logo" viewBox="0 0 32 32" aria-hidden="true"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#3B82F6"/><stop offset="1" stop-color="#7C3AED"/></linearGradient></defs>
                <rect width="32" height="32" rx="8" fill="url(#g)"/><path d="M7.5 10.5H21a1.5 1.5 0 0 1 1.5 1.5v9A1.5 1.5 0 0 1 21 22.5H7.5A1.5 1.5 0 0 1 6 21v-9a1.5 1.5 0 0 1 1.5-1.5zM6.5 11.5l7.75 5.5L22 11.5" fill="none" stroke="#fff" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>
              <div class="brand">LEWEBMAX<small>Rapport de migration d'emails</small></div>
            </header>
            <section class="card hero">
              <h1>{{H(StatusText(e.Status))}}<span class="badge" style="color:{{badge.Item1}};background:{{badge.Item2}}">{{badge.Item3}}</span></h1>
              <p style="margin:0"><b>{{H(e.SourceEmail)}}</b> → <b>{{H(e.DestinationEmail)}}</b></p>
              <p class="muted" style="margin:4px 0 0">Démarrée le {{started}} · durée {{H(Format.Duration(e.Duration))}}</p>
            </section>
            <div class="tiles">
              <div class="tile"><b class="num">{{Format.Number(e.SourceMessages)}}</b><span>Messages dans la source</span></div>
              <div class="tile"><b class="num ok">{{Format.Number(e.Copied)}}</b><span>Copiés</span></div>
              <div class="tile"><b class="num">{{Format.Number(e.Skipped)}}</b><span>Déjà présents (ignorés)</span></div>
              <div class="tile"><b class="num{{failedCss}}">{{Format.Number(e.Failed)}}</b><span>Échecs</span></div>
              <div class="tile"><b class="num">{{H(Format.Bytes(e.Bytes))}}</b><span>Volume copié</span></div>
            </div>
            <section class="card"><h2>Bilan par dossier</h2><div class="table-wrap"><table>
              <thead><tr><th>Dossier source</th><th>Dossier destination</th><th class="r">Source</th><th class="r">Copiés</th><th class="r">Ignorés</th><th class="r">Échecs</th><th class="r">Volume</th></tr></thead><tbody>
            """);
        foreach (var f in report.Folders)
            html.Append($"<tr><td>{H(f.SourcePath)}</td><td>{H(f.DestinationPath)}</td><td class=\"r num\">{Format.Number(f.SourceCount)}</td><td class=\"r num\">{Format.Number(f.Copied)}</td>"
                        + $"<td class=\"r num\">{Format.Number(f.Skipped)}</td><td class=\"r num{(f.Failed > 0 ? " err" : "")}\">{Format.Number(f.Failed)}</td><td class=\"r num\">{H(Format.Bytes(f.Bytes))}</td></tr>\n");
        html.Append($"""
            </tbody></table></div></section>
            <section class="card"><h2>Vérification source / destination</h2>
              <p class="muted" style="margin-top:-6px">Recomptage final des messages de chaque dossier, des deux côtés. {(gaps == 0 ? "<span class=\"ok\">Aucun écart.</span>" : $"<span class=\"warn\">{Format.Number(gaps)} message(s) d'écart.</span>")}</p>
              <div class="table-wrap"><table><thead><tr><th>Dossier</th><th class="r">Source</th><th class="r">Destination</th><th class="r">Écart</th></tr></thead><tbody>
            """);
        foreach (var f in report.Folders)
            html.Append($"<tr{(f.Gap > 0 ? " class=\"gap\"" : "")}><td>{H(f.DestinationPath)}</td><td class=\"r num\">{Format.Number(f.SourceCount)}</td><td class=\"r num\">{Format.Number(f.DestinationCountAfter)}</td>"
                        + $"<td class=\"r\">{(f.Gap == 0 ? "<span class=\"ok\">✔ OK</span>" : $"<span class=\"warn\">⚠ −{Format.Number(f.Gap)}</span>")}</td></tr>\n");
        html.Append("</tbody></table></div></section>\n");

        if (failures.Count > 0)
        {
            html.Append($"<section class=\"card\"><h2>Messages en échec ({Format.Number(failures.Count)})</h2><div class=\"table-wrap\"><table><thead><tr><th>Date</th><th>Dossier</th><th>Expéditeur</th><th>Sujet</th><th>Raison</th></tr></thead><tbody>\n");
            foreach (var m in failures)
                html.Append($"<tr><td class=\"num\">{m.Date.LocalDateTime:dd/MM/yyyy}</td><td>{H(m.Folder)}</td><td>{H(m.Sender)}</td><td>{H(m.Subject)}</td><td class=\"err\">{H(m.Error)}</td></tr>\n");
            html.Append("</tbody></table></div></section>\n");
        }

        html.Append($"<section class=\"card\"><h2>Détail des messages ({Format.Number(report.Messages.Count)})</h2><div class=\"table-wrap\"><table><thead><tr><th>Date</th><th>Dossier</th><th>Expéditeur</th><th>Sujet</th><th class=\"r\">Taille</th><th>Statut</th></tr></thead><tbody>\n");
        foreach (var m in report.Messages)
        {
            var css = m.Status switch { MessageStatus.Copied => "ok", MessageStatus.Failed => "err", _ => "muted" };
            html.Append($"<tr><td class=\"num\">{m.Date.LocalDateTime:dd/MM/yyyy}</td><td>{H(m.Folder)}</td><td>{H(m.Sender)}</td><td>{H(m.Subject)}</td>"
                        + $"<td class=\"r num\">{H(Format.Bytes(m.Size))}</td><td class=\"{css}\">{StatusText(m.Status)}</td></tr>\n");
        }
        html.Append($"""
            </tbody></table></div></section>
            <footer>Rapport généré le {DateTime.Now.ToString("dd/MM/yyyy 'à' HH:mm", Format.French)} par LEWEBMAX Mail Migrator.</footer>
            </main></body></html>
            """);
        return html.ToString();
    }
}
