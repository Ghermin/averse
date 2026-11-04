using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.PieceWriter;

namespace Averse;

/// <summary>Le moteur BitTorrent (MonoTorrent) et les commandes que l'interface peut lui envoyer.</summary>
public sealed class Engine
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Settings settings;
    private ClientEngine engine = null!;
    private string StateFile => Path.Combine(Settings.CacheDir, "engine.state");

    public Engine(Settings settings) => this.settings = settings;

    /// <summary>Un torrent a été refusé (message à montrer). Peut venir de n'importe quel fil.</summary>
    public event Action<string>? Warning;

    /// <summary>Les torrents venus d'ailleurs (navigateur, double-clic…) qui attendent l'accord de l'utilisateur.</summary>
    private readonly List<Pending> pending = [];

    private sealed record Pending(string Key, string Name, long Size, string? Link, byte[]? Bytes);

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

        var factories = Factories.Default.WithPieceWriterCreator(max => new SafeWriter(new DiskWriter(max), IsAllowed));
        if (File.Exists(StateFile))
        {
            try
            {
                engine = await ClientEngine.RestoreStateAsync(StateFile, factories);
                await engine.UpdateSettingsAsync(builder.ToSettings());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Restore failed: " + ex);
                engine = null!;
            }
        }
        engine ??= new ClientEngine(builder.ToSettings(), factories);

        foreach (var m in engine.Torrents.ToList())
        {
            if (!await AcceptAsync(m)) continue;
            if (!settings.Paused.Contains(Id(m))) await m.StartAsync();
        }
    }

    // ---------- Protection des chemins ----------

    private readonly ConditionalWeakTable<ITorrentManagerFile, Tuple<string, bool>> verdicts = new();

    /// <summary>Le fichier reste-t-il dans le dossier de son torrent ? (appelé par <see cref="SafeWriter"/>)</summary>
    private bool IsAllowed(ITorrentManagerFile file)
    {
        if (verdicts.TryGetValue(file, out var v) && v.Item1 == file.FullPath) return v.Item2;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var owner = engine?.Torrents.FirstOrDefault(m => m.Files.Any(f => ReferenceEquals(f, file)));
                var ok = owner != null && SafeWriter.IsInside(file.FullPath, owner.SavePath);
                verdicts.AddOrUpdate(file, Tuple.Create(file.FullPath, ok));
                return ok;
            }
            catch (InvalidOperationException)
            {
                // Liste des torrents modifiée pendant la recherche : on recommence.
            }
        }
        return false;
    }

    private static bool IsSafe(TorrentManager m) =>
        m.Torrent == null || m.Files.All(f => SafeWriter.IsInside(f.FullPath, m.SavePath));

    /// <summary>
    /// Surveille un torrent : si ses fichiers sortent de son dossier (tout de suite, ou une fois reçues les
    /// infos d'un lien magnet), il est retiré. Renvoie false s'il vient d'être retiré.
    /// </summary>
    private async Task<bool> AcceptAsync(TorrentManager m)
    {
        if (!IsSafe(m))
        {
            await RejectAsync(m);
            return false;
        }
        if (m.Torrent == null)
        {
            var checkedOnce = 0;
            void OnState(object? sender, TorrentStateChangedEventArgs e)
            {
                if (m.Torrent == null || Interlocked.Exchange(ref checkedOnce, 1) == 1) return;
                m.TorrentStateChanged -= OnState;
                if (!IsSafe(m)) _ = Task.Run(() => RejectAsync(m));
            }
            m.TorrentStateChanged += OnState;
        }
        return true;
    }

    private async Task RejectAsync(TorrentManager m)
    {
        var name = m.Torrent?.Name ?? m.MagnetLink?.Name ?? "Ce torrent";
        // Juste après la réception des infos d'un magnet, MonoTorrent s'arrête puis redémarre tout seul :
        // l'arrêt peut échouer pendant cette bascule, on réessaie. (SafeWriter bloque les écritures en attendant.)
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (!engine.Torrents.Contains(m)) break;
                if (m.State == TorrentState.Stopping)
                {
                    await Task.Delay(250);
                    continue;
                }
                if (m.State != TorrentState.Stopped) await m.StopAsync(TimeSpan.FromSeconds(3));
                await engine.RemoveAsync(m, RemoveMode.CacheDataOnly);
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Reject failed: " + ex.Message);
                await Task.Delay(250);
            }
        }
        settings.Paused.Remove(Id(m));
        await SaveAsync();
        Warning?.Invoke($"« {name} » a été refusé : il voulait écrire en dehors du dossier de téléchargement.");
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
            case "acceptPending":
            {
                var p = TakePending((string?)args["key"]);
                if (p.Bytes != null) await AddTorrentBytesAsync(p.Bytes);
                else await AddLinkAsync(p.Link!);
                return null;
            }
            case "rejectPending": TakePending((string?)args["key"]); return null;
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
        var asks = new JsonArray();
        lock (pending)
        {
            foreach (var p in pending)
                asks.Add(new JsonObject { ["key"] = p.Key, ["name"] = p.Name, ["size"] = p.Size });
        }
        return new JsonObject
        {
            ["torrents"] = list,
            ["pending"] = asks,
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

    // ---------- Torrents venus d'ailleurs : rien ne démarre sans l'accord de l'utilisateur ----------

    /// <summary>
    /// Un lien magnet ou un .torrent ouvert depuis Windows (navigateur, double-clic…) : vérifié puis mis en
    /// attente. L'interface demande « Ajouter ce torrent ? » et seul un oui le lance.
    /// </summary>
    public async Task QueueExternalAsync(string link)
    {
        link = link.Trim();
        Pending p;
        if (File.Exists(link))
        {
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(link);
            }
            catch
            {
                throw new UserError("Impossible de lire " + Path.GetFileName(link) + ".");
            }
            if (!Torrent.TryLoad(bytes, out var torrent) || torrent == null)
                throw new UserError("Ce fichier n'est pas un .torrent valide.");
            EnsureNew(torrent.InfoHashes);
            p = new Pending(torrent.InfoHashes.V1OrV2.ToHex().ToLowerInvariant(), torrent.Name, torrent.Size, null, bytes);
        }
        else
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(link, "^[0-9a-fA-F]{40}$"))
                link = "magnet:?xt=urn:btih:" + link;
            if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                p = new Pending("url:" + link, link, 0, link, null);
            }
            else
            {
                if (!MagnetLink.TryParse(link, out var magnet) || magnet == null)
                    throw new UserError("Ce lien magnet n'est pas valide.");
                EnsureNew(magnet.InfoHashes);
                var hash = magnet.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
                var name = string.IsNullOrWhiteSpace(magnet.Name) ? "Lien magnet " + hash[..8] : magnet.Name;
                p = new Pending(hash, name, 0, link, null);
            }
        }
        lock (pending)
        {
            if (pending.Any(x => x.Key == p.Key)) return;
            if (pending.Count >= 20) throw new UserError("Trop de torrents en attente : réponds d'abord aux demandes affichées.");
            pending.Add(p);
        }
    }

    private Pending TakePending(string? key)
    {
        lock (pending)
        {
            var p = pending.FirstOrDefault(x => x.Key == key) ?? throw new UserError("Cette demande n'est plus d'actualité.");
            pending.Remove(p);
            return p;
        }
    }

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
        if (!await AcceptAsync(m)) return;
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
        if (!await AcceptAsync(m)) return;
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
