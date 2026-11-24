using System.Runtime.InteropServices;
using System.Text;

namespace SynastriaLauncher.Core.Accounts;

// A game account the launcher logs in with. The password is encrypted for this Windows user (DPAPI), so the saved
// file is no use on another account or PC.
public sealed class SavedAccount
{
    public string Name { get; set; } = "";
    public string Password { get; set; } = "";

    // Characters to enter the world as, each "Name" or "Name (detail, ...)" as the DLL matches them.
    public List<string> Characters { get; set; } = [];

    public bool HasCharacter(string character) => Characters.Any(c => SameCharacter(c, character));

    public static bool SameCharacter(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

// What Launch logs in as: an account, and a character or none (stays at character select).
public sealed record LaunchTarget(string Account, string? Character)
{
    public string Label => Character is null ? $"{Account}: Character Select" : $"{Account}: {Character}";
    public string Caption => $"as {Character ?? Account}";
}

public static class AccountSecrets
{
    private static readonly byte[] Entropy = "SynastriaLauncher.Account"u8.ToArray();

    public static string Protect(string password) =>
        Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(password), protect: true)
            ?? throw new InvalidOperationException("Windows couldn't encrypt the password"));

    // Null when it can't be read here: saved by another Windows user or on another PC, or damaged.
    public static string? Unprotect(string saved)
    {
        string? password = null;
        try
        {
            var plain = Transform(Convert.FromBase64String(saved), protect: false);
            password = plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)
        {
            password = null;
        }
        return password;
    }

    private static byte[]? Transform(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var dataIn = new DataBlob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref dataIn, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var dataOut)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out dataOut);
            byte[]? output = null;
            if (ok)
            {
                output = new byte[dataOut.Size];
                Marshal.Copy(dataOut.Data, output, 0, dataOut.Size);
                LocalFree(dataOut.Data);
            }
            return output;
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
        }
    }

    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

// What the launcher hands the game so SynastriaLauncher.dll can log in: an environment variable for that one
// process, which the DLL reads and clears at startup (native AutoLogin.h).
public static class LoginHandoff
{
    public const string EnvironmentName = "SYNASTRIA_LAUNCHER_LOGIN";
    public const char Separator = '\x1F';

    public static string Value(string account, string password, string? character) =>
        string.Join(Separator, account, password, character ?? "");

    public static bool CanCarry(string account, string password, string? character) =>
        !$"{account}{password}{character}".Contains(Separator);
}

// The launcher's own arguments for starting the game straight away, as desktop shortcuts use them.
public sealed record LaunchRequest(string? Account, string? Character = null)
{
    public const string LaunchArg = "--launch";
    public const string AccountArg = "--account";
    public const string CharacterArg = "--character";

    // Null when the arguments don't ask to launch.
    public static LaunchRequest? Parse(IReadOnlyList<string> args)
    {
        var launch = args.Any(a => string.Equals(a, LaunchArg, StringComparison.OrdinalIgnoreCase));
        return launch ? new LaunchRequest(Value(args, AccountArg), Value(args, CharacterArg)) : null;
    }

    private static string? Value(IReadOnlyList<string> args, string name)
    {
        var at = args.ToList().FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Count ? args[at + 1] : null;
    }

    public static string Arguments(string account, string? character = null) =>
        $"{LaunchArg} {AccountArg} \"{account}\"" + (character is null ? "" : $" {CharacterArg} \"{character}\"");
}
