using System.IO.Pipes;

namespace Averse;

internal static class Program
{
    public const string MutexName = @"Local\Averse.SingleInstance";
    public const string PipeName = "Averse.Links";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--uninstall"))
        {
            if (MessageBox.Show("Désinstaller Averse ?\nTes fichiers téléchargés ne sont pas touchés.", "Averse",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Installer.Uninstall();
            }
            return 0;
        }

        // L'exe téléchargé s'installe dans %LOCALAPPDATA%\Programs\Averse et se relance de là.
        if (!Installer.IsRunningInstalled && !args.Contains("--portable") && !System.Diagnostics.Debugger.IsAttached)
        {
            if (Installer.InstallAndRelaunch(args)) return 0;
        }

        var links = args.Where(a => !a.StartsWith("--")).ToArray();

        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Déjà ouvert : lui passer les liens (ou juste le ramener devant).
            SendToRunningInstance(links);
            return 0;
        }

        if (Installer.IsRunningInstalled) Installer.RegisterAssociations();

        var form = new MainForm(new Engine(Settings.Load()), links);
        StartPipeServer(form);
        Application.Run(form);
        return 0;
    }

    private static void SendToRunningInstance(string[] links)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            using var w = new StreamWriter(pipe);
            w.WriteLine(links.Length == 0 ? "--show" : string.Join("\n", links));
        }
        catch { }
    }

    private static void StartPipeServer(MainForm form)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    pipe.WaitForConnection();
                    using var r = new StreamReader(pipe);
                    var lines = new List<string>();
                    while (r.ReadLine() is { } line) if (line.Length > 0) lines.Add(line);
                    form.BeginInvoke(async () =>
                    {
                        form.BringToFront(true);
                        foreach (var l in lines.Where(l => l != "--show")) await form.AddExternalAsync(l);
                    });
                }
                catch
                {
                    Thread.Sleep(500);
                }
            }
        })
        { IsBackground = true, Name = "Averse links" };
        thread.Start();
    }
}
