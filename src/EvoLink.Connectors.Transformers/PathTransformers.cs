using System.ComponentModel;
using System.Globalization;
using EvoLink.Connectors.Common;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Transformers
{
    /// <summary>
    /// Port of the EvoBus <c>DirectoryExtractFromPathByIndexTransformer</c>: extracts one segment (folder or file name) of
    /// a path. Differences: '\' and '/' are both separators whatever the platform, the root is only removed at the start of
    /// the path (EvoBus replaced it anywhere) and is optional, a negative depth counts from the end (-1 = file name).
    /// </summary>
    [OrtolanType("path-segment")]
    [Description("Extrait un segment (dossier ou nom de fichier) d'un chemin, par sa position.")]
    public class PathSegmentTransformer : TransformerModule
    {
        [Description("Propriété contenant le chemin.")]
        public string From { get; set; } = string.Empty;

        [Description("Propriété recevant le segment extrait.")]
        public string To { get; set; } = string.Empty;

        [Description("Racine retirée du début du chemin avant le découpage (vide = chemin complet).")]
        public string? RootPath { get; set; }

        [Description("Position du segment : 1 = premier segment après la racine ; -1 = dernier segment (nom du fichier), -2 = dossier parent...")]
        public int Depth { get; set; } = 1;

        public override Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("from", From), ("to", To));

            if (Depth == 0)
                throw ConnectorGuard.Invalid(DisplayName, "La propriété 'depth' ne peut pas valoir 0 (1 = premier segment, -1 = dernier).");

            var path = Helpers.Text(Helpers.GetRequired(entity, From));

            if (!string.IsNullOrWhiteSpace(RootPath))
                path = Helpers.RelativeTo(path, RootPath);

            var segments = Helpers.Segments(path);
            var index = Depth > 0 ? Depth - 1 : segments.Length + Depth;

            if (index < 0 || index >= segments.Length)
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Le chemin '{path}' ne contient pas de segment à la position {Depth} ({segments.Length} segment(s))."));

            entity[To] = segments[index];

            return Task.FromResult<Entity?>(entity);
        }
    }

    /// <summary>
    /// Port of the EvoBus <c>PathToFileInfoTransformer</c>: adds the information of a file (size, dates, name, extension,
    /// relative path...) under the EvoBus keys (<see cref="FileKeys"/>).
    /// Differences: <see cref="RelativeRoot"/> is optional and removed only at the start of the path, a missing file fails
    /// the entity (or only the path-derived keys are set when <see cref="RequireFile"/> is false), the readable size is
    /// culture-invariant, <c>FILE-ExcludedName</c> is the name without extension (EvoBus put the full path when no exclusion
    /// was configured), <c>FILE-NumberParentFolders</c> counts the folders between the root and the file (EvoBus counted
    /// every segment of the full path), <c>FILE-FilePath</c> never contains the "\\?\" long path prefix.
    /// </summary>
    [OrtolanType("file-info")]
    [Description("Ajoute les informations d'un fichier (taille, dates, nom, extension, chemin relatif...) sous les clés FILE-* d'EvoBus.")]
    public class FileInfoTransformer : TransformerModule
    {
        private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

        [Description("Propriété contenant le chemin du fichier.")]
        public string FilePathProperty { get; set; } = FileKeys.FilePath;

        [Description("Racine retirée du chemin pour calculer FILE-RelativePath (optionnelle : vide = chemin complet).")]
        public string? RelativeRoot { get; set; }

        [Description("Extensions retirées de la fin du nom avant de calculer FILE-ExcludedName et FILE-Extension (ex. p7m, tmp).")]
        public List<string> ExtensionExclusions { get; set; } = [];

        [Description("Met l'entité en échec lorsque le chemin est vide, hors de 'relativeRoot' ou que le fichier n'existe pas (sinon l'entité continue avec les informations disponibles).")]
        public bool RequireFile { get; set; } = true;

        public override Task<Entity?> TransformAsync(Entity entity, JobContext context)
        {
            ConnectorGuard.Required(DisplayName, ("filePathProperty", FilePathProperty));

            var raw = RequireFile
                ? Helpers.Text(Helpers.GetRequired(entity, FilePathProperty)).Trim()
                : Helpers.Text(entity.GetValueOrDefault(FilePathProperty)).Trim();

            if (raw.Length == 0)
            {
                if (RequireFile)
                    throw new InvalidOperationException($"La propriété '{FilePathProperty}' est vide.");

                // No file for this entity: it goes on without the FILE-* information.
                context.LogDebug($"[{DisplayName}] Propriété '{FilePathProperty}' vide : aucune information de fichier ajoutée.");
                return Task.FromResult<Entity?>(entity);
            }

            var filePath = Helpers.NormalizeSeparators(raw);
            string relativePath;

            try
            {
                relativePath = string.IsNullOrWhiteSpace(RelativeRoot) ? filePath : Helpers.RelativeTo(filePath, RelativeRoot);
            }
            catch (InvalidOperationException ex) when (!RequireFile)
            {
                context.LogWarning($"[{DisplayName}] {ex.Message} FILE-RelativePath reçoit le chemin complet.");
                relativePath = filePath;
            }
            var fileInfo = new FileInfo(ToIoPath(filePath));

            if (fileInfo.Exists)
            {
                entity[FileKeys.ByteSize] = fileInfo.Length;
                entity[FileKeys.ReadableSize] = ToReadableSize(fileInfo.Length);
                entity[FileKeys.CreationTimeStamp] = fileInfo.CreationTime.Date;
                entity[FileKeys.LastModificationTimeStamp] = fileInfo.LastWriteTime.Date;
            }
            else if (RequireFile)
            {
                throw new FileNotFoundException($"Fichier introuvable : '{filePath}'.", filePath);
            }
            else
            {
                context.LogDebug($"[{DisplayName}] Fichier '{filePath}' introuvable : seules les informations tirées du chemin sont ajoutées.");
            }

            var name = Path.GetFileName(filePath);

            entity[FileKeys.FilePath] = filePath;
            entity[FileKeys.Name] = name;
            entity[FileKeys.NameWithoutExtension] = Path.GetFileNameWithoutExtension(filePath);
            entity[FileKeys.RelativePath] = relativePath;
            entity[FileKeys.NumberParentFolders] = CountParentFolders(filePath, relativePath);
            entity[FileKeys.ExcludedName] = Path.GetFileNameWithoutExtension(name);
            entity[FileKeys.Extension] = Path.GetExtension(name);

            var exclusions = ExtensionExclusions
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim().StartsWith('.') ? e.Trim() : "." + e.Trim())
                .ToList();

            if (exclusions.Count == 0)
                return Task.FromResult<Entity?>(entity);

            entity[FileKeys.Exclusions] = string.Join(";", ExtensionExclusions.Select(e => e.Trim()));

            var excluded = name;
            var removed = string.Empty;

            while (exclusions.FirstOrDefault(e => excluded.Length > e.Length && excluded.EndsWith(e, StringComparison.OrdinalIgnoreCase)) is { } extension)
            {
                removed = excluded[^extension.Length..] + removed;
                excluded = excluded[..^extension.Length];
            }

            entity[FileKeys.ExcludedExtension] = removed;
            entity[FileKeys.ExcludedName] = Path.GetFileNameWithoutExtension(excluded);
            entity[FileKeys.Extension] = Path.GetExtension(excluded);

            return Task.FromResult<Entity?>(entity);
        }

        /// <summary>Number of folders between the root (relative root, drive or share) and the file.</summary>
        private int CountParentFolders(string filePath, string relativePath)
        {
            if (!string.IsNullOrWhiteSpace(RelativeRoot))
                return Math.Max(Helpers.Segments(relativePath).Length - 1, 0);

            var root = Path.GetPathRoot(filePath) ?? string.Empty;
            return Math.Max(Helpers.Segments(filePath[root.Length..]).Length - 1, 0);
        }

        /// <summary>Path used to access the file: the "\\?\" prefix lifts the 260 characters limit of Windows.</summary>
        private static string ToIoPath(string path)
        {
            if (!OperatingSystem.IsWindows() || path.Length <= 250 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return path;

            return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        }

        /// <summary>"1.52 MB": size in the largest unit below 1024, truncated to two decimals (as EvoBus, but culture-invariant).</summary>
        internal static string ToReadableSize(long bytes)
        {
            var value = (decimal)bytes;
            var unit = 0;

            while (value >= 1024 && unit < SizeUnits.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            var truncated = Math.Truncate(value * 100) / 100;
            return $"{truncated.ToString("0.##", CultureInfo.InvariantCulture)} {SizeUnits[unit]}";
        }
    }
}
