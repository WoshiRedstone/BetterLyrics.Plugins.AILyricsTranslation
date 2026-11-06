namespace BetterLyrics.TestSupport;

/// <summary>
/// Finds the built plugin without hard coding a machine specific path, so the harnesses run from any
/// checkout location. Resolution order:
///
/// 1. <c>BL_DIST_DIR</c> environment variable, when set — point it at the folder that holds the
///    <c>.blp</c> and the extracted plugin folder (<c>dist</c> in a standard layout).
/// 2. The <c>dist</c> folder next to this repository, found by walking up from the harness binaries
///    until the folder containing <c>BetterLyrics.Plugins.AILyricsTranslation.csproj</c> is reached.
/// 3. The <c>dist</c> folder of the current working directory.
/// </summary>
internal static class TestPaths
{
    public const string PluginId = "BetterLyrics.Plugins.AILyricsTranslation";

    private static readonly Lazy<string> DistDir = new(ResolveDistDirectory);

    /// <summary>Folder that holds <c>BetterLyrics.Plugins.AILyricsTranslation.dll</c> and the <c>.blp</c>.</summary>
    public static string DistDirectory => DistDir.Value;

    /// <summary>The extracted plugin assembly the harnesses load, i.e. <c>&lt;dist&gt;/&lt;id&gt;/&lt;id&gt;.dll</c>.</summary>
    public static string PluginAssemblyPath => Path.Combine(DistDirectory, PluginId, PluginId + ".dll");

    /// <summary>The newest packaged <c>.blp</c> in <see cref="DistDirectory"/>.</summary>
    public static string PackagePath
    {
        get
        {
            var newest = new DirectoryInfo(DistDirectory)
                .EnumerateFiles(PluginId + ".v*.blp")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            return newest?.FullName ?? Path.Combine(DistDirectory, PluginId + ".v1.0.0.blp");
        }
    }

    private static string ResolveDistDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("BL_DIST_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());

        foreach (var candidate in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            var directory = new DirectoryInfo(candidate);
            while (directory != null)
            {
                // The repository root: dist is a sibling of the plugin project in this workspace.
                if (File.Exists(Path.Combine(directory.FullName, PluginId + ".csproj")))
                {
                    var sibling = Path.GetFullPath(Path.Combine(directory.FullName, "..", "dist"));
                    if (Directory.Exists(sibling)) return sibling;
                }

                // Already inside the workspace root that contains both the repository and dist.
                var nested = Path.Combine(directory.FullName, "dist");
                if (Directory.Exists(Path.Combine(nested, PluginId))) return nested;

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the dist folder that holds {PluginId}. " +
            $"Build the plugin first, or set BL_DIST_DIR to the folder containing {PluginId}.dll.");
    }
}
