package io.github.ghermin.averse

import android.content.Context
import android.os.Environment
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import org.libtorrent4j.AddTorrentParams
import org.libtorrent4j.AlertListener
import org.libtorrent4j.SessionHandle
import org.libtorrent4j.SessionManager
import org.libtorrent4j.SessionParams
import org.libtorrent4j.SettingsPack
import org.libtorrent4j.Sha1Hash
import org.libtorrent4j.TorrentFlags
import org.libtorrent4j.TorrentHandle
import org.libtorrent4j.TorrentInfo
import org.libtorrent4j.TorrentStatus
import org.libtorrent4j.Vectors
import org.libtorrent4j.alerts.Alert
import org.libtorrent4j.alerts.AlertType
import org.libtorrent4j.alerts.SaveResumeDataAlert
import org.libtorrent4j.alerts.TorrentAlert
import org.libtorrent4j.swig.error_code
import org.libtorrent4j.swig.libtorrent
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/** Une erreur à montrer telle quelle à l'utilisateur. */
class UserError(message: String) : Exception(message)

/**
 * Le moteur BitTorrent (libtorrent via libtorrent4j). Tout passe par un fil dédié : la session,
 * les alertes et les commandes de l'interface.
 */
class TorrentEngine(private val context: Context) {
    private val thread = HandlerThread("averse-engine").apply { start() }
    val handler = Handler(thread.looper)
    private val session = SessionManager(false)
    private val resumeDir = File(context.filesDir, "torrents").apply { mkdirs() }
    private val stateFile = File(context.filesDir, "session.state")
    private val prefs = context.getSharedPreferences("averse", Context.MODE_PRIVATE)

    /** Prévenu (sur le fil du moteur) quand quelque chose change : le service met à jour sa notification. */
    var onChange: (() -> Unit)? = null

    val downloadDir: File by lazy { chooseDownloadDir() }

    fun start() = handler.post {
        session.addListener(object : AlertListener {
            override fun types(): IntArray = intArrayOf(
                AlertType.ADD_TORRENT.swig(), AlertType.SAVE_RESUME_DATA.swig(), AlertType.METADATA_RECEIVED.swig(),
                AlertType.TORRENT_FINISHED.swig(), AlertType.STATE_CHANGED.swig(), AlertType.TORRENT_ERROR.swig(),
                AlertType.FILE_ERROR.swig(),
            )

            override fun alert(alert: Alert<*>) = onAlert(alert)
        })

        val settings = SettingsPack()
            .listenInterfaces("0.0.0.0:${port()},[::]:${port()}")
        settings.setDhtBootstrapNodes(
            "dht.libtorrent.org:25401,router.bittorrent.com:6881,dht.transmissionbt.com:6881,router.utorrent.com:6881"
        )
        val params = try {
            if (stateFile.exists()) SessionParams(stateFile.readBytes()) else SessionParams()
        } catch (e: Exception) {
            SessionParams()
        }
        params.settings = settings
        session.start(params)

        // Reprendre les torrents de la dernière fois.
        resumeDir.listFiles { f -> f.name.endsWith(".resume") }?.forEach { f ->
            try {
                val ec = error_code()
                val p = libtorrent.read_resume_data_ex(Vectors.bytes2byte_vector(f.readBytes()), ec)
                if (ec.value() != 0) throw IllegalStateException(ec.message())
                if (p.save_path.isNullOrEmpty()) p.save_path = downloadDir.absolutePath
                session.swig().async_add_torrent(p)
            } catch (e: Exception) {
                Log.w(TAG, "Reprise impossible pour ${f.name}", e)
            }
        }

        handler.postDelayed(object : Runnable {
            override fun run() {
                saveAll(onlyIfModified = true)
                handler.postDelayed(this, 30_000)
            }
        }, 30_000)
    }

    private fun port(): Int {
        var p = prefs.getInt("port", 0)
        if (p == 0) {
            p = (20000..60000).random()
            prefs.edit().putInt("port", p).apply()
        }
        return p
    }

    /** Le dossier Download du téléphone, sinon (vieux téléphones sans accès) le dossier de l'appli. */
    private fun chooseDownloadDir(): File {
        val pub = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
        try {
            pub.mkdirs()
            val probe = File(pub, ".averse-test")
            probe.writeText("ok")
            probe.delete()
            return pub
        } catch (e: Exception) {
            Log.w(TAG, "Download inaccessible", e)
        }
        val own = File(context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS), "Averse")
        own.mkdirs()
        return own
    }

    private fun handles(): List<TorrentHandle> =
        SessionHandle(session.swig()).torrents().filter { it.isValid }

    private fun id(h: TorrentHandle) = h.infoHash().toHex()

    private fun find(id: String?): TorrentHandle {
        if (id == null) throw UserError("Ce torrent n'est plus dans la liste.")
        return handles().firstOrNull { id(it) == id } ?: throw UserError("Ce torrent n'est plus dans la liste.")
    }

    private fun onAlert(alert: Alert<*>) {
        when (alert.type()) {
            AlertType.SAVE_RESUME_DATA -> {
                val a = alert as SaveResumeDataAlert
                try {
                    val h = a.handle()
                    val bytes = AddTorrentParams.writeResumeDataBuf(a.params())
                    val f = File(resumeDir, "${id(h)}.resume")
                    val tmp = File(resumeDir, "${id(h)}.tmp")
                    tmp.writeBytes(bytes)
                    tmp.renameTo(f)
                } catch (e: Exception) {
                    Log.w(TAG, "Sauvegarde impossible", e)
                }
            }
            AlertType.ADD_TORRENT, AlertType.METADATA_RECEIVED, AlertType.TORRENT_FINISHED -> {
                val h = (alert as TorrentAlert<*>).handle()
                if (h.isValid) h.saveResumeData(TorrentHandle.SAVE_INFO_DICT)
                onChange?.invoke()
            }
            else -> onChange?.invoke()
        }
    }

    fun saveAll(onlyIfModified: Boolean = false) {
        for (h in handles()) {
            if (!onlyIfModified || h.needSaveResumeData()) h.saveResumeData(TorrentHandle.SAVE_INFO_DICT)
        }
        try {
            stateFile.writeBytes(session.saveState())
        } catch (_: Exception) {
        }
    }

    // ---------- Commandes ----------

    fun state(): JSONObject {
        val list = JSONArray()
        for (h in handles()) list.put(describe(h))
        return JSONObject()
            .put("torrents", list)
            .put("downloadDir", displayPath(downloadDir))
            .put("canChangeDir", false)
    }

    private fun displayPath(f: File): String {
        val root = Environment.getExternalStorageDirectory().absolutePath
        return if (f.absolutePath.startsWith(root)) "Stockage interne" + f.absolutePath.removePrefix(root) else f.absolutePath
    }

    private fun describe(h: TorrentHandle): JSONObject {
        val s = h.status()
        val paused = s.flags().and_(TorrentFlags.PAUSED).non_zero()
        val ec = s.errorCode()
        val state = when {
            ec.isError -> "error"
            paused -> if (s.isFinished) "done" else "paused"
            else -> when (s.state()) {
                TorrentStatus.State.CHECKING_FILES, TorrentStatus.State.CHECKING_RESUME_DATA -> "checking"
                TorrentStatus.State.DOWNLOADING_METADATA -> "metadata"
                TorrentStatus.State.DOWNLOADING -> "downloading"
                TorrentStatus.State.FINISHED, TorrentStatus.State.SEEDING -> "seeding"
                else -> "queued"
            }
        }
        val size = if (s.hasMetadata()) s.totalWanted() else 0L
        val done = if (s.hasMetadata()) s.totalWantedDone() else 0L
        val down = s.downloadPayloadRate()
        return JSONObject()
            .put("id", id(h))
            .put("name", s.name().ifEmpty { h.torrentFile()?.name().orEmpty() }.ifEmpty { "Lien magnet" })
            .put("state", state)
            .put("progress", if (s.hasMetadata()) s.progress().toDouble() else 0.0)
            .put("size", size)
            .put("done", done)
            .put("down", down)
            .put("up", s.uploadPayloadRate())
            .put("peers", s.numPeers())
            .put("eta", if (down > 0 && size > done) (size - done).toDouble() / down else -1.0)
            .put("error", if (ec.isError) ec.message else JSONObject.NULL)
    }

    /** Nombre de torrents qui travaillent (pour garder le service en vie). */
    fun activeCount(): Int = handles().count { h ->
        val s = h.status()
        !s.flags().and_(TorrentFlags.PAUSED).non_zero() && !s.isFinished && !s.errorCode().isError
    }

    fun totals(): Pair<Long, Long> {
        var d = 0L
        var u = 0L
        for (h in handles()) {
            val s = h.status()
            d += s.downloadPayloadRate()
            u += s.uploadPayloadRate()
        }
        return d to u
    }

    /** Lien magnet, empreinte seule ou adresse http(s) d'un .torrent. */
    fun addLink(raw: String) {
        var link = raw.trim()
        if (Regex("^[0-9a-fA-F]{40}$").matches(link)) link = "magnet:?xt=urn:btih:$link"
        if (link.startsWith("http://", true) || link.startsWith("https://", true)) {
            val bytes = try {
                val c = URL(link).openConnection() as HttpURLConnection
                c.connectTimeout = 15000
                c.readTimeout = 30000
                c.inputStream.use { it.readBytes() }
            } catch (e: Exception) {
                throw UserError("Impossible de télécharger ce fichier .torrent.")
            }
            addTorrentBytes(bytes)
            return
        }
        if (!link.startsWith("magnet:", true)) throw UserError("Ce n'est pas un lien magnet.")
        val p = try {
            AddTorrentParams.parseMagnetUri(link)
        } catch (e: Exception) {
            throw UserError("Ce lien magnet n'est pas valide.")
        }
        val hash = p.infoHashes.best
        if (hash.isAllZeros) throw UserError("Ce lien magnet n'est pas valide.")
        ensureNew(hash)
        p.savePath = downloadDir.absolutePath
        p.flags = p.flags.and_(TorrentFlags.AUTO_MANAGED.inv()).and_(TorrentFlags.PAUSED.inv())
        session.swig().async_add_torrent(p.swig())
    }

    fun addTorrentBytes(bytes: ByteArray) {
        val ti = try {
            TorrentInfo(bytes)
        } catch (e: Exception) {
            throw UserError("Ce fichier n'est pas un .torrent valide.")
        }
        ensureNew(ti.infoHash())
        val p = AddTorrentParams()
        p.torrentInfo = ti
        p.savePath = downloadDir.absolutePath
        p.flags = p.flags.and_(TorrentFlags.AUTO_MANAGED.inv()).and_(TorrentFlags.PAUSED.inv())
        session.swig().async_add_torrent(p.swig())
    }

    private fun ensureNew(hash: Sha1Hash) {
        val h = session.find(hash)
        if (h != null && h.isValid) throw UserError("Ce torrent est déjà dans la liste.")
    }

    fun pause(id: String?) {
        val h = find(id)
        h.unsetFlags(TorrentFlags.AUTO_MANAGED)
        h.pause()
        h.saveResumeData(TorrentHandle.SAVE_INFO_DICT)
    }

    fun resume(id: String?) {
        val h = find(id)
        h.unsetFlags(TorrentFlags.AUTO_MANAGED)
        h.resume()
        h.saveResumeData(TorrentHandle.SAVE_INFO_DICT)
    }

    fun remove(id: String?, deleteFiles: Boolean) {
        val h = find(id)
        val hash = id(h)
        if (deleteFiles) session.remove(h, SessionHandle.DELETE_FILES) else session.remove(h)
        File(resumeDir, "$hash.resume").delete()
    }

    /** Le fichier téléchargé s'il est unique et terminé, sinon null. */
    fun singleFile(id: String?): File? {
        val h = find(id)
        val ti = h.torrentFile() ?: return null
        if (ti.numFiles() != 1 || !h.status().isFinished) return null
        val f = File(h.savePath(), ti.files().filePath(0))
        return if (f.exists()) f else null
    }

    fun stop() {
        saveAll()
        Thread.sleep(500)
        session.stop()
    }

    companion object {
        const val TAG = "Averse"
    }
}
