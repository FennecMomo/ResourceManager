package dev.resourcemanager.android

import android.net.*
import dev.resourcemanager.protocol.*
import java.net.*
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import okhttp3.OkHttpClient

/** Wi-Fi routing is per client/socket, never a process-wide bind. */
class NetworkRuntime(private val app: RmApplication) {
    private val manager = app.getSystemService(ConnectivityManager::class.java)
    val linkEpoch = MutableStateFlow(0L)
    private var linkSignature = ""
    val wifi = MutableStateFlow<Network?>(null)
    private val refreshing = AtomicBoolean()
    private val failures = ConcurrentHashMap<String, Pair<Int, Long>>()
    val candidates = MutableStateFlow<Map<String, Candidate>>(emptyMap())

    data class Candidate(val id: String, val name: String, val address: String, val state: String)

    internal var isolatedDiscovery: (suspend ((Discovery, String) -> Unit) -> Unit)? = null
    private val wifiNetworks = ConcurrentHashMap<Network, Boolean>()
    private val callback =
        object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                wifiNetworks[network] = true
                select()
            }

            override fun onLost(network: Network) {
                wifiNetworks.remove(network)
                select()
            }

            override fun onLinkPropertiesChanged(network: Network, properties: LinkProperties) {
                wifiNetworks[network] = true
                select()
            }
        }

    init {
        manager.allNetworks
            .filter {
                manager
                    .getNetworkCapabilities(it)
                    ?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
            }
            .forEach { wifiNetworks[it] = true }
        manager.registerNetworkCallback(
            NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build(),
            callback,
        )
        select()
    }

    @Synchronized
    private fun select() {
        val ready =
            wifiNetworks.keys.filter { item ->
                manager.getLinkProperties(item)?.linkAddresses?.any {
                    it.address is Inet4Address && !it.address.isAnyLocalAddress
                } == true
            }
        val selected = manager.activeNetwork?.takeIf { it in ready } ?: ready.firstOrNull()
        val signature =
            selected?.let {
                manager.getLinkProperties(it)?.let { properties ->
                    "${properties.interfaceName}:${properties.linkAddresses}"
                }
            } ?: ""
        if (wifi.value == selected && linkSignature == signature) return
        linkSignature = signature
        app.downloads.pauseAll()
        app.client.setNetworkClient(
            selected?.let { network ->
                OkHttpClient.Builder()
                    .socketFactory(network.socketFactory)
                    .dns(
                        object : okhttp3.Dns {
                            override fun lookup(hostname: String) =
                                network.getAllByName(hostname).toList()
                        }
                    )
                    .connectTimeout(3, TimeUnit.SECONDS)
                    .readTimeout(12, TimeUnit.SECONDS)
                    .callTimeout(20, TimeUnit.SECONDS)
                    .followRedirects(false)
                    .followSslRedirects(false)
                    .build()
            }
        )
        wifi.value = selected
        linkEpoch.value++
        failures.clear()
        app.diagnostic("network", if (selected == null) "wifi_lost" else "wifi_available")
        if (selected != null) app.refresh()
    }

    private fun candidate(value: Candidate, epoch: Long, pending: Boolean = false) {
        synchronized(candidates) {
            if (linkEpoch.value != epoch) return
            val old = candidates.value[value.id]
            if (pending && old != null) return
            candidates.value = candidates.value + (value.id to value)
        }
    }

    suspend fun refresh() {
        if (!refreshing.compareAndSet(false, true)) return
        app.busy.value = true
        candidates.value = emptyMap()
        val epoch = linkEpoch.value
        val network = wifi.value
        val invalidated = ConcurrentHashMap.newKeySet<String>()
        val seen = ConcurrentHashMap.newKeySet<String>()
        val start = System.currentTimeMillis()
        try {
            withTimeoutOrNull(8000) {
                supervisorScope {
                    val historyGate = Semaphore(4)
                    val discoveryGate = Semaphore(4)
                    suspend fun probe(
                        host: String,
                        port: Int,
                        id: String,
                        name: String,
                        discovered: Boolean,
                    ) {
                        val endpoint = "$host:$port"
                        if (linkEpoch.value != epoch || !seen.add("$discovered:$endpoint")) return
                        if (
                            !discovered &&
                                (failures[endpoint]?.second ?: 0) > System.currentTimeMillis()
                        )
                            return
                        val before = app.store.peer(id)
                        candidate(Candidate(id, name, endpoint, "正在验证"), epoch, pending = true)
                        val began = System.currentTimeMillis()
                        try {
                            if (invalidated.add(endpoint)) app.client.invalidate(host, port)
                            app.client.connectAsync(host, port, expectedId = id)
                            if (linkEpoch.value != epoch) return
                            failures.remove(endpoint)
                            candidate(Candidate(id, name, endpoint, "在线"), epoch)
                            app.diagnostic(
                                "probe",
                                "ok elapsed=${System.currentTimeMillis()-began}",
                            )
                        } catch (e: Exception) {
                            if (e is CancellationException) throw e
                            if (linkEpoch.value != epoch) return
                            val count = (failures[endpoint]?.first ?: 0) + 1
                            failures[endpoint] =
                                count to if (count >= 2) System.currentTimeMillis() + 45000 else 0
                            val current = app.store.peer(id)
                            if (current != null && current.lastSeen != before?.lastSeen) return
                            candidate(Candidate(id, name, endpoint, e.message ?: "连接失败"), epoch)
                            app.store.markPeerFailure(id, before?.lastSeen, e.message ?: "连接失败")
                            app.diagnostic(
                                "probe",
                                "failed elapsed=${System.currentTimeMillis()-began} ${e.javaClass.simpleName}",
                            )
                        } finally {
                            app.changed()
                        }
                    }
                    fun discovered(response: Discovery, host: String) {
                        if (
                            response.protocol != "ResourceManager.LanDiscovery.v1" ||
                                response.type != "response" ||
                                response.deviceId == app.identity.deviceId ||
                                response.deviceId.length !in 1..100 ||
                                response.nickname.length !in 1..80 ||
                                response.port !in 1..65535 ||
                                candidates.value.size >= 64
                        )
                            return
                        candidate(
                            Candidate(
                                response.deviceId,
                                response.nickname,
                                "$host:${response.port}",
                                "正在验证",
                            ),
                            epoch,
                            pending = true,
                        )
                        launch {
                            discoveryGate.withPermit {
                                probe(
                                    host,
                                    response.port,
                                    response.deviceId,
                                    response.nickname,
                                    true,
                                )
                            }
                        }
                    }
                    // Discovery receives independently of the history queue.
                    launch(Dispatchers.IO) {
                        isolatedDiscovery?.let {
                            it(::discovered)
                            return@launch
                        }
                        if (network == null) return@launch
                        DatagramSocket().use { socket ->
                            network.bindSocket(socket)
                            socket.broadcast = true
                            socket.soTimeout = 200
                            val bytes =
                                wire
                                    .encodeToString(
                                        Discovery(
                                            type = "query",
                                            deviceId = app.identity.deviceId,
                                            nickname = app.nickname,
                                            port = PORT,
                                        )
                                    )
                                    .toByteArray()
                            val properties = manager.getLinkProperties(network)
                            val addresses =
                                properties?.interfaceName?.let {
                                    NetworkInterface.getByName(it)?.interfaceAddresses
                                } ?: emptyList()
                            val targets =
                                (addresses.mapNotNull { it.broadcast } +
                                        InetAddress.getByName("255.255.255.255"))
                                    .distinct()
                            fun broadcast() {
                                targets.forEach {
                                    runCatching {
                                        socket.send(
                                            DatagramPacket(bytes, bytes.size, it, DISCOVERY_PORT)
                                        )
                                    }
                                }
                            }
                            broadcast()
                            val until = System.currentTimeMillis() + 2500
                            var repeated = false
                            while (
                                isActive &&
                                    linkEpoch.value == epoch &&
                                    System.currentTimeMillis() < until
                            ) {
                                if (!repeated && System.currentTimeMillis() >= until - 1500) {
                                    broadcast()
                                    repeated = true
                                }
                                val packet = DatagramPacket(ByteArray(4096), 4096)
                                try {
                                    socket.receive(packet)
                                } catch (_: SocketTimeoutException) {
                                    continue
                                }
                                val response =
                                    runCatching {
                                        wire.decodeFromString<Discovery>(
                                            String(packet.data, 0, packet.length)
                                        )
                                    }
                                        .getOrNull() ?: continue
                                discovered(response, packet.address.hostAddress!!)
                            }
                        }
                    }
                    app.store.peers().forEach { peer ->
                        launch {
                            historyGate.withPermit {
                                probe(
                                    peer.host,
                                    peer.hello.port,
                                    peer.hello.deviceId,
                                    peer.hello.nickname,
                                    false,
                                )
                            }
                        }
                    }
                }
            }
            if (linkEpoch.value != epoch) return
            candidates.value =
                candidates.value.mapValues { (_, v) ->
                    if (v.state == "正在验证") v.copy(state = "本轮验证超时") else v
                }
            app.status.value = "刷新完成，离线设备已保留"
        } finally {
            app.busy.value = false
            refreshing.set(false)
            app.diagnostic("refresh", "elapsed=${System.currentTimeMillis()-start}")
            app.changed()
            if (linkEpoch.value != epoch && wifi.value != null) app.refresh()
        }
    }
}
