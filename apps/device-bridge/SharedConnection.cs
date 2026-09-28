using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
namespace Gonio.DeviceBridge;
public sealed class SharedConnection
{
    readonly HttpClient http=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(30)};
    readonly ConcurrentDictionary<string,(string Id,string Name,DateTimeOffset Expires)> identities=new();
    readonly ConcurrentDictionary<string,string> localCookies=new();
    readonly ConcurrentDictionary<string,string> ownerTokens=new();
    readonly SemaphoreSlim transfer=new(1,1);
    readonly SemaphoreSlim saves=new(1,1);
    public string Root{get;}
    public Uri Server{get;}
    static readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    public SharedConnection(string root)
    {
        Root=Path.Combine(root,"outbox");Directory.CreateDirectory(Root);
        var url=Environment.GetEnvironmentVariable("GONIO_SHARED_URL")??"http://127.0.0.1:5280/";
        Server=new Uri(url.TrimEnd('/')+"/");if(Server.Scheme!="https"&&!(Server.Scheme=="http"&&Server.IsLoopback))throw new ArgumentException("共有サーバーにはHTTPS URLを指定してください。");
    }
    public bool IsAuthenticated(HttpContext c){var token=Token(c);return identities.TryGetValue(token,out var user)&&user.Expires>DateTimeOffset.UtcNow;}
    string Token(HttpContext c){var h=c.Request.Headers.Authorization.ToString();if(h.StartsWith("Bearer "))return h[7..];return c.Request.Cookies.TryGetValue("GonioLocal",out var cookie)&&localCookies.TryGetValue(cookie,out var token)?token:"";}
    public void Map(WebApplication app)
    {
        app.Map("/api/{**path}",async(HttpContext c,string path)=>{
            if(!path.All(ch=>char.IsAsciiLetterOrDigit(ch)||ch is '/' or '-' or '_')){c.Response.StatusCode=400;return;}
            var token=Token(c);using var request=new HttpRequestMessage(new HttpMethod(c.Request.Method),new Uri(Server,"api/"+path+c.Request.QueryString));
            if(token.Length>0)request.Headers.Authorization=new("Bearer",token);
            if(c.Request.ContentLength is >0 || c.Request.Headers.ContainsKey("Transfer-Encoding")){request.Content=new StreamContent(c.Request.Body);if(c.Request.ContentType!=null)request.Content.Headers.ContentType=MediaTypeHeaderValue.Parse(c.Request.ContentType);}
            if(c.Request.Headers.Range.Count>0)request.Headers.TryAddWithoutValidation("Range",c.Request.Headers.Range.ToString());
            if(path=="auth/logout"){if(identities.TryRemove(token,out var who))ownerTokens.TryRemove(who.Id,out _);foreach(var entry in localCookies.Where(x=>x.Value==token))localCookies.TryRemove(entry.Key,out _);c.Response.Cookies.Delete("GonioLocal");}
            try {
                using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,c.RequestAborted);c.Response.StatusCode=(int)response.StatusCode;c.Response.ContentType=response.Content.Headers.ContentType?.ToString();
                if(response.Content.Headers.ContentRange!=null)c.Response.Headers.ContentRange=response.Content.Headers.ContentRange.ToString();
                if(response.IsSuccessStatusCode&&path=="auth/login") {
                    var bytes=await response.Content.ReadAsByteArrayAsync(c.RequestAborted);using var data=JsonDocument.Parse(bytes);var newToken=data.RootElement.GetProperty("token").GetString()!;Remember(newToken,data.RootElement.GetProperty("user"));var cookie=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));localCookies[cookie]=newToken;c.Response.Cookies.Append("GonioLocal",cookie,new CookieOptions{HttpOnly=true,SameSite=SameSiteMode.Strict,MaxAge=TimeSpan.FromHours(12),IsEssential=true});await c.Response.Body.WriteAsync(bytes,c.RequestAborted);return;
                }
                if(response.IsSuccessStatusCode&&path=="auth/me"){var bytes=await response.Content.ReadAsByteArrayAsync(c.RequestAborted);using var data=JsonDocument.Parse(bytes);Remember(token,data.RootElement);await c.Response.Body.WriteAsync(bytes,c.RequestAborted);return;}
                await response.Content.CopyToAsync(c.Response.Body,c.RequestAborted);
            }catch(HttpRequestException){c.Response.StatusCode=503;await c.Response.WriteAsJsonAsync(new{error="共有サーバーへ接続できません。PC内の保存データは保持しています。"});}
        });
        app.MapGet("/local/status",()=>new{server=Server.GetLeftPart(UriPartial.Authority),sourceBuild=Environment.GetEnvironmentVariable("GONIO_SOURCE_BUILD")=="1"});
        app.MapGet("/local/outbox",(HttpContext c)=>{
            if(!IsAuthenticated(c))return Results.Unauthorized();var owner=identities[Token(c)].Id;
            return Results.Ok(Directory.GetFiles(Root,"state.json",SearchOption.AllDirectories).Select(p=>{try{return JsonNode.Parse(File.ReadAllText(p));}catch{return null;}}).Where(s=>(string?)s?["ownerId"]==owner));
        });
        app.MapPut("/local/sessions/{id}",async(HttpContext c,string id)=>{
            if(!IsAuthenticated(c))return Results.Unauthorized();if(!Guid.TryParseExact(id,"D",out _))return Results.BadRequest();
            var who=identities[Token(c)];var destination=Path.Combine(Root,id);await saves.WaitAsync(c.RequestAborted);
            try {
                if(Directory.Exists(destination)){var state=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(destination,"state.json")))!;return (string?)state["ownerId"]==who.Id?Results.Ok(new{id,queued=true}):Results.Conflict();}
                var incoming=Path.Combine(Root,"incoming-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(incoming);
                try {var form=await c.Request.ReadFormAsync();foreach(var key in new[]{"session","csv","video"}){var file=form.Files.GetFile(key);if(file==null){if(key!="video")throw new ArgumentException("保存ファイルが不足しています。");continue;}await using var output=File.Create(Path.Combine(incoming,key));await file.CopyToAsync(output,c.RequestAborted);}
                    using var meta=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(incoming,"session")));if(meta.RootElement.GetProperty("id").GetString()!=id)throw new ArgumentException("セッションIDが一致しません。");
                    var state=new{id,ownerId=who.Id,status="pending",message="PC内に保存済み。共有サーバーへの送信待ちです。",videoType=form.Files.GetFile("video")?.ContentType??"video/webm"};Atomic(Path.Combine(incoming,"state.json"),state);Directory.Move(incoming,destination);
                }finally{if(Directory.Exists(incoming))Directory.Delete(incoming,true);}return Results.Ok(new{id,queued=true});
            }finally{saves.Release();}
        }).DisableAntiforgery();
        app.MapPost("/local/outbox/retry",async(HttpContext c)=>{if(!IsAuthenticated(c))return Results.Unauthorized();await Sync(c.RequestAborted);return Results.Ok();});
        app.Lifetime.ApplicationStarted.Register(()=>_ = Task.Run(async()=>{while(!app.Lifetime.ApplicationStopping.IsCancellationRequested){try{await Sync(app.Lifetime.ApplicationStopping);}catch(OperationCanceledException){break;}catch(Exception ex){app.Logger.LogError(ex,"Outbox sync failed");}try{await Task.Delay(15000,app.Lifetime.ApplicationStopping);}catch(OperationCanceledException){break;}}}));
    }
    void Remember(string token,JsonElement user){var id=user.GetProperty("id").GetString()!;identities[token]=(id,user.GetProperty("displayName").GetString()!,DateTimeOffset.UtcNow.AddHours(12));ownerTokens[id]=token;}
    async Task Sync(CancellationToken ct)
    {
        if(!await transfer.WaitAsync(0,ct))return;
        try{foreach(var folder in Directory.GetDirectories(Root).Where(p=>!Path.GetFileName(p).StartsWith("incoming-"))){var statePath=Path.Combine(folder,"state.json");if(!File.Exists(statePath))continue;var state=JsonNode.Parse(await File.ReadAllTextAsync(statePath,ct))!.AsObject();if((string?)state["status"]=="complete"||!ownerTokens.TryGetValue((string)state["ownerId"]!,out var token))continue;
            try {using var request=new HttpRequestMessage(HttpMethod.Put,new Uri(Server,"api/sessions/"+Uri.EscapeDataString((string)state["id"]!)));request.Headers.Authorization=new("Bearer",token);using var form=new MultipartFormDataContent();foreach(var name in new[]{"session","csv","video"}){var path=Path.Combine(folder,name);if(!File.Exists(path))continue;var content=new StreamContent(File.OpenRead(path));content.Headers.ContentType=new(name=="video"?(string)state["videoType"]!:name=="session"?"application/json":"text/csv");form.Add(content,name,name);}request.Content=form;using var response=await http.SendAsync(request,ct);if((int)response.StatusCode==401){ownerTokens.TryRemove((string)state["ownerId"]!,out _);throw new IOException("再ログインして同期を再試行してください。");}if(!response.IsSuccessStatusCode)throw new IOException("共有サーバー保存: HTTP "+(int)response.StatusCode);state["status"]="complete";state["message"]="共有サーバーに保存済み。Dropbox・解析動画の処理状態は共有履歴で確認してください。";
            }catch(OperationCanceledException){throw;}catch(Exception ex){state["status"]="error";state["message"]=ex.Message;}
            Atomic(statePath,state);
        }}finally{transfer.Release();}
    }
    static void Atomic(string path,object value){var tmp=path+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(value,json));File.Move(tmp,path,true);}
}
