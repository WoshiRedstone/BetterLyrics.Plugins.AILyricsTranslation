using BetterLyrics.Core.Helpers;
using BetterLyrics.TestSupport;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var failures = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail == null ? "" : " — " + detail)}");
    if (!ok) failures++;
}

var root = Path.Combine(Path.GetTempPath(), "bli-installer-test-" + Guid.NewGuid().ToString("N"));
var bundled = Path.Combine(root, "bundled");
var plugins = Path.Combine(root, "plugins");
const string pluginId = "BetterLyrics.Plugins.AILyricsTranslation";

void WritePluginFile(string pluginDirectory, string relativePath, string content, DateTime? timestamp = null)
{
    var path = Path.Combine(pluginDirectory, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
    if (timestamp != null) File.SetLastWriteTimeUtc(path, timestamp.Value);
}

try
{
    Console.WriteLine("== bundled plugin deployment ==");

    // nothing bundled at all
    Check("missing bundled directory is ignored",
        BundledPluginInstaller.Synchronize(Path.Combine(root, "nope"), plugins).Count == 0);
    Check("plugins directory is not created for a missing bundled directory", !Directory.Exists(plugins));

    // a real bundle: <id>/<id>.dll + Langs
    var source = Path.Combine(bundled, pluginId);
    WritePluginFile(source, pluginId + ".dll", "dll-v1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    WritePluginFile(source, "Langs/zh-Hans.json", "{\"a\":\"b\"}", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    // a folder without the "<id>/<id>.dll" pair can never be loaded by PluginService
    Directory.CreateDirectory(Path.Combine(bundled, "Broken.Plugin"));

    var first = BundledPluginInstaller.Synchronize(bundled, plugins);
    Check("bundled plugin is deployed", first.Count == 1 && first[0] == pluginId, string.Join(",", first));
    Check("assembly copied", File.Exists(Path.Combine(plugins, pluginId, pluginId + ".dll")));
    Check("nested Langs copied", File.Exists(Path.Combine(plugins, pluginId, "Langs", "zh-Hans.json")));
    Check("folder without a plugin assembly is skipped",
        !Directory.Exists(Path.Combine(plugins, "Broken.Plugin")));

    // second run: nothing to do
    Check("second deployment is a no-op", BundledPluginInstaller.Synchronize(bundled, plugins).Count == 0);

    // a newer bundled copy is pushed over the installed one
    WritePluginFile(source, pluginId + ".dll", "dll-v2",
        new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
    var upgraded = BundledPluginInstaller.Synchronize(bundled, plugins);
    Check("newer bundled copy is redeployed", upgraded.Count == 1, string.Join(",", upgraded));
    Check("installed assembly updated",
        File.ReadAllText(Path.Combine(plugins, pluginId, pluginId + ".dll")) == "dll-v2");

    // an installed copy that is newer than the bundled one must win (user upgraded by hand)
    WritePluginFile(Path.Combine(plugins, pluginId), pluginId + ".dll", "dll-local",
        new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    Check("locally newer install is kept", BundledPluginInstaller.Synchronize(bundled, plugins).Count == 0);
    Check("local install content survives",
        File.ReadAllText(Path.Combine(plugins, pluginId, pluginId + ".dll")) == "dll-local");

    // explicit uninstall must not be undone
    BundledPluginInstaller.MarkRemoved(plugins, pluginId);
    Directory.Delete(Path.Combine(plugins, pluginId), recursive: true);
    Check("removed plugin is not redeployed", BundledPluginInstaller.Synchronize(bundled, plugins).Count == 0);
    Check("removal marker exists",
        File.Exists(Path.Combine(plugins, ".removed-" + pluginId)));

    // ------------------------------------------------------------------------------------
    // The real .blp that is delivered in the dist folder must survive the host's own
    // install path: extract -> IdentifyPluginId -> move to plugins-pending -> synchronize.
    // ------------------------------------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine("== real .blp package ==");

    var distBlp = TestPaths.PackagePath;

    if (!File.Exists(distBlp))
    {
        Check("delivered .blp exists", false, distBlp);
    }
    else
    {
        Check("delivered .blp exists", true, new FileInfo(distBlp).Length + " bytes");

        var extracted = Path.Combine(root, "blp-extracted");
        System.IO.Compression.ZipFile.ExtractToDirectory(distBlp, extracted);

        // PluginService.InstallPlugin does exactly this to decide the plugin id / folder name.
        var identified = PluginMetadataHelper.IdentifyPluginId(extracted);
        Check("package id identified by the host helper", identified == pluginId, identified ?? "<null>");

        Check("package contains its assembly", File.Exists(Path.Combine(extracted, pluginId + ".dll")));
        Check("package contains the language files",
            File.Exists(Path.Combine(extracted, "Langs", "zh-Hans.json")) &&
            File.Exists(Path.Combine(extracted, "Langs", "en.json")));
        Check("package has no runtime logs",
            !Directory.Exists(Path.Combine(extracted, "logs")));

        // Deploy it as a bundled plugin and make sure the loader's "<id>/<id>.dll" contract holds.
        var realBundled = Path.Combine(root, "real-bundled");
        Directory.CreateDirectory(realBundled);
        Directory.Move(extracted, Path.Combine(realBundled, identified!));

        var realPlugins = Path.Combine(root, "real-plugins");
        var deployed = BundledPluginInstaller.Synchronize(realBundled, realPlugins);
        Check("real package is deployed", deployed.Count == 1 && deployed[0] == pluginId, string.Join(",", deployed));
        Check("loadable dll name matches its folder",
            File.Exists(Path.Combine(realPlugins, pluginId, pluginId + ".dll")));
        Check("language files deployed with it",
            File.Exists(Path.Combine(realPlugins, pluginId, "Langs", "zh-Hans.json")));
    }
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
