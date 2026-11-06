using BetterLyrics.Core.Helpers;
using BetterLyrics.Core.Implementations.Services.PluginService;
using BetterLyrics.Core.Serialization;
using BetterLyrics.Sdk.Abstractions.Plugins;
using BetterLyrics.Sdk.Enums;
using BetterLyrics.Sdk.Interfaces.Plugins;
using BetterLyrics.TestSupport;

namespace BetterLyrics.RealConfigHarness;

/// <summary>
/// Drives the host's real <see cref="PluginConfigurator"/> (and therefore the real
/// <see cref="SourceGenerationContext"/>) through a save → restart → reload cycle.
///
/// This exists because the plugin stores its settings as <c>Dictionary&lt;string, object&gt;</c>
/// and the generated context has to be able to serialize the boxed values that come out of the
/// settings card (string, bool, double).
/// </summary>
internal static class Program
{
    private static int _failures;

    private static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail == null ? "" : " — " + detail)}");
        if (!ok) _failures++;
    }

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var root = Path.Combine(Path.GetTempPath(), "bli-realcfg-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            Console.WriteLine("== real PluginConfigurator round trip ==");

            var first = new PluginConfigurator(root);

            // The settings card sends the values the plugin declared in its SettingDef schema:
            // Choice → string, Password/Text → string, Number → double, Bool → bool.
            TrySet(first, "Mode", "AI");
            TrySet(first, "ApiKey", "sk-secret");
            TrySet(first, "Temperature", 0.35d);
            TrySet(first, "ToSimplifiedChinese", true);

            var path = Path.Combine(root, "config", "config.json");
            Check("config file written", File.Exists(path), path);

            if (File.Exists(path))
                Console.WriteLine("      content: " + File.ReadAllText(path).Replace("\r", "").Replace("\n", " "));

            var second = new PluginConfigurator(root);
            Check("string value survives", second.Get("Mode", "Google") as string == "AI",
                second.Get("Mode", "Google")?.ToString());
            Check("secret survives", second.Get("ApiKey", "") as string == "sk-secret");
            Check("bool survives", second.Get("ToSimplifiedChinese", false) is bool b && b,
                second.Get("ToSimplifiedChinese", false)?.ToString());
            Check("double survives",
                second.Get("Temperature", 0.1d) is double t && Math.Abs(t - 0.35) < 1e-9,
                second.Get("Temperature", 0.1d)?.ToString());

            var config = new ProbeConfig();
            config.BindConfigurator(second);
            Check("Config wrapper reads persisted Mode", config.Mode == "AI", config.Mode);
            Check("Config wrapper reads persisted Temperature",
                Math.Abs(config.Temperature - 0.35) < 1e-9, config.Temperature.ToString("0.###"));
            Check("Config wrapper falls back for unwritten keys",
                config.ModelName == "gpt-3.5-turbo", config.ModelName);

            CheckShippedConfigRoundTrip();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void TrySet(IConfigurator configurator, string key, object value)
    {
        try
        {
            configurator.Set(key, value, ConfigChangedBy.Host);
        }
        catch (Exception ex)
        {
            // Record it as a failed check instead of tearing the whole run down.
            Check($"host can persist {key} ({value.GetType().Name})", false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Repeats the round trip for every setting the shipped plugin actually declares, loaded from the
    /// delivered assembly, so the check covers the real Config type rather than a stand-in.
    /// </summary>
    private static void CheckShippedConfigRoundTrip()
    {
        var shippedDll = TestPaths.PluginAssemblyPath;

        Console.WriteLine();
        Console.WriteLine("== shipped plugin Config round trip ==");

        if (!File.Exists(shippedDll))
        {
            Check("shipped plugin assembly found", false, shippedDll);
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "bli-shipped-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);

            var assembly = LoadWithSharedSdk(shippedDll);

            var configType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(PluginConfigBase).IsAssignableFrom(t) && !t.IsAbstract);

            if (configType == null)
            {
                Check("shipped plugin exposes a Config type", false);
                return;
            }

            var firstConfigurator = new PluginConfigurator(root);
            var firstConfig = (PluginConfigBase)Activator.CreateInstance(configType)!;
            firstConfig.BindConfigurator(firstConfigurator);

            // Values chosen so each one differs from the declared default.
            var written = new (string Key, object Value)[]
            {
                ("Mode", "Gemini"),
                ("ApiUrl", "https://example.test/v1"),
                ("ApiKey", "sk-shipped-secret"),
                ("ModelName", "deepseek-chat"),
                ("SystemPrompt", "Translate to Simplified Chinese."),
                ("Temperature", 0.42d),
                ("ToSimplifiedChinese", false),
                ("ChunkThreshold", 48d),
            };

            foreach (var (key, value) in written)
            {
                var property = configType.GetProperty(key);
                if (property == null || !property.CanWrite)
                {
                    Check($"Config declares {key}", false);
                    continue;
                }

                try { property.SetValue(firstConfig, value); }
                catch (Exception ex) { Check($"host can persist {key}", false, ex.GetType().Name + ": " + ex.Message); }
            }

            // A fresh configurator over the same directory is exactly what a restart produces.
            var secondConfigurator = new PluginConfigurator(root);
            var secondConfig = (PluginConfigBase)Activator.CreateInstance(configType)!;
            secondConfig.BindConfigurator(secondConfigurator);

            foreach (var (key, expected) in written)
            {
                var property = configType.GetProperty(key);
                if (property == null) continue;

                var actual = property.GetValue(secondConfig);
                var ok = expected switch
                {
                    double d => actual is double ad && Math.Abs(ad - d) < 1e-9,
                    bool b => actual is bool ab && ab == b,
                    _ => Equals(actual?.ToString(), expected.ToString())
                };

                Check($"{key} survives restart", ok, $"{actual}");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static System.Reflection.Assembly LoadWithSharedSdk(string path)
    {
        var resolver = new System.Runtime.Loader.AssemblyDependencyResolver(path);
        var context = new System.Runtime.Loader.AssemblyLoadContext("shipped-plugin", isCollectible: false);

        context.Resolving += (ctx, name) =>
        {
            var shared = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == name.Name);
            if (shared != null) return shared;

            var resolved = resolver.ResolveAssemblyToPath(name);
            return resolved != null && File.Exists(resolved) ? ctx.LoadFromAssemblyPath(resolved) : null;
        };

        return context.LoadFromAssemblyPath(path);
    }

    private sealed class ProbeConfig : PluginConfigBase
    {
        public string Mode
        {
            get => Get("Google");
            set => Set(value);
        }

        public string ModelName
        {
            get => Get("gpt-3.5-turbo");
            set => Set(value);
        }

        public double Temperature
        {
            get => Get(0.1d);
            set => Set(value);
        }
    }
}
