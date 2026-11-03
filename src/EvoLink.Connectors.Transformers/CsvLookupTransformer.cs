using System.ComponentModel;
using System.Text;
using EvoLink.Connectors.Common;
using Ortolan.Core.Exceptions;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Transformers
{
    /// <summary>
    /// Port of the EvoBus <c>CsvMatchingAdditionsTransformer</c>: looks the value of a property up in a column of a CSV file
    /// and copies the value of another column of the matching row into the entity.
    /// Differences: delimiter / encoding / case sensitivity are configurable, the path is resolved against the working
    /// directory, the file is read once per job (thread-safe), a value without match is reported (warning or failure with
    /// <see cref="FailWhenNotFound"/>), a missing file is a configuration error and an unreadable file a source unavailability.
    /// </summary>
    [OrtolanType("csv-lookup")]
    [Description("Complète les entités avec une valeur lue dans un fichier CSV de correspondance (colonne de recherche → colonne de données).")]
    public class CsvLookupTransformer : TransformerModule
    {
        private readonly Lock _lock = new();
        private Dictionary<string, string?>? _table;

        [Description("Propriété de l'entité contenant la valeur recherchée.")]
        public string From { get; set; } = string.Empty;

        [Description("Propriété recevant la valeur trouvée.")]
        public string To { get; set; } = string.Empty;

        [Description("Chemin du fichier CSV (relatif au dossier de travail ou absolu).")]
        public string FilePath { get; set; } = string.Empty;

        [Description("Colonne du CSV comparée à la valeur de la propriété 'from'.")]
        public string MatchingColumn { get; set; } = string.Empty;

        [Description("Colonne du CSV dont la valeur est copiée dans la propriété 'to'.")]
        public string DataColumn { get; set; } = string.Empty;

        [Description("Séparateur de colonnes.")]
        public string Delimiter { get; set; } = ";";

        [Description("Encodage du fichier (utf-8, windows-1252, iso-8859-1...).")]
        public string Encoding { get; set; } = "utf-8";

        [Description("Comparaison insensible à la casse.")]
        public bool IgnoreCase { get; set; }

        [Description("Supprime les espaces autour des valeurs comparées.")]
        public bool Trim { get; set; } = true;

        [Description("Met l'entité en échec lorsqu'aucune ligne ne correspond (sinon simple avertissement).")]
        public bool FailWhenNotFound { get; set; }

        public override Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            var table = GetTable(context);
            var raw = Helpers.Text(Helpers.GetRequired(entity, From));
            var key = Trim ? raw.Trim() : raw;

            if (table.TryGetValue(key, out var value))
            {
                entity[To] = value;
                return Task.FromResult<Entity?>(entity);
            }

            var message = $"[{DisplayName}] Aucune ligne du fichier '{FilePath}' ne correspond à la valeur '{key}' (colonne '{MatchingColumn}').";

            if (FailWhenNotFound)
                throw new KeyNotFoundException(message);

            context.LogWarning(message);
            return Task.FromResult<Entity?>(entity);
        }

        private Dictionary<string, string?> GetTable(JobContext context)
        {
            if (_table != null)
                return _table;

            lock (_lock)
            {
                return _table ??= Load(context);
            }
        }

        private Dictionary<string, string?> Load(JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("from", From), ("to", To), ("filePath", FilePath), ("matchingColumn", MatchingColumn), ("dataColumn", DataColumn), ("delimiter", Delimiter));

            var path = context.ResolvePath(FilePath);

            if (!File.Exists(path))
                throw ConnectorGuard.Invalid(DisplayName, $"Fichier CSV introuvable : '{path}'.");

            var encoding = CsvReader.GetEncoding(Encoding) ?? throw ConnectorGuard.Invalid(DisplayName, $"Encodage '{Encoding}' inconnu.");

            List<string[]> rows;
            try
            {
                rows = CsvReader.Read(File.ReadAllText(path, encoding), Delimiter);
            }
            catch (Exception ex) when (ExceptionClassifier.IsAccessFailure(ex))
            {
                throw ConnectorGuard.SourceUnavailable(DisplayName, path, ex);
            }

            if (rows.Count == 0)
                throw ConnectorGuard.Invalid(DisplayName, $"Le fichier CSV '{path}' est vide.");

            var header = rows[0].Select(h => h.Trim()).ToList();
            var matchingIndex = header.FindIndex(h => string.Equals(h, MatchingColumn, StringComparison.OrdinalIgnoreCase));
            var dataIndex = header.FindIndex(h => string.Equals(h, DataColumn, StringComparison.OrdinalIgnoreCase));

            if (matchingIndex < 0 || dataIndex < 0)
                throw ConnectorGuard.Invalid(DisplayName, $"Colonne(s) absente(s) du fichier '{path}' : {string.Join(", ", new[] { matchingIndex < 0 ? MatchingColumn : null, dataIndex < 0 ? DataColumn : null }.OfType<string>())}.");

            var table = new Dictionary<string, string?>(IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            // Like EvoBus, the last row wins when a value appears several times.
            foreach (var row in rows.Skip(1))
            {
                if (row.Length <= Math.Max(matchingIndex, dataIndex))
                    continue;

                var key = Trim ? row[matchingIndex].Trim() : row[matchingIndex];
                table[key] = Trim ? row[dataIndex].Trim() : row[dataIndex];
            }

            context.LogDebug($"[{DisplayName}] {table.Count} correspondance(s) chargée(s) depuis '{path}'.");

            return table;
        }
    }

    /// <summary>
    /// Minimal RFC 4180 reader: quoted fields, doubled quotes and line breaks inside quotes.
    /// </summary>
    internal static class CsvReader
    {
        public static Encoding? GetEncoding(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || name.Equals("utf8", StringComparison.OrdinalIgnoreCase)
                || name.Equals("utf-8-bom", StringComparison.OrdinalIgnoreCase))
                return new UTF8Encoding(false);

            System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            try
            {
                return System.Text.Encoding.GetEncoding(name);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        public static List<string[]> Read(string content, string delimiter)
        {
            var rows = new List<string[]>();
            var fields = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;
            var fieldStarted = false;
            var i = 0;

            void EndField()
            {
                fields.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }

            void EndRow()
            {
                EndField();

                if (fields.Count > 1 || fields[0].Length > 0)
                    rows.Add([.. fields]);

                fields.Clear();
            }

            while (i < content.Length)
            {
                var c = content[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < content.Length && content[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }

                        inQuotes = false;
                        i++;
                        continue;
                    }

                    field.Append(c);
                    i++;
                    continue;
                }

                if (c == '"' && !fieldStarted)
                {
                    inQuotes = true;
                    fieldStarted = true;
                    i++;
                }
                else if (string.CompareOrdinal(content, i, delimiter, 0, delimiter.Length) == 0)
                {
                    EndField();
                    i += delimiter.Length;
                }
                else if (c is '\r' or '\n')
                {
                    EndRow();
                    i += c == '\r' && i + 1 < content.Length && content[i + 1] == '\n' ? 2 : 1;
                }
                else
                {
                    field.Append(c);
                    fieldStarted = true;
                    i++;
                }
            }

            if (field.Length > 0 || fields.Count > 0)
                EndRow();

            return rows;
        }
    }
}
