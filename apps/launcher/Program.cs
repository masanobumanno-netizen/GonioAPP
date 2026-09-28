using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Gonio.Launcher;

if (args.Contains("--install")) { try { Installer.Run(); } catch(Exception ex) {Console.Error.WriteLine(ex.Message);Environment.ExitCode=1;} return; }
if (args.Contains("--self-test")) { LauncherTests.Run(); return; }
if (args.Length > 0 && args[0] == "--sign") {
    // Release operator only. Private key remains outside installation and update assets.
    if (args.Length != 7) throw new ArgumentException("--sign key.pem repository version zip output-manifest public-config");
    using var rsa=RSA.Create();rsa.ImportFromPem(File.ReadAllText(args[1]));
    Updates.ValidateRepository(args[2]);Updates.ValidVersion(args[3]);
    using var file=File.OpenRead(args[4]);
    var manifest=new ReleaseManifest(args[3],$"https://github.com/{args[2]}/releases/download/v{args[3]}/GonioWeb-update-{args[3]}.zip",Convert.ToHexString(SHA256.HashData(file)),file.Length);
    var payload=JsonSerializer.SerializeToUtf8Bytes(manifest,Updates.Json);
    Updates.AtomicJson(Path.GetFullPath(args[5]),new SignedManifest(Convert.ToBase64String(payload),Convert.ToBase64String(rsa.SignData(payload,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))));
    Updates.AtomicJson(Path.GetFullPath(args[6]),new UpdateConfig(args[2],rsa.ExportSubjectPublicKeyInfoPem()));return;
}
if (!OperatingSystem.IsWindows()) { Console.WriteLine("Windows用ランチャーです。"); return; }
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
Directory.CreateDirectory(root);
using var instanceLock = AcquireLock(Path.Combine(root,"launcher.lock"));
if(instanceLock==null) { Console.WriteLine("Gonio Webは既に起動しています。"); return; }
var portText=Environment.GetEnvironmentVariable("GONIO_PORT")??"4173";
if(!int.TryParse(portText,out var port)||port<1||port>65535)throw new ArgumentException("Invalid port");
// Never replace or terminate a server already running outside this launcher.
try { var probe=new TcpListener(IPAddress.Loopback,port);probe.Start();probe.Stop(); }
catch {Console.WriteLine($"ポート{port}は使用中です。以前のGonio Webを終了してから起動してください。");return;}
var statePath=Path.Combine(root,"state.json");
var state=Updates.Read<InstallState>(statePath);
Updates.ValidVersion(state.Current);
if(state.Previous!=null)Updates.ValidVersion(state.Previous);
if(state.Pending!=null)Updates.ValidVersion(state.Pending);
var statusPath=Path.Combine(root,"update-status.json");
void Status(string message){Console.WriteLine(message);Updates.AtomicJson(statusPath,new{version=state.Current,message,checkedAt=DateTimeOffset.UtcNow});}
Process Start(string version,string nonce) {
    var folder=Path.Combine(root,"versions",Updates.ValidVersion(version));
    var info=new ProcessStartInfo(Path.Combine(folder,"GonioWeb.exe")){WorkingDirectory=folder,UseShellExecute=false};
    info.Environment["GONIO_INSTANCE"]=nonce;info.Environment["GONIO_UPDATE_STATUS"]=statusPath;
    return Process.Start(info)??throw new IOException("起動できません。");
}
async Task<bool> Healthy(Process child,string version,string nonce) {
    using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(1)};
    for(int i=0;i<60;i++) {
        if(child.HasExited)return false;
        try {using var doc=JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/health"));var j=doc.RootElement;
            if(j.GetProperty("version").GetString()==version&&j.GetProperty("instance").GetString()==nonce)return true;
        }catch{} await Task.Delay(500);
    }return false;
}
// Crash recovery: a candidate not confirmed healthy is tried once only.
var trialPath=Path.Combine(root,"trial.json");
if(File.Exists(trialPath)) {
    var failed=Updates.Read<InstallState>(trialPath);
    if(state.Current != failed.Pending) {state=state with {Pending=null,Rejected=failed.Pending};Updates.AtomicJson(statePath,state);}
    File.Delete(trialPath);
}
var target=state.Pending??state.Current;
if(state.Pending!=null)Updates.AtomicJson(trialPath,state);
Process? child=null;bool healthy=false;
try{var nonce=Guid.NewGuid().ToString("N");child=Start(target,nonce);healthy=await Healthy(child,target,nonce);}catch(Exception ex){Status(ex.Message);}
if(!healthy) {
    if(child!=null&&!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}child?.Dispose();
    if(state.Pending==null){Status("アプリを起動できませんでした。インストールを確認してください。");return;}
    var failed=state.Pending;state=state with {Pending=null,Rejected=failed};Updates.AtomicJson(statePath,state);File.Delete(trialPath);
    Status("更新版を起動できなかったため、以前のバージョンに戻しました。");
    var nonce=Guid.NewGuid().ToString("N");child=Start(state.Current,nonce);
    if(!await Healthy(child,state.Current,nonce)){if(!child.HasExited)child.Kill(true);throw new IOException("以前の版も起動できません。");}
} else if(state.Pending!=null) {
    state=new InstallState(target,state.Current,null,state.Rejected);Updates.AtomicJson(statePath,state);File.Delete(trialPath);
}
try{Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/"){UseShellExecute=true});}catch{}
using var shutdown=new CancellationTokenSource();
var updates=Task.Run(async()=>{try{var config=Updates.Read<UpdateConfig>(Path.Combine(root,"update-config.json"));await Updates.CheckAndStage(root,config,Status,shutdown.Token);}catch(OperationCanceledException){}catch(Exception ex){Status("更新を取得できません。現在の版で続行します。"+ex.Message);}});
// Closing this console is an explicit user action; an update never stops the child.
await child!.WaitForExitAsync();shutdown.Cancel();await updates;child.Dispose();
static FileStream? AcquireLock(string path){try{return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){return null;}}
