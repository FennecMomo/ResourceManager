package dev.resourcemanager.android

import android.app.*
import android.content.*
import android.net.Uri
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import dev.resourcemanager.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.net.*
import java.time.Instant
import java.util.UUID

class RmApplication: Application() {
    lateinit var identity: AndroidIdentity; private set
    lateinit var store: Store; private set
    lateinit var files: Files; private set
    lateinit var client: PeerClient; private set
    val scope = CoroutineScope(SupervisorJob()+Dispatchers.IO)
    val revision = MutableStateFlow(0L)
    val status = MutableStateFlow("共享已停止")
    val sharing = MutableStateFlow(false)
    val busy = MutableStateFlow(false)
    lateinit var downloads: Downloads; private set
    var nickname: String
        get() = getSharedPreferences("settings",MODE_PRIVATE).getString("nickname",Build.MODEL)!!
        set(value) { require(value.isNotBlank() && value.length <= 80); getSharedPreferences("settings",MODE_PRIVATE).edit().putString("nickname",value).apply(); changed() }
    override fun onCreate() {
        super.onCreate(); identity = AndroidIdentity(this); store = Store(this); files = Files(this)
        client = PeerClient(identity,::hello,store::peer,store::savePeer); downloads = Downloads(this)
        store.transfers().filter { it.state == "Running" }.forEach { store.save(it.copy(state="Paused",error="进程退出，点击继续")) }
        store.messages().filter { it.state == "Sending" }.forEach { store.save(it.copy(state="Queued")) }
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel("sharing","设备共享",NotificationManager.IMPORTANCE_LOW))
        nm.createNotificationChannel(NotificationChannel("messages","聊天与资源提醒",NotificationManager.IMPORTANCE_DEFAULT))
    }
    fun hello() = Hello(identity.deviceId,nickname)
    fun changed() { synchronized(revision) { revision.value++ } }
    fun action(block: suspend () -> Unit) { scope.launch { try { block(); changed() } catch(e: Exception) { if(e is CancellationException) throw e; status.value = e.message ?: "操作失败"; changed() } } }
    fun startSharing() { ContextCompat.startForegroundService(this,Intent(this,SharingService::class.java)) }
    fun stopSharing() { stopService(Intent(this,SharingService::class.java)) }
    fun connect(host: String,port: Int = PORT) = action { client.connect(host.trim(),port); status.value = "已连接 $host" }
    fun refresh() = action {
        if(busy.value) return@action
        busy.value = true
        try {
            coroutineScope {
                val gate = Semaphore(4)
                store.peers().forEach { peer -> launch { gate.withPermit { runCatching { client.connect(peer.host,peer.hello.port) }.onFailure { store.savePeer(peer.copy(error=it.message)) }; changed() } } }
                launch {
                    DatagramSocket().use { socket ->
                        socket.broadcast = true; socket.soTimeout = 500
                        val bytes = wire.encodeToString(Discovery(type="query",deviceId=identity.deviceId,nickname=nickname,port=PORT)).toByteArray()
                        val targets = mutableSetOf(InetAddress.getByName("255.255.255.255"))
                        NetworkInterface.getNetworkInterfaces().toList().filter { it.isUp && !it.isLoopback }.flatMap { it.interfaceAddresses }.mapNotNull { it.broadcast }.forEach(targets::add)
                        targets.forEach { runCatching { socket.send(DatagramPacket(bytes,bytes.size,it,DISCOVERY_PORT)) } }
                        val until = System.currentTimeMillis()+2500; val seen = mutableSetOf<String>()
                        while(System.currentTimeMillis() < until && seen.size < 64) {
                            val packet = DatagramPacket(ByteArray(4096),4096)
                            try { socket.receive(packet) } catch(_: SocketTimeoutException) { continue }
                            val response = runCatching { wire.decodeFromString<Discovery>(String(packet.data,0,packet.length)) }.getOrNull() ?: continue
                            if(response.protocol != "ResourceManager.LanDiscovery.v1" || response.type != "response" || response.deviceId == identity.deviceId || !seen.add(response.deviceId)) continue
                            launch { gate.withPermit { runCatching { client.connect(packet.address.hostAddress!!,response.port) }; changed() } }
                        }
                    }
                }
            }
            status.value = "刷新完成；离线设备保留在列表中"
        } finally { busy.value = false }
    }
    fun publish(uri: Uri,folder: Boolean,copy: Boolean,group: String,note: String,recipient: String? = null) = action {
        val publication = files.import(uri,folder,copy || recipient != null,group,note,recipient,store::save)
        if(recipient != null) enqueue(recipient,"PrivateResource",resourceId=publication.id)
        status.value = if(recipient == null) "已发布 ${publication.name}" else "已加入私发队列"
    }
    fun enqueue(peer: String,kind: String,text: String? = null,resourceId: String? = null) {
        require(kind != "Text" || !text.isNullOrBlank() && text.length <= 2000)
        store.save(Message(ChatRequest(messageId=UUID.randomUUID().toString(),senderDeviceId=identity.deviceId,recipientDeviceId=peer,sentUtc=Instant.now().toString(),kind=kind,text=text,resourceId=resourceId),true,"Queued")); changed()
    }
    suspend fun deliverMessages() {
        store.messages().filter { it.outgoing && it.state == "Queued" && it.nextAttempt <= System.currentTimeMillis() }.forEach { msg ->
            if(!store.transition(msg,"Queued","Sending")) return@forEach
            try {
                if(Instant.parse(msg.request.sentUtc).isBefore(Instant.now().minusSeconds(7*86400))) { store.save(msg.copy(state="Failed",error="消息已超过 7 天")); return@forEach }
                val peer = requireNotNull(store.peer(msg.request.recipientDeviceId)); client.connect(peer.host,peer.hello.port)
                changed()
                client.request(peer,"/api/v1/chat/messages",wire.encodeToString(msg.request).toByteArray()).use { response ->
                    if(response.code in listOf(400,403,404,409)) { store.save(msg.copy(state="Failed",error="对方拒绝：${response.code}")); return@forEach }
                    check(response.isSuccessful) { "设备暂不可达：${response.code}" }
                    check(wire.decodeFromString<ChatReceipt>(PeerClient.bounded(response,65536)).accepted)
                }
                store.save(msg.copy(state="Delivered",error=null))
            } catch(e: Exception) {
                if(e is CancellationException) throw e
                store.save(msg.copy(state="Queued",attempts=msg.attempts+1,nextAttempt=System.currentTimeMillis()+minOf(300000,5000L*(1L shl minOf(msg.attempts,6))),error=e.message))
            }
            changed()
        }
    }
    fun remind(peer: Peer,r: Resource) = action {
        val reminder = Reminder(messageId=UUID.randomUUID().toString(),senderDeviceId=identity.deviceId,resourceId=r.id,resourceName=r.name,kind=r.kind,note=r.note.take(200),sentUtc=Instant.now().toString())
        client.request(peer,"/api/v1/reminders",wire.encodeToString(reminder).toByteArray()).use { check(it.isSuccessful) { "提醒发送失败：${it.code}" }; check(wire.decodeFromString<ChatReceipt>(PeerClient.bounded(it,65536)).accepted) { "对方未接收提醒" } }
        status.value = "提醒已送达"
    }
    fun notifyMessage(peer: String,text: String) {
        val intent = Intent(this,MainActivity::class.java).putExtra("peer",peer)
        val pending = PendingIntent.getActivity(this,peer.hashCode(),intent,PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val notification = NotificationCompat.Builder(this,"messages").setSmallIcon(android.R.drawable.stat_notify_chat).setContentTitle(store.peer(peer)?.hello?.nickname ?: "ResourceManager").setContentText(text).setContentIntent(pending).setAutoCancel(true).build()
        runCatching { getSystemService(NotificationManager::class.java).notify(peer.hashCode(),notification) }
    }
}
