package dev.resourcemanager.protocol

import kotlin.test.*

class ProofTest {
    @Test fun signatureRoundTripAndTampering() {
        val identity = JvmIdentity("android")
        val bytes = "中文😀 / + < &".toByteArray()
        repeat(50) { val signature = identity.sign(bytes); assertTrue(Proof.verify(identity.publicKey,signature,bytes)); assertFalse(Proof.verify(identity.publicKey,signature,bytes+1)) }
    }
    @Test fun authPinsIdentityAndRejectsReplayAndRanges() {
        val a = JvmIdentity("a"); val b = JvmIdentity("b")
        val auth = AuthVerifier(b) { if(it == "a") a.publicKey else null }
        val headers = Proof.headers(a,"b","GET","/api/v1/resources\nbytes=3-\nabc",byteArrayOf(),1000)
        assertNull(auth.verify({headers[it] ?: ""},"GET","/api/v1/resources\n\n",byteArrayOf(),now=1000))
        assertEquals("a",auth.verify({headers[it] ?: ""},"GET","/api/v1/resources\nbytes=3-\nabc",byteArrayOf(),now=1000))
        assertNull(auth.verify({headers[it] ?: ""},"GET","/api/v1/resources\nbytes=3-\nabc",byteArrayOf(),now=1000))
        val changed = Proof.headers(JvmIdentity("a"),"b","GET","/x\n\n",byteArrayOf(),1000)
        assertNull(auth.verify({changed[it] ?: ""},"GET","/x\n\n",byteArrayOf(),true,1000))
    }
    @Test fun unsafePathsRejected() {
        listOf("../x","/x","a/../x","a\\x",".git/config","a//b","a:b").forEach { assertFails { SafePath.segments(it) } }
        assertEquals(listOf("目录","hello.txt"), SafePath.segments("目录/hello.txt"))
    }
}
