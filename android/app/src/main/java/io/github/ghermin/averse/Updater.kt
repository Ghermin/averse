package io.github.ghermin.averse

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.widget.Toast
import androidx.core.app.NotificationCompat
import androidx.core.content.IntentCompat
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/**
 * Mise à jour depuis la dernière version GitHub (comme CamTel). Vérifie à l'ouverture de l'appli,
 * au plus toutes les 6 h. Installée tout de suite si rien ne se télécharge (ou juste après le lancement) ;
 * sinon à la prochaine ouverture. Dès qu'Averse s'est mis à jour une fois lui-même, Android ne demande
 * plus de confirmation ; la toute première fois, le système affiche une seule demande.
 */
object Updater {
    private const val TAG = "AverseUpdater"
    private const val LATEST_API = "https://api.github.com/repos/Ghermin/averse/releases/latest"
    private const val CHECK_INTERVAL_MS = 6 * 60 * 60 * 1000L
    private const val CHANNEL = "updates"

    @Volatile private var running = false
    // En mémoire exprès : chaque nouveau lancement de l'appli revérifie.
    @Volatile private var lastCheck = 0L
    private val processStart = System.currentTimeMillis()

    private fun justLaunched() = System.currentTimeMillis() - processStart < 120_000

    fun maybeUpdate(context: Context, engine: TorrentEngine) {
        if (BuildConfig.DEBUG || running) return
        val now = System.currentTimeMillis()
        if (now - lastCheck < CHECK_INTERVAL_MS) return
        lastCheck = now
        running = true
        val app = context.applicationContext
        val prefs = app.getSharedPreferences("averse", Context.MODE_PRIVATE)
        Thread({
            try {
                val release = latest() ?: return@Thread
                if (!isNewer(release.version, BuildConfig.VERSION_NAME)) return@Thread
                if (engine.activeCount() > 0 && !justLaunched()) {
                    lastCheck = 0L // réessayer à la prochaine ouverture
                    return@Thread
                }
                toast(app, "Mise à jour d'Averse vers ${release.version}…")
                val apk = download(app, release) ?: return@Thread
                // Les torrents reprendront où ils en sont.
                engine.handler.post { engine.saveAll() }
                Thread.sleep(1500)
                prefs.edit().putString("updatingTo", release.version).apply()
                install(app, apk)
            } catch (e: Exception) {
                Log.w(TAG, "mise à jour impossible", e)
            } finally {
                running = false
            }
        }, "averse-update").start()
    }

    private data class Release(val version: String, val apkUrl: String, val size: Long)

    private fun latest(): Release? {
        val conn = URL(LATEST_API).openConnection() as HttpURLConnection
        conn.setRequestProperty("Accept", "application/vnd.github+json")
        conn.setRequestProperty("User-Agent", "Averse-Android/${BuildConfig.VERSION_NAME}")
        conn.connectTimeout = 8000
        conn.readTimeout = 8000
        try {
            if (conn.responseCode != 200) return null
            val json = JSONObject(conn.inputStream.bufferedReader().readText())
            val version = json.getString("tag_name").trimStart('v')
            val assets = json.getJSONArray("assets")
            for (i in 0 until assets.length()) {
                val a = assets.getJSONObject(i)
                if (a.getString("name") == "Averse.apk")
                    return Release(version, a.getString("browser_download_url"), a.getLong("size"))
            }
            return null
        } finally {
            conn.disconnect()
        }
    }

    fun isNewer(candidate: String, current: String): Boolean {
        val a = candidate.split('.').map { it.toIntOrNull() ?: 0 }
        val b = current.split('.').map { it.toIntOrNull() ?: 0 }
        for (i in 0 until maxOf(a.size, b.size)) {
            val x = a.getOrElse(i) { 0 }
            val y = b.getOrElse(i) { 0 }
            if (x != y) return x > y
        }
        return false
    }

    private fun download(context: Context, release: Release): File? {
        val file = File(context.cacheDir, "update.apk")
        val conn = URL(release.apkUrl).openConnection() as HttpURLConnection
        conn.instanceFollowRedirects = true
        conn.connectTimeout = 10000
        conn.readTimeout = 30000
        try {
            conn.inputStream.use { input -> file.outputStream().use { input.copyTo(it) } }
        } finally {
            conn.disconnect()
        }
        return if (file.length() == release.size) file else null
    }

    private fun install(context: Context, apk: File) {
        val installer = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(context.packageName)
            if (Build.VERSION.SDK_INT >= 31) setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        }
        val id = installer.createSession(params)
        installer.openSession(id).use { session ->
            session.openWrite("Averse.apk", 0, apk.length()).use { out ->
                apk.inputStream().use { it.copyTo(out) }
                session.fsync(out)
            }
            val flags = PendingIntent.FLAG_UPDATE_CURRENT or
                (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
            val pending = PendingIntent.getBroadcast(
                context, id, Intent(context, InstallResultReceiver::class.java), flags
            )
            session.commit(pending.intentSender)
        }
    }

    private fun toast(context: Context, text: String) =
        Handler(Looper.getMainLooper()).post { Toast.makeText(context, text, Toast.LENGTH_LONG).show() }

    /** Affiche la confirmation du système quand Android en exige une (première mise à jour). */
    class InstallResultReceiver : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            when (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
                PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                    val confirm = IntentCompat.getParcelableExtra(intent, Intent.EXTRA_INTENT, Intent::class.java)
                    confirm?.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    confirm?.let { context.startActivity(it) }
                }
                PackageInstaller.STATUS_SUCCESS -> Unit
                else -> {
                    Log.w(TAG, "installation refusée : ${intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)}")
                    context.getSharedPreferences("averse", Context.MODE_PRIVATE).edit().remove("updatingTo").apply()
                }
            }
        }
    }

    /** Après une mise à jour, l'appli est fermée : on propose de la rouvrir. */
    class UpdatedReceiver : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
            val prefs = context.getSharedPreferences("averse", Context.MODE_PRIVATE)
            prefs.getString("updatingTo", null) ?: return
            prefs.edit().remove("updatingTo").apply()
            val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            if (Build.VERSION.SDK_INT >= 26)
                nm.createNotificationChannel(NotificationChannel(CHANNEL, "Mises à jour", NotificationManager.IMPORTANCE_DEFAULT))
            val open = PendingIntent.getActivity(
                context, 0, Intent(context, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE
            )
            nm.notify(
                2, NotificationCompat.Builder(context, CHANNEL)
                    .setSmallIcon(R.drawable.ic_notif)
                    .setContentTitle("Averse mis à jour (${BuildConfig.VERSION_NAME})")
                    .setContentText("Touche pour rouvrir Averse : tes torrents reprennent.")
                    .setContentIntent(open)
                    .setAutoCancel(true)
                    .build()
            )
        }
    }
}
