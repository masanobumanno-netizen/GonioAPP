using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Globalization;
using Microsoft.AspNetCore.StaticFiles;
using Gonio.DeviceBridge;

if (args.Contains("--self-test")) { SelfTests.Run(); return; }
var port = int.TryParse(Environment.GetEnvironmentVariable("GONIO_PORT"), out var configuredPort) ? configuredPort : 4173;
if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
var testMode = args.Contains("--test-device");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=2L*1024*1024*1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o=>o.MultipartBodyLengthLimit=2L*1024*1024*1024);
var app = builder.Build();
var lease = new SemaphoreSlim(1, 1);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var allowedOrigins = new[] { $"http://127.0.0.1:{port}", $"http://localhost:{port}" };
var dataRoot = Environment.GetEnvironmentVariable("GONIO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GonioWeb", "streams");
Directory.CreateDirectory(dataRoot);
var shared=new SharedConnection(Path.GetDirectoryName(dataRoot)!);
app.Use(async (context, next) => {
    if (!new[] {"127.0.0.1", "localhost"}.Contains(context.Request.Host.Host)) { context.Response.StatusCode = 403; return; }
    var origin = context.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && !allowedOrigins.Contains(origin)) { context.Response.StatusCode = 403; return; }
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});
app.UseWebSockets();
var types = new FileExtensionContentTypeProvider(); types.Mappings[".mjs"] = "text/javascript";
app.UseStaticFiles(new StaticFileOptions {ContentTypeProvider = types});
app.MapGet("/", () => Results.File(Path.Combine(app.Environment.WebRootPath,"apps","web","index.html"), "text/html; charset=utf-8"));
app.MapGet("/health", () => Results.Json(new { app = "gonio-web", version = "0.5.0", instance = Environment.GetEnvironmentVariable("GONIO_INSTANCE"), nativeBridge = true, testMode }));
app.MapGet("/device/status", () => Results.Json(new { connected = lease.CurrentCount == 0, platform = OperatingSystem.IsWindows() ? "windows" : "other", testMode, samplingRate = 100 }));
app.MapGet("/device/models", () => Results.Json(DeviceProfiles.All));
app.MapGet("/update/status", () => {
    if(Environment.GetEnvironmentVariable("GONIO_SOURCE_BUILD")=="1")return Results.Json(new{message="ソースビルド版です。計測終了後、WindowsのCodexにGitHubからの更新を依頼してください。",version="0.5.0"});
    var path = Environment.GetEnvironmentVariable("GONIO_UPDATE_STATUS");
    try { return path != null && File.Exists(path) ? Results.Text(File.ReadAllText(path), "application/json") : Results.Json(new { message = "ソース版の更新はWindowsのCodexからGitHubの最新版を取得して実施してください。", version = "0.5.0" }); }
    catch { return Results.Json(new { message = "更新状況を確認中です。" }); }
});
app.MapGet("/device/list", () => {
    try { return Results.Json(new { devices = testMode ? new List<DeviceInfo> {new("TEST000", "SIMULATED-TEST-ONLY")} : ContecDevice.Enumerate() }); }
    catch (Exception ex) { return Results.Json(new { error = Friendly(ex) }, statusCode: 503); }
});
shared.Map(app);
app.Map("/measurement/stream", async context => {
    if(!testMode&&!shared.IsAuthenticated(context)){context.Response.StatusCode=401;return;}
    if (!context.WebSockets.IsWebSocketRequest || !allowedOrigins.Contains(context.Request.Headers.Origin.ToString())) { context.Response.StatusCode = 403; return; }
    if (!await lease.WaitAsync(0)) { context.Response.StatusCode = 409; return; }
    WebSocket accepted;
    try { accepted = await context.WebSockets.AcceptWebSocketAsync(); } catch { lease.Release(); throw; }
    using var socket = accepted;
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
    IAnalogDevice? device = null;
    Task? producer = null, receiver = null;
    string? logPath = null; string endReason="disconnected";
    try {
        var request = context.Request.Query;
        var config = new CaptureConfig(request["device"].ToString(), int.Parse(request["ch1"].ToString(), CultureInfo.InvariantCulture), int.Parse(request["ch2"].ToString(), CultureInfo.InvariantCulture));
        config.Validate();
        device = testMode ? new SimulatedDevice() : new ContecDevice();
        device.Start(config);
        long epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var captureId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        logPath = Path.Combine(dataRoot, captureId+".csv");
        await File.WriteAllTextAsync(Path.Combine(dataRoot,captureId+".json"),JsonSerializer.Serialize(new {config,model=(device as ContecDevice)?.Model,epoch,samplingRateHz=100,source=testMode?"bridge-test":"contec",timestampBasis="host start + sample index * 10 ms; not hardware timestamp",status="capturing"},json),cancel.Token);
        await Send(new {type="connected",source=testMode?"bridge-test":"contec",device=config.DeviceName,model=(device as ContecDevice)?.Model,samplingRate=100,rawLog=logPath,captureId});
        var queue = Channel.CreateBounded<Sample[]>(new BoundedChannelOptions(256) {SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
        producer = Task.Run(async () => {
            Exception? failure = null;
            try {
                using var writer = new StreamWriter(logPath, false, new UTF8Encoding(false));
                await writer.WriteLineAsync("timestamp_iso,sample_index,elapsed_ms,ch1_voltage,ch2_voltage");
                long index = 0; var stale = Stopwatch.StartNew(); var flush = Stopwatch.StartNew();
                while (!cancel.IsCancellationRequested) {
                    var values = device.Read();
                    if (values.Length > 0) {
                        stale.Restart();
                        var batch = SampleDecoder.Decode(values,config,ref index,epoch);
                        foreach (var sample in batch) await writer.WriteLineAsync(FormattableString.Invariant($"{DateTimeOffset.FromUnixTimeMilliseconds(sample.Timestamp):O},{sample.SampleIndex},{sample.ElapsedMs},{sample.Ch1Voltage},{sample.Ch2Voltage}"));
                        if (flush.ElapsedMilliseconds >= 250) { await writer.FlushAsync(); flush.Restart(); }
                        if (!queue.Writer.TryWrite(batch)) throw new IOException("画面への送信が追いつきません。取得済みの電圧はローカルCSVに保存しました。");
                    } else if (stale.Elapsed > TimeSpan.FromSeconds(5)) throw new IOException("5秒間センサデータを取得できません。USB接続を確認してください。");
                    await Task.Delay(20,cancel.Token);
                }
            } catch(OperationCanceledException) {} catch(Exception ex) {failure=ex;}
            finally {queue.Writer.TryComplete(failure);}
        },cancel.Token);
        receiver = Task.Run(async () => {
            try { var buffer=new byte[1024]; while(!cancel.IsCancellationRequested) {var received=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),cancel.Token); if(received.MessageType==WebSocketMessageType.Close)break;} }
            catch(OperationCanceledException) {} catch(WebSocketException) {} finally {cancel.Cancel();}
        });
        await foreach(var batch in queue.Reader.ReadAllAsync(cancel.Token)) await Send(new {type="samples",samples=batch});
    } catch(OperationCanceledException) {} catch(Exception ex) {
        endReason=Friendly(ex);app.Logger.LogError(ex,"Capture failed");
        try { if(socket.State==WebSocketState.Open) await Send(new {type="error",message=Friendly(ex),rawLog=logPath}); } catch {}
    } finally {
        cancel.Cancel();
        if(producer!=null) try{await producer;}catch{}
        if(receiver!=null) try{await receiver;}catch{}
        try { device?.Dispose(); } catch(Exception ex) { app.Logger.LogError(ex,"Device cleanup failed"); }
        if(socket.State==WebSocketState.Open||socket.State==WebSocketState.CloseReceived) try{using var closeTimeout=new CancellationTokenSource(1000);await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"Stopped",closeTimeout.Token);}catch{}
        if(logPath!=null) try{await File.WriteAllTextAsync(logPath+".end.json",JsonSerializer.Serialize(new {endedAt=DateTimeOffset.UtcNow,reason=endReason},json));}catch(Exception ex){app.Logger.LogError(ex,"Could not write capture end marker");}
        lease.Release();
    }
    async Task Send(object value) {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token); timeout.CancelAfter(3000);
        await socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(value,json)),WebSocketMessageType.Text,true,timeout.Token);
    }
});
app.Lifetime.ApplicationStarted.Register(() => {
    var url=$"http://127.0.0.1:{port}/";
    Console.WriteLine($"Gonio Web: {url}\nRaw voltage logs: {dataRoot}\nKeep this window open. Ctrl+C stops the server.");
    if(OperatingSystem.IsWindows()&&args.Contains("--open")) try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch{Console.WriteLine("Open the URL above in Chrome or Edge.");}
});
await app.RunAsync();

static string Friendly(Exception ex) => ex switch {
    DllNotFoundException => "CONTECドライバDLLが見つかりません。API-AIO(WDM) x64をWindowsにインストールしてください。",
    BadImageFormatException => "CONTEC DLLと実行環境の32/64bitが一致しません。x64版ドライバを確認してください。",
    EntryPointNotFoundException => "CONTECドライバのAPIが見つかりません。API-AIO(WDM) 9.31を確認してください。",
    _ => ex.Message
};
