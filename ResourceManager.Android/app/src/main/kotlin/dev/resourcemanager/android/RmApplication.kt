package dev.resourcemanager.android

import android.app.*
import android.content.*
import android.net.Uri
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import dev.resourcemanager.protocol.*
import java.net.*
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow

class RmApplication : Application() {
    lateinit var identity: AndroidIdentity
        private set

    lateinit var store: Store
        private set

    lateinit var files: Files
        private set

    lateinit var client: PeerClient
        private set

    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    lateinit var network: NetworkRuntime
        private set

    val serviceState = MutableStateFlow("已停止")
    val revision = MutableStateFlow(0L)
    val status = MutableStateFlow("共享已停止")
    val sharing = MutableStateFlow(false)
    val sharingWanted = MutableStateFlow(false)
    val busy = MutableStateFlow(false)
    lateinit var downloads: Downloads
        private set

    var nickname: String
        get() = getSharedPreferences("settings", MODE_PRIVATE).getString("nickname", Build.MODEL)!!
        set(value) {
            require(value.isNotBlank() && value.length <= 80)
            getSharedPreferences("settings", MODE_PRIVATE)
                .edit()
                .putString("nickname", value)
                .apply()
            changed()
        }

    override fun onCreate() {
        super.onCreate()
        identity = AndroidIdentity(this)
        store = Store(this)
        files = Files(this)
        client = PeerClient(identity, ::hello, store::peer, store::savePeer)
        downloads = Downloads(this)
        store
            .transfers()
            .filter { it.state == "Running" }
            .forEach { store.save(it.copy(state = "Paused", error = "进程退出，点击继续")) }
        store
            .messages()
            .filter { it.state == "Sending" }
            .forEach { store.save(it.copy(state = "Queued")) }
        sharingWanted.value =
            getSharedPreferences("settings", MODE_PRIVATE).getBoolean("sharingWanted", false)
        network = NetworkRuntime(this)
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel("sharing", "设备共享", NotificationManager.IMPORTANCE_LOW)
        )
        nm.createNotificationChannel(
            NotificationChannel("messages", "聊天与资源提醒", NotificationManager.IMPORTANCE_DEFAULT)
        )
    }

    fun hello() = Hello(identity.deviceId, nickname)

    fun changed() {
        synchronized(revision) { revision.value++ }
    }

    fun action(block: suspend () -> Unit) {
        scope.launch {
            try {
                block()
                changed()
            } catch (e: Exception) {
                if (e is CancellationException) throw e
                status.value = e.message ?: "操作失败"
                changed()
            }
        }
    }

    fun startSharing() {
        sharingWanted.value = true
        getSharedPreferences("settings", MODE_PRIVATE)
            .edit()
            .putBoolean("sharingWanted", true)
            .apply()
        ContextCompat.startForegroundService(this, Intent(this, SharingService::class.java))
    }

    fun stopSharing() {
        sharingWanted.value = false
        getSharedPreferences("settings", MODE_PRIVATE)
            .edit()
            .putBoolean("sharingWanted", false)
            .apply()
        stopService(Intent(this, SharingService::class.java))
    }

    fun connect(host: String, port: Int = PORT) = action {
        client.connect(host.trim(), port)
        status.value = "已连接 $host"
    }

    fun refresh() = action { if (::network.isInitialized) network.refresh() }

    @Synchronized
    fun diagnostic(stage: String, message: String) {
        val file = java.io.File(filesDir, "diagnostics.log")
        if (file.length() > 512 * 1024) file.writeText("")
        file.appendText("${Instant.now()} $stage $message\n")
    }

    fun publish(
        uri: Uri,
        folder: Boolean,
        copy: Boolean,
        group: String,
        note: String,
        recipient: String? = null,
    ) = action {
        val publication =
            files.import(
                uri,
                folder,
                copy || recipient != null,
                group,
                note,
                recipient,
                store::save,
            )
        if (recipient != null) enqueue(recipient, "PrivateResource", resourceId = publication.id)
        status.value = if (recipient == null) "已发布 ${publication.name}" else "已加入私发队列"
    }

    fun enqueue(peer: String, kind: String, text: String? = null, resourceId: String? = null) {
        require(kind != "Text" || !text.isNullOrBlank() && text.length <= 2000)
        store.save(
            Message(
                ChatRequest(
                    messageId = UUID.randomUUID().toString(),
                    senderDeviceId = identity.deviceId,
                    recipientDeviceId = peer,
                    sentUtc = Instant.now().toString(),
                    kind = kind,
                    text = text,
                    resourceId = resourceId,
                ),
                true,
                "Queued",
            )
        )
        changed()
    }

    suspend fun deliverMessages() {
        store
            .messages()
            .filter {
                it.outgoing && it.state == "Queued" && it.nextAttempt <= System.currentTimeMillis()
            }
            .forEach { msg ->
                if (!store.transition(msg, "Queued", "Sending")) return@forEach
                try {
                    if (
                        Instant.parse(msg.request.sentUtc)
                            .isBefore(Instant.now().minusSeconds(7 * 86400))
                    ) {
                        store.save(msg.copy(state = "Failed", error = "消息已超过 7 天"))
                        return@forEach
                    }
                    val peer = requireNotNull(store.peer(msg.request.recipientDeviceId))
                    client.connectAsync(
                        peer.host,
                        peer.hello.port,
                        expectedId = peer.hello.deviceId,
                    )
                    changed()
                    val delivered = client.networkIO {
                        client
                            .request(
                                peer,
                                "/api/v1/chat/messages",
                                wire.encodeToString(msg.request).toByteArray(),
                            )
                            .use { response ->
                                if (response.code in listOf(400, 403, 404, 409)) {
                                    store.save(
                                        msg.copy(state = "Failed", error = "对方拒绝：${response.code}")
                                    )
                                    return@networkIO false
                                }
                                check(response.isSuccessful) { "设备暂不可达：${response.code}" }
                                check(
                                    wire
                                        .decodeFromString<ChatReceipt>(
                                            PeerClient.bounded(response, 65536)
                                        )
                                        .accepted
                                )
                                true
                            }
                    }
                    if (delivered) store.save(msg.copy(state = "Delivered", error = null))
                } catch (e: Exception) {
                    if (e is CancellationException) {
                        store.transition(msg, "Sending", "Queued")
                        throw e
                    }
                    store.save(
                        msg.copy(
                            state = "Queued",
                            attempts = msg.attempts + 1,
                            nextAttempt =
                                System.currentTimeMillis() +
                                    minOf(300000, 5000L * (1L shl minOf(msg.attempts, 6))),
                            error = e.message,
                        )
                    )
                }
                changed()
            }
    }

    fun remind(peer: Peer, r: Resource) = action {
        val reminder =
            Reminder(
                messageId = UUID.randomUUID().toString(),
                senderDeviceId = identity.deviceId,
                resourceId = r.id,
                resourceName = r.name,
                kind = r.kind,
                note = r.note.take(200),
                sentUtc = Instant.now().toString(),
            )
        client.request(peer, "/api/v1/reminders", wire.encodeToString(reminder).toByteArray()).use {
            check(it.isSuccessful) { "提醒发送失败：${it.code}" }
            check(wire.decodeFromString<ChatReceipt>(PeerClient.bounded(it, 65536)).accepted) {
                "对方未接收提醒"
            }
        }
        status.value = "提醒已送达"
    }

    fun notifyMessage(peer: String, text: String) {
        val intent = Intent(this, MainActivity::class.java).putExtra("peer", peer)
        val pending =
            PendingIntent.getActivity(
                this,
                peer.hashCode(),
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
        val notification =
            NotificationCompat.Builder(this, "messages")
                .setSmallIcon(android.R.drawable.stat_notify_chat)
                .setContentTitle(store.peer(peer)?.hello?.nickname ?: "ResourceManager")
                .setContentText(text)
                .setContentIntent(pending)
                .setAutoCancel(true)
                .build()
        runCatching {
            getSystemService(NotificationManager::class.java).notify(peer.hashCode(), notification)
        }
    }
}
