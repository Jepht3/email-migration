namespace MailMigrator.Core.Import;

public sealed record CsvAccountPair(string SourceEmail, string SourcePassword, string DestinationEmail, string DestinationPassword);

public sealed record CsvImportResult(IReadOnlyList<CsvAccountPair> Pairs, IReadOnlyList<string> Errors);

/// <summary>
/// Import de paires de boîtes : source ; mdp source ; destination ; mdp destination.
/// Séparateur « ; » ou « , » détecté automatiquement, ligne d'en-tête facultative,
/// champs entre guillemets acceptés.
/// </summary>
public static class CsvJobImporter
{
    public static CsvImportResult Parse(TextReader reader)
    {
        var pairs = new List<CsvAccountPair>();
        var errors = new List<string>();
        var lineNumber = 0;
        char? separator = null;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            separator ??= line.Count(c => c == ';') >= line.Count(c => c == ',') ? ';' : ',';

            var fields = Split(line, separator.Value);
            if (lineNumber == 1 && !fields[0].Contains('@')) continue; // en-tête

            if (fields.Count < 4)
            {
                errors.Add($"Ligne {lineNumber} : 4 colonnes attendues, {fields.Count} trouvée(s).");
                continue;
            }
            if (!fields[0].Contains('@') || !fields[2].Contains('@'))
            {
                errors.Add($"Ligne {lineNumber} : adresse email invalide.");
                continue;
            }
            pairs.Add(new(fields[0], fields[1], fields[2], fields[3]));
        }
        return new(pairs, errors);
    }

    private static List<string> Split(string line, char separator)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == separator && !quoted)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else current.Append(c);
        }
        fields.Add(current.ToString().Trim());
        return fields;
    }
}
