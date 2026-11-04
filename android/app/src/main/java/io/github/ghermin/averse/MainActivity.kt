package io.github.ghermin.averse

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.app.DownloadManager
import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.util.Base64
import android.view.View
import android.webkit.JavascriptInterface
import android.webkit.MimeTypeMap
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.FrameLayout
import androidx.core.content.FileProvider
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.webkit.WebViewAssetLoader
import org.json.JSONObject
import java.io.File

class MainActivity : Activity() {
    private lateinit var web: WebView
    private lateinit var engine: TorrentEngine
    private var pickReplyId = 0

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        engine = (application as AverseApp).engine
        engine.onWarning = { toast(it, error = true) }
        TorrentService.createChannel(this)

        val night = (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES
        val bg = if (night) Color.rgb(0x0e, 0x0e, 0x12) else Color.rgb(0xf6, 0xf5, 0xf1)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.decorView.setBackgroundColor(bg)
        WindowCompat.getInsetsController(window, window.decorView).apply {
            isAppearanceLightStatusBars = !night
            isAppearanceLightNavigationBars = !night
        }

        // La WebView ignore son propre padding : les marges des barres système vont sur un conteneur.
        web = WebView(this)
        web.setBackgroundColor(bg)
        val root = FrameLayout(this)
        root.setBackgroundColor(bg)
        root.addView(web, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
        setContentView(root)
        ViewCompat.setOnApplyWindowInsetsListener(root) { v, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
            val ime = insets.getInsets(WindowInsetsCompat.Type.ime())
            v.setPadding(bars.left, bars.top, bars.right, maxOf(bars.bottom, ime.bottom))
            WindowInsetsCompat.CONSUMED
        }

        val loader = WebViewAssetLoader.Builder()
            .addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(this))
            .build()
        web.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            allowFileAccess = false
            allowContentAccess = false
            setSupportZoom(false)
        }
        web.overScrollMode = View.OVER_SCROLL_NEVER
        web.webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? =
                loader.shouldInterceptRequest(request.url)

            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                val url = request.url
                if (url.host == "appassets.androidplatform.net") return false
                if (url.scheme == "magnet") {
                    addExternal(url.toString())
                } else {
                    try {
                        startActivity(Intent(Intent.ACTION_VIEW, url))
                    } catch (_: ActivityNotFoundException) {
                    }
                }
                return true
            }
        }
        web.addJavascriptInterface(Bridge(), "AverseAndroid")
        web.loadUrl("https://appassets.androidplatform.net/assets/index.html")

        handleIntent(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    override fun onResume() {
        super.onResume()
        Updater.maybeUpdate(this, engine)
    }

    override fun onStop() {
        super.onStop()
        engine.handler.post { engine.saveAll() }
    }

    override fun onDestroy() {
        engine.onWarning = null
        web.destroy()
        super.onDestroy()
    }

    // ---------- Liens magnet et fichiers .torrent ouverts depuis d'autres applis ----------

    private fun handleIntent(intent: Intent?) {
        if (intent == null) return
        val uri: Uri? = when (intent.action) {
            Intent.ACTION_VIEW -> intent.data
            Intent.ACTION_SEND -> {
                val text = intent.getStringExtra(Intent.EXTRA_TEXT)
                if (text != null) {
                    addExternal(Regex("magnet:\\?\\S+").find(text)?.value ?: text)
                    null
                } else {
                    @Suppress("DEPRECATION")
                    intent.getParcelableExtra(Intent.EXTRA_STREAM)
                }
            }
            else -> null
        }
        intent.action = null
        if (uri == null) return
        if (uri.scheme == "magnet" || uri.scheme == "http" || uri.scheme == "https") {
            addExternal(uri.toString())
        } else {
            addFromUri(uri, replyId = 0)
        }
    }

    /**
     * Lien venu d'une autre appli (navigateur, partage…). Il ne démarre pas tout seul : l'interface demande
     * d'abord « Ajouter ce torrent ? », pour qu'une page web ne puisse rien lancer.
     */
    private fun addExternal(link: String) {
        engine.handler.post {
            try {
                engine.queueLink(link)
                send(JSONObject().put("event", "changed"))
            } catch (e: UserError) {
                toast(e.message ?: "Erreur", error = true)
            } catch (e: Exception) {
                toast("Erreur : ${e.message}", error = true)
            }
        }
    }

    /** Un .torrent : choisi dans Averse (replyId ≠ 0, ajouté tout de suite) ou ouvert depuis une autre appli (mis en attente). */
    private fun addFromUri(uri: Uri, replyId: Int) {
        if (replyId != 0) ensurePermissions()
        val bytes = try {
            contentResolver.openInputStream(uri)?.use { it.readBytes() }
        } catch (e: Exception) {
            null
        }
        engine.handler.post {
            try {
                if (bytes == null) throw UserError("Impossible de lire ce fichier.")
                if (replyId != 0) {
                    engine.addTorrentBytes(bytes)
                    startService()
                    reply(replyId, null)
                } else {
                    engine.queueTorrentBytes(bytes)
                    send(JSONObject().put("event", "changed"))
                }
            } catch (e: Exception) {
                val msg = (e as? UserError)?.message ?: "Erreur : ${e.message}"
                if (replyId != 0) replyError(replyId, msg) else toast(msg, error = true)
            }
        }
    }

    private fun startService() = runOnUiThread { TorrentService.start(this) }

    // ---------- Permissions ----------

    private fun ensurePermissions() {
        val wanted = mutableListOf<String>()
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) wanted += Manifest.permission.POST_NOTIFICATIONS
        if (Build.VERSION.SDK_INT <= 29 &&
            checkSelfPermission(Manifest.permission.WRITE_EXTERNAL_STORAGE) != PackageManager.PERMISSION_GRANTED
        ) wanted += Manifest.permission.WRITE_EXTERNAL_STORAGE
        if (wanted.isNotEmpty()) runOnUiThread { requestPermissions(wanted.toTypedArray(), 1) }
    }

    // ---------- Choix d'un fichier ----------

    @Deprecated("API Activity simple, sans androidx.activity")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != PICK) return
        val id = pickReplyId
        pickReplyId = 0
        if (resultCode != RESULT_OK || data == null) {
            reply(id, null)
            return
        }
        val uris = mutableListOf<Uri>()
        data.clipData?.let { c -> for (i in 0 until c.itemCount) uris += c.getItemAt(i).uri }
        if (uris.isEmpty()) data.data?.let { uris += it }
        uris.forEachIndexed { i, u -> addFromUri(u, replyId = if (i == uris.lastIndex) id else 0) }
        if (uris.isEmpty()) reply(id, null)
    }

    private fun pickTorrent(replyId: Int) {
        pickReplyId = replyId
        val intent = Intent(Intent.ACTION_OPEN_DOCUMENT)
            .addCategory(Intent.CATEGORY_OPENABLE)
            .setType("*/*")
            .putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
        @Suppress("DEPRECATION")
        startActivityForResult(intent, PICK)
    }

    // ---------- Ouvrir un téléchargement ----------

    private fun openDownloads() {
        // D'abord le dossier Download lui-même dans l'appli Fichiers, sinon la vue Téléchargements.
        val root = android.os.Environment.getExternalStorageDirectory().absolutePath
        val dir = engine.downloadDir.absolutePath
        if (dir.startsWith("$root/")) {
            val docId = "primary:" + dir.removePrefix("$root/")
            val uri = android.provider.DocumentsContract.buildDocumentUri("com.android.externalstorage.documents", docId)
            try {
                startActivity(
                    Intent(Intent.ACTION_VIEW).setDataAndType(uri, android.provider.DocumentsContract.Document.MIME_TYPE_DIR)
                        .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
                )
                return
            } catch (_: Exception) {
            }
        }
        try {
            startActivity(Intent(DownloadManager.ACTION_VIEW_DOWNLOADS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        } catch (_: ActivityNotFoundException) {
            toast("Les fichiers sont dans " + engine.downloadDir.absolutePath)
        }
    }

    private fun openFile(f: File) {
        val uri = FileProvider.getUriForFile(this, "$packageName.files", f)
        val ext = f.extension.lowercase()
        val mime = MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "*/*"
        try {
            startActivity(
                Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime)
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
            )
        } catch (_: Exception) {
            openDownloads()
        }
    }

    // ---------- Pont avec l'interface ----------

    inner class Bridge {
        @JavascriptInterface
        fun post(json: String) {
            val msg = try {
                JSONObject(json)
            } catch (e: Exception) {
                return
            }
            val id = msg.optInt("id")
            val cmd = msg.optString("cmd")
            val args = msg.optJSONObject("args") ?: JSONObject()
            val sid = args.optString("id").ifEmpty { null }

            when (cmd) {
                "pickTorrent" -> {
                    runOnUiThread { pickTorrent(id) }
                    return
                }
                "openDir" -> {
                    runOnUiThread { openDownloads() }
                    reply(id, null)
                    return
                }
            }

            engine.handler.post {
                try {
                    val result: Any? = when (cmd) {
                        "state" -> engine.state()
                        "acceptPending" -> {
                            ensurePermissions()
                            engine.acceptPending(args.optString("key"))
                            startService()
                            null
                        }
                        "rejectPending" -> engine.rejectPending(args.optString("key")).let { null }
                        "addMagnet" -> {
                            ensurePermissions()
                            engine.addLink(args.optString("uri"))
                            startService()
                            null
                        }
                        "addTorrentFile" -> {
                            ensurePermissions()
                            engine.addTorrentBytes(Base64.decode(args.optString("data"), Base64.DEFAULT))
                            startService()
                            null
                        }
                        "pause" -> engine.pause(sid).let { null }
                        "resume" -> engine.resume(sid).also { startService() }.let { null }
                        "remove" -> engine.remove(sid, args.optBoolean("deleteFiles")).let { null }
                        "open" -> {
                            val f = engine.singleFile(sid)
                            runOnUiThread { if (f != null) openFile(f) else openDownloads() }
                            null
                        }
                        else -> throw UserError("Commande inconnue : $cmd")
                    }
                    reply(id, result)
                } catch (e: UserError) {
                    replyError(id, e.message ?: "Erreur")
                } catch (e: Exception) {
                    replyError(id, "Erreur : ${e.message}")
                }
            }
        }
    }

    private fun send(obj: JSONObject) {
        val js = "window.__averseReceive(" + JSONObject.quote(obj.toString()) + ")"
        runOnUiThread { if (!isDestroyed) web.evaluateJavascript(js, null) }
    }

    private fun reply(id: Int, result: Any?) {
        if (id == 0) return
        send(JSONObject().put("id", id).put("result", result ?: JSONObject.NULL))
    }

    private fun replyError(id: Int, message: String) {
        if (id == 0) return
        send(JSONObject().put("id", id).put("error", message))
    }

    private fun toast(message: String, error: Boolean = false) {
        send(JSONObject().put("event", "toast").put("message", message).put("error", error))
        send(JSONObject().put("event", "changed"))
    }

    companion object {
        private const val PICK = 42
    }
}
