using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Averse;

public sealed class MainForm : Form
{
    private const string Origin = "https://averse.app/";

    private readonly Engine engine;
    private readonly WebView2 web = new() { Dock = DockStyle.Fill };
    private readonly Queue<string> pendingLinks = new();
    private bool engineReady;
    private bool closing;

    // Mises à jour automatiques (comme CamTel) : au lancement puis toutes les 6 h.
    private readonly bool updated;
    private readonly DateTime startedAt = DateTime.UtcNow;
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 10 * 60 * 1000 };
    private DateTime nextUpdateCheck;
    private string? pendingUpdate;
    private bool checkingUpdate;
    private bool waitingToldUser;

    public MainForm(Engine engine, IEnumerable<string> links, bool updated)
    {
        this.engine = engine;
        this.updated = updated;
        foreach (var l in links) pendingLinks.Enqueue(l);

        Text = "Averse";
        Icon = LoadIcon();
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Size = new Size(Math.Min(980, area.Width - 80), Math.Min(760, area.Height - 80));
        MinimumSize = new Size(420, 480);
        BackColor = IsDarkMode() ? Color.FromArgb(0x0e, 0x0e, 0x12) : Color.FromArgb(0xf6, 0xf5, 0xf1);
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);
    }

    public static Icon LoadIcon()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("averse.ico")!;
        return new Icon(s);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = IsDarkMode() ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await InitWebAsync();
        try
        {
            await engine.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Le moteur torrent n'a pas pu démarrer :\n" + ex.Message, "Averse",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        engineReady = true;
        while (pendingLinks.Count > 0) await AddExternalAsync(pendingLinks.Dequeue());

        if (updated)
        {
            Updater.CleanUp();
            _ = ToastLaterAsync($"Averse a été mis à jour (version {Updater.Current}).");
        }
        updateTimer.Tick += (_, _) => _ = AutoUpdateAsync();
        updateTimer.Start();
        _ = AutoUpdateAsync();
    }

    /// <summary>
    /// Cherche une nouvelle version sur GitHub au lancement puis toutes les 6 heures. Elle est téléchargée
    /// en arrière-plan et installée tout de suite si rien ne se télécharge (ou si Averse vient d'être ouvert),
    /// sinon dès que les téléchargements en cours sont finis. Les torrents reprennent tout seuls.
    /// </summary>
    private async Task AutoUpdateAsync()
    {
        if (!Installer.IsRunningInstalled || checkingUpdate || closing) return;
        if (pendingUpdate != null)
        {
            if (CanRestartNow()) Updater.Apply(pendingUpdate);
            return;
        }
        if (DateTime.UtcNow < nextUpdateCheck) return;
        nextUpdateCheck = DateTime.UtcNow.AddHours(6);
        checkingUpdate = true;
        try
        {
            var release = await Updater.CheckAsync();
            if (release == null) return;
            pendingUpdate = await Updater.DownloadAsync(release);
            if (pendingUpdate == null) return;
            if (CanRestartNow())
            {
                Updater.Apply(pendingUpdate);
            }
            else if (!waitingToldUser)
            {
                waitingToldUser = true;
                Toast($"Averse {release.Version} est prêt : il s'installera à la fin des téléchargements.");
            }
        }
        finally
        {
            checkingUpdate = false;
        }
    }

    private bool CanRestartNow() => DateTime.UtcNow - startedAt < TimeSpan.FromMinutes(2) || !engine.IsBusy;

    private async Task ToastLaterAsync(string message)
    {
        await Task.Delay(2500);
        Toast(message);
    }

    private async Task InitWebAsync()
    {
        CoreWebView2Environment env;
        try
        {
            env = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Averse", "WebView2"));
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (MessageBox.Show(this,
                    "Averse a besoin de Microsoft Edge WebView2, qui n'est pas installé sur ce PC.\nOuvrir la page de téléchargement ?",
                    "Averse", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });
            }
            Environment.Exit(1);
            return;
        }

        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = Debugger.IsAttached;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        var files = LoadUiFiles();
        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, a) =>
        {
            var path = new Uri(a.Request.Uri).AbsolutePath.TrimStart('/');
            if (path == "") path = "index.html";
            if (files.TryGetValue(path, out var bytes))
            {
                a.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK",
                    "Content-Type: " + MimeOf(path) + "\nCache-Control: no-store");
            }
            else
            {
                a.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
            }
        };
        // Liens externes : dans le navigateur, jamais dans la fenêtre de l'appli.
        core.NewWindowRequested += (_, a) =>
        {
            a.Handled = true;
            OpenInBrowser(a.Uri);
        };
        core.NavigationStarting += (_, a) =>
        {
            if (a.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return;
            a.Cancel = true;
            if (a.Uri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) _ = AddExternalAsync(a.Uri);
            else if (a.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) OpenInBrowser(a.Uri);
        };
        core.WebMessageReceived += OnMessage;
        core.Navigate(Origin + "index.html");
    }

    private static Dictionary<string, byte[]> LoadUiFiles()
    {
        var asm = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("ui/")) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            files[name[3..].Replace('\\', '/')] = ms.ToArray();
        }
        return files;
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    private static void OpenInBrowser(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch { }
    }

    // ---------- Messages de l'interface ----------

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject? msg;
        try
        {
            msg = JsonNode.Parse(e.TryGetWebMessageAsString()) as JsonObject;
        }
        catch
        {
            return;
        }
        if (msg == null) return;
        var id = msg["id"]?.GetValue<int>() ?? 0;
        var cmd = (string?)msg["cmd"] ?? "";
        var args = msg["args"] as JsonObject ?? new JsonObject();

        var reply = new JsonObject { ["id"] = id };
        if (!engineReady && cmd != "state")
        {
            reply["error"] = "Averse démarre encore, réessaie dans un instant.";
        }
        else if (!engineReady)
        {
            reply["result"] = new JsonObject { ["torrents"] = new JsonArray(), ["downloadDir"] = "…" };
        }
        else
        {
            try
            {
                reply["result"] = await engine.HandleAsync(cmd, args, this);
            }
            catch (UserError ex)
            {
                reply["error"] = ex.Message;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                reply["error"] = "Erreur : " + ex.Message;
            }
        }
        Post(reply);
    }

    private void Post(JsonNode node)
    {
        if (closing || web.CoreWebView2 == null) return;
        try
        {
            web.CoreWebView2.PostWebMessageAsJson(node.ToJsonString());
        }
        catch { }
    }

    private void Toast(string message, bool error = false) =>
        Post(new JsonObject { ["event"] = "toast", ["message"] = message, ["error"] = error });

    /// <summary>Un lien magnet ou un .torrent ouvert depuis Windows (double-clic, navigateur…).</summary>
    public async Task AddExternalAsync(string link)
    {
        if (!engineReady)
        {
            pendingLinks.Enqueue(link);
            return;
        }
        try
        {
            if (File.Exists(link)) await engine.AddTorrentFileAsync(link);
            else await engine.AddLinkAsync(link);
            Toast("Torrent ajouté.");
        }
        catch (UserError ex)
        {
            Toast(ex.Message, error: true);
        }
        catch (Exception ex)
        {
            Toast("Erreur : " + ex.Message, error: true);
        }
        Post(new JsonObject { ["event"] = "changed" });
    }

    public void BringToFront(bool _ = true)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
        SetForegroundWindow(Handle);
    }

    // ---------- Boîtes de dialogue ----------

    public string[] PickTorrentFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Ouvrir un fichier .torrent",
            Filter = "Fichiers torrent (*.torrent)|*.torrent",
            Multiselect = true,
        };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileNames : [];
    }

    public string? PickFolder(string current)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Où ranger les téléchargements ?",
            UseDescriptionForTitle = true,
            InitialDirectory = current,
            ShowNewFolderButton = true,
        };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
    }

    // ---------- Fermeture ----------

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        closing = true;
        updateTimer.Stop();
        Hide();
        if (engineReady) await engine.StopAsync();
        Application.Exit();
    }

    private static bool IsDarkMode()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
