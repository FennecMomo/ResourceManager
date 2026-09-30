package dev.resourcemanager.android

import android.app.*
import android.content.*
import android.net.wifi.WifiManager
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import dev.resourcemanager.protocol.*
import java.net.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine

class SharingService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    @Volatile private var destroyed = false
    @Volatile
    internal var listening = false
        private set

    @Volatile private var reportedState = "启动中"
    private val resourceGate = Any()
    private val statusGate = Any()
    private var worker: Job? = null
    private var server: PeerServer? = null
    private var socket: DatagramSocket? = null
    private var multicast: WifiManager.MulticastLock? = null
    private var cpu: PowerManager.WakeLock? = null
    private val app
        get() = application as RmApplication

    private val power
        get() = getSystemService(PowerManager::class.java)

    private val receiver =
        object : BroadcastReceiver() {
            override fun onReceive(context: Context?, intent: Intent?) {
                app.diagnostic(
                    "power",
                    "idle=${power.isDeviceIdleMode} interactive=${power.isInteractive} exempt=${power.isIgnoringBatteryOptimizations(packageName)}",
                )
                if (app.sharing.value)
                    state(
                        if (
                            power.isDeviceIdleMode &&
                                !power.isIgnoringBatteryOptimizations(packageName)
                        )
                            "后台受限"
                        else "共享中"
                    )
            }
        }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun notification(text: String): Notification {
        val open =
            PendingIntent.getActivity(
                this,
                0,
                Intent(this, MainActivity::class.java),
                PendingIntent.FLAG_IMMUTABLE,
            )
        val stop =
            PendingIntent.getService(
                this,
                1,
                Intent(this, SharingService::class.java).setAction("stop"),
                PendingIntent.FLAG_IMMUTABLE,
            )
        return NotificationCompat.Builder(this, "sharing")
            .setSmallIcon(android.R.drawable.stat_sys_upload)
            .setContentTitle("ResourceManager · $text")
            .setContentText("持续共享会增加耗电，可随时停止")
            .setContentIntent(open)
            .addAction(0, "停止共享", stop)
            .setOngoing(true)
            .build()
    }

    private fun state(text: String) {
        synchronized(statusGate) {
            if (destroyed || app.sharingService !== this) return
            reportedState = text
            reportState()
        }
        getSystemService(NotificationManager::class.java).notify(1, notification(text))
        app.diagnostic("service", text)
        app.changed()
    }

    internal fun reportState() {
        synchronized(statusGate) {
            if (app.sharingService !== this) return
            app.sharing.value = listening && !destroyed
            app.serviceState.value = if (destroyed) "已停止" else reportedState
        }
    }

    override fun onCreate() {
        super.onCreate()
        startForeground(1, notification("启动中"))
        app.sharingService = this
        state("启动中")
        app.diagnostic("service", "created")
        androidx.core.content.ContextCompat.registerReceiver(
            this,
            receiver,
            IntentFilter().apply {
                addAction(Intent.ACTION_SCREEN_ON)
                addAction(Intent.ACTION_SCREEN_OFF)
                addAction(PowerManager.ACTION_DEVICE_IDLE_MODE_CHANGED)
            },
            androidx.core.content.ContextCompat.RECEIVER_NOT_EXPORTED,
        )
    }

    private fun startWorker() {
        if (worker?.isActive == true) return
        worker = scope.launch {
            combine(app.network.wifi, app.network.linkEpoch) { network, epoch -> network to epoch }
                .collectLatest { (network, _) ->
                    if (network == null) {
                        release()
                        state("等待 Wi-Fi 网络")
                        return@collectLatest
                    }
                    while (isActive) {
                        try {
                            state("启动中")
                            synchronized(resourceGate) {
                                check(!destroyed) { "服务已停止" }
                                cpu =
                                    power
                                        .newWakeLock(
                                            PowerManager.PARTIAL_WAKE_LOCK,
                                            "ResourceManager:sharing",
                                        )
                                        .apply {
                                            setReferenceCounted(false)
                                            acquire()
                                        }
                                multicast =
                                    (applicationContext.getSystemService(WIFI_SERVICE)
                                            as WifiManager)
                                        .createMulticastLock("ResourceManager discovery")
                                        .apply {
                                            setReferenceCounted(false)
                                            acquire()
                                        }
                                server = PeerServer(app).also { it.start() }
                                val udp =
                                    DatagramSocket(null).apply {
                                        reuseAddress = true
                                        bind(InetSocketAddress(DISCOVERY_PORT))
                                        soTimeout = 500
                                    }
                                socket = udp
                            }
                            val udp = requireNotNull(socket)
                            listening = true
                            state(
                                if (
                                    power.isDeviceIdleMode &&
                                        !power.isIgnoringBatteryOptimizations(packageName)
                                )
                                    "后台受限"
                                else "共享中"
                            )
                            coroutineScope {
                                // Let collectLatest subscribe before starting the blocking UDP
                                // loop.
                                yield()
                                launch {
                                    while (isActive) {
                                        app.deliverMessages()
                                        delay(1000)
                                    }
                                }
                                while (isActive) {
                                    val packet = DatagramPacket(ByteArray(4096), 4096)
                                    try {
                                        udp.receive(packet)
                                    } catch (_: SocketTimeoutException) {
                                        continue
                                    } catch (e: SocketException) {
                                        if (!isActive) break
                                        throw e
                                    }
                                    val query =
                                        runCatching {
                                            wire.decodeFromString<Discovery>(
                                                String(packet.data, 0, packet.length)
                                            )
                                        }
                                            .getOrNull() ?: continue
                                    if (
                                        query.protocol != "ResourceManager.LanDiscovery.v1" ||
                                            query.type != "query" ||
                                            query.deviceId == app.identity.deviceId
                                    )
                                        continue
                                    val bytes =
                                        wire
                                            .encodeToString(
                                                Discovery(
                                                    type = "response",
                                                    deviceId = app.identity.deviceId,
                                                    nickname = app.nickname,
                                                    port = PORT,
                                                )
                                            )
                                            .toByteArray()
                                    try {
                                        udp.send(
                                            DatagramPacket(
                                                bytes,
                                                bytes.size,
                                                packet.address,
                                                packet.port,
                                            )
                                        )
                                    } catch (e: SocketException) {
                                        app.diagnostic("udp", e.javaClass.simpleName)
                                    }
                                }
                            }
                        } catch (e: Exception) {
                            if (e is CancellationException) throw e
                            state("共享失败，正在重试")
                            app.diagnostic("service", e.javaClass.simpleName)
                        } finally {
                            release()
                        }
                        delay(2000)
                    }
                }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val prefs = getSharedPreferences("settings", MODE_PRIVATE)
        if (intent?.action == "stop") {
            prefs.edit().putBoolean("sharingWanted", false).apply()
            app.sharingWanted.value = false
        }
        if (!prefs.getBoolean("sharingWanted", false)) {
            stopSelf()
            return START_NOT_STICKY
        }
        app.sharingWanted.value = true
        // Re-entering the app also restores foreground status if the service was demoted.
        startForeground(1, notification(reportedState))
        startWorker()
        reportState()
        return START_STICKY
    }

    private fun release() =
        synchronized(resourceGate) {
            listening = false
            val ownedResources =
                socket != null || server != null || multicast != null || cpu != null
            socket?.close()
            socket = null
            server?.stop()
            server = null
            multicast?.let { if (it.isHeld) it.release() }
            multicast = null
            cpu?.let { if (it.isHeld) it.release() }
            cpu = null
            // A cancelled old worker may finish after the next service instance starts.
            // Releasing its already-cleared resources must not mark the new instance offline.
            if (ownedResources) reportState()
            app.diagnostic("locks", "released")
        }

    override fun onDestroy() {
        destroyed = true
        scope.cancel()
        release()
        if (app.sharingService === this) {
            app.sharingService = null
            app.sharing.value = false
            app.serviceState.value = "已停止"
            app.client.cancelPending()
            app.downloads.pauseAll()
        }
        unregisterReceiver(receiver)
        app.changed()
        app.diagnostic("service", "destroyed")
        super.onDestroy()
    }
}
