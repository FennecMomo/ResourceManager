package dev.resourcemanager.android

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import dev.resourcemanager.protocol.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

@Composable
internal fun ResourcePage(
    modifier: Modifier = Modifier,
    app: RmApplication,
    peer: Peer?,
    search: String,
    onSearch: (String) -> Unit,
) {
    var filter by rememberSaveable(peer?.hello?.deviceId) { mutableStateOf("全部") }
    var minMB by rememberSaveable(peer?.hello?.deviceId) { mutableStateOf("") }
    var filters by rememberSaveable { mutableStateOf(false) }
    var error by remember(peer?.hello?.deviceId) { mutableStateOf<String?>(null) }
    var reload by remember { mutableIntStateOf(0) }
    val catalog by
        produceState<Catalog?>(null, peer?.hello?.deviceId, reload) {
            value = null
            error = null
            if (peer != null)
                value =
                    withContext(Dispatchers.IO) {
                        try {
                            app.client.connectAsync(peer.host, peer.hello.port)
                            app.client.networkIO {
                                app.client.get<Catalog>(peer, "/api/v1/resource-catalog")
                            }
                        } catch (e: Exception) {
                            if (e is kotlinx.coroutines.CancellationException) throw e
                            error = e.message
                            null
                        }
                    }
        }
    LazyColumn(
        modifier.fillMaxSize().padding(horizontal = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
        contentPadding = PaddingValues(top = 8.dp, bottom = 24.dp),
    ) {
        item {
            OutlinedTextField(
                search,
                onSearch,
                placeholder = { Text("搜索名称或备注") },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
            )
            TextButton(onClick = { filters = !filters }) {
                Text(if (filters) "收起筛选" else "类型与大小筛选")
            }
            if (filters) {
                Row(Modifier.horizontalScroll(rememberScrollState())) {
                    listOf("全部", "文件", "文件夹").forEach { label ->
                        FilterChip(
                            selected = filter == label,
                            onClick = { filter = label },
                            label = { Text(label) },
                        )
                    }
                }
                OutlinedTextField(
                    minMB,
                    { minMB = it.filter { c -> c.isDigit() || c == '.' } },
                    label = { Text("最小大小（MB）") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            if (catalog == null && error == null) LinearProgressIndicator(Modifier.fillMaxWidth())
            error?.let {
                Text(it, color = MaterialTheme.colorScheme.error)
                TextButton(onClick = { reload++ }) { Text("重试") }
            }
            if (catalog?.resources?.isEmpty() == true) Empty("没有可见资源", "对方尚未发布，或者尚未向你开放访问。")
        }
        val resources =
            catalog?.resources.orEmpty().filter {
                (it.name.contains(search, true) || it.note.contains(search, true)) &&
                    (filter == "全部" || (if (it.kind == "Folder") "文件夹" else "文件") == filter) &&
                    it.size >= (minMB.toDoubleOrNull() ?: 0.0) * 1024 * 1024
            }
        items(resources, key = { it.id }) { r ->
            Card {
                Column(
                    Modifier.fillMaxWidth().padding(16.dp),
                    verticalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    Text(r.name, style = MaterialTheme.typography.titleMedium)
                    Text(
                        "${if(r.kind=="Folder") "文件夹" else "文件"} · ${size(r.size)}",
                        style = MaterialTheme.typography.bodySmall,
                    )
                    if (r.note.isNotBlank()) Text(r.note)
                    var tree by
                        remember(peer?.hello?.deviceId, r.id) {
                            mutableStateOf<List<RemoteFile>?>(null)
                        }
                    Row {
                        TextButton(
                            enabled = r.available,
                            onClick = { app.action { app.downloads.enqueue(peer!!, r) } },
                        ) {
                            Text("下载")
                        }
                        TextButton(
                            onClick = {
                                app.action {
                                    val result =
                                        app.client.get<List<RemoteFile>>(
                                            peer!!,
                                            "/api/v1/resources/${r.id}/tree",
                                        )
                                    withContext(Dispatchers.Main) { tree = result }
                                }
                            }
                        ) {
                            Text("目录")
                        }
                    }
                    tree?.take(100)?.forEach {
                        Text(
                            it.relativePath.ifBlank { r.name },
                            style = MaterialTheme.typography.bodySmall,
                        )
                    }
                    if ((tree?.size ?: 0) > 100) Text("预览前 100 项，下载包含全部内容")
                }
            }
        }
    }
}
