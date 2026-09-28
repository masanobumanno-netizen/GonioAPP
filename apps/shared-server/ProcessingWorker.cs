using System.Text.Json.Nodes;
namespace Gonio.Shared;
public sealed class ProcessingWorker(Store store,DropboxStorage dropbox,ILogger<ProcessingWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        store.Exec("UPDATE sessions SET analysis='pending' WHERE analysis='processing'");store.Exec("UPDATE sessions SET sync='pending' WHERE sync='uploading'");
        while(!ct.IsCancellationRequested){try{await Process(ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception ex){logger.LogError(ex,"Processing queue failed");}try{await Task.Delay(10000,ct);}catch(OperationCanceledException){break;}}
    }
    async Task Process(CancellationToken ct)
    {
        var pending=store.Query("SELECT id FROM sessions WHERE deleted=0 AND analysis='pending' ORDER BY created LIMIT 1").FirstOrDefault();
        if(pending!=null){var id=(string)pending["id"]!;store.Exec("UPDATE sessions SET analysis='processing',error='' WHERE id=$i",("$i",id));try{await AnalysisRenderer.Render(store.SessionDir(id),ct);store.Exec("UPDATE sessions SET analysis='complete',sync='pending' WHERE id=$i",("$i",id));}catch(OperationCanceledException){throw;}catch(Exception ex){store.Exec("UPDATE sessions SET analysis='error',error=$e WHERE id=$i",("$i",id),("$e",ex.Message));}}
        if(!dropbox.Connected)return;
        foreach(var row in store.Query("SELECT id,metadata FROM sessions WHERE deleted=0 AND sync IN ('pending','error') AND analysis NOT IN ('pending','processing') ORDER BY created LIMIT 5")){
            var id=(string)row["id"]!;var last=store.Setting("retry."+id);if(DateTimeOffset.TryParse(last,out var retry)&&retry>DateTimeOffset.UtcNow)continue;
            store.Exec("UPDATE sessions SET sync='uploading' WHERE id=$i",("$i",id));try{var meta=JsonNode.Parse((string)row["metadata"]!)!;var subject=(string?)meta["subjectId"]??"unknown";var slug=System.Text.RegularExpressions.Regex.Replace(subject,"[^a-zA-Z0-9_-]","_");var folder="/GonioWeb/subjects/"+slug+"-"+Store.HashToken(subject)[..8]+"/"+id;
                foreach(var file in Directory.GetFiles(store.SessionDir(id)).Where(p=>!p.Contains(".pending.")))await dropbox.Upload(file,folder+"/"+Path.GetFileName(file),ct);
                store.Exec("UPDATE sessions SET sync='complete',sync_error='' WHERE id=$i",("$i",id));
            }catch(OperationCanceledException){throw;}catch(Exception ex){store.Exec("UPDATE sessions SET sync='error',sync_error=$e WHERE id=$i",("$i",id),("$e",ex.Message));store.Setting("retry."+id,DateTimeOffset.UtcNow.AddMinutes(2).ToString("O"));}}
        foreach(var row in store.Query("SELECT * FROM stimuli WHERE deleted=0 AND sync='pending' LIMIT 5")){var id=(string)row["id"]!;try{await dropbox.Upload(Path.Combine(store.Root,"stimuli",id+"."+row["extension"]),"/GonioWeb/stimulus/"+id+"."+row["extension"],ct);var meta=Path.Combine(store.Root,"stimuli",id+".json");JsonTools.Atomic(meta,new{id,name=row["name"]});await dropbox.Upload(meta,"/GonioWeb/stimulus/"+id+".json",ct);store.Exec("UPDATE stimuli SET sync='complete' WHERE id=$i",("$i",id));}catch(OperationCanceledException){throw;}catch(Exception ex){logger.LogWarning("Stimulus sync failed: {Message}",ex.Message);break;}}
    }
}
