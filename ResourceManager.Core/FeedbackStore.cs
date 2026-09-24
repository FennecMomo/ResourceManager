using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ResourceManager.Core;

public sealed class FeedbackStore
{
    private readonly object gate = new();
    private readonly string connectionString;
    public string DataDirectory { get; }
    public string DraftDirectory => Path.Combine(DataDirectory, "feedback-drafts");

    public FeedbackStore(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? NodeDefaults.DataDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(DraftDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDirectory, "feedback.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS draft(
                singleton INTEGER PRIMARY KEY CHECK(singleton=1),category TEXT NOT NULL,title TEXT NOT NULL,
                body TEXT NOT NULL,updated_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS draft_attachments(
                file_name TEXT NOT NULL,staged_path TEXT PRIMARY KEY,size INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS history(
                submission_id TEXT PRIMARY KEY,target_kind TEXT NOT NULL,target_name TEXT NOT NULL,
                category TEXT NOT NULL,title TEXT NOT NULL,body TEXT NOT NULL,attachment_names TEXT NOT NULL,
                remote_issue_id TEXT,remote_url TEXT,status TEXT NOT NULL,created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,error TEXT);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private static SqliteCommand Cmd(SqliteConnection db, string sql, params object?[] args)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i += 2)
            command.Parameters.AddWithValue((string)args[i]!, args[i + 1] ?? DBNull.Value);
        return command;
    }

    public FeedbackDraft GetDraft()
    {
        lock (gate)
        {
            using var db = Open();
            using var draft = Cmd(db, "SELECT category,title,body,updated_utc FROM draft WHERE singleton=1");
            using var reader = draft.ExecuteReader();
            if (!reader.Read()) return new FeedbackDraft(FeedbackCategory.Problem, "", "", [], DateTimeOffset.UtcNow);
            var category = Enum.Parse<FeedbackCategory>(reader.GetString(0));
            var title = reader.GetString(1);
            var body = reader.GetString(2);
            var updated = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
            reader.Close();
            var attachments = new List<FeedbackAttachmentDraft>();
            using var files = Cmd(db, "SELECT file_name,staged_path,size FROM draft_attachments ORDER BY rowid");
            using var fileReader = files.ExecuteReader();
            while (fileReader.Read()) attachments.Add(new FeedbackAttachmentDraft(fileReader.GetString(0), fileReader.GetString(1), fileReader.GetInt64(2)));
            return new FeedbackDraft(category, title, body, attachments, updated);
        }
    }

    public void SaveDraft(FeedbackDraft draft)
    {
        lock (gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            using (var command = Cmd(db, """
                INSERT INTO draft(singleton,category,title,body,updated_utc) VALUES(1,$category,$title,$body,$updated)
                ON CONFLICT(singleton) DO UPDATE SET category=excluded.category,title=excluded.title,
                body=excluded.body,updated_utc=excluded.updated_utc
                """, "$category", draft.Category.ToString(), "$title", draft.Title, "$body", draft.Body,
                "$updated", draft.UpdatedUtc.ToString("O"))) command.ExecuteNonQuery();
            using (var clear = Cmd(db, "DELETE FROM draft_attachments")) clear.ExecuteNonQuery();
            foreach (var attachment in draft.Attachments)
            {
                using var insert = Cmd(db, "INSERT INTO draft_attachments(file_name,staged_path,size) VALUES($name,$path,$size)",
                    "$name", attachment.FileName, "$path", attachment.StagedPath, "$size", attachment.Size);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public void ClearDraft()
    {
        var draft = GetDraft();
        lock (gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            using (var files = Cmd(db, "DELETE FROM draft_attachments")) files.ExecuteNonQuery();
            using (var item = Cmd(db, "DELETE FROM draft")) item.ExecuteNonQuery();
            transaction.Commit();
        }
        foreach (var attachment in draft.Attachments)
            try { File.Delete(attachment.StagedPath); } catch { }
    }

    public void SaveHistory(FeedbackHistoryItem item)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                INSERT INTO history(submission_id,target_kind,target_name,category,title,body,attachment_names,
                    remote_issue_id,remote_url,status,created_utc,updated_utc,error)
                VALUES($id,$kind,$target,$category,$title,$body,$attachments,$remote,$url,$status,$created,$updated,$error)
                ON CONFLICT(submission_id) DO UPDATE SET remote_issue_id=excluded.remote_issue_id,
                    remote_url=excluded.remote_url,status=excluded.status,updated_utc=excluded.updated_utc,error=excluded.error
                """, "$id", item.ClientSubmissionId, "$kind", item.TargetKind.ToString(), "$target", item.TargetName,
                "$category", item.Category.ToString(), "$title", item.Title, "$body", item.Body,
                "$attachments", item.AttachmentNames, "$remote", item.RemoteIssueId, "$url", item.RemoteUrl,
                "$status", item.Status, "$created", item.CreatedUtc.ToString("O"), "$updated", item.UpdatedUtc.ToString("O"),
                "$error", item.Error);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<FeedbackHistoryItem> GetHistory()
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                SELECT submission_id,target_kind,target_name,category,title,body,attachment_names,remote_issue_id,
                    remote_url,status,created_utc,updated_utc,error FROM history ORDER BY created_utc DESC
                """);
            using var reader = command.ExecuteReader();
            var result = new List<FeedbackHistoryItem>();
            while (reader.Read()) result.Add(new FeedbackHistoryItem(reader.GetString(0), Enum.Parse<FeedbackTargetKind>(reader.GetString(1)),
                reader.GetString(2), Enum.Parse<FeedbackCategory>(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9), DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture), reader.IsDBNull(12) ? null : reader.GetString(12)));
            return result;
        }
    }

    public FeedbackHistoryItem? GetHistory(string submissionId) => GetHistory().FirstOrDefault(item => item.ClientSubmissionId == submissionId);

    public void DeleteHistory(string submissionId)
    {
        var item = GetHistory(submissionId) ?? throw new InvalidOperationException("反馈记录不存在。");
        if (!FeedbackRules.IsHistoryDeletable(item.Status)) throw new InvalidOperationException("反馈完成或由用户关闭后才能删除本地记录。");
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "DELETE FROM history WHERE submission_id=$id", "$id", submissionId);
            command.ExecuteNonQuery();
        }
    }

    public string? GetSetting(string key)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT value FROM settings WHERE key=$key", "$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string? value)
    {
        lock (gate)
        {
            using var db = Open();
            if (value is null)
            {
                using var remove = Cmd(db, "DELETE FROM settings WHERE key=$key", "$key", key);
                remove.ExecuteNonQuery();
            }
            else
            {
                using var save = Cmd(db, "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                    "$key", key, "$value", value);
                save.ExecuteNonQuery();
            }
        }
    }
}
