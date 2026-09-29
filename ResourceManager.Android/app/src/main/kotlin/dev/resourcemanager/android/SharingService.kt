package dev.resourcemanager.android

import android.app.*
import android.content.Intent
import android.os.IBinder
import android.net.wifi.WifiManager
import androidx.core.app.NotificationCompat
import dev.resourcemanager.protocol.*
import kotlinx.coroutines.*
import java.net.*

class SharingService: Service() {
    private val scope = CoroutineScope(SupervisorJob()+Dispatchers.IO)
    private var server: PeerServer? = null
    private var socket: DatagramSocket? = null
    private var lock: WifiManager.MulticastLock? = null
    override fun onBind(intent: Intent?): IBinder? = null
    override fun onCreate() {
        super.onCreate()
        val app = application as RmApplication
        val open = PendingIntent.getActivity(this,0,Intent(this,MainActivity::class.java),PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this,1,Intent(this,SharingService::class.java).setAction("stop"),PendingIntent.FLAG_IMMUTABLE)
        startForeground(1,NotificationCompat.Builder(this,"sharing").setSmallIcon(android.R.drawable.stat_sys_upload).setContentTitle("ResourceManager 共享中").setContentText("局域网设备可连接；点击停止会中断传输").setContentIntent(open).addAction(0,"停止共享",stop).setOngoing(true).build())
        scope.launch {
            try {
                server = PeerServer(app).also { it.start() }
                lock = (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager).createMulticastLock("ResourceManager discovery").apply { setReferenceCounted(false); acquire() }
                app.sharing.value = true; app.status.value = "共享已启动，端口 $PORT"; app.changed()
                launch { while(isActive) { app.deliverMessages(); delay(3000) } }
                val udp = DatagramSocket(null).apply { reuseAddress = true; bind(InetSocketAddress(DISCOVERY_PORT)); soTimeout = 1000 }; socket = udp
                while(isActive) {
                    val packet = DatagramPacket(ByteArray(4096),4096)
                    try { udp.receive(packet) } catch(_: SocketTimeoutException) { continue }
                    val query = runCatching { wire.decodeFromString<Discovery>(String(packet.data,0,packet.length)) }.getOrNull() ?: continue
                    if(query.protocol != "ResourceManager.LanDiscovery.v1" || query.type != "query" || query.deviceId == app.identity.deviceId) continue
                    val bytes = wire.encodeToString(Discovery(type="response",deviceId=app.identity.deviceId,nickname=app.nickname,port=PORT)).toByteArray()
                    udp.send(DatagramPacket(bytes,bytes.size,packet.address,packet.port))
                }
            } catch(e: Exception) { if(e !is CancellationException) { app.status.value = "共享停止：${e.message}"; stopSelf() } }
        }
    }
    override fun onStartCommand(intent: Intent?,flags: Int,startId: Int): Int { if(intent?.action == "stop") stopSelf(); return START_NOT_STICKY }
    override fun onDestroy() {
        val app = application as RmApplication
        socket?.close(); scope.cancel(); server?.stop(); lock?.let { if(it.isHeld) it.release() }; app.downloads.pauseAll()
        app.sharing.value = false; app.changed(); super.onDestroy()
    }
}
