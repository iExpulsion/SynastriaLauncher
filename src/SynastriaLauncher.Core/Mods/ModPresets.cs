using SynastriaLauncher.Core.GitHub;

namespace SynastriaLauncher.Core.Mods;

public sealed record PresetState(ModFile File, ModPreset? Current, bool Exists)
{
    public bool IsCustom => Exists && Current is null;
}

// Switches a mod's config file between the launcher's presets. The file the player has is saved as .bak first,
// and a file matching no preset counts as custom: shown as such and replaced only when asked. A no-file preset
// (Mod Defaults) is current whenever the config is missing.
public static class ModPresets
{
    public static string CacheFolder { get; set; } = Path.Combine(LauncherData.DefaultFolder, "presets");

    // A bundled preset's content, or a downloaded one's last fetched copy (null until fetched).
    public static byte[]? Content(ModPreset preset)
    {
        byte[]? bytes;
        if (preset.File is not null)
        {
            using var stream = typeof(ModPresets).Assembly.GetManifestResourceStream($"SynastriaLauncher.Core.Mods.Presets.{preset.File}");
            using var memory = new MemoryStream();
            stream?.CopyTo(memory);
            bytes = stream is null ? null : memory.ToArray();
        }
        else
        {
            bytes = preset.IsDownloaded && File.Exists(CachePath(preset)) ? File.ReadAllBytes(CachePath(preset)) : null;
        }
        return bytes;
    }

    private static string CachePath(ModPreset preset)
    {
        var key = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{preset.Url}|{preset.Entry}"));
        return Path.Combine(CacheFolder, Convert.ToHexStringLower(key)[..16] + ".preset");
    }

    public static void Store(ModPreset preset, byte[] bytes)
    {
        Directory.CreateDirectory(CacheFolder);
        File.WriteAllBytes(CachePath(preset), bytes);
    }

    public static bool IsCached(ModPreset preset) => !preset.IsDownloaded || File.Exists(CachePath(preset));

    // Downloads a preset from its source into the cache, so its newest copy is what gets applied and recognized.
    public static async Task FetchAsync(GitHubKeyless github, ModPreset preset, CancellationToken cancel = default)
    {
        if (preset.Url is { } url && preset.File is null)
        {
            var temp = Directory.CreateTempSubdirectory("launcher-preset-").FullName;
            try
            {
                var download = Path.Combine(temp, "download" + Path.GetExtension(new Uri(url).AbsolutePath));
                await github.DownloadAsync(url, download, cancel);
                var bytes = preset.Entry is null ? await File.ReadAllBytesAsync(download, cancel) : ModInstaller.ReadEntry(download, preset.Entry);
                Store(preset, bytes ?? throw new InvalidDataException($"{preset.Entry} wasn't found in the download"));
            }
            finally
            {
                SafePaths.DeleteTemp(temp);
            }
        }
    }

    public static ModFile? PresetFile(ModDefinition mod) =>
        mod.Files.FirstOrDefault(f => f.Config && f.Presets is { Count: > 0 });

    // What a fresh install does with this config: nothing when its first preset is the no-file one.
    public static bool InstallsNothing(ModFile file) => file.Presets is [{ IsNone: true }, ..];

    // A disabled mod's config sits in _Disabled with the rest of its files.
    public static string ConfigPath(ModFile file, string gameFolder, string dllFolder)
    {
        var live = ModInstaller.ResolvePath(gameFolder, dllFolder, file.Path);
        var parked = ModInstaller.ParkedPath(gameFolder, dllFolder, file.Path);
        return !File.Exists(live) && parked is not null && File.Exists(parked) ? parked : live;
    }

    // Line endings and trailing whitespace don't count, so a file saved by a text editor still matches.
    private static string Normalize(byte[] bytes) =>
        System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").TrimEnd();

    public static PresetState Detect(ModFile file, string gameFolder, string dllFolder)
    {
        var path = ConfigPath(file, gameFolder, dllFolder);
        var exists = File.Exists(path);
        var text = exists ? Normalize(File.ReadAllBytes(path)) : null;
        var current = text is null
            ? file.Presets?.FirstOrDefault(p => p.IsNone)
            : file.Presets?.FirstOrDefault(p => Content(p) is { } c && Normalize(c) == text);
        return new PresetState(file, current, exists);
    }

    public static void Apply(ModFile file, ModPreset preset, string gameFolder, string dllFolder)
    {
        var content = Content(preset);
        var path = ConfigPath(file, gameFolder, dllFolder);
        if (content is null && !preset.IsNone)
        {
            throw new InvalidDataException(preset.IsDownloaded
                ? $"The {preset.Name} preset couldn't be downloaded"
                : $"The {preset.Name} preset isn't included in this launcher");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
                File.Delete(path);
            }
            if (content is not null)
            {
                File.WriteAllBytes(path, content);
            }
        }
    }
}
