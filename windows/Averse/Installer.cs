using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Averse;

/// <summary>
/// L'exe téléchargé « s'installe tout seul » : copie dans %LOCALAPPDATA%\Programs\Averse, raccourci
/// dans le menu Démarrer, liens magnet et fichiers .torrent, entrée dans « Applications installées ».
/// Tout est par utilisateur, sans droits administrateur.
/// </summary>
public static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Averse";
    private const string ProgId = "Averse.torrent";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Averse");

    public static string InstalledExe => Path.Combine(InstallDir, "Averse.exe");

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Averse.lnk");

    public static bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(InstalledExe),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copie cet exe à la place de celui installé (en fermant une copie ouverte) et le lance.
    /// Renvoie false si la copie échoue : l'appelant continue alors depuis ici.
    /// </summary>
    public static bool InstallAndRelaunch(string[] args)
    {
        try
        {
            CloseRunningInstance();
            var firstInstall = !File.Exists(InstalledExe);
            if (firstInstall) args = [.. args, "--first-install"];
            Directory.CreateDirectory(InstallDir);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 40)
                {
                    Thread.Sleep(250);
                }
            }
            CreateShortcut();
            RegisterAssociations();
            RegisterUninstall();
            var psi = new ProcessStartInfo(InstalledExe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            // L'antivirus peut encore analyser l'exe qu'on vient d'écrire : on réessaie un peu.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Process.Start(psi);
                    return true;
                }
                catch when (attempt < 20)
                {
                    Thread.Sleep(500);
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private static void CloseRunningInstance()
    {
        foreach (var p in Process.GetProcessesByName("Averse"))
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                if (!string.Equals(p.MainModule?.FileName, InstalledExe, StringComparison.OrdinalIgnoreCase)) continue;
                p.CloseMainWindow();
                if (!p.WaitForExit(10000)) p.Kill();
            }
            catch { }
        }
    }

    private static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(ShortcutPath);
        link.TargetPath = InstalledExe;
        link.WorkingDirectory = InstallDir;
        link.Description = "Averse - client torrent sans pub";
        link.Save();
    }

    /// <summary>Averse ouvre les liens magnet et les fichiers .torrent (sauf si un autre choix a été fait dans Windows).</summary>
    public static void RegisterAssociations()
    {
        try
        {
            var exe = InstalledExe;
            var open = $"\"{exe}\" \"%1\"";
            var icon = $"\"{exe}\",0";

            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\magnet"))
            {
                k.SetValue("", "URL:Lien magnet");
                k.SetValue("URL Protocol", "");
                using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue("", icon);
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", open);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                k.SetValue("", "Fichier torrent");
                using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue("", icon);
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", open);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.torrent"))
            {
                k.SetValue("", ProgId);
                k.SetValue("Content Type", "application/x-bittorrent");
                using var o = k.CreateSubKey("OpenWithProgids");
                o.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            // Pour apparaître dans Paramètres > Applications par défaut.
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Averse\Capabilities"))
            {
                k.SetValue("ApplicationName", "Averse");
                k.SetValue("ApplicationDescription", "Client torrent sans pub");
                using (var f = k.CreateSubKey("FileAssociations")) f.SetValue(".torrent", ProgId);
                using (var u = k.CreateSubKey("UrlAssociations")) u.SetValue("magnet", "Averse.magnet");
            }
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Averse.magnet"))
            {
                k.SetValue("", "URL:Lien magnet");
                k.SetValue("URL Protocol", "");
                using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue("", icon);
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", open);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                k.SetValue("Averse", @"Software\Averse\Capabilities");

            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        }
        catch { }
    }

    private static void RegisterUninstall()
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "Averse");
        k.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        k.SetValue("Publisher", "Ghermin");
        k.SetValue("DisplayIcon", InstalledExe);
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("URLInfoAbout", "https://ghermin.github.io/averse/");
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>Retire raccourci, associations et fichiers de l'appli (pas les téléchargements).</summary>
    public static void Uninstall()
    {
        CloseRunningInstance();
        try { File.Delete(ShortcutPath); } catch { }
        foreach (var key in new[]
                 {
                     @"Software\Classes\Averse.torrent", @"Software\Classes\Averse.magnet", @"Software\Averse", UninstallKey,
                 })
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(key, false); } catch { }
        }
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))
                k?.DeleteValue("Averse", false);
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\magnet\shell\open\command"))
            {
                if (k?.GetValue("") is string cmd && cmd.Contains(InstalledExe, StringComparison.OrdinalIgnoreCase))
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\magnet", false);
            }
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.torrent", true))
            {
                if (k?.GetValue("") as string == ProgId) k.DeleteValue("", false);
                using var o = k?.OpenSubKey("OpenWithProgids", true);
                o?.DeleteValue(ProgId, false);
            }
        }
        catch { }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Averse");
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\" & rmdir /s /q \"{Settings.DataDir}\" & rmdir /s /q \"{local}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
