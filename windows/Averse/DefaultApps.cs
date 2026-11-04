using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Averse;

/// <summary>
/// Fait d'Averse l'appli par défaut des liens magnet et des fichiers .torrent.
/// Windows garde ce choix dans une clé « UserChoice » signée (ProgId + Hash) : on calcule la même
/// signature que Windows (algorithme public, vérifié contre les choix existants du PC).
/// À la première installation on prend la place ; ensuite on ne la reprend que si le choix
/// actuel est cassé (aucune appli, ou une appli désinstallée) : un autre client choisi exprès est respecté.
/// </summary>
public static class DefaultApps
{
    private static readonly (string Name, string ProgId, bool Protocol)[] Targets =
    [
        ("magnet", "Averse.magnet", true),
        (".torrent", "Averse.torrent", false),
    ];

    public static void Apply(bool force)
    {
        foreach (var (name, progId, protocol) in Targets)
        {
            try
            {
                if (force || !HasWorkingChoice(name, protocol)) SetUserChoice(name, progId, protocol);
            }
            catch
            {
                // Ne jamais empêcher Averse de démarrer pour ça.
            }
        }
        Notify();
    }

    private static string ChoicePath(string name, bool protocol) => protocol
        ? $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{name}\UserChoice"
        : $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{name}\UserChoice";

    /// <summary>Vrai si un choix existe et que son programme est encore là (Averse ou un autre).</summary>
    private static bool HasWorkingChoice(string name, bool protocol)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ChoicePath(name, protocol));
        if (key?.GetValue("ProgId") is not string progId || progId.Length == 0) return false;
        if (progId.StartsWith("Averse.", StringComparison.OrdinalIgnoreCase) ||
            progId.Equals(@"Applications\Averse.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (progId.StartsWith("AppX", StringComparison.OrdinalIgnoreCase)) return true; // appli du Store : on ne touche pas

        using var cmd = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
        if (cmd?.GetValue("") is not string command || command.Length == 0) return false;
        var exe = ExePath(command);
        return exe == null || File.Exists(Environment.ExpandEnvironmentVariables(exe));
    }

    private static string? ExePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var idx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? command[..(idx + 4)] : null;
    }

    private static void SetUserChoice(string name, string progId, bool protocol)
    {
        var path = ChoicePath(name, protocol);
        // Windows verrouille parfois cette clé (refus de « définir une valeur ») : on la supprime
        // entièrement (droit de suppression autorisé) puis on la recrée.
        try
        {
            Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
        catch
        {
            try
            {
                RegDeleteKeyEx(Registry.CurrentUser.Handle, path, 0x0100, 0);
            }
            catch
            {
            }
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            key.SetValue("ProgId", progId);
            var stamp = LastWriteMinute(key);
            key.SetValue("Hash", Hash((name + UserSid() + progId + stamp + Experience()).ToLowerInvariant()));
            // Le hash dépend de la minute d'écriture de la clé : on recommence si elle a changé entre-temps.
            if (LastWriteMinute(key) == stamp) return;
        }
    }

    private static string UserSid() => WindowsIdentity.GetCurrent().User?.Value ?? "";

    private static string? experience;

    /// <summary>La phrase « User Choice set via Windows User Experience {…} » tirée de shell32.dll.</summary>
    private static string Experience()
    {
        if (experience != null) return experience;
        const string fallback = "User Choice set via Windows User Experience {D18B6DD5-6124-4341-9318-804003BAFA0B}";
        try
        {
            var dll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");
            var text = Encoding.Unicode.GetString(File.ReadAllBytes(dll));
            var i = text.IndexOf("User Choice set via Windows User Experience", StringComparison.Ordinal);
            var j = i < 0 ? -1 : text.IndexOf('}', i);
            experience = i >= 0 && j > i ? text[i..(j + 1)] : fallback;
        }
        catch
        {
            experience = fallback;
        }
        return experience;
    }

    private static string LastWriteMinute(RegistryKey key)
    {
        RegQueryInfoKey(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out _, out _, out _, out _, out _, out _,
            out _, out var ft);
        var minute = ft / 600_000_000L * 600_000_000L;
        return ((ulong)minute).ToString("x16");
    }

    private static uint U32(byte[] b, int i) => BitConverter.ToUInt32(b, i);

    /// <summary>La signature des clés UserChoice (même calcul que Windows).</summary>
    internal static string Hash(string baseInfo)
    {
        var data = Encoding.Unicode.GetBytes(baseInfo).Concat(new byte[2]).ToArray();
        var md5 = MD5.HashData(data);
        var lb = baseInfo.Length * 2 + 2;
        var length = ((lb & 4) == 0 ? 1 : 0) + (lb >> 2) - 1;
        if (length <= 1) return "";
        data = data.Concat(new byte[16]).ToArray();
        var count = ((length - 2) >> 1) + 1;

        unchecked
        {
            uint cache = 0, out1 = 0, out2 = 0;
            var p = 0;
            var md51 = (U32(md5, 0) | 1) + 0x69FB0000u;
            var md52 = (U32(md5, 4) | 1) + 0x13DB0000u;
            for (var n = 0; n < count; n++)
            {
                var r0 = U32(data, p) + out1;
                var r1 = U32(data, p + 4);
                p += 8;
                var r2a = r0 * md51 - 0x10FA9605u * (r0 >> 16);
                var r2b = 0x79F8A395u * r2a + 0x689B6B9Fu * (r2a >> 16);
                var r3 = 0xEA970001u * r2b - 0x3C101569u * (r2b >> 16);
                var r4 = r3 + r1;
                var r5 = cache + r3;
                var r6a = r4 * md52 - 0x3CE8EC25u * (r4 >> 16);
                var r6b = 0x59C3AF2Du * r6a - 0x2232E0F1u * (r6a >> 16);
                out1 = 0x1EC90001u * r6b + 0x35BD1EC9u * (r6b >> 16);
                out2 = r5 + out1;
                cache = out2;
            }
            uint h0 = out1, h1 = out2;

            cache = 0;
            out1 = 0;
            p = 0;
            md51 = U32(md5, 0) | 1;
            md52 = U32(md5, 4) | 1;
            for (var n = 0; n < count; n++)
            {
                var r0 = U32(data, p) + out1;
                p += 8;
                var r1a = r0 * md51;
                var r1b = 0xB1110000u * r1a - 0x30674EEFu * (r1a >> 16);
                var r2a = 0x5B9F0000u * r1b - 0x78F7A461u * (r1b >> 16);
                var r2b = 0x12CEB96Du * (r2a >> 16) - 0x46930000u * r2a;
                var r3 = 0x1D830000u * r2b + 0x257E1D83u * (r2b >> 16);
                var r4a = md52 * (r3 + U32(data, p - 4));
                var r4b = 0x16F50000u * r4a - 0x5D8BE90Bu * (r4a >> 16);
                var r5a = 0x96FF0000u * r4b - 0x2C7C6901u * (r4b >> 16);
                var r5b = 0x2B890000u * r5a + 0x7C932B89u * (r5a >> 16);
                out1 = 0x9F690000u * r5b - 0x405B6097u * (r5b >> 16);
                out2 = out1 + cache + r3;
                cache = out2;
            }

            var result = new byte[8];
            BitConverter.GetBytes(out1 ^ h0).CopyTo(result, 0);
            BitConverter.GetBytes(out2 ^ h1).CopyTo(result, 4);
            return Convert.ToBase64String(result);
        }
    }

    private static void Notify() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("advapi32.dll")]
    private static extern int RegQueryInfoKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle hKey, IntPtr cls,
        IntPtr clsLen, IntPtr reserved, out int subKeys, out int maxSubKeyLen, out int maxClassLen, out int values,
        out int maxValueNameLen, out int maxValueLen, out int securityDescriptor, out long lastWriteTime);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegDeleteKeyEx(Microsoft.Win32.SafeHandles.SafeRegistryHandle hKey, string subKey,
        int samDesired, int reserved);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
