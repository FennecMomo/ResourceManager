using System.Text.Json;
namespace ResourceManager.Core;
public sealed partial class NodeStore
{
    public ChatProgressReceipt? GetLocalChatProgress(string peerId, string messageId)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                SELECT m.seq<=c.read_through,m.kind,p.state,p.done,p.total
                FROM chat_messages m JOIN chat_conversations c ON c.peer_id=m.peer_id
                LEFT JOIN chat_download_progress p ON p.peer_id=m.peer_id AND p.message_id=m.message_id
                WHERE m.peer_id=$peer AND m.message_id=$msg AND m.outgoing=0
                """, "$peer", peerId, "$msg", messageId);
            using var r = command.ExecuteReader();
            if (!r.Read()) return null;
            return new(messageId, r.GetBoolean(0), r.GetString(1) == "Text" ? null : r.IsDBNull(2) ? "未开始" : r.GetString(2),
                r.IsDBNull(3) ? 0 : r.GetInt64(3), r.IsDBNull(4) ? 0 : r.GetInt64(4), DateTimeOffset.UtcNow);
        }
    }

    public ChatProgressReceipt? GetCachedChatProgress(string peerId, string messageId)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT json FROM chat_progress_cache WHERE peer_id=$peer AND message_id=$msg", "$peer", peerId, "$msg", messageId);
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ChatProgressReceipt>(json) : null;
        }
    }

    public bool SaveChatProgress(string peerId, ChatProgressReceipt receipt)
    {
        if (receipt.DownloadedBytes < 0 || receipt.TotalBytes < 0 || receipt.DownloadedBytes > receipt.TotalBytes ||
            receipt.DownloadState is not (null or "未开始" or "等待下载" or "下载中" or "已暂停" or "已中断" or "已完成")) return false;
        lock (gate)
        {
            var message = GetChatMessage(peerId, receipt.MessageId, true);
            if (message?.State != "Delivered") return false;
            var previous = GetCachedChatProgress(peerId, receipt.MessageId);
            // A later poll must not undo an already observed read acknowledgement.
            receipt = receipt with { Read = receipt.Read || previous?.Read == true, ObservedUtc = DateTimeOffset.UtcNow };
            using var db = Open();
            using var command = Cmd(db, """
                INSERT INTO chat_progress_cache(peer_id,message_id,json) VALUES($peer,$msg,$json)
                ON CONFLICT(peer_id,message_id) DO UPDATE SET json=excluded.json
                """, "$peer", peerId, "$msg", receipt.MessageId, "$json", JsonSerializer.Serialize(receipt));
            command.ExecuteNonQuery();
            return true;
        }
    }
}
