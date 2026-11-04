using System.Runtime.InteropServices;
using System.Text.Json;

namespace Averse;

/// <summary>Réglages enregistrés dans %APPDATA%\Averse\settings.json.</summary>
public sealed class Settings
{
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Averse");

    public static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Averse", "cache");

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    public string DownloadDir { get; set; } = DefaultDownloads();

    /// <summary>Port d'écoute, tiré au hasard la première fois puis gardé (redirection de port stable).</summary>
    public int Port { get; set; } = Random.Shared.Next(20000, 60000);

    /// <summary>Empreintes des torrents mis en pause par l'utilisateur.</summary>
    public HashSet<string> Paused { get; set; } = [];

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (s != null) return s;
            }
        }
        catch { }
        var fresh = new Settings();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }

    private static string DefaultDownloads()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out var p) == 0)
        {
            var path = Marshal.PtrToStringUni(p);
            Marshal.FreeCoTaskMem(p);
            if (!string.IsNullOrEmpty(path)) return path;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags,
        IntPtr token, out IntPtr path);
}
