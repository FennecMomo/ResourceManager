using Microsoft.Data.Sqlite;
using ResourceManager.Core;
using ResourceManager.Server;

namespace ResourceManager.Tests;

public sealed class FeedbackTests
{
    [Fact]
    public void Submission_ValidatesAndBuildsGithubBodyWithUsernameAndCategory()
    {
        var submission = SampleSubmission();

        FeedbackRules.ValidateSubmission(submission);
        var body = FeedbackRules.BuildGithubBody(submission);

        Assert.Contains("| 分类 | 问题 |", body);
        Assert.Contains("| 用户名 | 测试用户 |", body);
        Assert.Contains(submission.ClientSubmissionId, body);
        Assert.Equal(FeedbackCategory.Problem, FeedbackRules.ParseCategory(body));
    }

    [Fact]
    public void Submission_RejectsMissingFieldsAndUndefinedCategoryAsValidationErrors()
    {
        Assert.Throws<ArgumentException>(() =>
            FeedbackRules.ValidateSubmission(SampleSubmission() with { Title = null! }));
        Assert.Throws<ArgumentException>(() =>
            FeedbackRules.ValidateSubmission(SampleSubmission() with { AppVersion = null! }));
        Assert.Throws<ArgumentException>(() =>
            FeedbackRules.ValidateSubmission(SampleSubmission() with { Category = (FeedbackCategory)999 }));
    }

    [Fact]
    public void Attachments_EnforceAllowlistCountAndLimits()
    {
        FeedbackRules.ValidateAttachments([("screen.png", 1024), ("details.log", 2048)]);

        Assert.Throws<ArgumentException>(() => FeedbackRules.ValidateAttachments([("run.exe", 1024)]));
        Assert.Throws<ArgumentException>(() => FeedbackRules.ValidateAttachments([("large.png", FeedbackRules.MaxImageBytes + 1)]));
        Assert.Throws<ArgumentException>(() => FeedbackRules.ValidateAttachments(
            Enumerable.Range(0, FeedbackRules.MaxAttachments + 1).Select(index => ($"{index}.txt", 1L))));
    }

    [Fact]
    public void FeedbackStore_PersistsDraftAndRestrictsHistoryDeletion()
    {
        using var space = new FeedbackTestSpace();
        var staged = space.Write("feedback-drafts/file.log", "diagnostic");
        var store = new FeedbackStore(space.Root);
        store.SaveDraft(new FeedbackDraft(FeedbackCategory.Suggestion, "改进标题", "改进正文",
            [new FeedbackAttachmentDraft("file.log", staged, new FileInfo(staged).Length)], DateTimeOffset.UtcNow));

        var draft = store.GetDraft();
        Assert.Equal(FeedbackCategory.Suggestion, draft.Category);
        Assert.Equal("改进标题", draft.Title);
        Assert.Single(draft.Attachments);

        var history = new FeedbackHistoryItem(Guid.NewGuid().ToString(), FeedbackTargetKind.LanServer, "测试服务端",
            FeedbackCategory.Suggestion, "改进标题", "改进正文", "file.log", "issue-1", null,
            "PendingReview", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        store.SaveHistory(history);
        Assert.Throws<InvalidOperationException>(() => store.DeleteHistory(history.ClientSubmissionId));

        store.SaveHistory(history with { Status = "UserClosed" });
        store.DeleteHistory(history.ClientSubmissionId);
        Assert.Empty(store.GetHistory());
    }

    [Fact]
    public void ServerStore_AcceptsLanSubmitterCreatesIdempotentlyAndSoftCloses()
    {
        using var space = new FeedbackTestSpace();
        var store = new ServerStore(space.Root, "测试服务端");
        var client = new FeedbackSubmitter(Guid.NewGuid().ToString("N"), "提交者");

        var submission = SampleSubmission();
        var first = store.SaveIssue(client, submission, []);
        var replay = store.SaveIssue(client, submission, []);

        Assert.Equal(first.IssueId, replay.IssueId);
        Assert.Equal(LocalIssueStatus.PendingReview, first.Status);
        var closed = store.CloseBySubmitter(client, submission.ClientSubmissionId);
        Assert.Equal(LocalIssueStatus.UserClosed, closed.Status);
        Assert.Equal(LocalIssueStatus.UserClosed, store.CloseBySubmitter(client, submission.ClientSubmissionId).Status);
        Assert.Single(store.GetIssues());
    }

    [Fact]
    public void ServerStore_SeparatesSubmittersWithoutRequiringPairing()
    {
        using var space = new FeedbackTestSpace();
        var store = new ServerStore(space.Root, "测试服务端");
        var first = new FeedbackSubmitter(Guid.NewGuid().ToString("N"), "第一台");
        var second = new FeedbackSubmitter(Guid.NewGuid().ToString("N"), "第二台");
        var submission = SampleSubmission();

        store.SaveIssue(first, submission, []);
        Assert.Null(store.GetIssueBySubmission(second.ClientId, submission.ClientSubmissionId));
        Assert.Throws<KeyNotFoundException>(() => store.CloseBySubmitter(second, submission.ClientSubmissionId));
    }

    [Fact]
    public void ServerStore_RequiresPersistsAndClearsRejectionReason()
    {
        using var space = new FeedbackTestSpace();
        var store = new ServerStore(space.Root, "测试服务端");
        var client = new FeedbackSubmitter(Guid.NewGuid().ToString("N"), "提交者");
        var receipt = store.SaveIssue(client, SampleSubmission(), []);

        Assert.Throws<ArgumentException>(() => store.SetStatus(receipt.IssueId, LocalIssueStatus.Rejected));
        Assert.Throws<ArgumentException>(() => store.SetStatus(receipt.IssueId, LocalIssueStatus.Rejected, new string('字', 1001)));

        store.SetStatus(receipt.IssueId, LocalIssueStatus.Rejected, "  当前版本不计划支持这个场景。  ");
        var rejected = Assert.Single(store.GetIssues());
        Assert.Equal(LocalIssueStatus.Rejected, rejected.Status);
        Assert.Equal("当前版本不计划支持这个场景。", rejected.RejectionReason);

        store.SetStatus(receipt.IssueId, LocalIssueStatus.Accepted);
        var accepted = Assert.Single(store.GetIssues());
        Assert.Equal(LocalIssueStatus.Accepted, accepted.Status);
        Assert.Null(accepted.RejectionReason);
    }

    [Fact]
    public void ServerStore_MigratesExistingIssueTableForRejectionReason()
    {
        using var space = new FeedbackTestSpace();
        Directory.CreateDirectory(space.Root);
        var database = Path.Combine(space.Root, "server.db");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE issues(
                    id TEXT PRIMARY KEY,submission_id TEXT NOT NULL,client_id TEXT NOT NULL,nickname TEXT NOT NULL,
                    category TEXT NOT NULL,title TEXT NOT NULL,body TEXT NOT NULL,user_name TEXT NOT NULL,
                    app_version TEXT NOT NULL,os_description TEXT NOT NULL,os_architecture TEXT NOT NULL,
                    process_architecture TEXT NOT NULL,status TEXT NOT NULL,created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL,
                    UNIQUE(client_id,submission_id));
                """;
            command.ExecuteNonQuery();
        }

        _ = new ServerStore(space.Root, "测试服务端");

        using var migrated = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        migrated.Open();
        using var schema = migrated.CreateCommand();
        schema.CommandText = "PRAGMA table_info(issues)";
        using var reader = schema.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(1));
        Assert.Contains("rejection_reason", columns);
    }

    [Fact]
    public void SubmissionLimiter_RejectsConcurrentOrRepeatedSubmissions()
    {
        var now = DateTimeOffset.UtcNow;
        var submissions = new SubmissionLimiter();
        Assert.Single(Enumerable.Range(0, 20).AsParallel().Where(_ => submissions.TryAcquire("client", now)));
        Assert.True(submissions.TryAcquire("client", now.AddMinutes(1)));
    }

    private static FeedbackSubmission SampleSubmission() => new(Guid.NewGuid().ToString(), FeedbackCategory.Problem,
        "无法提交反馈", "点击提交后出现错误。", "测试用户", "0.3.5", "Windows 11", "X64", "X64", DateTimeOffset.UtcNow);

    private sealed class FeedbackTestSpace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ResourceManagerFeedbackTests", Guid.NewGuid().ToString("N"));

        public string Write(string relativePath, string value)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
