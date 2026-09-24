using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ResourceManager.Core;

public sealed partial class NodeStore
{
    public void SavePeerCapabilities(string deviceId, IEnumerable<string>? capabilities)
    {
        var json = JsonSerializer.Serialize((capabilities ?? []).Where(value => value is { Length: > 0 and <= 80 })
            .Distinct(StringComparer.Ordinal).Take(32).ToArray());
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "INSERT INTO peer_capabilities(device_id,capabilities) VALUES($id,$value) ON CONFLICT(device_id) DO UPDATE SET capabilities=excluded.capabilities",
                "$id", deviceId, "$value", json);
            command.ExecuteNonQuery();
        }
    }

    public string[] GetPeerCapabilities(string deviceId)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT capabilities FROM peer_capabilities WHERE device_id=$id", "$id", deviceId);
            var json = command.ExecuteScalar() as string;
            return json is null ? [] : JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
    }

    public IReadOnlyList<ChatConversation> GetChatConversations()
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                SELECT c.peer_id,c.nickname,c.muted_until,c.removed,
                  (SELECT COUNT(*) FROM chat_messages m WHERE m.peer_id=c.peer_id AND m.outgoing=0 AND m.seq>c.read_through),
                  (SELECT MAX(sent_utc) FROM chat_messages m WHERE m.peer_id=c.peer_id)
                FROM chat_conversations c ORDER BY (SELECT MAX(seq) FROM chat_messages m WHERE m.peer_id=c.peer_id) DESC,
                  c.nickname COLLATE NOCASE
                """);
            using var reader = command.ExecuteReader();
            var result = new List<ChatConversation>();
            while (reader.Read())
                result.Add(new ChatConversation(reader.GetString(0), reader.GetString(1), reader.GetInt64(4),
                    reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    reader.GetInt64(3) != 0,
                    reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
            return result;
        }
    }

    public void EnsureChatConversation(string peerId, string nickname)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                INSERT INTO chat_conversations(peer_id,nickname) VALUES($id,$name)
                ON CONFLICT(peer_id) DO UPDATE SET nickname=excluded.nickname,removed=0
                """, "$id", peerId, "$name", nickname);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ChatMessage> GetChatMessages(string peerId)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                SELECT message_id,peer_id,outgoing,kind,text,resource_id,resource_name,sent_utc,received_utc,state,
                  next_attempt_utc,attempts,error FROM chat_messages WHERE peer_id=$id ORDER BY sent_utc,seq
                """, "$id", peerId);
            using var reader = command.ExecuteReader();
            var result = new List<ChatMessage>();
            while (reader.Read()) result.Add(ReadChatMessage(reader));
            return result;
        }
    }

    public ChatMessage? GetChatMessage(string peerId, string messageId, bool outgoing)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                SELECT message_id,peer_id,outgoing,kind,text,resource_id,resource_name,sent_utc,received_utc,state,
                  next_attempt_utc,attempts,error FROM chat_messages WHERE peer_id=$peer AND message_id=$id AND outgoing=$out
                """, "$peer", peerId, "$id", messageId, "$out", outgoing ? 1 : 0);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadChatMessage(reader) : null;
        }
    }

    private static ChatMessage ReadChatMessage(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
        reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
        reader.GetString(9), reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
        reader.GetInt32(11), reader.IsDBNull(12) ? null : reader.GetString(12));

    public ChatMessage QueueChatMessage(string peerId, string kind, string? text, string? resourceId,
        string? resourceName, DateTimeOffset now)
    {
        var peer = GetPeer(peerId) ?? throw new InvalidOperationException("设备已移除。");
        if (!GetPeerCapabilities(peerId).Contains(NodeDefaults.ChatCapability, StringComparer.Ordinal))
            throw new InvalidOperationException("对方版本不支持聊天。");
        var id = Guid.NewGuid().ToString("N");
        lock (gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            using (var count = Cmd(db, "SELECT COUNT(*) FROM chat_messages WHERE peer_id=$id AND outgoing=1 AND state IN ('Queued','Sending')", "$id", peerId))
            {
                count.Transaction = transaction;
                if ((long)count.ExecuteScalar()! >= 500) throw new InvalidOperationException("该会话待发消息已达 500 条，请稍后重试。");
            }
            using (var convo = Cmd(db, """
                       INSERT INTO chat_conversations(peer_id,nickname) VALUES($id,$name)
                       ON CONFLICT(peer_id) DO UPDATE SET nickname=excluded.nickname,removed=0
                       """, "$id", peerId, "$name", peer.Nickname))
            {
                convo.Transaction = transaction;
                convo.ExecuteNonQuery();
            }
            using (var insert = Cmd(db, """
                       INSERT INTO chat_messages(message_id,peer_id,outgoing,kind,text,resource_id,resource_name,
                         sent_utc,state,next_attempt_utc,auto_expire_utc) VALUES($msg,$peer,1,$kind,$text,$resource,$name,$sent,'Queued',$next,$expire)
                       """, "$msg", id, "$peer", peerId, "$kind", kind, "$text", text, "$resource", resourceId,
                       "$name", resourceName, "$sent", now.ToString("O"), "$next", now.ToString("O"),
                       "$expire", now.AddDays(7).ToString("O")))
            {
                insert.Transaction = transaction;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        return GetChatMessage(peerId, id, true)!;
    }

    // INSERT OR IGNORE and its receipt are in one transaction: an ACK lost in transit is safe to retry.
    public (bool Inserted, DateTimeOffset ReceivedUtc) AcceptChatMessage(ChatMessageRequest request,
        string nickname, string? resourceName, DateTimeOffset now)
    {
        lock (gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            using (var convo = Cmd(db, """
                       INSERT INTO chat_conversations(peer_id,nickname) VALUES($id,$name)
                       ON CONFLICT(peer_id) DO UPDATE SET nickname=excluded.nickname,removed=0
                       """, "$id", request.SenderDeviceId, "$name", nickname))
            {
                convo.Transaction = transaction;
                convo.ExecuteNonQuery();
            }
            using var insert = Cmd(db, """
                INSERT OR IGNORE INTO chat_messages(message_id,peer_id,outgoing,kind,text,resource_id,resource_name,
                  sent_utc,received_utc,state) VALUES($msg,$peer,0,$kind,$text,$resource,$name,$sent,$received,'Received')
                """, "$msg", request.MessageId, "$peer", request.SenderDeviceId, "$kind", request.Kind,
                "$text", request.Text, "$resource", request.ResourceId, "$name", resourceName,
                "$sent", request.SentUtc.ToString("O"), "$received", now.ToString("O"));
            insert.Transaction = transaction;
            var inserted = insert.ExecuteNonQuery() == 1;
            using var receipt = Cmd(db, "SELECT received_utc FROM chat_messages WHERE peer_id=$peer AND message_id=$msg AND outgoing=0",
                "$peer", request.SenderDeviceId, "$msg", request.MessageId);
            receipt.Transaction = transaction;
            var received = DateTimeOffset.Parse((string)receipt.ExecuteScalar()!, CultureInfo.InvariantCulture);
            transaction.Commit();
            return (inserted, received);
        }
    }

    public void MarkChatRead(string peerId)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                UPDATE chat_conversations SET read_through=COALESCE(
                  (SELECT MAX(seq) FROM chat_messages WHERE peer_id=$id AND outgoing=0),read_through)
                WHERE peer_id=$id
                """, "$id", peerId);
            command.ExecuteNonQuery();
        }
    }

    public void SetChatMutedUntil(string peerId, DateTimeOffset? until)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "UPDATE chat_conversations SET muted_until=$until WHERE peer_id=$id",
                "$until", until?.ToString("O"), "$id", peerId);
            command.ExecuteNonQuery();
        }
    }

    public void ClearChatConversation(string peerId)
    {
        lock (gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            using (var delete = Cmd(db, "DELETE FROM chat_messages WHERE peer_id=$id", "$id", peerId))
            {
                delete.Transaction = transaction;
                delete.ExecuteNonQuery();
            }
            using (var update = Cmd(db, "UPDATE chat_conversations SET read_through=0 WHERE peer_id=$id", "$id", peerId))
            {
                update.Transaction = transaction;
                update.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public IReadOnlyList<ChatMessage> GetDueChatMessages(DateTimeOffset now)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                SELECT m.message_id,m.peer_id,m.outgoing,m.kind,m.text,m.resource_id,m.resource_name,m.sent_utc,
                  m.received_utc,m.state,m.next_attempt_utc,m.attempts,m.error
                FROM chat_messages m JOIN chat_conversations c ON c.peer_id=m.peer_id
                WHERE m.outgoing=1 AND m.state='Queued' AND c.removed=0 AND m.next_attempt_utc<=$now
                  AND NOT EXISTS (SELECT 1 FROM chat_messages prior WHERE prior.peer_id=m.peer_id AND prior.outgoing=1
                    AND prior.seq<m.seq AND prior.state IN ('Queued','Sending'))
                ORDER BY m.seq
                """, "$now", now.ToString("O"));
            using var reader = command.ExecuteReader();
            var result = new List<ChatMessage>();
            while (reader.Read()) result.Add(ReadChatMessage(reader));
            return result;
        }
    }

    public void UpdateChatState(string peerId, string messageId, string state, DateTimeOffset? nextAttempt = null,
        string? error = null, DateTimeOffset? received = null, bool incrementAttempt = false)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                UPDATE chat_messages SET state=$state,next_attempt_utc=$next,error=$error,
                  received_utc=COALESCE($received,received_utc),attempts=attempts+$increment
                WHERE peer_id=$peer AND message_id=$msg AND outgoing=1
                """, "$state", state, "$next", nextAttempt?.ToString("O"), "$error", error,
                "$received", received?.ToString("O"), "$increment", incrementAttempt ? 1 : 0,
                "$peer", peerId, "$msg", messageId);
            command.ExecuteNonQuery();
        }
    }

    public DateTimeOffset GetChatAutoExpiry(string peerId, string messageId)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT COALESCE(auto_expire_utc,datetime(sent_utc,'+7 days')) FROM chat_messages WHERE peer_id=$peer AND message_id=$msg AND outgoing=1",
                "$peer", peerId, "$msg", messageId);
            return DateTimeOffset.Parse((string)command.ExecuteScalar()!, CultureInfo.InvariantCulture);
        }
    }

    public void RetryChatMessage(string peerId, string messageId, DateTimeOffset now)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "UPDATE chat_messages SET state='Queued',next_attempt_utc=$now,auto_expire_utc=$expire,error=NULL,attempts=0 WHERE peer_id=$peer AND message_id=$msg AND outgoing=1 AND state='Failed'",
                "$now", now.ToString("O"), "$expire", now.AddDays(7).ToString("O"), "$peer", peerId,
                "$msg", messageId);
            command.ExecuteNonQuery();
        }
    }

    public void RecoverSendingChatMessages()
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "UPDATE chat_messages SET state='Queued',next_attempt_utc=$now WHERE outgoing=1 AND state='Sending'",
                "$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public void WakeChatMessages(string peerId)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "UPDATE chat_messages SET next_attempt_utc=$now WHERE peer_id=$id AND outgoing=1 AND state='Queued'",
                "$now", DateTimeOffset.UtcNow.ToString("O"), "$id", peerId);
            command.ExecuteNonQuery();
        }
    }
}
