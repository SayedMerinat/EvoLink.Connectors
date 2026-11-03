namespace EvoLink.Connectors.Transformers
{
    /// <summary>
    /// Names of the file properties set by <see cref="FileInfoTransformer"/> and read by <see cref="Conditions.FileSizeCondition"/>.
    /// They are identical to the EvoBus keys (<c>Keys.File</c>) to ease the migration, except <see cref="LastModifiedBy"/>
    /// which pointed to "FILE-CreatedBy" in EvoBus.
    /// </summary>
    public static class FileKeys
    {
        public const string FilePath = "FILE-FilePath";
        public const string Extension = "FILE-Extension";
        public const string Name = "FILE-Name";
        public const string NameWithoutExtension = "FILE-NameWithoutExtension";
        public const string CreationTimeStamp = "FILE-CreationTimeStamp";
        public const string CreatedBy = "FILE-CreatedBy";
        public const string LastModificationTimeStamp = "FILE-LastModificationTimeStamp";
        public const string LastModifiedBy = "FILE-LastModifiedBy";
        public const string NumberParentFolders = "FILE-NumberParentFolders";
        public const string ReadableSize = "FILE-ReadableSize";
        public const string RelativePath = "FILE-RelativePath";
        public const string ByteSize = "FILE-ByteSize";
        public const string ExcludedExtension = "FILE-ExcludedExtension";
        public const string ExcludedName = "FILE-ExcludedName";

        /// <summary>Excluded extensions (the misspelling of the EvoBus key is kept on purpose for compatibility).</summary>
        public const string Exclusions = "FILE-Exculsions";
    }
}
