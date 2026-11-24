package cz.nks.remotecmd

import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class PinningTrustManagerTest {
    private val relayA = cert(
        """
        MIIBETCBuaADAgECAgEBMAoGCCqGSM49BAMCMBIxEDAOBgNVBAMMB3JlbGF5LWEw
        IBcNMjYwMTAxMDAwMDAwWhgPMjEyNTEyMDgwMDAwMDBaMBIxEDAOBgNVBAMMB3Jl
        bGF5LWEwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAASbrJ0COEE9B8pTqi4dZK4y
        gXxaD6wl8HwOQCHG1ZaA18khH3DtCGeeb7HanDTGgABMXcS/ujn9eBWGU1LjeTDu
        MAoGCCqGSM49BAMCA0cAMEQCIEi+TeYgyNcCPF/xpd7BEBnGEQ60SgAEc1hlmLMa
        jyKOAiA6nfLdVCrgG36aBBdCGzkkNQVewWXhoXTD0IkXZD2KnA==
        """
    )
    private val relayB = cert(
        """
        MIIBEjCBuaADAgECAgEBMAoGCCqGSM49BAMCMBIxEDAOBgNVBAMMB3JlbGF5LWIw
        IBcNMjYwMTAxMDAwMDAwWhgPMjEyNTEyMDgwMDAwMDBaMBIxEDAOBgNVBAMMB3Jl
        bGF5LWIwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAAS7RQhWx3fXNvdpGigad1iM
        crFWi907IzLXkxnIQNKabK/GaiL6/AQrfpbCM0o98Fdhf86u/JVftj8wZggWDwj2
        MAoGCCqGSM49BAMCA0gAMEUCIEzJESuqQjZ4I7nDeX/Deax26kQ1w41waWJZqgnR
        QkqvAiEA1GOHlCGe4XPYo3aMWBLXDdgKKZDbKrSQj8u4f5lTuqM=
        """
    )
    private val pinA = "a71596ab16b20004a2ea6d202cf7c9cdc0533fa8f0bc7ba7ab45c34f2eef8487"

    @Test
    fun firstUsePinsTheCertificateAndThenRefusesAnother() {
        val stored = mutableListOf<String>()
        val tm = PinningTrustManager(null) { stored += it }
        tm.checkServerTrusted(arrayOf(relayA), "ECDHE_ECDSA")
        assertEquals(listOf(pinA), stored)
        tm.checkServerTrusted(arrayOf(relayA), "ECDHE_ECDSA")
        assertThrows(CertificateException::class.java) { tm.checkServerTrusted(arrayOf(relayB), "ECDHE_ECDSA") }
    }

    @Test
    fun storedPinRefusesADifferentCertificate() {
        val tm = PinningTrustManager(pinA.uppercase()) { error("already pinned") }
        tm.checkServerTrusted(arrayOf(relayA), "ECDHE_ECDSA")
        assertThrows(CertificateException::class.java) { tm.checkServerTrusted(arrayOf(relayB), "ECDHE_ECDSA") }
        assertThrows(CertificateException::class.java) { tm.checkServerTrusted(arrayOf(), "ECDHE_ECDSA") }
    }

    private fun cert(base64: String): X509Certificate {
        val pem = "-----BEGIN CERTIFICATE-----\n${base64.trimIndent()}\n-----END CERTIFICATE-----\n"
        return CertificateFactory.getInstance("X.509").generateCertificate(pem.byteInputStream()) as X509Certificate
    }
}
