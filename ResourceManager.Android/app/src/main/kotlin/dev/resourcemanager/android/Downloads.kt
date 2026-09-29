package dev.resourcemanager.android

import dev.resourcemanager.protocol.*
import kotlinx.coroutines.*
import java.io.File
import java.net.URLEncoder
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

class Downloads(private val app: RmApplication) {
    private val jobs = ConcurrentHashMap<String,Job>()
    fun enqueue(peer: Peer,r: Resource,privateResource: Boolean = false) {
        val transfer = Transfer(UUID.randomUUID().toString(),peer.hello.deviceId,r,privateResource,total=r.size)
        app.store.save(transfer); resume(transfer)
    }
    fun pauseAll() { jobs.keys.toList().forEach(::pause) }
    fun pause(id: String) { jobs[id]?.cancel(); app.store.transfers().find { it.id == id }?.let { app.store.save(it.copy(state="Paused")); app.changed() } }
    @Synchronized fun resume(t: Transfer) {
        if(jobs.containsKey(t.id)) return
        require(app.sharing.value) { "请先启动共享，传输期间保持共享服务运行" }
        val job = app.scope.launch(start=CoroutineStart.LAZY) {
            var current = t.copy(state="Running",error=null); app.store.save(current); app.changed()
            try {
                val peer = requireNotNull(app.store.peer(t.peerId)); app.client.connect(peer.host,peer.hello.port)
                val prefix = if(t.privateResource) "/api/v1/chat/resources/" else "/api/v1/resources/"
                val tree = app.client.get<List<RemoteFile>>(peer,prefix+t.resource.id+"/tree",t.privateResource)
                require(tree.size <= 100000)
                val root = File(app.filesDir,"transfers/${t.id}/content").apply { mkdirs() }
                val chunks = File(app.filesDir,"transfers/${t.id}/parts").apply { mkdirs() }
                val paths = tree.map { SafePath.segments(it.relativePath).joinToString("/") }
                require(paths.distinct().size == paths.size) { "目录包含重复路径" }
                var done = 0L
                for(entry in tree) {
                    currentCoroutineContext().ensureActive()
                    val segments = SafePath.segments(entry.relativePath)
                    require(entry.size >= 0)
                    val relative = if(t.resource.kind == "File") { require(segments.isEmpty()); "file" } else { require(segments.isNotEmpty()); segments.joinToString("/") }
                    val destination = File(root,relative)
                    if(entry.isDirectory) { check(destination.isDirectory || destination.mkdirs()); continue }
                    destination.parentFile!!.mkdirs()
                    val token = Proof.hash(relative.toByteArray())
                    val part = File(chunks,"$token.part"); val tag = File(chunks,"$token.etag")
                    var offset = if(tag.exists()) part.length() else 0L
                    if(!tag.exists() && part.exists()) part.writeBytes(byteArrayOf())
                    val url = prefix+t.resource.id+"/content?path="+URLEncoder.encode(entry.relativePath,"UTF-8").replace("+","%20")
                    app.client.request(peer,url,range=if(offset > 0) "bytes=$offset-" else "",etag=if(offset > 0) tag.readText() else "",privateResource=t.privateResource).use { response ->
                        if(response.code == 416) { part.writeBytes(byteArrayOf()); tag.delete(); error("文件长度已变化，点击继续重新下载") }
                        check(response.code in listOf(200,206)) { "下载失败：${response.code}" }
                        if(response.code == 200) offset = 0
                        else require(response.header("Content-Range")?.startsWith("bytes $offset-") == true)
                        val etag = response.header("ETag"); if(etag != null) tag.writeText(etag) else tag.delete()
                        response.body!!.byteStream().use { input -> java.io.FileOutputStream(part,offset > 0).use { output ->
                            val buffer = ByteArray(65536); var checkpoint = System.currentTimeMillis()
                            while(true) { currentCoroutineContext().ensureActive(); val n = input.read(buffer); if(n < 0) break; output.write(buffer,0,n); offset += n
                                if(System.currentTimeMillis()-checkpoint > 500) { current = current.copy(bytes=done+offset); app.store.save(current); app.changed(); checkpoint = System.currentTimeMillis() }
                            }
                            output.fd.sync()
                        } }
                        check(part.length() == entry.size) { "文件已变化或长度不完整，请刷新目录后重试" }
                    }
                    check(part.renameTo(destination)); tag.delete(); done += entry.size
                }
                app.store.save(current.copy(state="Completed",bytes=done,total=done,error=null))
            } catch(e: Exception) { app.store.save(current.copy(state=if(e is CancellationException) "Paused" else "Failed",error=e.message)) }
            finally { jobs.remove(t.id); app.changed() }
        }
        jobs[t.id] = job; job.start()
    }
    fun export(t: Transfer): File {
        require(t.state == "Completed")
        val source = File(app.filesDir,"transfers/${t.id}/content")
        val dir = File(app.filesDir,"exports/${t.id}").apply { mkdirs() }
        val safeName = t.resource.name.replace(Regex("[\\\\/:*?\"<>|\\p{Cntrl}]"),"_").ifBlank { "resource" }
        return if(t.resource.kind == "File") File(dir,safeName).also { File(source,"file").copyTo(it,overwrite=true) }
        else File(dir,"$safeName.zip").also { target -> java.util.zip.ZipOutputStream(target.outputStream()).use { zip ->
            source.walkTopDown().filter { it != source }.forEach { file ->
                val path = file.relativeTo(source).invariantSeparatorsPath + if(file.isDirectory) "/" else ""
                zip.putNextEntry(java.util.zip.ZipEntry(path)); if(file.isFile) file.inputStream().use { it.copyTo(zip) }; zip.closeEntry()
            }
        } }
    }
}
