package dev.resourcemanager.protocol

import java.io.IOException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.suspendCancellableCoroutine
import okhttp3.*
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import okio.BufferedSource
import okio.ForwardingSource
import okio.buffer

class PeerClient(
    private val identity: Identity,
    private val local: () -> Hello,
    private val findPeer: (String) -> Peer?,
    private val savePeer: (Peer) -> Unit,
) {
    private data class NetworkState(val client: OkHttpClient, val generation: Long)

    private data class Cached(val peer: Peer, val until: Long, val generation: Long)

    private class Attempt(val network: NetworkState) {
        var canceled = false
        val calls = mutableSetOf<Call>()
    }

    private val http =
        OkHttpClient.Builder()
            .connectTimeout(3, TimeUnit.SECONDS)
            .readTimeout(12, TimeUnit.SECONDS)
            .callTimeout(20, TimeUnit.SECONDS)
            .followRedirects(false)
            .followSslRedirects(false)
            .build()
    private val lifecycle = Any()
    private var network = NetworkState(http, 0)
    private val gates = ConcurrentHashMap<String, ReentrantLock>()
    private val cache = ConcurrentHashMap<String, Cached>()
    private val calls = ConcurrentHashMap.newKeySet<Call>()
    private val attempt = ThreadLocal<Attempt>()

    fun setNetworkClient(client: OkHttpClient?) =
        synchronized(lifecycle) {
            network = NetworkState(client ?: http, network.generation + 1)
            calls.forEach(Call::cancel)
            cache.clear()
        }

    fun cancelPending() = setNetworkClient(synchronized(lifecycle) { network.client })

    fun invalidate(host: String, port: Int) {
        cache.remove("$host:$port")
    }

    /** Includes response-body reads in cancellation, not just receipt of headers. */
    suspend fun <T> networkIO(block: () -> T): T = suspendCancellableCoroutine { continuation ->
        val context = Attempt(synchronized(lifecycle) { network })
        val future = executor.submit {
            attempt.set(context)
            try {
                val value = block()
                if (continuation.isActive) continuation.resume(value)
            } catch (e: Exception) {
                if (continuation.isActive) continuation.resumeWithException(e)
            } finally {
                attempt.remove()
            }
        }
        continuation.invokeOnCancellation {
            synchronized(context) {
                context.canceled = true
                context.calls.forEach(Call::cancel)
            }
            future.cancel(true)
        }
    }

    suspend fun connectAsync(
        host: String,
        port: Int,
        force: Boolean = false,
        expectedId: String? = null,
    ): Peer = networkIO { connect(host, port, force, expectedId) }

    private fun checkActive(context: Attempt?, state: NetworkState) {
        check(!Thread.currentThread().isInterrupted && (context == null || !context.canceled)) {
            "连接已取消"
        }
        check(network.generation == state.generation) { "网络已变化，请重试" }
    }

    private fun execute(request: Request, timeoutMillis: Long? = null): Response {
        val context = attempt.get()
        val state = context?.network ?: synchronized(lifecycle) { network }
        val call = state.client.newCall(request)
        if (timeoutMillis != null)
            call.timeout().timeout(timeoutMillis.coerceAtLeast(1), TimeUnit.MILLISECONDS)
        else if (request.url.encodedPath.endsWith("/content")) call.timeout().clearTimeout()
        synchronized(lifecycle) {
            checkActive(context, state)
            calls.add(call)
        }
        synchronized(context ?: lifecycle) {
            context?.calls?.add(call)
            if (context?.canceled == true) call.cancel()
        }
        fun release() {
            calls.remove(call)
            synchronized(context ?: lifecycle) { context?.calls?.remove(call) }
        }
        try {
            val response = call.execute()
            val body =
                response.body
                    ?: run {
                        release()
                        return response
                    }
            val source =
                object : ForwardingSource(body.source()) {
                        override fun read(sink: okio.Buffer, byteCount: Long): Long =
                            try {
                                super.read(sink, byteCount).also { if (it == -1L) release() }
                            } catch (e: IOException) {
                                release()
                                throw e
                            }

                        override fun close() {
                            try {
                                super.close()
                            } finally {
                                release()
                            }
                        }
                    }
                    .buffer()
            return response
                .newBuilder()
                .body(
                    object : ResponseBody() {
                        override fun contentType() = body.contentType()

                        override fun contentLength() = body.contentLength()

                        override fun source(): BufferedSource = source
                    }
                )
                .build()
        } catch (e: Exception) {
            release()
            throw e
        }
    }

    fun connect(host: String, port: Int, force: Boolean = false, expectedId: String? = null): Peer {
        require(port in 1..65535)
        val authority = "$host:$port"
        val began = System.nanoTime()
        val state = attempt.get()?.network ?: synchronized(lifecycle) { network }
        val gate = gates.computeIfAbsent(authority) { ReentrantLock() }
        check(gate.tryLock(3, TimeUnit.SECONDS)) { "身份验证排队超时" }
        try {
            val cached = cache[authority]
            if (
                !force &&
                    cached != null &&
                    cached.generation == state.generation &&
                    cached.until > System.currentTimeMillis()
            ) {
                require(expectedId == null || cached.peer.hello.deviceId == expectedId) {
                    "此地址已属于另一台设备"
                }
                return cached.peer
            }
            fun budget(): Long =
                (3000 - TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - began)).also {
                    check(it > 0) { "身份验证超时" }
                }
            val base = HttpUrl.Builder().scheme("http").host(host).port(port).build()
            val hello =
                execute(Request.Builder().url(base.resolve("/api/v1/health")!!).build(), budget())
                    .use {
                        check(it.isSuccessful) { "设备未响应：${it.code}" }
                        wire.decodeFromString<Hello>(bounded(it, HELLO_LIMIT))
                    }
            require(
                hello.deviceId.length in 1..100 &&
                    hello.deviceId != identity.deviceId &&
                    hello.port == port &&
                    hello.nickname.length in 1..80
            ) {
                "设备握手资料不合法"
            }
            require(expectedId == null || hello.deviceId == expectedId) { "此地址已属于另一台设备" }
            require(hello.capabilities?.contains("signed-device-v1") == true) { "设备不支持安全身份协议" }
            val data = wire.encodeToString(local()).toByteArray()
            val headers =
                Proof.headers(identity, hello.deviceId, "POST", "/api/v1/auth/hello\n\n", data)
            val request =
                Request.Builder()
                    .url(base.resolve("/api/v1/auth/hello")!!)
                    .post(data.toRequestBody("application/json".toMediaType()))
            headers.forEach { (k, v) -> request.header(k, v) }
            val signed =
                execute(request.build(), budget()).use {
                    check(it.isSuccessful) { "身份请求被拒绝：${it.code}，请核对设备信任与系统时间" }
                    wire.decodeFromString<SignedHello>(bounded(it, HELLO_LIMIT))
                }
            require(signed.hello.deviceId == hello.deviceId) { "握手设备身份不一致" }
            require(signed.nonce == headers["X-RM-Nonce"]) { "握手随机数不匹配" }
            require(
                Proof.verify(
                    signed.publicKey,
                    signed.signature,
                    Proof.hello(signed.hello, signed.nonce, identity.deviceId),
                )
            ) {
                "握手签名不匹配"
            }
            val peer = Peer(signed.hello, host, signed.publicKey, System.currentTimeMillis())
            synchronized(attempt.get() ?: lifecycle) {
                synchronized(lifecycle) {
                    checkActive(attempt.get(), state)
                    val existing = findPeer(hello.deviceId)
                    require(existing == null || existing.key == signed.publicKey) {
                        "设备身份密钥已变化，拒绝连接"
                    }
                    savePeer(peer)
                    cache[authority] =
                        Cached(peer, System.currentTimeMillis() + 30000, state.generation)
                }
            }
            return peer
        } catch (e: Exception) {
            cache.remove(authority)
            throw e
        } finally {
            gate.unlock()
        }
    }

    fun request(
        peer: Peer,
        path: String,
        body: ByteArray? = null,
        range: String = "",
        etag: String = "",
        privateResource: Boolean = false,
    ): Response {
        require(path.startsWith("/api/v1/"))
        val verified = connect(peer.host, peer.hello.port, expectedId = peer.hello.deviceId)
        val url =
            HttpUrl.Builder()
                .scheme("http")
                .host(verified.host)
                .port(verified.hello.port)
                .build()
                .resolve(path)!!
        fun signedRequest(): Request {
            val method = if (body == null) "GET" else "POST"
            val builder = Request.Builder().url(url)
            if (body != null) builder.post(body.toRequestBody("application/json".toMediaType()))
            if (range.isNotEmpty()) builder.header("Range", range)
            if (etag.isNotEmpty()) builder.header("If-Range", etag)
            if (privateResource) builder.header("X-ResourceManager-Recipient", identity.deviceId)
            val route =
                url.encodedPath + (url.encodedQuery?.let { "?$it" } ?: "") + "\n$range\n$etag"
            Proof.headers(identity, peer.hello.deviceId, method, route, body ?: byteArrayOf())
                .forEach { (k, v) -> builder.header(k, v) }
            return builder.build()
        }
        val response = execute(signedRequest())
        if (response.code != 401) return response
        // Authentication rejects before dispatch: retry once with fresh identity proof.
        response.close()
        invalidate(peer.host, peer.hello.port)
        connect(peer.host, peer.hello.port, expectedId = peer.hello.deviceId)
        return execute(signedRequest())
    }

    inline fun <reified T> get(peer: Peer, path: String, privateResource: Boolean = false): T =
        request(peer, path, privateResource = privateResource).use {
            check(it.isSuccessful) { "请求失败：${it.code}" }
            wire.decodeFromString(bounded(it, 8 * 1024 * 1024))
        }

    companion object {
        const val HELLO_LIMIT = 768 * 1024
        private val executor = Executors.newCachedThreadPool { task ->
            Thread(task, "rm-network").apply { isDaemon = true }
        }

        fun bounded(response: Response, limit: Int): String {
            val body = requireNotNull(response.body)
            require(body.contentLength() <= limit) { "响应超过大小限制" }
            val source = body.source()
            source.request(limit.toLong() + 1)
            require(source.buffer.size <= limit) { "响应超过大小限制" }
            return source.readUtf8()
        }
    }
}
