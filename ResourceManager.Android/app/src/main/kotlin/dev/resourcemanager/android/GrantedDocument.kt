package dev.resourcemanager.android

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import java.io.File

/** Read-only document adapter preserving the original tree grant for descendant directories. */
class GrantedDocument private constructor(private val context: Context, val uri: Uri) {
    private val file: File? get() = if(uri.scheme == "file") File(requireNotNull(uri.path)) else null
    private fun field(column: String): String? = context.contentResolver.query(uri,arrayOf(column),null,null,null)?.use { if(it.moveToFirst() && !it.isNull(0)) it.getString(0) else null }
    val name: String? get() = file?.name ?: field(OpenableColumns.DISPLAY_NAME)
    val type: String? get() = if(file != null) if(file!!.isDirectory) DocumentsContract.Document.MIME_TYPE_DIR else "application/octet-stream" else context.contentResolver.getType(uri)
    val isDirectory get() = file?.isDirectory ?: (type == DocumentsContract.Document.MIME_TYPE_DIR)
    val isFile get() = file?.isFile ?: (type != null && !isDirectory)
    fun lastModified() = file?.lastModified() ?: runCatching {field(DocumentsContract.Document.COLUMN_LAST_MODIFIED)?.toLongOrNull() ?: 0}.getOrDefault(0)
    fun length() = file?.length() ?: (field(OpenableColumns.SIZE)?.toLongOrNull() ?: -1)
    fun canRead() = file?.let { it.canRead() && !java.nio.file.Files.isSymbolicLink(it.toPath()) } ?: exists()
    fun exists() = file?.exists() ?: runCatching {name != null}.getOrDefault(false)
    fun findFile(name: String) = listFiles().singleOrNull { it.name == name }
    fun listFiles(): List<GrantedDocument> {
        if(!isDirectory) return emptyList()
        file?.let { return it.listFiles()?.filterNot { child -> java.nio.file.Files.isSymbolicLink(child.toPath()) }?.map { child -> from(context,Uri.fromFile(child)) } ?: emptyList() }
        val children = DocumentsContract.buildChildDocumentsUriUsingTree(uri,DocumentsContract.getDocumentId(uri))
        return context.contentResolver.query(children,arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID),null,null,null)?.use { cursor ->
            buildList { while(cursor.moveToNext()) add(GrantedDocument(context,DocumentsContract.buildDocumentUriUsingTree(uri,cursor.getString(0)))) }
        } ?: emptyList()
    }
    companion object {
        fun from(context: Context,uri: Uri): GrantedDocument {
            require(uri.scheme in listOf("content","file"))
            val documentUri = if(uri.scheme == "content" && DocumentsContract.isTreeUri(uri) && !DocumentsContract.isDocumentUri(context,uri)) DocumentsContract.buildDocumentUriUsingTree(uri,DocumentsContract.getTreeDocumentId(uri)) else uri
            return GrantedDocument(context,documentUri)
        }
    }
}
