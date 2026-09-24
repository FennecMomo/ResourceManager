using System.Globalization;
using Microsoft.Data.Sqlite;
using ResourceManager.Core;

namespace ResourceManager.Server;

public sealed record FeedbackSubmitter(string ClientId, string UserName);
public sealed record StagedServerAttachment(string Id, string FileName, string TempPath, long Size, string Sha256, bool IsImage);
public sealed record StoredAttachment(string Id, string IssueId, string FileName, string StoredPath, long Size, string Sha256, bool IsImage);
public sealed record ServerLocalIssue(string Id, string SubmissionId, string ClientId, string Nickname, FeedbackCategory Category,
    string Title, string Body, string UserName, string AppVersion, string OsDescription, string OsArchitecture,
    string ProcessArchitecture, string? RejectionReason, LocalIssueStatus Status, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc,
    IReadOnlyList<StoredAttachment> Attachments);
public sealed record CachedGitHubIssue(int Number, string Title, string Body, string State, string? StateReason,
    string HtmlUrl, string Author, FeedbackCategory? Category, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc,
    IReadOnlyList<string> Labels);

public sealed class ServerStore
{
    private readonly object gate = new();
    private readonly string connectionString;
    public string DataDirectory { get; }
    public string AttachmentDirectory => Path.Combine(DataDirectory, "attachments");
    public string TemporaryDirectory => Path.Combine(DataDirectory, "temp");
    public string ServerId { get; }
    public string ServerName { get; }

    public ServerStore(string dataDirectory, string? serverName)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(AttachmentDirectory);
        Directory.CreateDirectory(TemporaryDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDirectory, "server.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 10,
            Pooling = false
        }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS issues(
                id TEXT PRIMARY KEY,submission_id TEXT NOT NULL,client_id TEXT NOT NULL,nickname TEXT NOT NULL,
                category TEXT NOT NULL,title TEXT NOT NULL,body TEXT NOT NULL,user_name TEXT NOT NULL,
                app_version TEXT NOT NULL,os_description TEXT NOT NULL,os_architecture TEXT NOT NULL,
                process_architecture TEXT NOT NULL,rejection_reason TEXT,status TEXT NOT NULL,
                created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL,
                UNIQUE(client_id,submission_id));
            CREATE TABLE IF NOT EXISTS attachments(
                id TEXT PRIMARY KEY,issue_id TEXT NOT NULL,file_name TEXT NOT NULL,stored_path TEXT NOT NULL,
                size INTEGER NOT NULL,sha256 TEXT NOT NULL,is_image INTEGER NOT NULL,
                FOREIGN KEY(issue_id) REFERENCES issues(id));
            CREATE TABLE IF NOT EXISTS github_issues(
                number INTEGER PRIMARY KEY,title TEXT NOT NULL,body TEXT NOT NULL,state TEXT NOT NULL,state_reason TEXT,
                html_url TEXT NOT NULL,author TEXT NOT NULL,category TEXT,created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,labels TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(db, "issues", "rejection_reason", "TEXT");
        ServerId = GetSetting("server_id") ?? Guid.NewGuid().ToString("N");
        SetSetting("server_id", ServerId);
        ServerName = string.IsNullOrWhiteSpace(serverName) ? GetSetting("server_name") ?? Environment.MachineName : serverName.Trim();
        SetSetting("server_name", ServerName);
        foreach (var file in Directory.EnumerateFiles(TemporaryDirectory))
            try { File.Delete(file); } catch { }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        using var pragma = db.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON";
        pragma.ExecuteNonQuery();
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

    private static void EnsureColumn(SqliteConnection db, string table, string column, string definition)
    {
        var exists = false;
        using (var pragma = Cmd(db, $"PRAGMA table_info({table})"))
        using (var reader = pragma.ExecuteReader())
            while (reader.Read())
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
        if (exists) return;
        using var alter = Cmd(db, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
        alter.ExecuteNonQuery();
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
            using var command = value is null
                ? Cmd(db, "DELETE FROM settings WHERE key=$key", "$key", key)
                : Cmd(db, "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                    "$key", key, "$value", value);
            command.ExecuteNonQuery();
        }
    }

    public FeedbackReceipt SaveIssue(FeedbackSubmitter client, FeedbackSubmission submission, IReadOnlyList<StagedServerAttachment> staged)
    {
        FeedbackRules.ValidateSubmission(submission);
        FeedbackRules.ValidateAttachments(staged.Select(item => (item.FileName, item.Size)));
        var existing = GetIssueBySubmission(client.ClientId, submission.ClientSubmissionId);
        if (existing is not null)
        {
            foreach (var item in staged) try { File.Delete(item.TempPath); } catch { }
            return Receipt(existing);
        }
        var issueId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var moved = new List<string>();
        try
        {
            lock (gate)
            {
                using var db = Open();
                using var transaction = db.BeginTransaction();
                using (var insert = Cmd(db, """
                    INSERT INTO issues(id,submission_id,client_id,nickname,category,title,body,user_name,app_version,
                        os_description,os_architecture,process_architecture,status,created_utc,updated_utc)
                    VALUES($id,$submission,$client,$nickname,$category,$title,$body,$username,$version,$os,$osarch,$process,
                        $status,$created,$updated)
                    """, "$id", issueId, "$submission", submission.ClientSubmissionId, "$client", client.ClientId,
                    "$nickname", client.UserName.Trim(), "$category", submission.Category.ToString(), "$title", submission.Title.Trim(),
                    "$body", submission.Body.Trim(), "$username", submission.UserName.Trim(), "$version", submission.AppVersion,
                    "$os", submission.OsDescription, "$osarch", submission.OsArchitecture, "$process", submission.ProcessArchitecture,
                    "$status", LocalIssueStatus.PendingReview.ToString(), "$created", now.ToString("O"), "$updated", now.ToString("O")))
                    insert.ExecuteNonQuery();
                foreach (var attachment in staged)
                {
                    var finalPath = Path.Combine(AttachmentDirectory, attachment.Id);
                    File.Move(attachment.TempPath, finalPath);
                    moved.Add(finalPath);
                    using var file = Cmd(db, """
                        INSERT INTO attachments(id,issue_id,file_name,stored_path,size,sha256,is_image)
                        VALUES($id,$issue,$name,$path,$size,$hash,$image)
                        """, "$id", attachment.Id, "$issue", issueId, "$name", attachment.FileName, "$path", finalPath,
                        "$size", attachment.Size, "$hash", attachment.Sha256, "$image", attachment.IsImage ? 1 : 0);
                    file.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }
        catch
        {
            foreach (var path in moved) try { File.Delete(path); } catch { }
            foreach (var item in staged) try { File.Delete(item.TempPath); } catch { }
            var raced = GetIssueBySubmission(client.ClientId, submission.ClientSubmissionId);
            if (raced is not null) return Receipt(raced);
            throw;
        }
        return new FeedbackReceipt(submission.ClientSubmissionId, issueId, LocalIssueStatus.PendingReview, now, now);
    }

    public ServerLocalIssue? GetIssueBySubmission(string clientId, string submissionId)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, IssueSelect + " WHERE client_id=$client AND submission_id=$submission",
                "$client", clientId, "$submission", submissionId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadIssue(reader) : null;
        }
    }

    public IReadOnlyList<ServerLocalIssue> GetIssues()
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, IssueSelect + " ORDER BY updated_utc DESC");
            using var reader = command.ExecuteReader();
            var result = new List<ServerLocalIssue>();
            while (reader.Read()) result.Add(ReadIssue(reader));
            return result;
        }
    }

    public FeedbackStatusSnapshot SetStatus(string issueId, LocalIssueStatus status, string? rejectionReason = null)
    {
        var reason = rejectionReason?.Trim();
        if (status == LocalIssueStatus.Rejected)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("拒绝反馈时必须填写拒绝原因。");
            if (reason.Length > 1000) throw new ArgumentException("拒绝原因不能超过 1000 个字符。");
        }
        else reason = null;
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "UPDATE issues SET status=$status,rejection_reason=$reason,updated_utc=$updated WHERE id=$id",
                "$status", status.ToString(), "$reason", reason, "$updated", now.ToString("O"), "$id", issueId);
            if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("反馈不存在。");
            using var read = Cmd(db, "SELECT submission_id FROM issues WHERE id=$id", "$id", issueId);
            return new FeedbackStatusSnapshot((string)read.ExecuteScalar()!, issueId, status, now);
        }
    }

    public FeedbackStatusSnapshot CloseBySubmitter(FeedbackSubmitter client, string submissionId)
    {
        var issue = GetIssueBySubmission(client.ClientId, submissionId) ?? throw new KeyNotFoundException("反馈不存在。");
        if (issue.Status == LocalIssueStatus.Completed) return new FeedbackStatusSnapshot(issue.SubmissionId, issue.Id, issue.Status, issue.UpdatedUtc);
        if (issue.Status == LocalIssueStatus.UserClosed) return new FeedbackStatusSnapshot(issue.SubmissionId, issue.Id, issue.Status, issue.UpdatedUtc);
        return SetStatus(issue.Id, LocalIssueStatus.UserClosed);
    }

    public StoredAttachment? GetAttachment(string id)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, "SELECT id,issue_id,file_name,stored_path,size,sha256,is_image FROM attachments WHERE id=$id", "$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadAttachment(reader) : null;
        }
    }

    public void SaveGitHubIssues(IEnumerable<GitHubIssue> issues)
    {
        lock (gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            using (var clear = Cmd(db, "DELETE FROM github_issues")) clear.ExecuteNonQuery();
            foreach (var issue in issues)
            {
                var category = FeedbackRules.ParseCategory(issue.Body);
                using var command = Cmd(db, """
                    INSERT INTO github_issues(number,title,body,state,state_reason,html_url,author,category,created_utc,updated_utc,labels)
                    VALUES($number,$title,$body,$state,$reason,$url,$author,$category,$created,$updated,$labels)
                    """, "$number", issue.Number, "$title", issue.Title, "$body", issue.Body, "$state", issue.State,
                    "$reason", issue.StateReason, "$url", issue.HtmlUrl, "$author", issue.Author,
                    "$category", category?.ToString(), "$created", issue.CreatedUtc.ToString("O"),
                    "$updated", issue.UpdatedUtc.ToString("O"), "$labels", string.Join('\n', issue.Labels));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public IReadOnlyList<CachedGitHubIssue> GetGitHubIssues()
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Cmd(db, """
                SELECT number,title,body,state,state_reason,html_url,author,category,created_utc,updated_utc,labels
                FROM github_issues ORDER BY updated_utc DESC
                """);
            using var reader = command.ExecuteReader();
            var result = new List<CachedGitHubIssue>();
            while (reader.Read()) result.Add(new CachedGitHubIssue(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : Enum.Parse<FeedbackCategory>(reader.GetString(7)),
                DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                reader.GetString(10).Split('\n', StringSplitOptions.RemoveEmptyEntries)));
            return result;
        }
    }

    private const string IssueSelect = """
        SELECT id,submission_id,client_id,nickname,category,title,body,user_name,app_version,os_description,
            os_architecture,process_architecture,rejection_reason,status,created_utc,updated_utc FROM issues
        """;

    private ServerLocalIssue ReadIssue(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var result = new ServerLocalIssue(id, reader.GetString(1), reader.GetString(2), reader.GetString(3),
            Enum.Parse<FeedbackCategory>(reader.GetString(4)), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12), Enum.Parse<LocalIssueStatus>(reader.GetString(13)),
            DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture), []);
        return result with { Attachments = GetAttachments(id) };
    }

    private IReadOnlyList<StoredAttachment> GetAttachments(string issueId)
    {
        using var db = Open();
        using var files = Cmd(db, "SELECT id,issue_id,file_name,stored_path,size,sha256,is_image FROM attachments WHERE issue_id=$id ORDER BY rowid", "$id", issueId);
        using var reader = files.ExecuteReader();
        var result = new List<StoredAttachment>();
        while (reader.Read()) result.Add(ReadAttachment(reader));
        return result;
    }

    private static StoredAttachment ReadAttachment(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetInt32(6) != 0);

    private static FeedbackReceipt Receipt(ServerLocalIssue issue) => new(issue.SubmissionId, issue.Id, issue.Status, issue.CreatedUtc, issue.UpdatedUtc);
}
