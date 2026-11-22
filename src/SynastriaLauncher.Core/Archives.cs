using System.IO.Compression;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;

namespace SynastriaLauncher.Core;

// The download formats addon releases come in, read by content rather than by name.
public static class Archives
{
    private static readonly string[] Extensions = [".zip", ".7z", ".rar", ".tar.gz", ".tgz", ".tar.xz", ".tar.bz2", ".tar"];

    // Lower is preferred: a release that offers a .zip and a .7z of the same addon gets the .zip. -1 for files that
    // aren't archives (installers, checksums, single .lua files).
    public static int Rank(string fileName) =>
        Array.FindIndex(Extensions, e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static T? Best<T>(IEnumerable<T> files, Func<T, string> name) where T : class =>
        files.Select(f => (File: f, Rank: Rank(name(f)))).Where(f => f.Rank >= 0).OrderBy(f => f.Rank)
            .Select(f => f.File).FirstOrDefault();

    // Unpacks into folder, refusing entries that would land outside it. A damaged or unknown archive is reported as
    // invalid data, like the launcher's other download problems.
    public static void ExtractTo(string archivePath, string folder)
    {
        try
        {
            using var stream = File.OpenRead(archivePath);
            var magic = new byte[2];
            stream.ReadExactly(magic);
            stream.Position = 0;
            if (magic is [(byte)'P', (byte)'K'])
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
                {
                    using var source = entry.Open();
                    Write(source, folder, entry.FullName);
                }
            }
            else if (SevenZipArchive.IsSevenZipFile(stream))
            {
                stream.Position = 0;
                using var archive = SevenZipArchive.OpenArchive(stream, new ReaderOptions());
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    using var source = entry.OpenEntryStream();
                    Write(source, folder, entry.Key ?? "");
                }
            }
            else
            {
                stream.Position = 0;
                using var reader = ReaderFactory.OpenReader(stream, new ReaderOptions());
                while (reader.MoveToNextEntry())
                {
                    if (!reader.Entry.IsDirectory)
                    {
                        using var source = reader.OpenEntryStream();
                        Write(source, folder, reader.Entry.Key ?? "");
                    }
                }
            }
        }
        catch (Exception e) when (e is SharpCompress.Common.ExtractionException or SharpCompress.Common.ArchiveException
            or InvalidOperationException or NotSupportedException or EndOfStreamException)
        {
            throw new InvalidDataException($"The download couldn't be read as an archive: {e.Message}", e);
        }
    }

    private static void Write(Stream source, string folder, string key)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, key.Replace('\\', '/')));
        var inside = key.Length > 0 && !Path.IsPathRooted(key) && target.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        if (inside)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var file = File.Create(target);
            source.CopyTo(file);
        }
        else
        {
            throw new InvalidDataException($"The download has a file outside its own folder ({key}), so nothing was installed");
        }
    }
}
