package cz.nks.remotecmd

import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.X509TrustManager

/**
 * Trusts the relay's self-signed certificate by the SHA-256 of its DER encoding. With no pin yet,
 * the first certificate seen is accepted and passed to [onFirstUse] for storing; after that any
 * other certificate is refused, so a man in the middle cannot read the token from the poll URL.
 */
class PinningTrustManager(
    @Volatile private var pin: String?,
    private val onFirstUse: (String) -> Unit,
) : X509TrustManager {
    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw CertificateException("client certificates are not accepted")
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull() ?: throw CertificateException("relay sent no certificate")
        val seen = sha256Hex(leaf.encoded)
        val expected = pin
        if (expected == null) {
            pin = seen
            onFirstUse(seen)
            return
        }
        if (!expected.equals(seen, ignoreCase = true)) {
            throw CertificateException(
                "relay certificate changed (pinned $expected, got $seen); " +
                    "clear the app's data to trust a new relay certificate"
            )
        }
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()

    companion object {
        fun sha256Hex(der: ByteArray): String =
            MessageDigest.getInstance("SHA-256").digest(der).joinToString("") { "%02x".format(it) }
    }
}
