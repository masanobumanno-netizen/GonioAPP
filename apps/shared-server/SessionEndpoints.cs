using System.Text.Json.Nodes;
using System.Text.Json;
namespace Gonio.Shared;
public static class SessionEndpoints
{
    static User Who(HttpContext c)=>(User)c.Items["user"]!;
    static Dictionary<string,object?>? Row(Store store,string id)=>store.Query("SELECT * FROM sessions WHERE id=$id AND deleted=0",("$id",Store.Id(id))).FirstOrDefault();
    public static void Map(WebApplication app,Store store)
    {
        app.MapGet("/api/sessions/deleted",()=>store.Query("SELECT id FROM sessions WHERE deleted=1").Select(r=>r["id"]));
        app.MapGet("/api/sessions",(HttpContext c)=>{
            var offset=int.TryParse(c.Request.Query["offset"],out var minutes)?Math.Clamp(minutes,-840,840):0;var q=c.Request.Query["q"].ToString();var date=c.Request.Query["date"].ToString();var side=c.Request.Query["side"].ToString();var op=c.Request.Query["operator"].ToString();
            return Results.Ok(store.Query("SELECT s.id,s.metadata,s.analysis,s.sync,s.error,s.sync_error,u.display_name FROM sessions s JOIN users u ON u.id=s.owner WHERE s.deleted=0 ORDER BY s.created DESC").Select(r=>Summary(r)).Where(s=>(q.Length==0||((string?)s["subjectId"]+" "+(string?)s["condition"]).Contains(q,StringComparison.OrdinalIgnoreCase))&&(side.Length==0||(string?)s["side"]==side)&&(date.Length==0||DateTimeOffset.Parse((string)s["startedAt"]!).ToOffset(TimeSpan.FromMinutes(offset)).ToString("yyyy-MM-dd")==date)&&(op.Length==0||((string?)s["operator"]??"").Contains(op,StringComparison.OrdinalIgnoreCase))).Take(1000));
        });
        app.MapGet("/api/sessions/{id}/status",(string id)=>{var row=Row(store,id);return row==null?Results.NotFound():Results.Ok(new{analysis=row["analysis"],sync=row["sync"],error=row["error"],syncError=row["sync_error"]});});
        app.MapGet("/api/sessions/{id}",(string id)=>{var row=Row(store,id);if(row==null)return Results.NotFound();var path=Path.Combine(store.SessionDir(id),"session.json");var data=JsonNode.Parse(File.ReadAllText(path))!.AsObject();data["analysisStatus"]=(string)row["analysis"]!;data["syncStatus"]=(string)row["sync"]!;data["processingError"]=(string)row["error"]!;data["syncError"]=(string)row["sync_error"]!;data["remote"]=true;return Results.Json(data);});
        app.MapPut("/api/sessions/{id}",async(HttpContext c,string id)=>{
            Store.Id(id);var existing=store.Query("SELECT owner,deleted FROM sessions WHERE id=$id",("$id",id)).FirstOrDefault();
            if(existing!=null)return (string)existing["owner"]! == Who(c).Id && Convert.ToInt64(existing["deleted"])==0?Results.Ok(new{id,alreadySaved=true}):Results.Conflict(new{error="このIDは使用できません。"});
            var staging=Path.Combine(store.Root,"incoming",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
            try {
                var form=await c.Request.ReadFormAsync();var meta=form.Files.GetFile("session")??throw new ArgumentException("セッション情報がありません。");if(meta.Length>128*1024*1024)throw new ArgumentException("セッション情報が大きすぎます。");
                using var source=meta.OpenReadStream();var data=(await JsonNode.ParseAsync(source))?.AsObject()??throw new ArgumentException("セッション情報が不正です。");
                if((string?)data["id"]!=id||(data["samples"] is not JsonArray samples)||samples.Count>3600000||string.IsNullOrWhiteSpace((string?)data["subjectId"])||((string?)data["subjectId"])!.Length>80||!new[]{"right","left"}.Contains((string?)data["side"])||!DateTimeOffset.TryParse((string?)data["startedAt"],out _))throw new ArgumentException("セッション情報が不正です。");
                for(int i=0;i<samples.Count;i++){var sample=samples[i]!;if(sample["sample_index"]?.GetValue<int>()!=i||sample["elapsed_ms"]?.GetValue<double>()!=i*10.0||!double.IsFinite(sample["raw_angle_deg"]?.GetValue<double>()??double.NaN)||!double.IsFinite(sample["smoothed_angle_deg"]?.GetValue<double>()??double.NaN))throw new ArgumentException("サンプル列が不正です。");}
                data.Remove("chunks");data["operatorId"]=Who(c).Id;data["operator"]=Who(c).DisplayName;
                var csv=form.Files.GetFile("csv")??throw new ArgumentException("CSVがありません。");await Save(csv,Path.Combine(staging,"data.csv"));
                var video=form.Files.GetFile("video");if(video!=null&&video.Length>0){var ext=video.ContentType.StartsWith("video/mp4")?"mp4":"webm";await Save(video,Path.Combine(staging,"raw_video."+ext));data["videoExtension"]=ext;}
                await File.WriteAllTextAsync(Path.Combine(staging,"session.json"),data.ToJsonString());
                var summary=data.DeepClone().AsObject();summary.Remove("samples");var destination=store.SessionDir(id);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // An orphan left by a crash is retained; never silently overwrite user files.
                if(Directory.Exists(destination))Directory.Move(destination,destination+".orphan-"+Guid.NewGuid().ToString("N"));
                Directory.Move(staging,destination);
                store.Exec("INSERT INTO sessions(id,owner,metadata,created,analysis) VALUES($i,$o,$m,$c,$a)",("$i",id),("$o",Who(c).Id),("$m",summary.ToJsonString()),("$c",(string?)data["startedAt"]),("$a",video!=null&&video.Length>0?"pending":"no-video"));store.Audit(Who(c).Id,"session.save",id);return Results.Ok(new{id});
            }finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
        }).DisableAntiforgery();
        app.MapGet("/api/sessions/{id}/files/{kind}",(string id,string kind)=>{
            if(Row(store,id)==null)return Results.NotFound();var dir=store.SessionDir(id);var file=kind switch{"csv"=>"data.csv","analysis"=>"analysis_video.mp4","video"=>File.Exists(Path.Combine(dir,"raw_video.mp4"))?"raw_video.mp4":"raw_video.webm",_=>""};
            if(file==""||!File.Exists(Path.Combine(dir,file)))return Results.NotFound();return Results.File(Path.Combine(dir,file),kind=="csv"?"text/csv":file.EndsWith("mp4")?"video/mp4":"video/webm",enableRangeProcessing:true);
        });
        app.MapPost("/api/sessions/{id}/retry",(HttpContext c,string id)=>{var row=Row(store,id);if(row==null)return Results.NotFound();if((string)row["owner"]! != Who(c).Id&&Who(c).Role!="admin")return Results.StatusCode(403);store.Setting("retry."+id,DateTimeOffset.UtcNow.ToString("O"));store.Exec("UPDATE sessions SET analysis=CASE WHEN analysis='error' THEN 'pending' ELSE analysis END,sync=CASE WHEN sync='error' THEN 'pending' ELSE sync END WHERE id=$id",("$id",id));return Results.Ok();});
        app.MapDelete("/api/admin/sessions/{id}",(HttpContext c,string id)=>{store.Exec("UPDATE sessions SET deleted=1 WHERE id=$id",("$id",Store.Id(id)));store.Audit(Who(c).Id,"session.hide",id);return Results.Ok(new{message="履歴から非表示にしました。保存ファイルは保持しています。"});});
        app.MapGet("/api/stimuli",()=>store.Query("SELECT id,name,extension,created,sync FROM stimuli WHERE deleted=0 ORDER BY created DESC"));
        app.MapPost("/api/admin/stimuli",async(HttpContext c)=>{
            var form=await c.Request.ReadFormAsync();var name=form["name"].ToString().Trim();if(name.Length==0||name.Length>160)throw new ArgumentException("動画名を指定してください。");var file=form.Files.GetFile("video")??throw new ArgumentException("動画を指定してください。");
            if(!new[]{"video/mp4","video/webm"}.Contains(file.ContentType))throw new ArgumentException("MP4かWebMを指定してください。");var ext=file.ContentType=="video/mp4"?"mp4":"webm";var id=Guid.NewGuid().ToString();var dir=Path.Combine(store.Root,"stimuli");Directory.CreateDirectory(dir);await Save(file,Path.Combine(dir,id+"."+ext));store.Exec("INSERT INTO stimuli(id,name,extension,created) VALUES($i,$n,$e,$c)",("$i",id),("$n",name),("$e",ext),("$c",DateTimeOffset.UtcNow.ToString("O")));store.Audit(Who(c).Id,"stimulus.add",id);return Results.Ok(new{id});
        }).DisableAntiforgery();
        app.MapGet("/api/stimuli/{id}/video",(string id)=>{var row=store.Query("SELECT extension FROM stimuli WHERE id=$i AND deleted=0",("$i",Store.Id(id))).FirstOrDefault();return row==null?Results.NotFound():Results.File(Path.Combine(store.Root,"stimuli",id+"."+row["extension"]),"video/"+row["extension"],enableRangeProcessing:true);});
        app.MapPut("/api/admin/stimuli/{id}",(HttpContext c,string id,JsonObject data)=>{var name=((string?)data["name"]??"").Trim();if(name.Length==0||name.Length>160)throw new ArgumentException("動画名が不正です。");store.Exec("UPDATE stimuli SET name=$n,sync='pending' WHERE id=$i AND deleted=0",("$n",name),("$i",Store.Id(id)));store.Audit(Who(c).Id,"stimulus.rename",id);return Results.Ok();});
        app.MapDelete("/api/admin/stimuli/{id}",(HttpContext c,string id)=>{store.Exec("UPDATE stimuli SET deleted=1 WHERE id=$i",("$i",Store.Id(id)));store.Audit(Who(c).Id,"stimulus.hide",id);return Results.Ok();});
    }
    static JsonObject Summary(Dictionary<string,object?> row){var data=JsonNode.Parse((string)row["metadata"]!)!.AsObject();data["operator"]=(string)row["display_name"]!;data["analysisStatus"]=(string)row["analysis"]!;data["syncStatus"]=(string)row["sync"]!;data["remote"]=true;return data;}
    static async Task Save(IFormFile file,string path){if(file.Length==0||file.Length>2L*1024*1024*1024)throw new ArgumentException("ファイルサイズが不正です。");await using var output=File.Create(path);await file.CopyToAsync(output);}
}
