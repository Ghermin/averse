using MonoTorrent;
using MonoTorrent.PieceWriter;
using ReusableTasks;

namespace Averse;

/// <summary>
/// Toutes les lectures et écritures de MonoTorrent passent par ici. Un fichier dont le chemin sort du
/// dossier de son torrent est refusé : MonoTorrent 3.0.2 laisse passer « ..\ » et les chemins absolus
/// dans le nom d'un torrent à fichier unique, ce qui permettrait à un torrent piégé d'écrire n'importe où
/// (dossier Démarrage, etc.).
/// Un fichier supprimé à la main par l'utilisateur (<paramref name="isGone"/>) n'est plus jamais ouvert :
/// MonoTorrent le recréerait sinon (ouverture en OpenOrCreate) ou relancerait une vérification complète
/// qui le retéléchargerait.
/// </summary>
public sealed class SafeWriter(
    IPieceWriter inner, Func<ITorrentManagerFile, bool> isAllowed, Func<ITorrentManagerFile, bool> isGone) : IPieceWriter
{
    public int OpenFiles => inner.OpenFiles;
    public int MaximumOpenFiles => inner.MaximumOpenFiles;

    private void Check(ITorrentManagerFile file)
    {
        if (!isAllowed(file))
            throw new UnauthorizedAccessException("Chemin refusé, hors du dossier de téléchargement : " + file.FullPath);
    }

    public ReusableTask CloseAsync(ITorrentManagerFile file) => inner.CloseAsync(file);
    public ReusableTask FlushAsync(ITorrentManagerFile file) => inner.FlushAsync(file);
    public ReusableTask SetMaximumOpenFilesAsync(int maximumOpenFiles) => inner.SetMaximumOpenFilesAsync(maximumOpenFiles);
    public void Dispose() => inner.Dispose();

    // Fichier supprimé : on fait comme s'il était toujours là (pas de nouvelle vérification au démarrage),
    // les lectures ne renvoient rien et les écritures sont ignorées.

    public ReusableTask<bool> ExistsAsync(ITorrentManagerFile file)
    {
        Check(file);
        return isGone(file) ? ReusableTask.FromResult(true) : inner.ExistsAsync(file);
    }

    public ReusableTask<int> ReadAsync(ITorrentManagerFile file, long offset, Memory<byte> buffer)
    {
        Check(file);
        if (!isGone(file)) return inner.ReadAsync(file, offset, buffer);
        buffer.Span.Clear();
        return ReusableTask.FromResult(0);
    }

    public ReusableTask WriteAsync(ITorrentManagerFile file, long offset, ReadOnlyMemory<byte> buffer)
    {
        Check(file);
        return isGone(file) ? ReusableTask.CompletedTask : inner.WriteAsync(file, offset, buffer);
    }

    public ReusableTask MoveAsync(ITorrentManagerFile file, string fullPath, bool overwrite)
    {
        Check(file);
        return isGone(file) ? ReusableTask.CompletedTask : inner.MoveAsync(file, fullPath, overwrite);
    }

    /// <summary>Vrai si <paramref name="path"/> est strictement à l'intérieur de <paramref name="root"/>.</summary>
    public static bool IsInside(string path, string root)
    {
        try
        {
            // Partage réseau, \\?\, \\.\ : seulement si le dossier choisi en est un lui-même.
            if (path.StartsWith(@"\\", StringComparison.Ordinal) && !root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var full = Path.GetFullPath(path);
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            return full.Length > dir.Length && full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
