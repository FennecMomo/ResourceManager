using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ResourceManager.Core;

[JsonConverter(typeof(JsonStringEnumConverter<FeedbackCategory>))]
public enum FeedbackCategory { Problem, Suggestion, Experience, Other }

[JsonConverter(typeof(JsonStringEnumConverter<LocalIssueStatus>))]
public enum LocalIssueStatus { PendingReview, Accepted, Rejected, Completed, UserClosed }

[JsonConverter(typeof(JsonStringEnumConverter<FeedbackTargetKind>))]
public enum FeedbackTargetKind { Github, LanServer }

public sealed record FeedbackAttachmentDraft(string FileName, string StagedPath, long Size);

public sealed record FeedbackDraft(
    FeedbackCategory Category,
    string Title,
    string Body,
    IReadOnlyList<FeedbackAttachmentDraft> Attachments,
    DateTimeOffset UpdatedUtc);

public sealed record FeedbackSubmission(
    string ClientSubmissionId,
    FeedbackCategory Category,
    string Title,
    string Body,
    string UserName,
    string AppVersion,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    DateTimeOffset SubmittedAtUtc);

public sealed record FeedbackReceipt(
    string ClientSubmissionId,
    string IssueId,
    LocalIssueStatus Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record FeedbackStatusSnapshot(
    string ClientSubmissionId,
    string IssueId,
    LocalIssueStatus Status,
    DateTimeOffset UpdatedUtc);

public sealed record FeedbackHistoryItem(
    string ClientSubmissionId,
    FeedbackTargetKind TargetKind,
    string TargetName,
    FeedbackCategory Category,
    string Title,
    string Body,
    string AttachmentNames,
    string? RemoteIssueId,
    string? RemoteUrl,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? Error);

public sealed record FeedbackServerBinding(
    string ServerId,
    string Name,
    string BaseUrl,
    string ClientId);

public sealed record FeedbackServerCapabilities(
    string ServerId,
    string Name,
    string ServerVersion,
    string Protocol,
    int MaxAttachments,
    long MaxAttachmentBytes,
    long MaxTotalAttachmentBytes);

public sealed record FeedbackServerAdvertisement(
    string Protocol,
    string Type,
    string ServerId,
    string Name,
    int ApiPort,
    string ServerVersion,
    string Address = "")
{
    public string BaseUrl => $"http://{Address}:{ApiPort}/";
}

public static partial class FeedbackRules
{
    public const string Protocol = "feedback-v1";
    public const string DiscoveryProtocol = "resource-manager-feedback-discovery-v1";
    public const string ClientIdHeader = "X-ResourceManager-Client-Id";
    public const int DefaultApiPort = 37644;
    public const int DefaultDiscoveryPort = 37645;
    public const int DefaultAdminPort = 37646;
    public const int MaxAttachments = 10;
    public const long MaxImageBytes = 10L * 1024 * 1024;
    public const long MaxOtherBytes = 25L * 1024 * 1024;
    public const long MaxTotalBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    private static readonly HashSet<string> OtherExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".jsonc", ".csv", ".tsv",
        ".pdf", ".docx", ".xlsx", ".pptx", ".zip", ".gz", ".tgz"
    };

    public static IReadOnlyCollection<string> AllowedExtensions => ImageExtensions.Concat(OtherExtensions).ToArray();

    public static bool IsImage(string fileName) => ImageExtensions.Contains(Path.GetExtension(fileName));

    public static void ValidateSubmission(FeedbackSubmission submission)
    {
        if (!Guid.TryParse(submission.ClientSubmissionId, out _))
            throw new ArgumentException("反馈提交编号无效。");
        if (!Enum.IsDefined(submission.Category)) throw new ArgumentException("反馈分类无效。");
        if (string.IsNullOrWhiteSpace(submission.Title) || string.IsNullOrWhiteSpace(submission.Body))
            throw new ArgumentException("反馈标题和正文不能为空。");
        var title = submission.Title.Trim();
        var body = submission.Body.Trim();
        if (title.Length is < 1 or > 120) throw new ArgumentException("反馈标题须为 1 至 120 个字符。");
        if (body.Length is < 1 or > 10_000) throw new ArgumentException("反馈正文须为 1 至 10000 个字符。");
        if (string.IsNullOrWhiteSpace(submission.UserName) || submission.UserName.Length > 80 ||
            string.IsNullOrWhiteSpace(submission.AppVersion) || submission.AppVersion.Length > 40 ||
            string.IsNullOrWhiteSpace(submission.OsDescription) || submission.OsDescription.Length > 200 ||
            string.IsNullOrWhiteSpace(submission.OsArchitecture) || submission.OsArchitecture.Length > 30 ||
            string.IsNullOrWhiteSpace(submission.ProcessArchitecture) || submission.ProcessArchitecture.Length > 30)
            throw new ArgumentException("反馈环境信息无效。");
    }

    public static void ValidateAttachments(IEnumerable<(string FileName, long Size)> attachments)
    {
        var items = attachments.ToArray();
        if (items.Length > MaxAttachments) throw new ArgumentException($"最多只能添加 {MaxAttachments} 个附件。");
        long total = 0;
        foreach (var item in items)
        {
            var name = Path.GetFileName(item.FileName);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name != item.FileName)
                throw new ArgumentException("附件名称无效。");
            var extension = Path.GetExtension(name);
            if (!ImageExtensions.Contains(extension) && !OtherExtensions.Contains(extension))
                throw new ArgumentException($"不支持附件类型 {extension}。");
            var maximum = ImageExtensions.Contains(extension) ? MaxImageBytes : MaxOtherBytes;
            if (item.Size is <= 0 || item.Size > maximum) throw new ArgumentException($"附件“{name}”大小超出限制。");
            total = checked(total + item.Size);
        }
        if (total > MaxTotalBytes) throw new ArgumentException("附件总大小不能超过 50 MB。");
    }

    public static string CategoryText(FeedbackCategory category) => category switch
    {
        FeedbackCategory.Problem => "问题",
        FeedbackCategory.Suggestion => "建议",
        FeedbackCategory.Experience => "体验",
        _ => "其他"
    };

    public static string StatusText(LocalIssueStatus status) => status switch
    {
        LocalIssueStatus.PendingReview => "待确认",
        LocalIssueStatus.Accepted => "已接受",
        LocalIssueStatus.Rejected => "已拒绝",
        LocalIssueStatus.Completed => "已完成",
        _ => "用户已关闭"
    };

    public static bool IsHistoryDeletable(string status) =>
        status is "Completed" or "UserClosed" or "Failed";

    public static string BuildGithubBody(FeedbackSubmission submission)
    {
        ValidateSubmission(submission);
        return $$"""
            {{submission.Body.Trim()}}

            ---

            | 反馈信息 | 内容 |
            | --- | --- |
            | 分类 | {{CategoryText(submission.Category)}} |
            | 用户名 | {{EscapeTable(submission.UserName)}} |
            | ResourceManager | {{EscapeTable(submission.AppVersion)}} |
            | Windows | {{EscapeTable(submission.OsDescription)}} |
            | 系统架构 | {{EscapeTable(submission.OsArchitecture)}} |
            | 进程架构 | {{EscapeTable(submission.ProcessArchitecture)}} |

            <!-- resourcemanager-feedback:v1;submission={{submission.ClientSubmissionId}};category={{submission.Category.ToString().ToLowerInvariant()}} -->
            """;
    }

    public static FeedbackCategory? ParseCategory(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var match = FeedbackMarker().Match(body);
        return match.Success && Enum.TryParse<FeedbackCategory>(match.Groups[1].Value, true, out var category)
            ? category : null;
    }

    private static string EscapeTable(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    [GeneratedRegex(@"resourcemanager-feedback:v1;submission=[^;]+;category=([a-z]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FeedbackMarker();
}
