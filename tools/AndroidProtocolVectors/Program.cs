using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResourceManager.Core;

if (args.Length == 3 && args[0] == "android") {
    var store = new NodeStore(Path.GetFullPath(args[1]));
    using var client = new PeerClient(store,supportsChat:true,supportsReminders:true);
    var peer = await client.ConnectAsync("127.0.0.1",int.Parse(args[2]));
    var resources = await client.GetResourcesAsync(peer);
    var resource = resources.Single(r => r.Id == "android-live");
    var downloads = new DownloadManager(store,client);
    var job=downloads.CreateJob(peer,resource,Path.Combine(store.DataDirectory,"download"));
    await downloads.RunAsync(job.Id);
    if(File.ReadAllText(job.TargetPath) != "Android -> Windows 中文 😀") throw new Exception("Android file mismatch");
    Console.WriteLine("Actual Windows PeerClient verified Android Keystore hello, catalog and file bytes");
    return;
}

if (args.Length >= 2 && args[0] == "serve") {
    var root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
    var port = args.Length > 2 ? int.Parse(args[2]) : 47642;
    var store = new NodeStore(Path.Combine(root,"store")); store.SaveSettings("互通测试电脑 中文 + 😀",null,port,true);
    var source = Path.Combine(root,"fixture.txt"); File.WriteAllText(source,"ResourceManager Android 互通\n0123456789",new UTF8Encoding(false));
    if (store.GetResources().Count == 0) {
        store.AddResource(source,PublishMode.Reference);
        var folder = Path.Combine(root,"目录"); Directory.CreateDirectory(Path.Combine(folder,"empty")); File.WriteAllText(Path.Combine(folder,"子文件.txt"),"nested"); store.AddResource(folder,PublishMode.Reference);
        var hidden = store.SaveResourceGroup("私有"); store.SetGroupPermission(hidden.Id,GroupAccess.Private,[]); store.AddResource(source,PublishMode.Reference,groupId:hidden.Id);
    }
    await using var node = new PeerNode(store); await node.StartAsync(port,"127.0.0.1");
    File.WriteAllText(Path.Combine(root,"ready"),port.ToString());
    while(!File.Exists(Path.Combine(root,"stop"))) await Task.Delay(200);
    return;
}

// Reflection intentionally calls the actual desktop implementation without widening its production API.
var proof = typeof(PeerHello).Assembly.GetType("ResourceManager.Core.PeerProof", true)!;
byte[] Invoke(string method, params object[] values) => (byte[])proof.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, values)!;
var hello = new PeerHello("windows-fixture", "中文 \"A\" + <>&'` 😀", 37642, null,
    ["signed-device-v1", "resource-groups-v1", "resource-access-v1", "chat-v1", "chat-private-resource-v1", "reminder-v1"]);
var ascii = new string(Enumerable.Range(0, 128).Select(i => (char)i).ToArray()) + "中文😀\u2028\u2029";
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var vectors = new[] {
    new { name = "hello", bytes = Invoke("HelloPayload", hello, "ABC123", "android-fixture") },
    new { name = "request", bytes = Invoke("RequestPayload", "android-fixture", "windows-fixture", "GET", "/api/v1/resources/a/content?path=%E4%B8%AD\nbytes=3-\n\"etag\"", "1700000000", new string('A',48), Encoding.UTF8.GetBytes("中文 body")) },
    new { name = "escaping", bytes = Invoke("Payload", (object)new[] { ascii }) }
};
var output = new {
    hello,
    publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
    vectors = vectors.Select(v => new { v.name, payload = Encoding.UTF8.GetString(v.bytes), signature = Convert.ToBase64String(key.SignData(v.bytes, HashAlgorithmName.SHA256)) })
};
if (args.Length == 2 && args[0] == "verify") {
    using var doc = JsonDocument.Parse(File.ReadAllText(args[1]));
    key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(doc.RootElement.GetProperty("publicKey").GetString()!), out _);
    var data = Encoding.UTF8.GetBytes(doc.RootElement.GetProperty("payload").GetString()!);
    if (!key.VerifyData(data, Convert.FromBase64String(doc.RootElement.GetProperty("signature").GetString()!), HashAlgorithmName.SHA256)) throw new Exception("Android/JVM signature failed .NET validation");
    Console.WriteLine("Android/JVM P1363 signature verified by .NET");
} else {
    var path = args.Single(); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, JsonSerializer.Serialize(output, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), new UTF8Encoding(false));
    Console.WriteLine(path);
}
