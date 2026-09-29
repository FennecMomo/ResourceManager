package dev.resourcemanager.protocol

object SafePath {
    fun segments(path: String): List<String> {
        if(path.isEmpty()) return emptyList()
        require(path.length <= 4096 && !path.startsWith('/') && '\\' !in path)
        return path.split('/').also { list -> require(list.all { it.isNotEmpty() && it != "." && it != ".." && !it.equals(".git",true) && ':' !in it && it.none { c -> c.code < 32 } }) }
    }
}
