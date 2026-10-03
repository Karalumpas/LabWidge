/// <summary>Moves a folder's content into another without overwriting anything that already exists there.</summary>
internal static class DirectoryMerge
{
    /// <summary>
    /// If the target does not exist, the whole folder is moved. Otherwise the files the target lacks are moved; files that
    /// exist in both places are kept in the target. The source is deleted once nothing in it is missing from the target.
    /// Locked files are skipped, so a later call can finish the job. True when the source is gone.
    /// </summary>
    public static bool Merge(string source, string target)
    {
        if (!Directory.Exists(source)) return true;
        if (!Directory.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            Directory.Move(source, target);
            return true;
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList())
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            if (File.Exists(destination)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(file, destination);
            }
            catch (IOException)
            {
                // Locked file – retried next time
            }
            catch (UnauthorizedAccessException)
            {
                // No access right now – retried next time
            }
        }

        var missing = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Any(f => !File.Exists(Path.Combine(target, Path.GetRelativePath(source, f))));
        if (missing) return false;
        Directory.Delete(source, recursive: true);
        return true;
    }
}
