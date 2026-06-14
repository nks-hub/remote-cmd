package cz.nks.remotecmd

import java.io.ByteArrayOutputStream
import java.util.concurrent.TimeUnit

/**
 * Executes commands and file I/O as root, mirroring the Windows client's
 * PowerShell execution. The su invocation differs between Magisk (`su -c CMD`)
 * and AOSP (`su 0 sh -c CMD`), so the working form is detected once at runtime;
 * if no root is available it falls back to a plain (app-uid) shell.
 */
object RootShell {
    data class Result(val output: String, val exitCode: Int)

    // Argv prefix that takes the command as the final single argument.
    private val prefix: List<String> by lazy { detectPrefix() }

    private fun argv(command: String): List<String> = prefix + command

    private fun detectPrefix(): List<String> {
        val candidates = listOf(
            listOf("su", "-c"),            // Magisk / most rooted devices
            listOf("su", "0", "sh", "-c"), // AOSP su (emulator, userdebug)
            listOf("su", "root", "sh", "-c"),
        )
        for (c in candidates) {
            try {
                val p = ProcessBuilder(c + "id").redirectErrorStream(true).start()
                val out = p.inputStream.bufferedReader().readText()
                p.waitFor(5, TimeUnit.SECONDS)
                if (out.contains("uid=0")) return c
            } catch (_: Exception) { /* try next */ }
        }
        return listOf("sh", "-c") // no root: degraded, app-uid shell
    }

    /** Run a shell command as root, capturing stdout+stderr, with a kill timeout. */
    fun exec(command: String, timeoutMs: Long = 60_000): Result {
        return try {
            val process = ProcessBuilder(argv(command)).redirectErrorStream(false).start()
            process.outputStream.close()

            val out = StringBuilder()
            val err = StringBuilder()
            val tOut = Thread { process.inputStream.bufferedReader().forEachLine { out.appendLine(it) } }
            val tErr = Thread { process.errorStream.bufferedReader().forEachLine { err.appendLine(it) } }
            tOut.start(); tErr.start()

            val finished = process.waitFor(timeoutMs, TimeUnit.MILLISECONDS)
            if (!finished) {
                process.destroyForcibly()
                return Result("[KILLED] Command exceeded ${timeoutMs / 1000}s timeout", -1)
            }
            tOut.join(2000); tErr.join(2000)

            var combined = out.toString().trimEnd('\n')
            if (err.isNotBlank()) combined += "\n[STDERR]\n" + err.toString().trimEnd('\n')
            Result(combined.trimEnd(), process.exitValue())
        } catch (e: Exception) {
            Result("[EXEC ERROR] ${e.message}", -1)
        }
    }

    /** Read a file as root (full bytes). */
    fun readFile(path: String): ByteArray {
        val process = ProcessBuilder(argv("cat ${shellQuote(path)}")).redirectErrorStream(false).start()
        process.outputStream.close()
        val buffer = ByteArrayOutputStream()
        val reader = Thread { process.inputStream.copyTo(buffer) }
        val errBuf = StringBuilder()
        val errReader = Thread { process.errorStream.bufferedReader().forEachLine { errBuf.appendLine(it) } }
        reader.start(); errReader.start()
        val finished = process.waitFor(5, TimeUnit.MINUTES)
        if (!finished) { process.destroyForcibly(); throw RuntimeException("read timeout") }
        reader.join(5000); errReader.join(2000)
        if (process.exitValue() != 0) {
            val err = errBuf.toString().trim()
            throw RuntimeException(if (err.isNotEmpty()) err else "cat failed (${process.exitValue()})")
        }
        return buffer.toByteArray()
    }

    /** Write a file as root by streaming bytes into `cat > path`. */
    fun writeFile(path: String, data: ByteArray) {
        val dir = path.substringBeforeLast('/', "")
        if (dir.isNotEmpty()) exec("mkdir -p ${shellQuote(dir)}")

        val process = ProcessBuilder(argv("cat > ${shellQuote(path)}")).redirectErrorStream(false).start()
        process.outputStream.use { it.write(data); it.flush() }
        val finished = process.waitFor(5, TimeUnit.MINUTES)
        if (!finished) { process.destroyForcibly(); throw RuntimeException("write timeout") }
        if (process.exitValue() != 0) {
            val err = process.errorStream.bufferedReader().readText().trim()
            throw RuntimeException(if (err.isNotEmpty()) err else "write failed (${process.exitValue()})")
        }
    }

    fun exists(path: String): Boolean =
        exec("test -e ${shellQuote(path)} && echo Y || echo N").output.trim() == "Y"

    private fun shellQuote(s: String): String = "'" + s.replace("'", "'\\''") + "'"
}
