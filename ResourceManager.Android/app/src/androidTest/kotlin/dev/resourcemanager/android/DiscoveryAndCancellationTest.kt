package dev.resourcemanager.android

import androidx.test.platform.app.InstrumentationRegistry
import dev.resourcemanager.protocol.*
import io.ktor.http.*
import io.ktor.server.cio.*
import io.ktor.server.engine.*
import io.ktor.server.request.*
import io.ktor.server.response.*
import io.ktor.server.routing.*
import java.net.ServerSocket
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.collect
import org.junit.Assert.*
import org.junit.Test

class DiscoveryAndCancellationTest {
    @Test
    fun lanDoesNotWaitForTwentyStalledPeers() = runBlocking {
        val app =
            InstrumentationRegistry.getInstrumentation().targetContext.applicationContext
                as RmApplication
        app.store.remove("peer","fast-lan-fixture")
        val port = ServerSocket(0).use { it.localPort }
        val identity = JvmIdentity("fast-lan-fixture")
        val hello = Hello(identity.deviceId, "快速测试设备", port)
        val node =
            embeddedServer(CIO, port = port, host = "127.0.0.1") {
                    routing {
                        get("/api/v1/health") {
                            call.respondText(
                                wire.encodeToString(hello),
                                ContentType.Application.Json,
                            )
                        }
                        post("/api/v1/auth/hello") {
                            val sender = wire.decodeFromString<Hello>(call.receiveText())
                            val nonce = call.request.headers["X-RM-Nonce"]!!
                            call.respondText(
                                wire.encodeToString(
                                    SignedHello(
                                        hello,
                                        nonce,
                                        identity.publicKey,
                                        identity.sign(Proof.hello(hello, nonce, sender.deviceId)),
                                    )
                                ),
                                ContentType.Application.Json,
                            )
                        }
                    }
                }
                .start(false)
        val sockets = (0..19).map { ServerSocket(0) }
        sockets.forEachIndexed { index, socket ->
            app.store.savePeer(
                Peer(
                    Hello("stall-$index", "历史设备 $index", socket.localPort),
                    "127.0.0.1",
                    JvmIdentity("stall-$index").publicKey,
                )
            )
        }
        var appearedAt = 0L
        val start = System.currentTimeMillis()
        val observer = launch {
            app.network.candidates.collect {
                if (it[identity.deviceId]?.state == "在线" && appearedAt == 0L)
                    appearedAt = System.currentTimeMillis() - start
            }
        }
        try {
            while (app.busy.value) delay(30)
            app.network.isolatedDiscovery = { callback ->
                delay(30)
                callback(
                    Discovery(
                        type = "response",
                        deviceId = identity.deviceId,
                        nickname = hello.nickname,
                        port = port,
                    ),
                    "127.0.0.1",
                )
                callback(
                    Discovery(
                        type = "response",
                        deviceId = identity.deviceId,
                        nickname = hello.nickname,
                        port = port,
                    ),
                    "127.0.0.1",
                )
            }
            app.network.refresh()
            assertTrue("LAN confirmation elapsed=$appearedAt", appearedAt in 1..5000)
            assertTrue(
                "foreground elapsed=${System.currentTimeMillis()-start}",
                System.currentTimeMillis() - start < 9000,
            )
            assertEquals("在线", app.network.candidates.value[identity.deviceId]?.state)
            assertEquals(20, app.store.peers().count { it.hello.deviceId.startsWith("stall-") })
            app.diagnostic(
                "performance",
                "20_stalls lan=$appearedAt total=${System.currentTimeMillis()-start}",
            )
        } finally {
            observer.cancel()
            app.network.isolatedDiscovery = null
            sockets.forEach { it.close() }
            (0..19).forEach { app.store.remove("peer", "stall-$it") }
            node.stop(0, 1000)
        }
    }

    @Test
    fun cancellationClosesStalledHelloBody() = runBlocking {
        val listener = ServerSocket(0)
        val closed = CountDownLatch(1)
        val server = Thread {
            try {
                listener.accept().use { socket ->
                    socket.soTimeout = 4000
                    val input = socket.getInputStream().bufferedReader()
                    while (input.readLine()?.isNotEmpty() == true) {}
                    socket
                        .getOutputStream()
                        .write(
                            "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n{"
                                .toByteArray()
                        )
                    socket.getOutputStream().flush()
                    if (socket.getInputStream().read() == -1) closed.countDown()
                }
            } catch (_: Exception) {} finally {
                listener.close()
            }
        }
            .apply { start() }
        val identity = JvmIdentity("cancel-test")
        val client =
            PeerClient(
                identity,
                { Hello(identity.deviceId, "cancel") },
                { null },
                { fail("canceled handshake must not persist identity") },
            )
        val start = System.currentTimeMillis()
        withTimeoutOrNull(250) { client.connectAsync("127.0.0.1", listener.localPort) }
        assertTrue("cancellation completes promptly", System.currentTimeMillis() - start < 1500)
        assertTrue("underlying HTTP socket closes", closed.await(2, TimeUnit.SECONDS))
        server.join(4500)
    }
}
