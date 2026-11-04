using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Averse;

/// <summary>Cherche une version plus récente sur GitHub et remplace l'exe installé par elle.</summary>
public static class Updater
{
    private const string LatestApi = "https://api.github.com/repos/Ghermin/averse/releases/latest";
    private static string Dir => Path.Combine(Path.GetTempPath(), "Averse-update");

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public sealed record Release(Version Version, string ExeUrl, long ExeSize);

    /// <summary>La dernière version si elle est plus récente que cet exe, sinon null.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = NewClient();
            var json = await http.GetStringAsync(LatestApi, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v'), out var latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            if (latest <= Current) return null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != "Averse.exe") continue;
                return new Release(latest, asset.GetProperty("browser_download_url").GetString()!,
                    asset.GetProperty("size").GetInt64());
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>Télécharge le nouvel exe ; renvoie son chemin, ou null si le fichier est incomplet.</summary>
    public static async Task<string?> DownloadAsync(Release release, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, $"Averse-{release.Version}.exe");
            if (File.Exists(path) && new FileInfo(path).Length == release.ExeSize) return path;
            var partial = path + ".part";
            using (var http = NewClient())
            await using (var src = await http.GetStreamAsync(release.ExeUrl, ct))
            await using (var dst = File.Create(partial))
            {
                await src.CopyToAsync(dst, ct);
            }
            if (new FileInfo(partial).Length != release.ExeSize)
            {
                File.Delete(partial);
                return null;
            }
            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lance l'exe téléchargé. Il s'installe par-dessus celui-ci : il ferme cette fenêtre (les torrents
    /// sont enregistrés), se copie dans le dossier d'installation et se relance de là.
    /// </summary>
    public static void Apply(string exe)
    {
        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false });
    }

    /// <summary>Supprime les exe téléchargés une fois la mise à jour lancée.</summary>
    public static void CleanUp()
    {
        try
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
        }
        catch
        {
        }
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Averse", Current.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
