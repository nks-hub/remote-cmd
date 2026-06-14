package cz.nks.remotecmd

import android.content.Context
import java.util.UUID

/** Persisted connection settings + stable per-name client id. */
class Prefs(context: Context) {
    private val sp = context.getSharedPreferences("remotecmd", Context.MODE_PRIVATE)

    var server: String
        get() = sp.getString("server", "") ?: ""
        set(v) = sp.edit().putString("server", v).apply()

    var token: String
        get() = sp.getString("token", "") ?: ""
        set(v) = sp.edit().putString("token", v).apply()

    var name: String
        get() = sp.getString("name", android.os.Build.MODEL ?: "android") ?: "android"
        set(v) = sp.edit().putString("name", v).apply()

    var autostart: Boolean
        get() = sp.getBoolean("autostart", false)
        set(v) = sp.edit().putBoolean("autostart", v).apply()

    var enabled: Boolean
        get() = sp.getBoolean("enabled", false)
        set(v) = sp.edit().putBoolean("enabled", v).apply()

    /** Stable client id scoped per name, so multiple devices/aliases don't collide. */
    fun clientId(forName: String): String {
        val key = "clientId_" + forName.replace(Regex("[^A-Za-z0-9_-]"), "_")
        var id = sp.getString(key, null)
        if (id == null || id.length < 8) {
            id = UUID.randomUUID().toString().replace("-", "")
            sp.edit().putString(key, id).apply()
        }
        return id
    }

    fun isConfigured(): Boolean = server.isNotBlank() && token.isNotBlank()
}
