using System.ComponentModel;
using System.Text.RegularExpressions;
using EvoLink.Connectors.Common;
using Ortolan.Core.Models;
using Ortolan.Core.Modules;
using Ortolan.Core.Pipeline;
using Ortolan.Core.Serialization;

namespace EvoLink.Connectors.Transformers
{
    /// <summary>
    /// Port of the EvoBus <c>FileSystemCleanerPostProcessor</c>: deletes (or archives) the files of the entities processed
    /// successfully, then optionally the folders left empty. Differences: the files of the failed or skipped entities are never
    /// touched, only the files inside <see cref="RootPath"/> are handled when it is set, the root folder itself is never deleted,
    /// and a failure is reported instead of being only logged.
    /// </summary>
    [OrtolanType("file-cleanup")]
    [Description("Supprime ou archive les fichiers des entités traitées avec succès et, en option, les dossiers devenus vides (FileSystemCleanerPostProcessor d'EvoBus). À placer après le writer / synchroniser.")]
    public class FileCleanupProcessor : ProcessorModule
    {
        [Description("Propriété contenant le chemin du fichier.")]
        public string FilePathProperty { get; set; } = FileKeys.FilePath;

        [Description("Dossier racine : seuls les fichiers qu'il contient sont traités. Obligatoire pour supprimer les dossiers vides.")]
        public string? RootPath { get; set; }

        [Description("Dossier d'archive : les fichiers y sont déplacés (arborescence relative à la racine conservée) au lieu d'être supprimés.")]
        public string? MoveTo { get; set; }

        [Description("Supprime les dossiers devenus vides sous la racine (la racine elle-même est conservée).")]
        public bool DeleteEmptyDirectories { get; set; }

        [Description("Expression régulière sur le chemin relatif des dossiers à conserver même vides.")]
        public string? KeepDirectoriesPattern { get; set; }

        [Description("Résultats des entités dont le fichier est traité.")]
        public List<SyncResult> OnResults { get; set; } = [SyncResult.Created, SyncResult.Updated, SyncResult.Unchanged];

        [Description("Met l'entité en échec lorsque son fichier ne peut pas être supprimé ou déplacé (sinon simple avertissement).")]
        public bool FailOnError { get; set; }

        public override Task<List<Entity>> ProcessAsync(List<Entity> entities, JobContext context)
        {
            ConnectorGuard.Required(DisplayName, (nameof(FilePathProperty), FilePathProperty));

            if (DeleteEmptyDirectories && string.IsNullOrWhiteSpace(RootPath))
                throw ConnectorGuard.Invalid(DisplayName, "rootPath est obligatoire pour supprimer les dossiers vides.");

            var root = string.IsNullOrWhiteSpace(RootPath) ? null : Path.GetFullPath(context.ResolvePath(RootPath));
            var archive = string.IsNullOrWhiteSpace(MoveTo) ? null : Path.GetFullPath(context.ResolvePath(MoveTo));
            var keep = string.IsNullOrWhiteSpace(KeepDirectoriesPattern) ? null : Helpers.CreateRegex(DisplayName, KeepDirectoriesPattern, true, 1000);
            int done = 0, skipped = 0, failed = 0;

            foreach (var entity in entities)
            {
                if (!OnResults.Contains(entity.SyncResult) || entity.SyncAction == SyncAction.Delete)
                    continue;

                if (ValueConverter.ToText(entity.GetValueOrDefault(FilePathProperty)) is not { Length: > 0 } text)
                    continue;

                var path = Path.GetFullPath(context.ResolvePath(text.Trim()));

                if (root != null && !IsUnder(path, root))
                {
                    context.LogWarning($"[{DisplayName}] '{path}' est hors du dossier racine '{root}' : fichier conservé.");
                    skipped++;
                    continue;
                }

                if (!File.Exists(path))
                {
                    context.LogDebug($"[{DisplayName}] '{path}' n'existe plus.");
                    continue;
                }

                try
                {
                    if (archive == null)
                        File.Delete(path);
                    else
                        Archive(path, root, archive);

                    done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    var message = $"[{DisplayName}] Impossible de {(archive == null ? "supprimer" : "déplacer")} '{path}' : {ex.Message}";

                    if (FailOnError)
                    {
                        entity.SyncResult = SyncResult.Failed;
                        entity.Error = message;
                        context.Report.Increment(SyncResult.Failed);
                        context.LogError(message);
                    }
                    else
                    {
                        context.LogWarning(message);
                    }
                }
            }

            var directories = DeleteEmptyDirectories && root != null && Directory.Exists(root) ? DeleteEmpty(root, root, keep) : 0;

            context.LogInfo($"[{DisplayName}] {done} fichier(s) {(archive == null ? "supprimé(s)" : "archivé(s)")}, {skipped} hors racine, {failed} en erreur, {directories} dossier(s) vide(s) supprimé(s).");

            return Task.FromResult(entities);
        }

        private static bool IsUnder(string path, string root)
            => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static void Archive(string path, string? root, string archive)
        {
            var relative = root != null ? Path.GetRelativePath(root, path) : Path.GetFileName(path);
            var destination = Path.Combine(archive, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // An archived file is never overwritten: a numbered name is used instead.
            var candidate = destination;
            for (var i = 1; File.Exists(candidate); i++)
                candidate = Path.Combine(Path.GetDirectoryName(destination)!, $"{Path.GetFileNameWithoutExtension(destination)}_{i}{Path.GetExtension(destination)}");

            File.Move(path, candidate);
        }

        /// <summary>Deletes the empty folders under <paramref name="directory"/> (not the root itself), returns their count.</summary>
        private static int DeleteEmpty(string directory, string root, Regex? keep)
        {
            var deleted = 0;

            foreach (var child in Directory.GetDirectories(directory))
                deleted += DeleteEmpty(child, root, keep);

            if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase) || Directory.EnumerateFileSystemEntries(directory).Any())
                return deleted;

            if (keep != null && keep.IsMatch(Path.GetRelativePath(root, directory)))
                return deleted;

            Directory.Delete(directory);
            return deleted + 1;
        }
    }
}
