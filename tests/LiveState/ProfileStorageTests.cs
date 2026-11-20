using D2XPad.Live;

internal static class ProfileStorageTests
{
    public static void Run(string workspace)
    {
        string source = Path.Combine(workspace, "legacy");
        string destination = Path.Combine(workspace, "portable", "profiles");
        ProfileStorage.EnsureMigrated(destination, source);
        Check(!Directory.Exists(destination), "A missing legacy catalog created a new directory.");

        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        Write(source, "Adventure.json", "legacy current");
        Write(source, "Adventure.bak", "legacy previous");
        Write(source, "Conflict.json", "legacy conflicting entry");
        Write(source, "Removed.bak", "legacy deleted entry");
        Write(source, "Related.tmp", "non-profile");
        Write(destination, "Conflict.json", "existing portable current");
        string longName = new string('p', 250) + ".json";
        Write(source, longName, "long filename profile");

        Parallel.For(0, 8, _ => ProfileStorage.EnsureMigrated(destination, source));
        Check(Read(destination, "Adventure.json") == "legacy current" && Read(destination, "Adventure.bak") == "legacy previous",
            "Migration did not preserve the active profile and its backup.");
        Check(Read(destination, "Conflict.json") == "existing portable current" && Read(source, "Conflict.json") == "legacy conflicting entry",
            "Migration replaced the portable conflict or modified its legacy source.");
        Check(!File.Exists(Path.Combine(destination, "Removed.json")) && Read(destination, "Removed.bak") == "legacy deleted entry",
            "Migration restored a legacy backup as an active profile.");
        Check(Read(destination, longName) == "long filename profile" && !Directory.EnumerateFiles(destination, "*.tmp").Any(),
            "Migration truncated a long filename or copied temporary files.");

        File.Delete(Path.Combine(destination, "Adventure.json"));
        ProfileStorage.EnsureMigrated(destination, source);
        Check(!File.Exists(Path.Combine(destination, "Adventure.json")) && Read(source, "Adventure.json") == "legacy current",
            "An already migrated entry was restored after deletion or removed from the legacy catalog.");

        if (OperatingSystem.IsWindows()) CheckRetry(workspace);
        Console.WriteLine("Passed portable profile migration, concurrency, collision handling, backups and deleted-profile retention.");
    }

    private static void CheckRetry(string workspace)
    {
        string source = Path.Combine(workspace, "migration-retry-source");
        string destination = Path.Combine(workspace, "migration-retry-destination");
        Directory.CreateDirectory(source);
        Write(source, "Retry.json", "available after sharing lock");
        using (File.Open(Path.Combine(source, "Retry.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try
            {
                ProfileStorage.EnsureMigrated(destination, source);
                throw new InvalidDataException("Migration unexpectedly completed while its source was locked.");
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 32) { }
        }
        Check(!Directory.EnumerateFiles(destination, "*.tmp").Any(), "Interrupted migration left an incomplete temporary file.");
        ProfileStorage.EnsureMigrated(destination, source);
        Check(Read(destination, "Retry.json") == "available after sharing lock", "Migration did not resume after the file became available.");
    }

    private static void Write(string directory, string name, string value) => File.WriteAllText(Path.Combine(directory, name), value);
    private static string Read(string directory, string name) => File.ReadAllText(Path.Combine(directory, name));

    private static void Check(bool condition, string error)
    {
        if (!condition) throw new InvalidDataException(error);
    }
}
