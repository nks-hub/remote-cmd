package cz.nks.remotecmd

import android.Manifest
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat

class MainActivity : AppCompatActivity() {
    private lateinit var prefs: Prefs
    private lateinit var serverInput: EditText
    private lateinit var tokenInput: EditText
    private lateinit var nameInput: EditText
    private lateinit var autostart: CheckBox
    private lateinit var statusText: TextView
    private lateinit var logText: TextView

    private val logLines = ArrayDeque<String>()

    private val logReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            val line = intent?.getStringExtra(PollService.EXTRA_LINE) ?: return
            appendLog(line)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        prefs = Prefs(this)

        serverInput = findViewById(R.id.serverInput)
        tokenInput = findViewById(R.id.tokenInput)
        nameInput = findViewById(R.id.nameInput)
        autostart = findViewById(R.id.autostartCheck)
        statusText = findViewById(R.id.statusText)
        logText = findViewById(R.id.logText)

        serverInput.setText(prefs.server)
        tokenInput.setText(prefs.token)
        nameInput.setText(prefs.name)
        autostart.isChecked = prefs.autostart

        findViewById<Button>(R.id.startButton).setOnClickListener { onStartClicked() }
        findViewById<Button>(R.id.stopButton).setOnClickListener { onStopClicked() }

        requestNotificationPermission()
        refreshStatus()
    }

    override fun onResume() {
        super.onResume()
        ContextCompat.registerReceiver(
            this, logReceiver, IntentFilter(PollService.ACTION_LOG),
            ContextCompat.RECEIVER_NOT_EXPORTED
        )
        refreshStatus()
    }

    override fun onPause() {
        super.onPause()
        try { unregisterReceiver(logReceiver) } catch (_: Exception) {}
    }

    private fun onStartClicked() {
        prefs.server = serverInput.text.toString().trim()
        prefs.token = tokenInput.text.toString().trim()
        prefs.name = nameInput.text.toString().trim().ifEmpty { Build.MODEL ?: "android" }
        prefs.autostart = autostart.isChecked

        if (!prefs.isConfigured()) {
            statusText.text = "Set server and token first."
            return
        }
        PollService.start(this)
        refreshStatus()
    }

    private fun onStopClicked() {
        prefs.autostart = autostart.isChecked
        PollService.stop(this)
        statusText.text = getString(R.string.status_stopped)
    }

    private fun refreshStatus() {
        statusText.text = if (prefs.enabled) getString(R.string.status_running)
        else getString(R.string.status_stopped)
    }

    private fun appendLog(line: String) {
        logLines.addLast(line)
        while (logLines.size > 50) logLines.removeFirst()
        logText.text = logLines.joinToString("\n")
        refreshStatus()
    }

    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }
    }
}
