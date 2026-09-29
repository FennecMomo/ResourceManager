package dev.resourcemanager.protocol

import okhttp3.*
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import java.util.concurrent.TimeUnit

class PeerClient(private val identity: Identity, private val local: () -> Hello, private val findPeer: (String) -> Peer?, private val savePeer: (Peer) -> Unit) {
    private val http = OkHttpClient.Builder().connectTimeout(3,TimeUnit.SECONDS).readTimeout(12,TimeUnit.SECONDS).callTimeout(20,TimeUnit.SECONDS).followRedirects(false).followSslRedirects(false).build()
    fun connect(host: String, port: Int): Peer {
        require(port in 1..65535)
        val base = HttpUrl.Builder().scheme("http").host(host).port(port).build()
        val hello = http.newCall(Request.Builder().url(base.resolve("/api/v1/health")!!).build()).execute().use { response ->
            check(response.isSuccessful) { "设备未响应：${response.code}" }; wire.decodeFromString<Hello>(bounded(response, 65536))
        }
        require(hello.deviceId.length in 1..100 && hello.deviceId != identity.deviceId && hello.port == port && hello.nickname.length in 1..80)
        require(hello.capabilities?.contains("signed-device-v1") == true) { "设备不支持安全身份协议" }
        val data = wire.encodeToString(local()).toByteArray()
        val headers = Proof.headers(identity,hello.deviceId,"POST","/api/v1/auth/hello\n\n",data)
        val req = Request.Builder().url(base.resolve("/api/v1/auth/hello")!!).post(data.toRequestBody("application/json".toMediaType()))
        headers.forEach { (k,v) -> req.header(k,v) }
        val signed = http.newCall(req.build()).execute().use { response ->
            check(response.isSuccessful) { "身份校验失败：${response.code}" }; wire.decodeFromString<SignedHello>(bounded(response,65536))
        }
        require(signed.hello.deviceId == hello.deviceId && signed.nonce == headers["X-RM-Nonce"] && Proof.verify(signed.publicKey,signed.signature,Proof.hello(signed.hello,signed.nonce,identity.deviceId))) { "设备签名无效" }
        val existing = findPeer(hello.deviceId)
        require(existing == null || existing.key == signed.publicKey) { "设备身份密钥已变化，拒绝连接" }
        return Peer(signed.hello,host,signed.publicKey,System.currentTimeMillis()).also(savePeer)
    }
    fun request(peer: Peer, path: String, body: ByteArray? = null, range: String = "", etag: String = "", privateResource: Boolean = false): Response {
        require(path.startsWith("/api/v1/"))
        val url = HttpUrl.Builder().scheme("http").host(peer.host).port(peer.hello.port).build().resolve(path)!!
        val method = if(body == null) "GET" else "POST"
        val builder = Request.Builder().url(url)
        if(body != null) builder.post(body.toRequestBody("application/json".toMediaType()))
        if(range.isNotEmpty()) builder.header("Range",range)
        if(etag.isNotEmpty()) builder.header("If-Range",etag)
        if(privateResource) builder.header("X-ResourceManager-Recipient",identity.deviceId)
        val route = url.encodedPath + (url.encodedQuery?.let { "?$it" } ?: "") + "\n$range\n$etag"
        Proof.headers(identity,peer.hello.deviceId,method,route,body ?: byteArrayOf()).forEach { (k,v) -> builder.header(k,v) }
        return http.newCall(builder.build()).execute()
    }
    inline fun <reified T> get(peer: Peer, path: String, privateResource: Boolean = false): T = request(peer,path,privateResource=privateResource).use {
        check(it.isSuccessful) { "请求失败：${it.code}" }; wire.decodeFromString(bounded(it, 8*1024*1024))
    }
    companion object {
        fun bounded(response: Response, limit: Int): String {
            val body = requireNotNull(response.body); require(body.contentLength() <= limit)
            val source = body.source(); source.request(limit.toLong()+1); require(source.buffer.size <= limit) { "响应超过大小限制" }
            return source.readUtf8()
        }
    }
}
