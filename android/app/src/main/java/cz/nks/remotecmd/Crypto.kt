package cz.nks.remotecmd

import android.util.Base64
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * AES-256-GCM wire-compatible with the .NET 9 relay and the .NET 4.8 client.
 * Key = SHA-256("RemoteCmd:v1:"+token). Wire format = nonce(12)+tag(16)+ciphertext(N).
 *
 * Java's GCM cipher emits/consumes ciphertext||tag, so encryption rearranges the
 * tag to the front and decryption reassembles ciphertext||tag before doFinal.
 */
object Crypto {
    private const val NONCE_LEN = 12
    private const val TAG_LEN = 16

    private lateinit var key: ByteArray
    private val rng = SecureRandom()

    fun init(token: String) {
        key = MessageDigest.getInstance("SHA-256")
            .digest("RemoteCmd:v1:$token".toByteArray(Charsets.UTF_8))
    }

    fun encrypt(data: ByteArray): ByteArray {
        val nonce = ByteArray(NONCE_LEN).also { rng.nextBytes(it) }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_LEN * 8, nonce))
        val ctAndTag = cipher.doFinal(data) // ciphertext || tag
        val ctLen = ctAndTag.size - TAG_LEN

        val out = ByteArray(NONCE_LEN + TAG_LEN + ctLen)
        System.arraycopy(nonce, 0, out, 0, NONCE_LEN)
        System.arraycopy(ctAndTag, ctLen, out, NONCE_LEN, TAG_LEN)          // tag
        System.arraycopy(ctAndTag, 0, out, NONCE_LEN + TAG_LEN, ctLen)      // ciphertext
        return out
    }

    fun decrypt(data: ByteArray): ByteArray {
        require(data.size >= NONCE_LEN + TAG_LEN) { "Invalid encrypted data" }
        val nonce = data.copyOfRange(0, NONCE_LEN)
        val tag = data.copyOfRange(NONCE_LEN, NONCE_LEN + TAG_LEN)
        val ct = data.copyOfRange(NONCE_LEN + TAG_LEN, data.size)

        val ctAndTag = ByteArray(ct.size + TAG_LEN)
        System.arraycopy(ct, 0, ctAndTag, 0, ct.size)
        System.arraycopy(tag, 0, ctAndTag, ct.size, TAG_LEN)

        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_LEN * 8, nonce))
        return cipher.doFinal(ctAndTag)
    }

    fun decryptString(base64: String): String =
        String(decrypt(Base64.decode(base64, Base64.DEFAULT)), Charsets.UTF_8)
}
