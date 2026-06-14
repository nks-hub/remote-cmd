package cz.nks.remotecmd

import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.net.HttpURLConnection
import java.net.URL
import java.security.cert.X509Certificate
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

/**
 * Polling client mirroring the .NET RemoteCmd client: fetches commands and file
 * jobs from the relay, runs them as root, and posts results back. AES-256-GCM on
 * every payload; self-signed TLS is trusted explicitly.
 */
class PollClient(
    private val server: String,
    private val token: String,
    private val name: String,
    private val clientId: String,
    private val log: (String) -> Unit,
) {
    @Volatile var running = true

    private val baseUrl: String =
        if (server.startsWith("http")) server.trimEnd('/') else "https://$server"

    private val qs = "?token=$token&clientId=$clientId&name=${enc(name)}"
    private val pollUrl = "$baseUrl/api/poll$qs"
    private val resultUrl = "$baseUrl/api/result$qs"
    private val filePollUrl = "$baseUrl/api/file-poll$qs"
    private val fileDataUrl = "$baseUrl/api/file-data$qs"
    private val fileDoneUrl = "$baseUrl/api/file-done$qs"
    private val fileUploadUrl = "$baseUrl/api/file-upload$qs"

    fun loop() {
        Crypto.init(token)
        log("Started. Server=$baseUrl Name=$name Id=$clientId")
        var retryDelay = 1
        while (running) {
            try {
                val pollJson = httpGetString(pollUrl)
                val encCommand = optString(pollJson, "command")
                if (encCommand != null) {
                    val command = Crypto.decryptString(encCommand)
                    log("[CMD] $command")
                    val res = RootShell.exec(command)
                    val resultJson = JSONObject()
                        .put("output", res.output)
                        .put("exitCode", res.exitCode)
                        .toString()
                    httpPostBytes(resultUrl, Crypto.encrypt(resultJson.toByteArray(Charsets.UTF_8)))
                }

                val filePollJson = httpGetString(filePollUrl)
                val encMeta = optString(filePollJson, "e")
                if (encMeta != null) {
                    val meta = JSONObject(Crypto.decryptString(encMeta))
                    val action = meta.optString("action")
                    val path = meta.optString("path")
                    val size = meta.optLong("size", 0)
                    if (action == "upload" && path.isNotEmpty()) receiveFile(path, size)
                    else if (action == "download" && path.isNotEmpty()) sendFile(path)
                }

                retryDelay = 1
                Thread.sleep(800)
            } catch (e: InterruptedException) {
                break
            } catch (e: Exception) {
                if (!running) break
                log("[ERROR] ${e.message} - retry in ${retryDelay}s")
                try { Thread.sleep(retryDelay * 1000L) } catch (_: InterruptedException) { break }
                retryDelay = minOf(retryDelay * 2, 30)
            }
        }
        log("Stopped.")
    }

    private fun receiveFile(path: String, size: Long) {
        log("[FILE] Receiving ${size / 1024 / 1024}MB -> $path")
        try {
            val fileData = Crypto.decrypt(httpGetBytes(fileDataUrl))
            RootShell.writeFile(path, fileData)
            httpPostBytes(fileDoneUrl, ByteArray(0))
            log("[FILE] Saved ${fileData.size / 1024 / 1024}MB -> $path")
        } catch (e: Exception) {
            log("[FILE ERROR] ${e.message}")
            httpPostBytes(fileDoneUrl, ByteArray(0))
        }
    }

    private fun sendFile(path: String) {
        log("[FILE] Uploading <- $path")
        try {
            if (!RootShell.exists(path)) {
                httpPostBytes("$fileUploadUrl&error=${enc("File not found: $path")}", ByteArray(0))
                return
            }
            val fileData = RootShell.readFile(path)
            httpPostBytes(fileUploadUrl, Crypto.encrypt(fileData))
            log("[FILE] Uploaded ${fileData.size / 1024 / 1024}MB <- $path")
        } catch (e: Exception) {
            log("[FILE ERROR] ${e.message}")
            httpPostBytes("$fileUploadUrl&error=${enc(e.message ?: "error")}", ByteArray(0))
        }
    }

    // --- HTTP helpers ---

    private fun open(urlStr: String): HttpURLConnection {
        val conn = URL(urlStr).openConnection() as HttpURLConnection
        if (conn is HttpsURLConnection) {
            conn.sslSocketFactory = trustAllFactory
            conn.hostnameVerifier = HostnameVerifier { _, _ -> true }
        }
        conn.connectTimeout = 10_000
        conn.readTimeout = 300_000
        return conn
    }

    private fun httpGetString(urlStr: String): String {
        val conn = open(urlStr)
        try {
            conn.requestMethod = "GET"
            return conn.inputStream.bufferedReader().readText()
        } finally { conn.disconnect() }
    }

    private fun httpGetBytes(urlStr: String): ByteArray {
        val conn = open(urlStr)
        try {
            conn.requestMethod = "GET"
            val buf = ByteArrayOutputStream()
            conn.inputStream.copyTo(buf)
            return buf.toByteArray()
        } finally { conn.disconnect() }
    }

    private fun httpPostBytes(urlStr: String, body: ByteArray): String {
        val conn = open(urlStr)
        try {
            conn.requestMethod = "POST"
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", "application/octet-stream")
            conn.setFixedLengthStreamingMode(body.size)
            conn.outputStream.use { it.write(body) }
            val code = conn.responseCode
            val stream = if (code in 200..299) conn.inputStream else conn.errorStream
            return stream?.bufferedReader()?.readText() ?: ""
        } finally { conn.disconnect() }
    }

    private fun optString(json: String, key: String): String? {
        val o = JSONObject(json)
        if (!o.has(key) || o.isNull(key)) return null
        val v = o.optString(key, "")
        return if (v.isEmpty()) null else v
    }

    companion object {
        private fun enc(s: String): String = java.net.URLEncoder.encode(s, "UTF-8")

        private val trustAllFactory by lazy {
            val tm = object : X509TrustManager {
                override fun checkClientTrusted(c: Array<out X509Certificate>?, a: String?) {}
                override fun checkServerTrusted(c: Array<out X509Certificate>?, a: String?) {}
                override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
            }
            SSLContext.getInstance("TLS").apply { init(null, arrayOf(tm), java.security.SecureRandom()) }
                .socketFactory
        }
    }
}
