package dev.resourcemanager.protocol

import kotlinx.serialization.json.*
import kotlin.test.*
import java.io.File

class DesktopInteropTest {
    @Test fun actualDesktopSigningVectors() {
        val doc = wire.parseToJsonElement(javaClass.getResource("/desktop-vectors.json")!!.readText()).jsonObject
        val key = doc.getValue("publicKey").jsonPrimitive.content
        val hello = wire.decodeFromJsonElement<Hello>(doc.getValue("hello"))
        val expected = mapOf(
            "hello" to Proof.hello(hello,"ABC123","android-fixture"),
            "request" to Proof.request("android-fixture","windows-fixture","GET","/api/v1/resources/a/content?path=%E4%B8%AD\nbytes=3-\n\"etag\"","1700000000","A".repeat(48),"中文 body".toByteArray()),
            "escaping" to DotNetJson.array((0..127).map(Int::toChar).joinToString("")+"中文😀\u2028\u2029")
        )
        doc.getValue("vectors").jsonArray.forEach { value ->
            val v = value.jsonObject; val name = v.getValue("name").jsonPrimitive.content
            val bytes = expected.getValue(name)
            assertEquals(v.getValue("payload").jsonPrimitive.content,bytes.decodeToString(),name)
            assertTrue(Proof.verify(key,v.getValue("signature").jsonPrimitive.content,bytes),name)
        }
        val identity = JvmIdentity("android-fixture"); val payload = expected.getValue("hello")
        File("build/android-signature.json").writeText(buildJsonObject {
            put("publicKey",identity.publicKey);put("payload",payload.decodeToString());put("signature",identity.sign(payload))
        }.toString())
    }
}
