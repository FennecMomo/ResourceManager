package dev.resourcemanager.android

import android.content.Context
import android.net.Uri

import dev.resourcemanager.protocol.*
import java.io.File
import java.io.InputStream
import java.time.Instant
import java.util.UUID

class Files(private val context: Context) {
    private fun document(uri: Uri): GrantedDocument = GrantedDocument.from(context,uri)
    fun root(p: Publication): GrantedDocument = if(p.uri.startsWith("file:")) GrantedDocument.from(context,Uri.parse(p.uri)) else document(Uri.parse(p.uri))
    fun tree(p: Publication): List<RemoteFile> {
        val root = root(p); check(root.exists() && root.canRead()) { "源文件授权失效或已移除" }
        val output = mutableListOf<RemoteFile>()
        fun visit(doc: GrantedDocument,path: String,depth: Int) {
            require(depth <= 64 && output.size < 100000) { "目录过深或条目过多" }
            require(doc.isDirectory || doc.length() >= 0) { "源未提供文件大小，请使用复制发布" }
            if(path.isNotEmpty() || !p.folder) output += RemoteFile(path,if(doc.isDirectory) 0 else doc.length(),Instant.ofEpochMilli(doc.lastModified()).toString(),doc.isDirectory)
            if(doc.isDirectory) doc.listFiles().sortedBy { it.name }.forEach { child ->
                val name = child.name ?: return@forEach
                if(name.equals(".git",true)) return@forEach
                SafePath.segments(name); require('/' !in name)
                visit(child,if(path.isEmpty()) name else "$path/$name",depth+1)
            }
        }
        visit(root,"",0); return output
    }
    fun resolve(p: Publication,path: String): GrantedDocument {
        val segments = SafePath.segments(path)
        require(p.folder || segments.isEmpty()); require(!p.folder || segments.isNotEmpty())
        var doc = root(p)
        segments.forEach { doc = requireNotNull(doc.findFile(it)) }
        require(doc.isFile && doc.canRead()); return doc
    }
    fun stream(doc: GrantedDocument): InputStream = if(doc.uri.scheme == "file") File(requireNotNull(doc.uri.path)).inputStream() else requireNotNull(context.contentResolver.openInputStream(doc.uri))
    fun describe(p: Publication): Resource = runCatching {
        val entries = tree(p)
        Resource(p.id,p.name,if(p.folder) "Folder" else "File",p.mode,entries.sumOf { it.size },Instant.ofEpochMilli(root(p).lastModified()).toString(),true,p.note,p.groupId)
    }.getOrElse { Resource(p.id,p.name,if(p.folder) "Folder" else "File",p.mode,0,Instant.EPOCH.toString(),false,p.note,p.groupId) }
    @Synchronized fun import(uri: Uri, folder: Boolean, copy: Boolean, group: String, note: String, recipient: String? = null, onImported: (Publication) -> Unit = {}): Publication {
        val source = document(uri); require(source.exists() && source.canRead())
        val id = (if(recipient == null) "" else "private-") + UUID.randomUUID().toString().replace("-","") + if(recipient != null) UUID.randomUUID().toString().replace("-","") else ""
        val name = source.name ?: "共享文件"; SafePath.segments(name); require('/' !in name)
        if(!copy) return Publication(id,name,uri.toString(),folder,"Reference",note,group,recipient).also(onImported)
        val base = File(context.filesDir,"published/$id"); check(base.mkdirs())
        val target = File(base,name)
        fun copyDoc(doc: GrantedDocument,to: File,depth: Int) {
            require(depth <= 64)
            if(doc.isDirectory) { check(to.mkdirs()); doc.listFiles().forEach { child ->
                val n = requireNotNull(child.name); if(!n.equals(".git",true)) { SafePath.segments(n); require('/' !in n); copyDoc(child,File(to,n),depth+1) }
            } } else { stream(doc).use { input -> to.outputStream().use { input.copyTo(it) } } }
        }
        try { copyDoc(source,target,0); return Publication(id,name,Uri.fromFile(target).toString(),folder,"Copy",note,group,recipient).also(onImported) }
        catch(e: Exception) { base.deleteRecursively(); throw e }
    }
    @Synchronized fun export(p: Publication): File {
        val name=p.name.replace(Regex("[\\\\/:*?\"<>|\\p{Cntrl}]"),"_").ifBlank {"resource"}
        val dir=File(context.filesDir,"exports/${p.id}").apply {mkdirs()}
        if(!p.folder) return File(dir,name).also { target -> stream(root(p)).use { input -> target.outputStream().use {input.copyTo(it)} } }
        return File(dir,"$name.zip").also { target -> java.util.zip.ZipOutputStream(target.outputStream()).use { zip ->
            tree(p).forEach { entry -> zip.putNextEntry(java.util.zip.ZipEntry(entry.relativePath+if(entry.isDirectory) "/" else "")); if(!entry.isDirectory) stream(resolve(p,entry.relativePath)).use {it.copyTo(zip)};zip.closeEntry() }
        } }
    }
    @Synchronized fun cleanUnusedCopies(active: () -> Set<String>): Long {
        val live = active()
        val root=File(context.filesDir,"published");var released=0L
        root.listFiles()?.filter {it.isDirectory && it.name !in live}?.forEach { directory ->
            require(directory.canonicalPath.startsWith(root.canonicalPath+File.separator))
            val bytes=directory.walkTopDown().filter {it.isFile}.sumOf {it.length()}
            if(directory.deleteRecursively()) released+=bytes
        }
        return released
    }
}
