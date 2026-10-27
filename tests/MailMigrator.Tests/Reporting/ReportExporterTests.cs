using System.Text;
using MailMigrator.Core.Models;
using MailMigrator.Core.Reporting;

namespace MailMigrator.Tests.Reporting;

public class ReportExporterTests
{
    private static JobReport Sample(JobStatus status = JobStatus.CompletedWithErrors) => new(
        new MigrationHistoryEntry
        {
            Id = Guid.NewGuid(),
            ColorIndex = 2,
            SourceEmail = "contact@client.fr",
            DestinationEmail = "contact@nouveau.fr",
            StartedAt = new DateTimeOffset(2026, 10, 27, 16, 30, 0, TimeSpan.FromHours(1)),
            Duration = TimeSpan.FromMinutes(12),
            Status = status,
            SourceMessages = 1234,
            Copied = 1200,
            Skipped = 33,
            Failed = 1,
            Bytes = 50_000_000,
        },
        [
            new FolderReport("INBOX", "INBOX", SpecialFolder.Inbox, 1000, 990, 9, 1, 40_000_000, 999),
            new FolderReport("Sent", "Envoyés", SpecialFolder.Sent, 234, 210, 24, 0, 10_000_000, 234),
        ],
        [
            new MessageReportLine("INBOX", new DateTimeOffset(2024, 3, 12, 9, 0, 0, TimeSpan.Zero), "Dupont; SARL", "Devis \"urgent\"\nligne 2", 2048, MessageStatus.Copied, null),
            new MessageReportLine("INBOX", new DateTimeOffset(2024, 3, 13, 9, 0, 0, TimeSpan.Zero), "=HYPERLINK(\"x\")", "<script>alert(1)</script>", 999, MessageStatus.Failed, "Message trop volumineux"),
        ]);

    [Fact]
    public async Task Csv_is_excel_friendly_escaped_and_injection_safe()
    {
        using var stream = new MemoryStream();

        await ReportExporter.WriteCsvAsync(Sample(), stream);

        var bytes = stream.ToArray();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // BOM UTF-8 pour Excel
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n");
        Assert.Equal("Dossier;Date;Expéditeur;Sujet;Taille (octets);Statut;Erreur", lines[0]);
        Assert.StartsWith("INBOX;12/03/2024", lines[1]);
        Assert.Contains(";\"Dupont; SARL\";\"Devis \"\"urgent\"\"\nligne 2\";2048;Copié;", string.Join("\r\n", lines));
        Assert.Contains(";\"'=HYPERLINK(\"\"x\"\")\";", string.Join("\r\n", lines));
        Assert.Contains("Échec;Message trop volumineux", string.Join("\r\n", lines));
    }

    [Fact]
    public void Html_contains_key_figures_and_escapes_message_content()
    {
        var html = ReportExporter.BuildHtml(Sample());

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("Migration terminée avec des erreurs", html);
        Assert.Contains("contact@client.fr", html);
        Assert.Contains("1 200", html); // nombres au format français (espace fine insécable)
        Assert.Contains("Messages en échec (1)", html);
        Assert.Contains("⚠ −1", html); // écart sur INBOX mis en évidence
        Assert.Contains("--accent:#0D9488", html); // couleur d'identité de la tâche
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("@media print", html);
        Assert.Contains("width=device-width", html);
    }

    [Fact]
    public void In_progress_report_is_labelled_as_such()
    {
        var html = ReportExporter.BuildHtml(Sample(JobStatus.Copying));

        Assert.Contains("Migration en cours", html);
        Assert.Contains(">En cours<", html);
    }

    [Fact]
    public void Suggested_file_name_is_safe()
    {
        Assert.Equal("Rapport migration contact@client.fr 2026-10-27 1630.html",
            ReportExporter.SuggestedFileName(Sample().Summary with { StartedAt = new DateTimeOffset(2026, 10, 27, 16, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 27))) }, ".html"));
    }
}
