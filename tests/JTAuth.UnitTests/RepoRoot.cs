namespace JTAuth.UnitTests;

/// <summary>The repository root (the folder holding JTAuth.sln), for tests that read checked-in files.</summary>
internal static class RepoRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "JTAuth.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (JTAuth.sln).");
    }
}
