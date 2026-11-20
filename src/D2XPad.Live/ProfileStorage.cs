using System.Diagnostics;

namespace D2XPad.Live;

internal static class ProfileStorage
{
    private const string MigrationMarker = ".localappdata-migrated-v1";

    public static string ManagedDirectory => Path.Combine(AppContext.BaseDirectory, "profiles");

    public static void EnsureMigrated(string destination, string? legacyDirectory = null)
    {
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D2XPad", "profiles")));
        string marker = Path.Combine(destination, MigrationMarker);
        if (string.Equals(destination, source, StringComparison.OrdinalIgnoreCase) || File.Exists(marker) ||
            !Directory.Exists(source)) return;

        Directory.CreateDirectory(destination);
        using var lease = AcquireMigrationLock(marker + ".lock");
        if (File.Exists(marker)) return;
        foreach (string file in Directory.EnumerateFiles(source))
        {
            string extension = Path.GetExtension(file);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bak", StringComparison.OrdinalIgnoreCase))
                CopyIfMissing(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        // Commit only after every file has been copied or an existing portable copy has won.
        // Keep the marker after profile deletion so the original files cannot revive old entries.
        using var completed = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        completed.Flush(flushToDisk: true);
    }

    private static FileStream AcquireMigrationLock(string path)
    {
        var wait = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                if (wait.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new IOException("Profile migration is busy. Try again after it finishes.", ex);
                Thread.Sleep(25);
            }
        }
    }

    private static void CopyIfMissing(string source, string destination)
    {
        if (File.Exists(destination)) return;
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".profile-migrate-{Guid.NewGuid():N}.tmp");
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { } // A concurrent portable edit wins.
        }
        finally { File.Delete(temporary); }
    }
}
