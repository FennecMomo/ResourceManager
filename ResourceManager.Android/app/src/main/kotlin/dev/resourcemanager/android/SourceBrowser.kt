package dev.resourcemanager.android

import android.content.Intent
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

private data class SourceEntry(val uri: Uri,val name: String,val directory: Boolean,val size: Long,val type: String)
@Composable fun SourceBrowser(app: RmApplication,onPublish: (Uri,Boolean) -> Unit) {
    val revision by app.revision.collectAsState()
    var path by remember { mutableStateOf<List<Uri>>(emptyList()) }
    var entries by remember { mutableStateOf<List<SourceEntry>>(emptyList()) }
    var selected by remember { mutableStateOf<Set<Uri>>(emptySet()) }
    var filter by remember { mutableStateOf("全部") }
    var query by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    val roots = remember(revision) { app.store.all("source").map(Uri::parse) }
    val grant = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri -> if(uri != null) {
        runCatching { app.contentResolver.takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION);app.store.put("source",uri.toString(),uri.toString());app.changed();path=listOf(uri) }.onFailure {error=it.message}
    } }
    LaunchedEffect(path,revision) {
        selected=emptySet();error=null
        entries=withContext(Dispatchers.IO) { runCatching {
            val docs=if(path.isEmpty()) roots.map {GrantedDocument.from(app,it)} else GrantedDocument.from(app,path.last()).listFiles().toList()
            docs.map {SourceEntry(it.uri,it.name ?: "未命名",it.isDirectory,it.length(),it.type ?: "")}.sortedWith(compareByDescending<SourceEntry>{it.directory}.thenBy {it.name})
        }.getOrElse { error=it.message;emptyList() } }
    }
    Column(verticalArrangement=Arrangement.spacedBy(4.dp)) {
        Text("手机文件浏览器",style=MaterialTheme.typography.titleMedium)
        Text("授权目录后可按类型筛选、多选文件或发布当前文件夹。",style=MaterialTheme.typography.bodySmall)
        Row { TextButton(onClick={grant.launch(null)}) {Text("添加授权目录")}; if(path.isNotEmpty()) TextButton(onClick={path=path.dropLast(1)}) {Text("上一级")} }
        OutlinedTextField(query,{query=it},label={Text("筛选文件名")},singleLine=true,modifier=Modifier.fillMaxWidth())
        Row { listOf("全部","图片","视频","音频","文档").forEach { value -> FilterChip(selected=filter==value,onClick={filter=value},label={Text(value)}) } }
        error?.let {Text("目录不可用，请重新授权：$it")}
        val visible = entries.filter { entry -> entry.name.contains(query,true) && (entry.directory || when(filter) {"图片"->entry.type.startsWith("image/");"视频"->entry.type.startsWith("video/");"音频"->entry.type.startsWith("audio/");"文档"->!entry.type.startsWith("image/") && !entry.type.startsWith("video/") && !entry.type.startsWith("audio/");else->true}) }
        visible.take(300).forEach { entry -> Row(Modifier.fillMaxWidth()) {
            if(!entry.directory) Checkbox(entry.uri in selected,{checked -> selected=if(checked) selected+entry.uri else selected-entry.uri})
            TextButton(onClick={if(entry.directory) path=path+entry.uri else selected=if(entry.uri in selected) selected-entry.uri else selected+entry.uri},modifier=Modifier.weight(1f)) {Text((if(entry.directory) "▸ " else "")+entry.name)}
        } }
        if(visible.size>300) Text("已显示前 300 项，请输入文件名缩小范围。")
        Row { if(selected.isNotEmpty()) Button(onClick={selected.forEach {onPublish(it,false)};selected=emptySet()}) {Text("发布 ${selected.size} 个文件")}; if(path.isNotEmpty()) TextButton(onClick={onPublish(path.last(),true)}) {Text("发布当前目录")} }
    }
}
