package dev.resourcemanager.protocol

import java.io.File
import kotlin.test.*
import kotlinx.serialization.json.*

class DesktopInteropTest {
    @Test
    fun actualDesktopSigningVectors() {
        val doc =
            wire
                .parseToJsonElement(javaClass.getResource("/desktop-vectors.json")!!.readText())
                .jsonObject
        val key = doc.getValue("publicKey").jsonPrimitive.content
        val hello = wire.decodeFromJsonElement<Hello>(doc.getValue("hello"))
        val expected =
            mapOf(
                "hello" to Proof.hello(hello, "ABC123", "android-fixture"),
                "request" to
                    Proof.request(
                        "android-fixture",
                        "windows-fixture",
                        "GET",
                        "/api/v1/resources/a/content?path=%E4%B8%AD\nbytes=3-\n\"etag\"",
                        "1700000000",
                        "A".repeat(48),
                        "中文 body".toByteArray(),
                    ),
                "escaping" to
                    DotNetJson.array(
                        (0..127).map(Int::toChar).joinToString("") + "中文😀\u2028\u2029"
                    ),
            )
        doc.getValue("vectors").jsonArray.forEach { value ->
            val v = value.jsonObject
            val name = v.getValue("name").jsonPrimitive.content
            val bytes = expected.getValue(name)
            assertEquals(v.getValue("payload").jsonPrimitive.content, bytes.decodeToString(), name)
            assertTrue(
                Proof.verify(key, v.getValue("signature").jsonPrimitive.content, bytes),
                name,
            )
        }
        doc.getValue("avatarVectors").jsonArray.forEach { entry ->
            val v = entry.jsonObject
            val size = v.getValue("size").jsonPrimitive.int
            val avatar =
                if (size < 0) null
                else
                    java.util.Base64.getEncoder()
                        .encodeToString(ByteArray(size) { (it % 256).toByte() })
            val changed = hello.copy(avatar = avatar)
            val bytes = Proof.hello(changed, "ABC123", "android-fixture")
            assertEquals(hello.deviceId, changed.deviceId)
            assertEquals(
                v.getValue("digest").jsonPrimitive.content,
                Proof.hash(bytes),
                "avatar $size",
            )
            assertTrue(
                Proof.verify(key, v.getValue("signature").jsonPrimitive.content, bytes),
                "avatar $size",
            )
            assertFalse(
                Proof.verify(
                    key,
                    v.getValue("signature").jsonPrimitive.content,
                    Proof.hello(changed.copy(nickname = "tampered"), "ABC123", "android-fixture"),
                )
            )
        }
        val identity = JvmIdentity("android-fixture")
        val payload = expected.getValue("hello")
        File("build/android-signature.json")
            .writeText(
                buildJsonObject {
                    put("publicKey", identity.publicKey)
                    put("payload", payload.decodeToString())
                    put("signature", identity.sign(payload))
                }
                    .toString()
            )
    }
}
