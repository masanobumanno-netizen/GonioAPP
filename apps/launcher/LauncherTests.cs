using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
namespace Gonio.Launcher;
public static class LauncherTests
{
    public static void Run()
    {
        int count=0;
        void Assert(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS: "+name);}
        bool Fails(Action action){try{action();return false;}catch{return true;}}
        using var rsa=RSA.Create(2048);var config=new UpdateConfig("example/gonio",rsa.ExportSubjectPublicKeyInfoPem());
        string Sign(ReleaseManifest value){var data=JsonSerializer.SerializeToUtf8Bytes(value,Updates.Json);return JsonSerializer.Serialize(new SignedManifest(Convert.ToBase64String(data),Convert.ToBase64String(rsa.SignData(data,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))),Updates.Json);}
        var release=new ReleaseManifest("0.5.0","https://github.com/example/gonio/releases/download/v0.5.0/GonioWeb-update-0.5.0.zip",new string('a',64),100);
        Assert(Updates.Verify(Sign(release),config)==release,"trusted signature accepted");
        using var other=RSA.Create(2048);Assert(Fails(()=>Updates.Verify(Sign(release),config with {PublicKeyPem=other.ExportSubjectPublicKeyInfoPem()})),"wrong signing key rejected");
        var envelope=JsonSerializer.Deserialize<SignedManifest>(Sign(release),Updates.Json)!;
        var changed=JsonSerializer.Serialize(envelope with {Payload=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(release with {Version="0.6.0"},Updates.Json))},Updates.Json);
        Assert(Fails(()=>Updates.Verify(changed,config)),"tampered payload rejected");
        Assert(Fails(()=>Updates.Verify(Sign(release with {Url="https://evil.example/app.zip"}),config)),"unexpected asset source rejected");
        Assert(Fails(()=>Updates.Verify(Sign(release with {Size=Updates.MaxDownload+1}),config)),"oversized update rejected");
        Assert(Fails(()=>Updates.ValidVersion("../0.5.0")),"version path traversal rejected");
        var root=Path.Combine(Path.GetTempPath(),"gonio-update-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            string Zip(string name,params (string,string)[] files){var path=Path.Combine(root,name);using var z=ZipFile.Open(path,ZipArchiveMode.Create);foreach(var (key,value) in files){using var w=new StreamWriter(z.CreateEntry(key).Open());w.Write(value);}return path;}
            ReleaseManifest Meta(string path){using var f=File.OpenRead(path);return release with {Size=f.Length,Sha256=Convert.ToHexString(SHA256.HashData(f))};}
            var good=Zip("good.zip",("GonioWeb.exe","test"),("wwwroot/apps/web/index.html","test"),("version.txt","0.5.0"));
            Updates.ExtractVerified(good,Path.Combine(root,"good"),Meta(good));Assert(File.Exists(Path.Combine(root,"good","GonioWeb.exe")),"verified update extracted");
            Assert(Fails(()=>Updates.ExtractVerified(good,Path.Combine(root,"hash"),Meta(good) with {Sha256=new string('0',64)})),"corrupt archive rejected before extraction");
            var traversal=Zip("traversal.zip",("../escape","bad"));Assert(Fails(()=>Updates.ExtractVerified(traversal,Path.Combine(root,"bad"),Meta(traversal))),"archive traversal rejected");Assert(!File.Exists(Path.Combine(root,"escape")),"no write outside staging");
            var missing=Zip("missing.zip",("version.txt","0.5.0"));Assert(Fails(()=>Updates.ExtractVerified(missing,Path.Combine(root,"missing"),Meta(missing))),"incomplete app rejected");
            var wrong=Zip("wrong.zip",("GonioWeb.exe","test"),("wwwroot/apps/web/index.html","test"),("version.txt","0.4.0"));Assert(Fails(()=>Updates.ExtractVerified(wrong,Path.Combine(root,"wrong"),Meta(wrong))),"package version mismatch rejected");
            var state=new InstallState("0.4.0",null,"0.5.0");var path=Path.Combine(root,"state.json");Updates.AtomicJson(path,state);Assert(Updates.Read<InstallState>(path)==state,"install state survives atomic save");
            Updates.AtomicJson(path,new InstallState("0.4.0"));
            var messages=new List<string>();
            Updates.CheckAndStage(root,config,messages.Add,CancellationToken.None,new FakeFeed(Sign(Meta(good)),File.ReadAllBytes(good))).GetAwaiter().GetResult();
            var staged=Updates.Read<InstallState>(path);
            Assert(staged.Current=="0.4.0" && staged.Pending=="0.5.0","download stages update without switching running version");
            Assert(File.Exists(Path.Combine(root,"versions","0.5.0","GonioWeb.exe")),"staged app ready for next launch");
            Updates.CheckAndStage(root,config,messages.Add,CancellationToken.None,new FakeFeed(Sign(Meta(good)),File.ReadAllBytes(good))).GetAwaiter().GetResult();
            Assert(Updates.Read<InstallState>(path)==staged,"repeated check preserves prepared update");
            Updates.AtomicJson(path,new InstallState("0.4.0",null,null,"0.5.0"));
            Updates.CheckAndStage(root,config,messages.Add,CancellationToken.None,new FakeFeed(Sign(Meta(good)),File.ReadAllBytes(good))).GetAwaiter().GetResult();
            Assert(Updates.Read<InstallState>(path).Pending==null,"failed version is not downloaded again");
            Updates.AtomicJson(path,new InstallState("0.6.0"));
            Updates.CheckAndStage(root,config,messages.Add,CancellationToken.None,new FakeFeed(Sign(Meta(good)),File.ReadAllBytes(good))).GetAwaiter().GetResult();
            Assert(Updates.Read<InstallState>(path).Pending==null,"older release never downgrades current version");
            Assert(Fails(()=>Updates.CheckAndStage(root,config,messages.Add,CancellationToken.None,new FakeFeed("invalid",[])).GetAwaiter().GetResult()),"invalid feed rejected");
            Assert(Updates.Read<InstallState>(path).Current=="0.6.0","failed download leaves active state intact");
        }finally{Directory.Delete(root,true);}
        Console.WriteLine($"{count} updater tests passed.");
    }
    private sealed class FakeFeed(string manifest,byte[] archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            HttpContent content=request.RequestUri!.AbsolutePath.EndsWith(".json")?new StringContent(manifest):new ByteArrayContent(archive);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){RequestMessage=request,Content=content});
        }
    }
}
