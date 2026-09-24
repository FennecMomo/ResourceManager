using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using ResourceManager.Core;

namespace ResourceManager.App;

internal sealed class FeedbackSecretStore
{
    private readonly string path;
    private static readonly byte[] Entropy = "ResourceManager.Feedback.v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public FeedbackSecretStore(string dataDirectory) => path = Path.Combine(dataDirectory, "feedback-secrets.dat");

    public (GitHubSession? GitHub, string? GitHubLogin, FeedbackServerBinding? Server) Load()
    {
        if (!File.Exists(path)) return (null, null, null);
        try
        {
            var encrypted = File.ReadAllBytes(path);
            var clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var data = JsonSerializer.Deserialize<SecretData>(clear, Json);
            return (data?.GitHub, data?.GitHubLogin, data?.Server);
        }
        catch
        {
            return (null, null, null);
        }
    }

    public void Save(GitHubSession? github, string? githubLogin, FeedbackServerBinding? server)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(new SecretData(github, githubLogin, server), Json);
        var encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, path, true);
    }

    private sealed record SecretData(GitHubSession? GitHub, string? GitHubLogin, FeedbackServerBinding? Server);
}
