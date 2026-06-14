package cz.nks.remotecmd

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** Restarts the polling service after boot when autostart is enabled. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        val action = intent?.action ?: return
        if (action == Intent.ACTION_BOOT_COMPLETED || action == "android.intent.action.QUICKBOOT_POWERON") {
            val prefs = Prefs(context)
            if (prefs.autostart && prefs.isConfigured()) {
                PollService.start(context)
            }
        }
    }
}
