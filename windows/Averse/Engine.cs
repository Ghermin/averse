using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using MonoTorrent;
using MonoTorrent.Client;

namespace Averse;

/// <summary>Le moteur BitTorrent (MonoTorrent) et les commandes que l'interface peut lui envoyer.</summary>
public sealed class Engine
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Settings settings;
    private ClientEngine engine = null!;
    private string StateFile => Path.Combine(Settings.CacheDir, "engine.state");

    public Engine(Settings settings) => this.settings = settings;

    public async Task StartAsync()
    {
        Directory.CreateDirectory(Settings.CacheDir);
        await SeedDhtCacheAsync();
        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = Settings.CacheDir,
            AllowPortForwarding = true,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(IPAddress.Any, settings.Port) },
                { "ipv6", new IPEndPoint(IPAddress.IPv6Any, settings.Port) },
            },
            DhtEndPoint = new IPEndPoint(IPAddress.Any, settings.Port),
        };

        if (File.Exists(StateFile))
        {
            try
            {
                engine = await ClientEngine.RestoreStateAsync(StateFile);
                await engine.UpdateSettingsAsync(builder.ToSettings());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Restore failed: " + ex);
                engine = null!;
            }
        }
        engine ??= new ClientEngine(builder.ToSettings());

        foreach (var m in engine.Torrents)
        {
            if (!settings.Paused.Contains(Id(m))) await m.StartAsync();
        }
    }

    /// <summary>
    /// MonoTorrent n'amorce la DHT que par router.bittorrent.com, souvent injoignable : sans nœuds en cache,
    /// les liens magnet sans tracker ne trouvent alors personne. On lui donne d'autres points d'entrée connus.
    /// MonoTorrent 3.0.2 enregistre ce cache en liste bencodée mais le relit comme une suite brute de nœuds
    /// compacts (id 20 octets + IPv4 + port) : on le réécrit donc au format brut à chaque démarrage.
    /// </summary>
    private static async Task SeedDhtCacheAsync()
    {
        var path = Path.Combine(Settings.CacheDir, "dht_nodes.cache");
        var nodes = new List<byte[]>();
        try
        {
            var saved = File.Exists(path) ? await File.ReadAllBytesAsync(path) : [];
            if (saved.Length > 0 && saved[0] == (byte)'l')
            {
                // l26:<26 octets>26:<26 octets>…e
                for (var i = 1; i + 29 <= saved.Length && saved[i] == (byte)'2' && saved[i + 1] == (byte)'6' && saved[i + 2] == (byte)':'; i += 29)
                    nodes.Add(saved.AsSpan(i + 3, 26).ToArray());
            }
            else
            {
                for (var i = 0; i + 26 <= saved.Length; i += 26) nodes.Add(saved.AsSpan(i, 26).ToArray());
            }
        }
        catch { }
        if (nodes.Count > 300) nodes = nodes.GetRange(0, 300);

        (string host, int port)[] routers =
        [
            ("dht.transmissionbt.com", 6881), ("dht.libtorrent.org", 25401), ("router.utorrent.com", 6881),
            ("router.bittorrent.com", 6881), ("dht.aelitis.com", 6881),
        ];
        foreach (var (host, port) in routers)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                foreach (var ip in await Dns.GetHostAddressesAsync(host, System.Net.Sockets.AddressFamily.InterNetwork, cts.Token))
                {
                    var node = new byte[26];
                    Random.Shared.NextBytes(node.AsSpan(0, 20));
                    ip.GetAddressBytes().CopyTo(node, 20);
                    node[24] = (byte)(port >> 8);
                    node[25] = (byte)port;
                    nodes.Add(node);
                }
            }
            catch { }
        }
        if (nodes.Count == 0) return;

        using var ms = new MemoryStream();
        foreach (var n in nodes) ms.Write(n);
        try
        {
            await File.WriteAllBytesAsync(path, ms.ToArray());
        }
        catch { }
    }

    public async Task StopAsync()
    {
        try
        {
            await engine.StopAllAsync(TimeSpan.FromSeconds(4));
        }
        catch { }
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            await engine.SaveStateAsync(StateFile);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Save failed: " + ex);
        }
        settings.Save();
    }

    /// <summary>Vrai si un torrent est en train de télécharger (on ne redémarre pas pour une mise à jour).</summary>
    public bool IsBusy => engine != null && engine.Torrents.Any(m => m.State is TorrentState.Downloading
        or TorrentState.Metadata or TorrentState.Hashing or TorrentState.Starting or TorrentState.FetchingHashes);

    private static string Id(TorrentManager m) => m.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();

    private TorrentManager Find(string? id) =>
        engine.Torrents.FirstOrDefault(m => Id(m) == id) ?? throw new UserError("Ce torrent n'est plus dans la liste.");

    // ---------- Commandes ----------

    public async Task<JsonNode?> HandleAsync(string cmd, JsonObject args, MainForm ui)
    {
        switch (cmd)
        {
            case "state": return State();
            case "addMagnet": await AddLinkAsync((string?)args["uri"] ?? ""); return null;
            case "addTorrentFile":
                await AddTorrentBytesAsync(Convert.FromBase64String((string?)args["data"] ?? ""));
                return null;
            case "pickTorrent":
                foreach (var path in ui.PickTorrentFiles()) await AddTorrentFileAsync(path);
                return null;
            case "pause":
            {
                var m = Find((string?)args["id"]);
                settings.Paused.Add(Id(m));
                await m.StopAsync();
                await SaveAsync();
                return null;
            }
            case "resume":
            {
                var m = Find((string?)args["id"]);
                settings.Paused.Remove(Id(m));
                if (m.State == TorrentState.Error) await m.StopAsync();
                await m.StartAsync();
                await SaveAsync();
                return null;
            }
            case "remove": await RemoveAsync(Find((string?)args["id"]), (bool?)args["deleteFiles"] ?? false); return null;
            case "open": Open(Find((string?)args["id"])); return null;
            case "openDir":
                Directory.CreateDirectory(settings.DownloadDir);
                Process.Start(new ProcessStartInfo("explorer.exe", Quote(settings.DownloadDir)));
                return null;
            case "changeDir":
                var dir = ui.PickFolder(settings.DownloadDir);
                if (dir != null)
                {
                    settings.DownloadDir = dir;
                    settings.Save();
                }
                return null;
            default: throw new UserError("Commande inconnue : " + cmd);
        }
    }

    private JsonObject State()
    {
        var list = new JsonArray();
        foreach (var m in engine.Torrents) list.Add(Describe(m));
        return new JsonObject
        {
            ["torrents"] = list,
            ["downloadDir"] = settings.DownloadDir,
            ["canChangeDir"] = true,
        };
    }

    private static JsonObject Describe(TorrentManager m)
    {
        var size = m.Torrent?.Size ?? 0;
        var progress = m.Torrent == null ? 0 : m.Progress / 100.0;
        var done = (long)(progress * size);
        var down = m.Monitor.DownloadRate;
        var state = m.State switch
        {
            TorrentState.Metadata => "metadata",
            TorrentState.Hashing or TorrentState.HashingPaused or TorrentState.FetchingHashes => "checking",
            TorrentState.Starting => "queued",
            TorrentState.Downloading => "downloading",
            TorrentState.Seeding => "seeding",
            TorrentState.Error => "error",
            _ => m.Complete ? "done" : "paused",
        };
        return new JsonObject
        {
            ["id"] = Id(m),
            ["name"] = m.Torrent?.Name ?? m.MagnetLink?.Name ?? "Lien magnet",
            ["state"] = state,
            ["progress"] = progress,
            ["size"] = size,
            ["done"] = done,
            ["down"] = down,
            ["up"] = m.Monitor.UploadRate,
            ["peers"] = m.OpenConnections,
            ["eta"] = down > 0 && size > done ? (size - done) / (double)down : -1,
            ["error"] = m.Error == null ? null : ErrorText(m.Error),
        };
    }

    private static string ErrorText(MonoTorrent.Client.Error error) => error.Reason switch
    {
        Reason.ReadFailure => "lecture du disque impossible",
        Reason.WriteFailure => error.Exception is IOException io && (io.HResult & 0xFFFF) is 0x70 or 0x27
            ? "disque plein"
            : "écriture sur le disque impossible",
        _ => error.Exception?.Message ?? "inconnue",
    };

    // ---------- Ajout ----------

    /// <summary>Lien magnet, empreinte seule ou adresse http(s) d'un .torrent.</summary>
    public async Task AddLinkAsync(string link)
    {
        link = link.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(link, "^[0-9a-fA-F]{40}$"))
            link = "magnet:?xt=urn:btih:" + link;

        if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            byte[] bytes;
            try
            {
                bytes = await Http.GetByteArrayAsync(link);
            }
            catch
            {
                throw new UserError("Impossible de télécharger ce fichier .torrent.");
            }
            await AddTorrentBytesAsync(bytes);
            return;
        }

        if (!MagnetLink.TryParse(link, out var magnet) || magnet == null)
            throw new UserError("Ce lien magnet n'est pas valide.");
        EnsureNew(magnet.InfoHashes);
        var m = await engine.AddAsync(magnet, settings.DownloadDir);
        await m.StartAsync();
        await SaveAsync();
    }

    public async Task AddTorrentFileAsync(string path)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch
        {
            throw new UserError("Impossible de lire " + Path.GetFileName(path) + ".");
        }
        await AddTorrentBytesAsync(bytes);
    }

    private async Task AddTorrentBytesAsync(byte[] bytes)
    {
        if (!Torrent.TryLoad(bytes, out var torrent) || torrent == null)
            throw new UserError("Ce fichier n'est pas un .torrent valide.");
        EnsureNew(torrent.InfoHashes);
        var m = await engine.AddAsync(torrent, settings.DownloadDir);
        await m.StartAsync();
        await SaveAsync();
    }

    private void EnsureNew(InfoHashes hashes)
    {
        foreach (var m in engine.Torrents)
        {
            if (Same(m.InfoHashes.V1, hashes.V1) || Same(m.InfoHashes.V2, hashes.V2))
                throw new UserError("Ce torrent est déjà dans la liste.");
        }
    }

    private static bool Same(InfoHash? a, InfoHash? b) => a != null && b != null && a.Equals(b);

    // ---------- Retrait et ouverture ----------

    private async Task RemoveAsync(TorrentManager m, bool deleteFiles)
    {
        var dir = m.Torrent != null && m.Files.Count > 1 ? m.ContainingDirectory : null;
        settings.Paused.Remove(Id(m));
        if (m.State != TorrentState.Stopped) await m.StopAsync(TimeSpan.FromSeconds(3));
        await engine.RemoveAsync(m, deleteFiles ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly);
        if (deleteFiles && dir != null) DeleteEmptyDirs(dir);
        await SaveAsync();
    }

    private static void DeleteEmptyDirs(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var sub in Directory.GetDirectories(dir)) DeleteEmptyDirs(sub);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

    private static void Open(TorrentManager m)
    {
        string? target = null;
        if (m.Torrent != null)
        {
            target = m.Files.Count == 1 ? m.Files[0].FullPath : m.ContainingDirectory;
        }
        if (target != null && File.Exists(target))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Quote(target)));
        }
        else if (target != null && Directory.Exists(target))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", Quote(target)));
        }
        else
        {
            Directory.CreateDirectory(m.SavePath);
            Process.Start(new ProcessStartInfo("explorer.exe", Quote(m.SavePath)));
        }
    }

    private static string Quote(string s) => "\"" + s + "\"";
}

/// <summary>Une erreur à montrer telle quelle à l'utilisateur.</summary>
public sealed class UserError(string message) : Exception(message);
