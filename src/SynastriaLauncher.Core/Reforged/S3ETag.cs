using System.Security.Cryptography;

namespace SynastriaLauncher.Core.Reforged;

// The bucket publishes no manifest, so a file is checked against the ETag it reports: the MD5 of the file, or for
// a file uploaded in parts, the MD5 of the parts' MD5s followed by "-<part count>". The part size isn't reported, so
// every size the uploads are known to use that gives the right count is tried, all in one pass over the file.
public static class S3ETag
{
    public static readonly IReadOnlyList<int> PartSizesMiB = [5, 8, 10, 15, 16, 32, 64, 100];

    public static bool IsMultipart(string etag) => etag.Contains('-');

    public static IReadOnlyList<long> CandidatePartSizes(long fileSize, string etag)
    {
        var dash = etag.LastIndexOf('-');
        var parts = dash >= 0 && int.TryParse(etag[(dash + 1)..], out var count) ? count : 0;
        bool Fits(long size) => parts > 0 && (fileSize + size - 1) / size == parts;
        var known = PartSizesMiB.Select(mib => mib * 1024L * 1024L).Where(Fits).ToList();
        // An upload tool with an unlisted part size: any whole MiB size giving that count, a few at most, since each
        // size is hashed alongside the others.
        return known.Count > 0 ? known
            : Enumerable.Range(5, 5116).Select(mib => mib * 1024L * 1024L).Where(Fits).Take(8).ToList();
    }

    public static async Task<bool> MatchesAsync(string path, string etag, IProgress<long>? progress = null, CancellationToken cancel = default)
    {
        var expected = etag.Trim('"').ToLowerInvariant();
        var size = new FileInfo(path).Length;
        var partSizes = IsMultipart(expected) ? CandidatePartSizes(size, expected) : [0L];
        var results = await ComputeAsync(path, partSizes, progress, cancel);
        return results.Contains(expected);
    }

    // The ETag for each part size (0 = a plain MD5 of the whole file), reading the file once.
    public static async Task<IReadOnlyList<string>> ComputeAsync(string path, IReadOnlyList<long> partSizes,
        IProgress<long>? progress = null, CancellationToken cancel = default)
    {
        var states = partSizes.Select(p => new PartState(p)).ToList();
        var buffer = new byte[8 * 1024 * 1024];
        long done = 0;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
        var read = await stream.ReadAsync(buffer, cancel);
        while (read > 0)
        {
            foreach (var state in states)
            {
                state.Add(buffer.AsSpan(0, read));
            }
            done += read;
            progress?.Report(done);
            read = await stream.ReadAsync(buffer, cancel);
        }
        return states.Select(s => s.Finish()).ToList();
    }

    private sealed class PartState(long partSize)
    {
        private IncrementalHash _part = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        private readonly MemoryStream _partHashes = new();
        private long _inPart;

        public void Add(ReadOnlySpan<byte> data)
        {
            var rest = data;
            while (rest.Length > 0)
            {
                var take = partSize > 0 ? (int)Math.Min(rest.Length, partSize - _inPart) : rest.Length;
                _part.AppendData(rest[..take]);
                _inPart += take;
                rest = rest[take..];
                if (partSize > 0 && _inPart == partSize)
                {
                    _partHashes.Write(_part.GetHashAndReset());
                    _inPart = 0;
                }
            }
        }

        public string Finish()
        {
            string etag;
            if (partSize > 0)
            {
                if (_inPart > 0)
                {
                    _partHashes.Write(_part.GetHashAndReset());
                }
                var count = _partHashes.Length / 16;
                etag = $"{Convert.ToHexStringLower(MD5.HashData(_partHashes.ToArray()))}-{count}";
            }
            else
            {
                etag = Convert.ToHexStringLower(_part.GetHashAndReset());
            }
            _part.Dispose();
            return etag;
        }
    }
}
