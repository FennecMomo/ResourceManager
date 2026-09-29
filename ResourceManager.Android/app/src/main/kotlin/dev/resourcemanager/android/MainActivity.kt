package dev.resourcemanager.android

import android.Manifest
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.resourcemanager.protocol.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.time.Instant
import java.util.UUID

class MainActivity: ComponentActivity() {
    private val app get() = application as RmApplication
    private var incoming by mutableStateOf<List<Uri>>(emptyList())
    private var requestedPeer by mutableStateOf<String?>(null)
    private val notifications = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState); receiveShare(intent)
        setContent { MaterialTheme { Surface(Modifier.fillMaxSize()) { AppUi(app,incoming,{ incoming=emptyList();intent=Intent(this,MainActivity::class.java) },::share,::sharePublication,requestedPeer) } } }
        if(Build.VERSION.SDK_INT >= 33) notifications.launch(Manifest.permission.POST_NOTIFICATIONS)
    }
    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); setIntent(intent); receiveShare(intent) }
    @Suppress("DEPRECATION") private fun receiveShare(intent: Intent?) {
        requestedPeer = intent?.getStringExtra("peer")
        incoming = when(intent?.action) {
            Intent.ACTION_SEND -> listOfNotNull(intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))
            Intent.ACTION_SEND_MULTIPLE -> intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)?.toList() ?: emptyList()
            else -> emptyList()
        }.filter { it.scheme == "content" }.take(100)
    }
    private fun share(t: Transfer) = app.action {
        shareFile(app.downloads.export(t))
    }
    private fun sharePublication(p: Publication) = app.action {shareFile(app.files.export(p))}
    private suspend fun shareFile(file: java.io.File) {
        val uri = FileProvider.getUriForFile(this,"$packageName.files",file)
        withContext(Dispatchers.Main) { startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType(contentResolver.getType(uri) ?: "application/octet-stream").putExtra(Intent.EXTRA_STREAM,uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),"分享文件")) }
    }
}

@Composable private fun AppUi(app: RmApplication,incoming: List<Uri>,clearIncoming: () -> Unit,share: (Transfer) -> Unit,sharePublication: (Publication) -> Unit,requestedPeer: String?) {
    val revision by app.revision.collectAsStateWithLifecycle()
    val status by app.status.collectAsStateWithLifecycle()
    val sharing by app.sharing.collectAsStateWithLifecycle()
    var tab by remember { mutableIntStateOf(0) }
    var peer by remember { mutableStateOf<Peer?>(null) }
    var catalog by remember { mutableStateOf<Catalog?>(null) }
    var query by remember { mutableStateOf("") }
    var kind by remember { mutableStateOf("全部") }
    var minSize by remember { mutableStateOf("") }
    var host by remember { mutableStateOf("") }
    var nickname by remember { mutableStateOf(app.nickname) }
    var chat by remember { mutableStateOf("") }
    var groupName by remember { mutableStateOf("") }
    var groupId by remember { mutableStateOf("default") }
    var note by remember { mutableStateOf("") }
    var copy by remember { mutableStateOf(false) }
    var privateRecipient by remember { mutableStateOf<String?>(null) }
    var publishMenu by remember { mutableStateOf(false) }
    var resourceMenu by remember { mutableStateOf(false) }
    var fileType by remember { mutableStateOf("*/*") }
    var exporting by remember { mutableStateOf<Transfer?>(null) }
    var storageBytes by remember { mutableLongStateOf(0) }
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    val exportPicker = androidx.activity.compose.rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        val transfer = exporting; exporting=null
        if(uri != null && transfer != null) app.action {
            val file=app.downloads.export(transfer)
            file.inputStream().use { input -> requireNotNull(context.contentResolver.openOutputStream(uri,"wt")).use { input.copyTo(it) } }
            app.status.value="文件已保存"
        }
    }
    val filesPicker = androidx.activity.compose.rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        uris.forEach { uri ->
            val persisted = runCatching { context.contentResolver.takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION) }.isSuccess
            app.publish(uri,false,copy || !persisted,groupId,note,privateRecipient)
        }; privateRecipient = null
    }
    val treePicker = androidx.activity.compose.rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if(uri != null) { val persisted = runCatching { context.contentResolver.takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION) }.isSuccess; app.publish(uri,true,copy || !persisted,groupId,note,privateRecipient) }; privateRecipient = null
    }
    val peers = remember(revision) { app.store.peers() }
    LaunchedEffect(requestedPeer) { if(requestedPeer != null) {peer=app.store.peer(requestedPeer);tab=2} }
    val publications = remember(revision) { app.store.publications().filter { it.recipient == null } }
    val groups = remember(revision) { app.store.groups() }
    val messages = remember(revision,peer) { app.store.messages().filter { it.request.senderDeviceId == peer?.hello?.deviceId || it.request.recipientDeviceId == peer?.hello?.deviceId } }
    val transfers = remember(revision) { app.store.transfers() }
    LaunchedEffect(tab,revision) {if(tab==4) storageBytes=withContext(Dispatchers.IO) {app.filesDir.walkTopDown().filter {it.isFile}.sumOf {it.length()}}}
    fun loadCatalog(p: Peer) { app.action { val data = app.client.get<Catalog>(p,"/api/v1/resource-catalog"); withContext(Dispatchers.Main) { catalog=data; peer=p; tab=0 } } }
    Scaffold(topBar={ Column(Modifier.padding(16.dp)) {
        Row(horizontalArrangement=Arrangement.SpaceBetween,modifier=Modifier.fillMaxWidth()) { Text("ResourceManager",style=MaterialTheme.typography.titleLarge); Text("0.1.0",style=MaterialTheme.typography.labelMedium) }
        Row { FilterChip(selected=sharing,onClick={ if(sharing) app.stopSharing() else app.startSharing() },label={ Text(if(sharing) "共享中 · 点击停止" else "启动共享") }); Spacer(Modifier.width(8.dp)); TextButton(onClick={ app.refresh() }) { Text("刷新设备") } }
        Text(status,style=MaterialTheme.typography.bodySmall,maxLines=2)
    } },bottomBar={ NavigationBar { listOf("设备","我的发布","聊天","传输","设置").forEachIndexed { i,title -> NavigationBarItem(selected=tab==i,onClick={tab=i},icon={Text(listOf("◉","↑","☏","↓","⚙")[i])},label={Text(title)}) } } }) { padding ->
        LazyColumn(Modifier.padding(padding).padding(horizontal=16.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
            if(incoming.isNotEmpty()) item { Card { Column(Modifier.padding(12.dp)) { Text("其他应用分享了 ${incoming.size} 个文件"); Text("确认后复制到本机发布区；不会自动公开。",style=MaterialTheme.typography.bodySmall)
                Row { Button(onClick={ incoming.forEach { app.publish(it,false,true,groupId,note) }; clearIncoming(); tab=1 }) { Text("确认发布") }; TextButton(onClick=clearIncoming) { Text("取消") } }
            } } }
            when(tab) {
                0 -> {
                    item { Row { OutlinedTextField(host,{host=it},label={Text("手动连接 IP[:端口]")},singleLine=true,modifier=Modifier.weight(1f)); TextButton(onClick={ val pieces=host.split(':'); val port=pieces.getOrNull(1)?.toIntOrNull() ?: PORT; app.connect(pieces[0],port) }) { Text("连接") } } }
                    items(peers,key={it.hello.deviceId}) { p -> Card { Column(Modifier.padding(12.dp)) {
                        Text(p.hello.nickname,style=MaterialTheme.typography.titleMedium); Text("${p.host}:${p.hello.port} · ${if(p.error == null && System.currentTimeMillis()-p.lastSeen < 120000) "最近在线" else "离线 / 待刷新"}")
                        p.error?.let { Text(it,style=MaterialTheme.typography.bodySmall) }
                        Row { TextButton(onClick={loadCatalog(p)}) { Text("浏览资源") }; TextButton(onClick={peer=p;tab=2}) { Text("聊天") } }
                    } } }
                    if(catalog != null) {
                        item { Text("${peer?.hello?.nickname} 的资源",style=MaterialTheme.typography.titleLarge); OutlinedTextField(query,{query=it},label={Text("检索名称或备注")},modifier=Modifier.fillMaxWidth()); Row { listOf("全部","文件","文件夹").forEach { k -> FilterChip(selected=kind==k,onClick={kind=k},label={Text(k)}) } }; OutlinedTextField(minSize,{minSize=it.filter(Char::isDigit)},label={Text("最小大小（MB，可留空）")},singleLine=true) }
                        items(catalog!!.resources.filter { (query.isBlank() || it.name.contains(query,true) || it.note.contains(query,true)) && (kind=="全部" || (it.kind=="File")== (kind=="文件")) && it.size >= (minSize.toLongOrNull() ?: 0)*1024*1024 },key={"remote:${it.id}"}) { r -> Card { Column(Modifier.padding(12.dp)) {
                            Text(r.name,style=MaterialTheme.typography.titleMedium); Text("${if(r.kind=="Folder") "文件夹" else "文件"} · ${size(r.size)} · ${if(r.available) "可用" else "源不可用"}"); if(r.note.isNotBlank()) Text(r.note)
                            var tree by remember(r.id) { mutableStateOf<List<RemoteFile>?>(null) }
                            Row { TextButton(enabled=r.available,onClick={ app.action { app.downloads.enqueue(peer!!,r) } }) { Text("下载") }; TextButton(onClick={app.action { val value=app.client.get<List<RemoteFile>>(peer!!,"/api/v1/resources/${r.id}/tree"); withContext(Dispatchers.Main) { tree=value } }}) { Text("查看目录") } }
                            tree?.take(200)?.forEach { Text((if(it.isDirectory) "▸ " else "· ")+it.relativePath+"  "+size(it.size),style=MaterialTheme.typography.bodySmall) }
                            if((tree?.size ?: 0)>200) Text("目录预览前 200 项；下载包含全部内容")
                        } } }
                    }
                }
                1 -> {
                    item { Text("发布文件与目录",style=MaterialTheme.typography.titleLarge)
                        OutlinedTextField(note,{note=it.take(2000)},label={Text("备注")},modifier=Modifier.fillMaxWidth(),minLines=1,maxLines=5)
                        Row { Checkbox(copy,{copy=it}); Text("复制到应用内（取消则引用原文件）") }
                        Text("文件类型"); Row { listOf("全部" to "*/*","图片" to "image/*","视频" to "video/*","音频" to "audio/*").forEach { (label,mime) -> FilterChip(selected=fileType==mime,onClick={fileType=mime},label={Text(label)}) } }
                        Text("分组"); groups.forEach { g -> FilterChip(selected=groupId==g.group.id,onClick={groupId=g.group.id},label={Text(g.group.name)}) }
                        Box { Button(onClick={publishMenu=true}) {Text("发布 ▾")}; DropdownMenu(expanded=publishMenu,onDismissRequest={publishMenu=false}) { DropdownMenuItem(text={Text("选择文件（可多选）")},onClick={publishMenu=false; filesPicker.launch(arrayOf(fileType))}); DropdownMenuItem(text={Text("选择文件夹")},onClick={publishMenu=false;treePicker.launch(null)}) } }
                        Row { OutlinedTextField(groupName,{groupName=it.take(80)},label={Text("新建分组")},modifier=Modifier.weight(1f)); TextButton(enabled=groupName.isNotBlank(),onClick={app.store.save(LocalGroup(Group(UUID.randomUUID().toString(),groupName,createdUtc=Instant.now().toString())));groupName="";app.changed()}) {Text("添加")} }
                        groups.find { it.group.id==groupId }?.let { g ->
                            Text("分组访问权限"); Row { listOf("Public" to "公开","Private" to "仅自己","AllowList" to "白名单").forEach { (value,label) -> FilterChip(selected=g.access==value,onClick={app.store.save(g.copy(access=value));app.changed()},label={Text(label)}) } }
                            if(g.access=="AllowList") peers.forEach { p -> Row { Checkbox(p.hello.deviceId in g.devices,{ checked -> app.store.save(g.copy(devices=if(checked) (g.devices+p.hello.deviceId).distinct() else g.devices-p.hello.deviceId));app.changed() }); Text(p.hello.nickname) } }
                        }
                    }
                    item { SourceBrowser(app) { uri,folder -> app.publish(uri,folder,copy,groupId,note) } }
                    items(publications,key={it.id}) { p -> Card { Column(Modifier.padding(12.dp)) {
                        Text(p.name,style=MaterialTheme.typography.titleMedium); Text("${if(p.mode=="Copy") "应用内副本" else "引用原文件"} · ${p.note}")
                        Row { TextButton(onClick={app.store.remove("publication",p.id);app.changed()}) { Text("撤销发布") }; TextButton(onClick={sharePublication(p)}) {Text("分享")}; if(peer != null) TextButton(onClick={app.action { app.remind(peer!!,app.files.describe(p)) }}) { Text("提醒 ${peer!!.hello.nickname}") } }
                    } } }
                }
                2 -> {
                    item { Text("聊天",style=MaterialTheme.typography.titleLarge); peers.forEach { p -> FilterChip(selected=peer?.hello?.deviceId==p.hello.deviceId,onClick={peer=p},label={Text(p.hello.nickname)}) }; if(peer==null) Text("先连接并选择一台设备") }
                    items(messages,key={"${it.outgoing}:${it.request.messageId}"}) { m -> Card { Column(Modifier.padding(12.dp)) {
                        Text(if(m.outgoing) "我 → ${peer?.hello?.nickname}" else "${peer?.hello?.nickname} → 我",style=MaterialTheme.typography.labelMedium)
                        Text(m.request.text ?: "${if(m.request.kind=="PrivateResource") "私发资源" else "资源"}：${m.request.resourceId}")
                        Text(when(m.state) {"Delivered"->"对方已接收";"Received"->"已收到";"Queued"->"等待送达";"Sending"->"发送中";"Failed"->"失败";else->m.state},style=MaterialTheme.typography.bodySmall)
                        m.error?.let {Text(it,style=MaterialTheme.typography.bodySmall)}
                        if(m.outgoing && m.state=="Queued") TextButton(onClick={if(app.store.transition(m,"Queued","Canceled") && m.request.kind=="PrivateResource") m.request.resourceId?.let {app.store.remove("publication",it)};app.changed()}) {Text("取消发送")}
                        if(m.outgoing && m.state=="Failed") TextButton(onClick={app.store.transition(m,"Failed","Queued");app.changed()}) {Text("重试")}
                        if(!m.outgoing && m.request.resourceId != null) TextButton(onClick={app.action { val private=m.request.kind=="PrivateResource"; val prefix=if(private) "/api/v1/chat/resources/" else "/api/v1/resources/"; val r=app.client.get<Resource>(peer!!,prefix+m.request.resourceId,private);app.downloads.enqueue(peer!!,r,private) }}) {Text("下载资源")}
                    } } }
                    if(peer!=null) item { OutlinedTextField(chat,{chat=it.take(2000)},label={Text("输入消息")},modifier=Modifier.fillMaxWidth(),minLines=1,maxLines=5)
                        Row { Button(enabled=chat.isNotBlank(),onClick={app.enqueue(peer!!.hello.deviceId,"Text",chat);chat=""}) {Text("发送")}; TextButton(onClick={privateRecipient=peer!!.hello.deviceId;filesPicker.launch(arrayOf("*/*"))}) {Text("私发文件")}; TextButton(onClick={privateRecipient=peer!!.hello.deviceId;treePicker.launch(null)}) {Text("私发目录")} }
                        Box { TextButton(onClick={resourceMenu=true}) {Text("发送已发布资源")}; DropdownMenu(expanded=resourceMenu,onDismissRequest={resourceMenu=false}) { publications.filter {app.store.allowed(it,peer!!.hello.deviceId)}.forEach { p -> DropdownMenuItem(text={Text(p.name)},onClick={app.enqueue(peer!!.hello.deviceId,"Resource",resourceId=p.id);resourceMenu=false}) } } }
                        Text("发送与接收需要启动共享。排队消息在设备恢复连接后重试。",style=MaterialTheme.typography.bodySmall)
                    }
                }
                3 -> {
                    item { Text("传输任务",style=MaterialTheme.typography.titleLarge); Text("文件先存入应用内，完成后可通过系统分享保存或发送。") }
                    items(transfers,key={it.id}) { t -> Card { Column(Modifier.padding(12.dp)) {
                        Text(t.resource.name,style=MaterialTheme.typography.titleMedium); Text("${when(t.state) {"Queued"->"等待下载";"Running"->"下载中";"Paused"->"已暂停";"Completed"->"已完成";"Failed"->"失败";else->t.state}} · ${size(t.bytes)} / ${size(t.total)}"); t.error?.let {Text(it)}
                        if(t.total>0) LinearProgressIndicator(progress={(t.bytes.toFloat()/t.total).coerceIn(0f,1f)},modifier=Modifier.fillMaxWidth())
                        Row { if(t.state=="Running") TextButton(onClick={app.downloads.pause(t.id)}) {Text("暂停")} else if(t.state!="Completed") TextButton(onClick={app.action {app.downloads.resume(t)}}) {Text("继续")}; if(t.state=="Completed") { TextButton(onClick={share(t)}) {Text("分享")}; TextButton(onClick={exporting=t;exportPicker.launch(t.resource.name+if(t.resource.kind=="Folder") ".zip" else "")}) {Text("保存到…")} } }
                    } } }
                }
                4 -> item { Text("设置与诊断",style=MaterialTheme.typography.titleLarge); OutlinedTextField(nickname,{nickname=it.take(80)},label={Text("本机昵称")}); Button(enabled=nickname.isNotBlank(),onClick={app.nickname=nickname}) {Text("保存昵称")}
                    Text("设备 ID：${app.identity.deviceId}"); Text("HTTP $PORT · UDP $DISCOVERY_PORT"); Text("最低 Android 10；局域网点对点传输。共享服务运行时显示常驻通知，停止共享后不会保持在线。"); Text("协议验证身份但不加密局域网 HTTP 内容，请在可信网络使用。"); Text("系统限制、网络切换或省电策略可能中断共享；离线设备和任务记录会保留。"); Text("当前不支持服务器中继、推送唤醒或 APK 差分更新。")
                    Button(onClick={context.startActivity(Intent(android.provider.Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(android.provider.Settings.EXTRA_APP_PACKAGE,context.packageName))}) {Text("通知设置")}
                    Text("应用内文件占用：${size(storageBytes)}")
                    Text("可清理已撤销发布或取消私发后留下的副本；原文件与传输内容保留。",style=MaterialTheme.typography.bodySmall)
                    Button(onClick={app.action {val released=app.files.cleanUnusedCopies {app.store.publications().map {it.id}.toSet()};app.status.value="已清理 ${size(released)} 无引用副本"}}) {Text("清理无引用副本")}
                }
            }
            item { Spacer(Modifier.height(20.dp)) }
        }
    }
}
private fun size(bytes: Long) = when { bytes >= 1024*1024*1024 -> "%.1f GB".format(bytes.toDouble()/(1024*1024*1024)); bytes >= 1024*1024 -> "%.1f MB".format(bytes.toDouble()/(1024*1024)); bytes>=1024 -> "%.1f KB".format(bytes.toDouble()/1024); else -> "$bytes B" }
