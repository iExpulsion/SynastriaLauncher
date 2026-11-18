using System.Text;

namespace SynastriaLauncher.Core.GitHub;

public sealed record GitRefs(string? HeadSha, string? DefaultBranch, IReadOnlyDictionary<string, string> Refs)
{
    // Parses the pkt-line ref advertisement GitHub sends for `info/refs?service=git-upload-pack`.
    public static GitRefs Parse(byte[] body)
    {
        var refs = new Dictionary<string, string>();
        string? head = null;
        string? branch = null;
        var position = 0;
        while (position + 4 <= body.Length)
        {
            var length = Convert.ToInt32(Encoding.ASCII.GetString(body, position, 4), 16);
            if (length > 4)
            {
                var line = Encoding.UTF8.GetString(body, position + 4, length - 4).TrimEnd('\n');
                var parts = line.Split('\0');
                var isRef = !line.StartsWith('#') && parts[0].Length > 41 && parts[0][40] == ' ';
                if (isRef)
                {
                    var sha = parts[0][..40];
                    var name = parts[0][41..];
                    refs[name] = sha;
                    head = name == "HEAD" ? sha : head;
                }
                var symref = parts.Length > 1
                    ? parts[1].Split(' ').FirstOrDefault(c => c.StartsWith("symref=HEAD:refs/heads/"))
                    : null;
                branch = symref?["symref=HEAD:refs/heads/".Length..] ?? branch;
            }
            position += Math.Max(length, 4);
        }
        return new GitRefs(head, branch, refs);
    }
}
