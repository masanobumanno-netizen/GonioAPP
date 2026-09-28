using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Gonio.Shared;

if(args.Contains("--self-test")){await DropboxSelfTests.Run();return;}
var builder=WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")??"http://127.0.0.1:5280");
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=2L*1024*1024*1024);
var root=Environment.GetEnvironmentVariable("GONIO_SHARED_DATA")??Path.Combine(AppContext.BaseDirectory,"data");
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o=>{o.MultipartBodyLengthLimit=2L*1024*1024*1024;o.MemoryBufferThreshold=64*1024;});
builder.Services.AddSingleton(new Store(root));
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root,"keys"))).SetApplicationName("GonioShared");
builder.Services.AddSingleton<DropboxStorage>();builder.Services.AddHostedService<ProcessingWorker>();
builder.Services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddPolicy("login",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"local",_=>new FixedWindowRateLimiterOptions{PermitLimit=10,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));});
var app=builder.Build();var store=app.Services.GetRequiredService<Store>();var dropbox=app.Services.GetRequiredService<DropboxStorage>();
var uploadLease=new SemaphoreSlim(1,1);
app.UseRateLimiter();
app.Use(async(c,next)=>{
    c.Response.Headers.CacheControl="no-store";c.Response.Headers.XContentTypeOptions="nosniff";
    try {
        if(c.Request.Path.StartsWithSegments("/api")&&!new[]{"/api/auth/login","/api/auth/setup","/api/auth/status"}.Contains(c.Request.Path.Value)) {
            var token=c.Request.Headers.Authorization.ToString();var user=store.Authenticate(token.StartsWith("Bearer ")?token[7..]:"");
            if(user==null){c.Response.StatusCode=401;await c.Response.WriteAsJsonAsync(new{error="ログインが必要です。"});return;}
            c.Items["user"]=user;
            if(c.Request.Path.StartsWithSegments("/api/admin")&&user.Role!="admin"){c.Response.StatusCode=403;return;}
        }
        if(c.Request.Method=="PUT"&&c.Request.Path.StartsWithSegments("/api/sessions")){await uploadLease.WaitAsync(c.RequestAborted);try{await next();}finally{uploadLease.Release();}}else await next();
    }catch(ArgumentException ex){c.Response.StatusCode=400;await c.Response.WriteAsJsonAsync(new{error=ex.Message});}
    catch(Exception ex){app.Logger.LogError(ex,"API failed");if(!c.Response.HasStarted){c.Response.StatusCode=500;await c.Response.WriteAsJsonAsync(new{error="サーバー処理に失敗しました。ローカルデータは保持して再試行してください。"});}}
});
app.MapGet("/health",()=>new{app="gonio-shared",version="0.5.0"});
app.MapGet("/api/auth/status",()=>new{setupRequired=store.Query("SELECT id FROM users LIMIT 1").Count==0});
app.MapPost("/api/auth/setup",(Login input)=>{
    var setup=Environment.GetEnvironmentVariable("GONIO_SETUP_TOKEN");
    if(string.IsNullOrEmpty(setup)||!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(Store.HashToken(input.SetupToken??"")),System.Text.Encoding.UTF8.GetBytes(Store.HashToken(setup))))return Results.Json(new{error="初期設定キーが一致しません。"},statusCode:403);
    ValidateUser(input.Username,input.DisplayName??input.Username,"admin");var password=Store.Password(input.Password);
    using var db=store.Open();using var tx=db.BeginTransaction();using var count=Store.Command(db,"SELECT count(*) FROM users");count.Transaction=tx;if(Convert.ToInt64(count.ExecuteScalar())!=0)return Results.Conflict();
    using var command=Store.Command(db,"INSERT INTO users VALUES($id,$name,$display,$password,'admin',1)",("$id",Guid.NewGuid().ToString()),("$name",input.Username),("$display",input.DisplayName??input.Username),("$password",password));command.Transaction=tx;command.ExecuteNonQuery();tx.Commit();return Results.Ok(new{message="管理者を作成しました。ログインしてください。"});
}).RequireRateLimiting("login");
app.MapPost("/api/auth/login",(Login input)=>{
    if(input.Password.Length>256)return Results.Unauthorized();
    var row=store.Query("SELECT * FROM users WHERE username=$name AND active=1",("$name",input.Username)).FirstOrDefault();
    if(row==null||!Store.VerifyPassword(input.Password,(string)row["password"]!))return Results.Json(new{error="ユーザー名またはパスワードが違います。"},statusCode:401);
    var user=User.From(row);var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));store.Exec("DELETE FROM tokens WHERE expires<$now",("$now",DateTimeOffset.UtcNow.ToString("O")));store.Exec("INSERT INTO tokens VALUES($h,$id,$expires)",("$h",Store.HashToken(token)),("$id",user.Id),("$expires",DateTimeOffset.UtcNow.AddHours(12).ToString("O")));store.Audit(user.Id,"login",user.Id);return Results.Ok(new{token,user});
}).RequireRateLimiting("login");
app.MapGet("/api/auth/me",(HttpContext c)=>Who(c));
app.MapPost("/api/auth/logout",(HttpContext c)=>{store.Exec("DELETE FROM tokens WHERE hash=$h",("$h",Store.HashToken(c.Request.Headers.Authorization.ToString()[7..])));return Results.Ok();});
app.MapGet("/api/admin/users",()=>store.Query("SELECT id,username,display_name AS displayName,role,active FROM users ORDER BY username"));
app.MapPost("/api/admin/users",(HttpContext c,UserEdit input)=>{
    ValidateUser(input.Username,input.DisplayName,input.Role);var id=Guid.NewGuid().ToString();var hash=Store.Password(input.Password??"");
    if(store.Query("SELECT id FROM users WHERE username=$u",("$u",input.Username)).Count>0)return Results.Conflict(new{error="ユーザー名は使用済みです。"});
    store.Exec("INSERT INTO users VALUES($i,$u,$d,$p,$r,$a)",("$i",id),("$u",input.Username),("$d",input.DisplayName),("$p",hash),("$r",input.Role),("$a",input.Active?1:0));store.Audit(Who(c).Id,"user.create",id);return Results.Ok(new{id});
});
app.MapPut("/api/admin/users/{id}",(HttpContext c,string id,UserEdit input)=>{
    Store.Id(id);ValidateUser(input.Username,input.DisplayName,input.Role);string? password=input.Password is {Length:>0}?Store.Password(input.Password):null;
    using var db=store.Open();using var tx=db.BeginTransaction();using var command=Store.Command(db,"SELECT count(*) FROM users WHERE role='admin' AND active=1 AND id<>$i",("$i",id));command.Transaction=tx;
    if((!input.Active||input.Role!="admin")&&Convert.ToInt64(command.ExecuteScalar())==0)return Results.Conflict(new{error="有効な管理者を最低1人残してください。"});
    using var edit=Store.Command(db,"UPDATE users SET username=$u,display_name=$d,role=$r,active=$a,password=COALESCE($p,password) WHERE id=$i",("$i",id),("$u",input.Username),("$d",input.DisplayName),("$r",input.Role),("$a",input.Active?1:0),("$p",password));edit.Transaction=tx;edit.ExecuteNonQuery();using var revoke=Store.Command(db,"DELETE FROM tokens WHERE user_id=$i",("$i",id));revoke.Transaction=tx;revoke.ExecuteNonQuery();tx.Commit();store.Audit(Who(c).Id,"user.edit",id);return Results.Ok();
});
app.MapGet("/api/settings",()=>JsonNode.Parse(store.Setting("preferences")??"{\"duration\":2,\"window\":10,\"axis\":\"auto\",\"min\":0,\"max\":150}"));
app.MapPut("/api/admin/settings",(HttpContext c,JsonObject settings)=>{
    var duration=settings["duration"]?.GetValue<int>()??0;var window=settings["window"]?.GetValue<int>()??0;var min=settings["min"]?.GetValue<double>()??double.NaN;var max=settings["max"]?.GetValue<double>()??double.NaN;
    if(duration<1||duration>5||!new[]{10,20,30}.Contains(window)||!new[]{"auto","fixed"}.Contains((string?)settings["axis"])||!double.IsFinite(min)||!double.IsFinite(max)||min>=max)throw new ArgumentException("設定値が不正です。");
    store.Setting("preferences",settings.ToJsonString());store.Audit(Who(c).Id,"settings.update","preferences");return Results.Ok();
});
SessionEndpoints.Map(app,store);
app.MapGet("/api/admin/dropbox/status",()=>dropbox.Status());
app.MapPost("/api/admin/dropbox/connect",()=>Results.Ok(new{url=dropbox.Authorize()}));
app.MapGet("/dropbox/callback",async(HttpContext c)=>{await dropbox.Complete(c.Request.Query["code"].ToString(),c.Request.Query["state"].ToString());return Results.Text("Dropbox接続が完了しました。Gonio Webの設定画面へ戻ってください。","text/plain; charset=utf-8");});
app.MapGet("/api/admin/audit",()=>store.Query("SELECT * FROM audit ORDER BY id DESC LIMIT 200"));
await app.RunAsync();
static User Who(HttpContext c)=>(User)c.Items["user"]!;
static void ValidateUser(string username,string display,string role){if(!System.Text.RegularExpressions.Regex.IsMatch(username,@"^[a-zA-Z0-9_.-]{3,64}$")||string.IsNullOrWhiteSpace(display)||display.Length>80||!new[]{"admin","user"}.Contains(role))throw new ArgumentException("ユーザー名・表示名・権限が不正です。");}
