using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class ResourcePathSafetyTests
{
    [Theory]
    [InlineData(".git./config")]
    [InlineData(".git /config")]
    [InlineData("NUL")]
    [InlineData("nested/file.txt.")]
    public void WindowsAliasPathsCannotBypassThePublishedTree(string path)
    {
        var root = Path.Combine(Path.GetTempPath(), "ResourceManagerPathSafety", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new NodeStore(Path.Combine(root, "profile"));
            var source = Path.Combine(root, "repo");
            Directory.CreateDirectory(Path.Combine(source, ".git"));
            File.WriteAllText(Path.Combine(source, ".git", "config"), "private metadata fixture");
            var resource = store.AddResource(source, PublishMode.Reference);
            Assert.Throws<ArgumentException>(() => new ResourceCatalog(store).ResolveFile(resource.Id, path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
