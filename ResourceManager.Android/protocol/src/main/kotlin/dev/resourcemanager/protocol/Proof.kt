package dev.resourcemanager.protocol

import kotlinx.serialization.json.*
import java.security.*
import java.security.spec.X509EncodedKeySpec
import java.util.Base64

/** System.Text.Json's default encoder is part of the existing signed wire protocol. */
object DotNetJson {
    fun string(value: String): String = buildString {
        append('"')
        value.forEach { c -> when (c) {
            '\\' -> append("\\\\")
            '\n' -> append("\\n")
            '\r' -> append("\\r")
            '\t' -> append("\\t")
            '\b' -> append("\\b")
            '\u000C' -> append("\\f")
            else -> if (c.code < 32 || c.code > 126 || c in "\"<>&'`+") append("\\u%04X".format(c.code)) else append(c)
        } }
        append('"')
    }
    fun element(value: JsonElement): String = when(value) {
        is JsonObject -> value.entries.joinToString(",", "{", "}") { string(it.key) + ":" + element(it.value) }
        is JsonArray -> value.joinToString(",", "[", "]") { element(it) }
        is JsonPrimitive -> if(value.isString) string(value.content) else value.toString()
    }
    fun array(vararg fields: String) = fields.joinToString(",", "[", "]", transform = ::string).toByteArray(Charsets.UTF_8)
}

interface Identity { val deviceId: String; val publicKey: String; fun sign(bytes: ByteArray): String }
class JvmIdentity(override val deviceId: String, val key: KeyPair = KeyPairGenerator.getInstance("EC").apply { initialize(java.security.spec.ECGenParameterSpec("secp256r1")) }.generateKeyPair()) : Identity {
    override val publicKey = Base64.getEncoder().encodeToString(key.public.encoded)
    override fun sign(bytes: ByteArray): String = Base64.getEncoder().encodeToString(Proof.derToRaw(Signature.getInstance("SHA256withECDSA").run { initSign(key.private); update(bytes); sign() }))
}
object Proof {
    fun hash(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02X".format(it) }
    fun request(sender: String, target: String, method: String, route: String, time: String, nonce: String, body: ByteArray) = DotNetJson.array("signed-device-v1", sender, target, method, route, time, nonce, hash(body))
    fun hello(hello: Hello, nonce: String, recipient: String) = DotNetJson.array("signed-device-hello-v1", recipient, nonce, DotNetJson.element(wire.encodeToJsonElement(hello)))
    fun verify(key: String, signature: String, bytes: ByteArray): Boolean = runCatching {
        require(key.length <= 256 && signature.length <= 256)
        val pub = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(Base64.getDecoder().decode(key))) as java.security.interfaces.ECPublicKey
        require(pub.params.curve.field.fieldSize == 256)
        Signature.getInstance("SHA256withECDSA").run { initVerify(pub); update(bytes); verify(rawToDer(Base64.getDecoder().decode(signature))) }
    }.getOrDefault(false)
    fun derToRaw(der: ByteArray): ByteArray {
        require(der.size in 8..72 && der[0] == 0x30.toByte() && der[1].toInt() == der.size - 2)
        var p = 2
        fun integer(): ByteArray {
            require(der[p++] == 2.toByte()); val n = der[p++].toInt() and 255
            require(n in 1..33 && p + n <= der.size)
            val b = der.copyOfRange(p, p+n); p += n
            val v = if (b.size == 33) { require(b[0] == 0.toByte()); b.drop(1).toByteArray() } else b
            return ByteArray(32-v.size) + v
        }
        val out = integer() + integer(); require(p == der.size); return out
    }
    fun rawToDer(raw: ByteArray): ByteArray {
        require(raw.size == 64)
        fun integer(offset: Int): ByteArray {
            var v = raw.copyOfRange(offset, offset+32).dropWhile { it == 0.toByte() }.toByteArray()
            if (v.isEmpty()) v = byteArrayOf(0)
            if(v[0].toInt() and 128 != 0) v = byteArrayOf(0)+v
            return byteArrayOf(2, v.size.toByte()) + v
        }
        val body = integer(0)+integer(32); return byteArrayOf(0x30, body.size.toByte())+body
    }
    fun headers(identity: Identity, target: String, method: String, route: String, body: ByteArray, time: Long = System.currentTimeMillis()/1000): Map<String, String> {
        val nonce = ByteArray(24).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02X".format(it) }
        return mapOf("X-RM-Device" to identity.deviceId, "X-RM-Time" to time.toString(), "X-RM-Nonce" to nonce, "X-RM-Key" to identity.publicKey,
            "X-RM-Signature" to identity.sign(request(identity.deviceId,target,method,route,time.toString(),nonce,body)))
    }
}

/** Bounded, synchronized replay protection; a full cache rejects, it never evicts fresh proofs. */
class AuthVerifier(private val identity: Identity, private val trusted: (String) -> String?) {
    private val nonces = LinkedHashMap<String, Long>()
    @Synchronized fun verify(headers: (String) -> String, method: String, route: String, body: ByteArray, firstContact: Boolean = false, now: Long = System.currentTimeMillis()/1000): String? {
        val device = headers("X-RM-Device"); val time = headers("X-RM-Time"); val nonce = headers("X-RM-Nonce"); val key = headers("X-RM-Key")
        val t = time.toLongOrNull() ?: return null
        if(device.length !in 1..100 || device == identity.deviceId || nonce.length != 48 || t < now-300 || t > now+300 || body.size > 2*1024*1024) return null
        val pinned = trusted(device)
        if(pinned != null && pinned != key || !firstContact && pinned == null) return null
        if(!Proof.verify(key, headers("X-RM-Signature"), Proof.request(device,identity.deviceId,method,route,time,nonce,body))) return null
        nonces.entries.removeAll { it.value < now-300 }
        val token = "$device:$nonce"
        if(nonces.containsKey(token) || nonces.size >= 20000) return null
        nonces[token] = t; return device
    }
}
