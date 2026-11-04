package io.github.ghermin.averse

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.util.Log

/**
 * Garde Averse en vie tant qu'un torrent se télécharge, même appli fermée ou écran éteint.
 * S'arrête tout seul quand plus rien ne travaille.
 */
class TorrentService : Service() {
    private lateinit var engine: TorrentEngine
    private var wakeLock: PowerManager.WakeLock? = null
    private var running = false

    private val tick = object : Runnable {
        override fun run() {
            if (!running) return
            val active = engine.activeCount()
            if (active == 0) {
                stopSelf()
                return
            }
            notificationManager().notify(NOTIF_ID, build(active))
            engine.handler.postDelayed(this, 2000)
        }
    }

    override fun onCreate() {
        super.onCreate()
        engine = (application as AverseApp).engine
        createChannel(this)
        wakeLock = (getSystemService(POWER_SERVICE) as PowerManager)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Averse:download")
            .apply { setReferenceCounted(false) }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val notif = build(0)
        try {
            if (Build.VERSION.SDK_INT >= 34) {
                startForeground(NOTIF_ID, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
            } else {
                startForeground(NOTIF_ID, notif)
            }
        } catch (e: Exception) {
            Log.w(TorrentEngine.TAG, "Service de premier plan refusé", e)
            stopSelf()
            return START_NOT_STICKY
        }
        wakeLock?.acquire(6 * 60 * 60 * 1000L)
        if (!running) {
            running = true
            // Laisser le temps aux torrents ajoutés de démarrer avant le premier contrôle.
            engine.handler.postDelayed(tick, 5000)
        }
        return START_NOT_STICKY
    }

    private fun build(active: Int): Notification {
        val (down, up) = if (active > 0) engine.totals() else (0L to 0L)
        val title = when (active) {
            0 -> "Averse"
            1 -> "1 téléchargement en cours"
            else -> "$active téléchargements en cours"
        }
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val builder = if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, CHANNEL) else Notification.Builder(this)
        return builder
            .setSmallIcon(R.drawable.ic_notif)
            .setContentTitle(title)
            .setContentText("↓ ${rate(down)}   ↑ ${rate(up)}")
            .setContentIntent(open)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .build()
    }

    private fun notificationManager() = getSystemService(NOTIFICATION_SERVICE) as NotificationManager

    override fun onDestroy() {
        running = false
        engine.handler.removeCallbacks(tick)
        wakeLock?.let { if (it.isHeld) it.release() }
        engine.handler.post { engine.saveAll() }
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        private const val CHANNEL = "downloads"
        private const val NOTIF_ID = 1

        fun createChannel(context: Context) {
            if (Build.VERSION.SDK_INT < 26) return
            val nm = context.getSystemService(NOTIFICATION_SERVICE) as NotificationManager
            val ch = NotificationChannel(CHANNEL, "Téléchargements", NotificationManager.IMPORTANCE_LOW)
            ch.setShowBadge(false)
            nm.createNotificationChannel(ch)
        }

        fun start(context: Context) {
            try {
                context.startForegroundService(Intent(context, TorrentService::class.java))
            } catch (e: Exception) {
                Log.w(TorrentEngine.TAG, "Impossible de démarrer le service", e)
            }
        }

        fun rate(bytes: Long): String {
            var n = bytes.toDouble()
            val units = arrayOf("o/s", "Ko/s", "Mo/s", "Go/s")
            var i = 0
            while (n >= 1000 && i < units.size - 1) {
                n /= 1024; i++
            }
            return if (i == 0 || n >= 100) "%.0f %s".format(n, units[i]) else "%.1f %s".format(n, units[i]).replace('.', ',')
        }
    }
}
