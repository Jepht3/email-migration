using MailMigrator.Core.Import;

namespace MailMigrator.Tests;

public class CsvJobImporterTests
{
    [Fact]
    public void Parse_skips_header_and_detects_semicolon()
    {
        var csv = "source;mdp source;destination;mdp destination\n" +
                  "a@old.fr;\"p;1\";a@new.fr;p2\n";

        var result = CsvJobImporter.Parse(new StringReader(csv));

        var pair = Assert.Single(result.Pairs);
        Assert.Equal("a@old.fr", pair.SourceEmail);
        Assert.Equal("p;1", pair.SourcePassword);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_reports_incomplete_lines()
    {
        var result = CsvJobImporter.Parse(new StringReader("a@old.fr,p1,a@new.fr\n"));

        Assert.Empty(result.Pairs);
        Assert.Contains("Ligne 1", Assert.Single(result.Errors));
    }
}
