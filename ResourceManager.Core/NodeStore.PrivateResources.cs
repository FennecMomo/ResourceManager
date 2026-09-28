using System.Globalization;

namespace ResourceManager.Core;

public sealed partial class NodeStore
{
    // The random 256-bit resource ID is a bearer capability, sent only in the private message.
    // Public catalog, tree, content and update routes never resolve private resources.
    public LocalResource? GetPrivateResource(string id, string peerId)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                SELECT id,name,kind,mode,source_path,published_utc,note FROM private_resources
                WHERE id=$id AND private_peer=$peer
                  AND EXISTS (SELECT 1 FROM peers WHERE device_id=$peer)
                  AND EXISTS (SELECT 1 FROM chat_messages WHERE peer_id=$peer AND resource_id=$id
                    AND outgoing=1 AND kind='PrivateResource' AND state<>'Canceled')
                """, "$id", id, "$peer", peerId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new LocalResource(reader.GetString(0), reader.GetString(1),
                Enum.Parse<ResourceKind>(reader.GetString(2)), Enum.Parse<PublishMode>(reader.GetString(3)),
                reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                reader.GetString(6)) : null;
        }
    }

    public void RemovePrivateResources(string peerId)
    {
        foreach (var resource in GetResources(peerId)) RemoveResource(resource.Id, peerId);
    }
}
