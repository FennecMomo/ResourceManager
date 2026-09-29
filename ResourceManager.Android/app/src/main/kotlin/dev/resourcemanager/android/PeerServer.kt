package dev.resourcemanager.android

import dev.resourcemanager.protocol.*
import io.ktor.server.application.*
import io.ktor.server.cio.*
import io.ktor.server.engine.*
import io.ktor.server.request.*
import io.ktor.server.response.*
import io.ktor.server.routing.*
import io.ktor.http.*
import io.ktor.utils.io.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.time.Instant

class PeerServer(private val app: RmApplication) {
    private val auth = AuthVerifier(app.identity) { app.store.peer(it)?.key }
    private var server: EmbeddedServer<CIOApplicationEngine,CIOApplicationEngine.Configuration>? = null
    private val messagesPerMinute = mutableMapOf<String,Pair<Long,Int>>()
    @Synchronized private fun acceptMessage(device: String): Boolean {
        val now = System.currentTimeMillis()/60000
        messagesPerMinute.entries.removeAll { it.value.first < now }
        val count = messagesPerMinute[device]?.second ?: 0
        if(count >= 60) return false
        messagesPerMinute[device] = now to count+1; return true
    }
    fun start() {
        server = embeddedServer(CIO,port=PORT,host="0.0.0.0") { routing { route("/api/v1/{...}") { handle { handleCall(call) } } } }.start(false)
    }
    fun stop() { server?.stop(500,2000); server = null }
    private suspend inline fun <reified T> json(call: ApplicationCall, value: T, status: HttpStatusCode = HttpStatusCode.OK) = call.respondText(wire.encodeToString(value),ContentType.Application.Json,status)
    private suspend fun body(call: ApplicationCall, limit: Int): ByteArray {
        require((call.request.header("Content-Length")?.toLongOrNull() ?: 0) <= limit)
        val input = call.receiveChannel(); val buffer = ByteArray(8192); val output = ByteArrayOutputStream()
        while(true) { val n = input.readAvailable(buffer); if(n < 0) break; require(output.size()+n <= limit); output.write(buffer,0,n) }
        return output.toByteArray()
    }
    private suspend fun handleCall(call: ApplicationCall) = withContext(Dispatchers.IO) {
        try {
            val path = call.request.path()
            if(path == "/api/v1/health" && call.request.httpMethod == HttpMethod.Get) { json(call,app.hello()); return@withContext }
            val bytes = body(call,if(path.endsWith("/messages") || path.endsWith("/reminders")) 16384 else 2*1024*1024)
            val device = auth.verify({call.request.header(it) ?: ""},call.request.httpMethod.value,call.request.uri+"\n"+(call.request.header("Range") ?: "")+"\n"+(call.request.header("If-Range") ?: ""),bytes,path == "/api/v1/auth/hello")
            if(device == null) { call.respond(HttpStatusCode.Unauthorized); return@withContext }
            if(path == "/api/v1/auth/hello" && call.request.httpMethod == HttpMethod.Post) {
                val hello = wire.decodeFromString<Hello>(bytes.decodeToString())
                require(hello.deviceId == device && hello.nickname.isNotBlank() && hello.nickname.length <= 80 && hello.port in 1..65535)
                app.store.savePeer(Peer(hello,call.request.local.remoteHost,call.request.header("X-RM-Key")!!,System.currentTimeMillis()))
                val nonce = call.request.header("X-RM-Nonce")!!; val self = app.hello()
                json(call,SignedHello(self,nonce,app.identity.publicKey,app.identity.sign(Proof.hello(self,nonce,device)))); app.changed(); return@withContext
            }
            if(path == "/api/v1/chat/messages" && call.request.httpMethod == HttpMethod.Post) {
                if(!acceptMessage(device)) { call.respond(HttpStatusCode.TooManyRequests); return@withContext }
                val message = wire.decodeFromString<ChatRequest>(bytes.decodeToString())
                require(message.protocol == "chat-v1" && message.senderDeviceId == device && message.recipientDeviceId == app.identity.deviceId && message.messageId.length in 1..64)
                Instant.parse(message.sentUtc)
                require(message.kind in listOf("Text","Resource","PrivateResource"))
                require(if(message.kind == "Text") !message.text.isNullOrBlank() && (message.text?.length ?: 0) <= 2000 && message.resourceId == null else message.text == null && validId(message.resourceId))
                require(message.kind != "PrivateResource" || message.resourceId!!.startsWith("private-"))
                val old = app.store.received(message)
                if(old != null) {
                    if(old.request != message) call.respond(HttpStatusCode.Conflict)
                    else json(call,ChatReceipt(true,duplicate=true,receivedUtc=Instant.now().toString()))
                    return@withContext
                }
                if(message.kind != "Text") {
                    val peer = requireNotNull(app.store.peer(device)); val prefix = if(message.kind == "PrivateResource") "/api/v1/chat/resources/" else "/api/v1/resources/"
                    val resource = app.client.get<Resource>(peer,prefix+message.resourceId,message.kind == "PrivateResource"); require(resource.available)
                }
                val fresh = app.store.receive(message)
                json(call,ChatReceipt(true,duplicate=!fresh,receivedUtc=Instant.now().toString()))
                if(fresh) app.notifyMessage(device, message.text ?: "收到资源：${message.resourceId}")
                app.changed(); return@withContext
            }
            if(path == "/api/v1/reminders" && call.request.httpMethod == HttpMethod.Post) {
                if(!acceptMessage(device)) { call.respond(HttpStatusCode.TooManyRequests); return@withContext }
                val reminder = wire.decodeFromString<Reminder>(bytes.decodeToString())
                require(reminder.protocol == "reminder-v1" && reminder.senderDeviceId == device && reminder.messageId.length in 1..64 && reminder.note.length <= 200 && reminder.resourceName.length in 1..260 && validId(reminder.resourceId))
                Instant.parse(reminder.sentUtc)
                val peer = requireNotNull(app.store.peer(device)); val resource = app.client.get<Resource>(peer,"/api/v1/resources/${reminder.resourceId}"); require(resource.available)
                val msg = ChatRequest(messageId=reminder.messageId,senderDeviceId=device,recipientDeviceId=app.identity.deviceId,sentUtc=reminder.sentUtc,kind="Resource",resourceId=resource.id)
                val fresh = app.store.receive(msg); json(call,ChatReceipt(true)); if(fresh) app.notifyMessage(device,"资源提醒：${resource.name}"); app.changed(); return@withContext
            }
            if(call.request.httpMethod !in listOf(HttpMethod.Get,HttpMethod.Head)) { call.respond(HttpStatusCode.MethodNotAllowed); return@withContext }
            val publications = app.store.publications().filter { it.recipient == null && app.store.allowed(it,device) }
            if(path == "/api/v1/resources") { json(call,publications.map(app.files::describe)); return@withContext }
            if(path == "/api/v1/resource-catalog") {
                val resources = publications.map(app.files::describe)
                val ids = resources.map { it.groupId }.toSet()
                json(call,Catalog(app.store.groups().filter { it.group.id in ids }.map { it.group.copy(parentId=null) },resources)); return@withContext
            }
            val private = path.startsWith("/api/v1/chat/resources/")
            val prefix = if(private) "/api/v1/chat/resources/" else "/api/v1/resources/"
            if(!path.startsWith(prefix)) { call.respond(HttpStatusCode.NotFound); return@withContext }
            val tail = path.removePrefix(prefix).split('/'); val p = app.store.publication(tail[0])
            if(p == null || !app.store.allowed(p,device) || (p.recipient != null) != private || private && call.request.header("X-ResourceManager-Recipient") != device) { call.respond(HttpStatusCode.NotFound); return@withContext }
            when(tail.getOrNull(1)) {
                null -> json(call,app.files.describe(p))
                "tree" -> json(call,app.files.tree(p))
                "content" -> content(call,p)
                else -> call.respond(HttpStatusCode.NotFound)
            }
        } catch(e: IllegalArgumentException) { call.respond(HttpStatusCode.BadRequest) }
          catch(e: SecurityException) { call.respond(HttpStatusCode.Forbidden) }
          catch(e: Exception) { if(e is kotlinx.coroutines.CancellationException) throw e; call.respond(HttpStatusCode.ServiceUnavailable) }
    }
    private fun validId(id: String?) = id != null && id.length in 1..100 && id.all { it.isLetterOrDigit() || it == '-' || it == '_' }
    private suspend fun content(call: ApplicationCall,p: Publication) {
        val doc = app.files.resolve(p,call.request.queryParameters["path"] ?: "")
        val length = doc.length(); require(length >= 0)
        val etag = if(doc.lastModified() > 0) "\"${length.toString(16)}-${doc.lastModified().toString(16)}\"" else null
        if(etag != null) { call.response.header("ETag",etag); call.response.header("Accept-Ranges","bytes") }
        var start = 0L; var end = length-1
        val range = call.request.header("Range")
        val ifRange = call.request.header("If-Range")
        val partial = etag != null && range != null && (ifRange == null || ifRange == etag)
        if(partial) {
            val match = Regex("bytes=(\\d*)-(\\d*)").matchEntire(range!!)
            if(match == null) { call.respond(HttpStatusCode.RequestedRangeNotSatisfiable); return }
            if(match.groupValues[1].isEmpty()) {
                val suffix = match.groupValues[2].toLongOrNull() ?: 0
                start = if(suffix > 0) (length-suffix).coerceAtLeast(0) else length
            } else {
                start = match.groupValues[1].toLongOrNull() ?: length
                end = match.groupValues[2].toLongOrNull()?.coerceAtMost(end) ?: end
            }
            if(start >= length || end < start) { call.response.header("Content-Range","bytes */$length"); call.respond(HttpStatusCode.RequestedRangeNotSatisfiable); return }
            call.response.header("Content-Range","bytes $start-$end/$length")
        }
        val count = if(length == 0L) 0 else end-start+1
        val status = if(partial) HttpStatusCode.PartialContent else HttpStatusCode.OK
        if(call.request.httpMethod == HttpMethod.Head) { call.response.header("Content-Length",count); call.respond(status); return }
        call.respondOutputStream(ContentType.Application.OctetStream,status,contentLength=count) {
            app.files.stream(doc).use { input ->
                var skip = start; while(skip > 0) { val n = input.skip(skip); if(n == 0L) { check(input.read() >= 0); skip-- } else skip -= n }
                val buffer = ByteArray(64*1024); var remaining = count
                while(remaining > 0) { val n = input.read(buffer,0,minOf(remaining,buffer.size.toLong()).toInt()); check(n > 0); write(buffer,0,n); remaining -= n }
            }
        }
    }
}
