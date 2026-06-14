package cz.nks.remotecmd

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat

/**
 * Foreground service that owns the polling loop so it survives the Activity being
 * closed. Restarts via START_STICKY and BootReceiver after reboots.
 */
class PollService : Service() {
    private var client: PollClient? = null
    private var worker: Thread? = null

    @Volatile private var lastLog: String = ""

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForegroundCompat("Connecting…")

        if (worker?.isAlive == true) return START_STICKY

        val prefs = Prefs(this)
        if (!prefs.isConfigured()) {
            stopSelf()
            return START_NOT_STICKY
        }
        prefs.enabled = true

        val name = prefs.name
        val pc = PollClient(
            server = prefs.server,
            token = prefs.token,
            name = name,
            clientId = prefs.clientId(name),
            log = { line -> onLog(line) },
        )
        client = pc
        worker = Thread { pc.loop() }.also { it.isDaemon = true; it.start() }
        return START_STICKY
    }

    private fun onLog(line: String) {
        lastLog = line
        updateNotification(line)
        val i = Intent(ACTION_LOG).putExtra(EXTRA_LINE, line).setPackage(packageName)
        sendBroadcast(i)
    }

    override fun onDestroy() {
        client?.running = false
        worker?.interrupt()
        Prefs(this).enabled = false
        super.onDestroy()
    }

    private fun startForegroundCompat(text: String) {
        ensureChannel()
        val notif = buildNotification(text)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIF_ID, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(NOTIF_ID, notif)
        }
    }

    private fun updateNotification(text: String) {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.notify(NOTIF_ID, buildNotification(text))
    }

    private fun buildNotification(text: String): Notification =
        NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text.take(120))
            .setStyle(NotificationCompat.BigTextStyle().bigText(text.take(400)))
            .setSmallIcon(R.drawable.ic_stat)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .build()

    private fun ensureChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            if (nm.getNotificationChannel(CHANNEL_ID) == null) {
                nm.createNotificationChannel(
                    NotificationChannel(CHANNEL_ID, getString(R.string.notif_channel), NotificationManager.IMPORTANCE_LOW)
                )
            }
        }
    }

    companion object {
        private const val CHANNEL_ID = "remotecmd_service"
        private const val NOTIF_ID = 1001
        const val ACTION_LOG = "cz.nks.remotecmd.LOG"
        const val EXTRA_LINE = "line"

        fun start(context: Context) {
            val i = Intent(context, PollService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) context.startForegroundService(i)
            else context.startService(i)
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, PollService::class.java))
        }
    }
}
