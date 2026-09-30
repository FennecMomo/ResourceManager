package dev.resourcemanager.android

import android.content.Context
import android.content.ContentValues
import android.database.sqlite.SQLiteOpenHelper
import android.database.sqlite.SQLiteDatabase
import dev.resourcemanager.protocol.*
import kotlinx.serialization.Serializable
import java.time.Instant

@Serializable data class Publication(val id: String, val name: String, val uri: String, val folder: Boolean, val mode: String, val note: String = "", val groupId: String = "default", val recipient: String? = null)
@Serializable data class LocalGroup(val group: Group, val access: String = "Public", val devices: List<String> = emptyList())
@Serializable data class Message(val request: ChatRequest, val outgoing: Boolean, val state: String, val error: String? = null, val attempts: Int = 0, val nextAttempt: Long = 0)
@Serializable data class Transfer(val id: String, val peerId: String, val resource: Resource, val privateResource: Boolean = false, val state: String = "Queued", val bytes: Long = 0, val total: Long = 0, val error: String? = null)

/** SQLite transactions serialize updates from UI, the inbound service, and transfer workers. */
class Store(context: Context): SQLiteOpenHelper(context,"resource-manager.db",null,1) {
    override fun onCreate(db: SQLiteDatabase) { db.execSQL("CREATE TABLE records (bucket TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(bucket,id))") }
    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) = Unit
    fun all(bucket: String): List<String> = readableDatabase.query("records",arrayOf("json"),"bucket=?",arrayOf(bucket),null,null,"rowid").use { c -> buildList { while(c.moveToNext()) add(c.getString(0)) } }
    fun get(bucket: String, id: String): String? = readableDatabase.query("records",arrayOf("json"),"bucket=? AND id=?",arrayOf(bucket,id),null,null,null).use { if(it.moveToFirst()) it.getString(0) else null }
    @Synchronized fun put(bucket: String,id: String,json: String) { writableDatabase.insertWithOnConflict("records",null,ContentValues().apply { put("bucket",bucket); put("id",id); put("json",json) },SQLiteDatabase.CONFLICT_REPLACE).also { check(it != -1L) } }
    @Synchronized fun remove(bucket: String,id: String) { writableDatabase.delete("records","bucket=? AND id=?",arrayOf(bucket,id)) }
    fun peers() = all("peer").map { wire.decodeFromString<Peer>(it) }
    fun peer(id: String) = get("peer",id)?.let { wire.decodeFromString<Peer>(it) }
    @Synchronized fun savePeer(peer: Peer) { val old = peer(peer.hello.deviceId); require(old == null || old.key == peer.key) { "设备密钥已改变" }; put("peer",peer.hello.deviceId,wire.encodeToString(peer)) }
    @Synchronized fun markPeerFailure(id: String, expectedSeen: Long?, error: String) {
        val current = peer(id) ?: return
        if(current.lastSeen == expectedSeen) savePeer(current.copy(error=error))
    }
    fun publications() = all("publication").map { wire.decodeFromString<Publication>(it) }
    fun publication(id: String) = get("publication",id)?.let { wire.decodeFromString<Publication>(it) }
    fun save(p: Publication) = put("publication",p.id,wire.encodeToString(p))
    fun groups(): List<LocalGroup> = all("group").map { wire.decodeFromString<LocalGroup>(it) }.let { groups -> if(groups.any { it.group.id == "default" }) groups else listOf(LocalGroup(Group("default","默认分组",createdUtc=Instant.EPOCH.toString())))+groups }
    fun save(g: LocalGroup) = put("group",g.group.id,wire.encodeToString(g))
    fun allowed(p: Publication, device: String): Boolean {
        if(p.recipient != null) return p.recipient == device && messages().any { it.outgoing && it.request.resourceId == p.id && it.request.recipientDeviceId == device && it.state != "Canceled" }
        val groups = groups().associateBy { it.group.id }; var g = groups[p.groupId]; val visited = mutableSetOf<String>()
        while(g != null && visited.add(g.group.id)) {
            when(g.access) { "Public" -> return true; "Private" -> return false; "AllowList" -> return device in g.devices }
            g = groups[g.group.parentId]
        }
        return g == null
    }
    fun messages() = all("message").map { wire.decodeFromString<Message>(it) }
    private fun messageKey(m: Message) = "${if(m.outgoing) "out" else "in"}:${if(m.outgoing) m.request.recipientDeviceId else m.request.senderDeviceId}:${m.request.messageId}"
    fun save(m: Message) = put("message",messageKey(m),wire.encodeToString(m))
    @Synchronized fun transition(m: Message,expected: String,state: String): Boolean {
        val old=get("message",messageKey(m))?.let {wire.decodeFromString<Message>(it)} ?: return false
        if(old.state != expected) return false
        save(old.copy(state=state,nextAttempt=0,error=null));return true
    }
    fun received(request: ChatRequest) = get("message",messageKey(Message(request,false,"Received")))?.let { wire.decodeFromString<Message>(it) }
    @Synchronized fun receive(request: ChatRequest): Boolean {
        val m = Message(request,false,"Received"); val old = get("message",messageKey(m))?.let { wire.decodeFromString<Message>(it) }
        if(old != null) { require(old.request == request) { "消息 ID 冲突" }; return false }
        save(m); return true
    }
    fun transfers() = all("transfer").map { wire.decodeFromString<Transfer>(it) }
    @Synchronized fun updateRunning(t: Transfer) {
        val current = get("transfer",t.id)?.let {wire.decodeFromString<Transfer>(it)} ?: return
        if(current.state == "Running") save(t)
    }
    fun save(t: Transfer) = put("transfer",t.id,wire.encodeToString(t))
}
