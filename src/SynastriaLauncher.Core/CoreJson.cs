using System.Text.Json.Serialization;
using SynastriaLauncher.Core.Addons;
using SynastriaLauncher.Core.GitHub;
using SynastriaLauncher.Core.Mods;

namespace SynastriaLauncher.Core;

// Source-generated JSON, so a trimmed single-file build still reads and writes it.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LauncherData))]
[JsonSerializable(typeof(ModManifest))]
[JsonSerializable(typeof(Addons.AddonCatalog))]
[JsonSerializable(typeof(GitHubKeyless.ApiRelease))]
[JsonSerializable(typeof(GitHubKeyless.ApiTree))]
[JsonSerializable(typeof(GitHubKeyless.ApiRepo))]
[JsonSerializable(typeof(Dictionary<string, Reforged.ReforgedService.CacheEntry>))]
internal sealed partial class CoreJson : JsonSerializerContext;
