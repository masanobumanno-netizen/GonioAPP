using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
namespace Gonio.Shared;
public static class DropboxSelfTests
{
    public static async Task Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"gonio-dropbox-test-"+Guid.NewGuid().ToString("N"));
        var oldKey=Environment.GetEnvironmentVariable("DROPBOX_APP_KEY");var oldRedirect=Environment.GetEnvironmentVariable("DROPBOX_REDIRECT_URI");
        Environment.SetEnvironmentVariable("DROPBOX_APP_KEY","test-app");Environment.SetEnvironmentVariable("DROPBOX_REDIRECT_URI","https://test.example/dropbox/callback");
        try {
            var store=new Store(root);var handler=new FakeDropbox();using var http=new HttpClient(handler);
            var service=new DropboxStorage(store,new EphemeralDataProtectionProvider(),http);
            var query=Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(service.Authorize()).Query);
            Check(query["code_challenge_method"]=="S256"&&query["token_access_type"]=="offline","PKCE/offline authorization");handler.Challenge=query["code_challenge"].ToString();
            await service.Complete("test-code",query["state"].ToString());Check(service.Connected,"connected");Check(!store.Setting("dropbox.refresh")!.Contains("test-refresh"),"refresh token encryption");
            try{await service.Complete("test-code",query["state"].ToString());throw new Exception("state replay accepted");}catch(ArgumentException){}
            var file=Path.Combine(root,"payload");await File.WriteAllBytesAsync(file,new byte[9*1024*1024+3]);
            await service.Upload(file,"/GonioWeb/test/data.csv",CancellationToken.None);
            await service.Upload(file,"/GonioWeb/test/data.csv",CancellationToken.None);
            Check(handler.Refreshes==1&&handler.Finishes==2&&handler.Appends==4,"chunk offsets and cached access token");
            Console.WriteLine("PASS: Dropbox PKCE binding, single-use state, encrypted refresh, token refresh/cache, chunk upload offsets, overwrite commit, existing folders (mock HTTP; no live Dropbox access)");
        }finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);Environment.SetEnvironmentVariable("DROPBOX_APP_KEY",oldKey);Environment.SetEnvironmentVariable("DROPBOX_REDIRECT_URI",oldRedirect);}
    }
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    sealed class FakeDropbox:HttpMessageHandler
    {
        public string Challenge="";public int Refreshes,Finishes,Appends;long offset;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var uri=request.RequestUri!;Check(uri.Scheme=="https","HTTPS");string output="{}";var status=HttpStatusCode.OK;
            if(uri.AbsolutePath=="/oauth2/token"){
                Check(uri.Host=="api.dropboxapi.com","token host");var fields=Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
                if(fields["grant_type"]=="authorization_code"){
                    var hash=Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"].ToString()))).TrimEnd('=').Replace('+','-').Replace('/','_');Check(hash==Challenge&&fields["code"]=="test-code","PKCE binding");output="{\"refresh_token\":\"test-refresh\"}";
                }else{Check(fields["refresh_token"]=="test-refresh","refresh value");Refreshes++;output="{\"access_token\":\"test-access\",\"expires_in\":14400}";}
            }else{
                Check(request.Headers.Authorization?.ToString()=="Bearer test-access","API authorization");
                if(uri.AbsolutePath=="/2/files/create_folder_v2"){status=HttpStatusCode.Conflict;output="{\"error_summary\":\"path/conflict/folder\"}";}
                else {
                    Check(uri.Host=="content.dropboxapi.com","content host");using var arg=JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());var bytes=await request.Content!.ReadAsByteArrayAsync(ct);
                    if(uri.AbsolutePath.EndsWith("/start")){Check(bytes.Length==0,"empty start");offset=0;output="{\"session_id\":\"test-session\"}";}
                    else {Check(arg.RootElement.GetProperty("cursor").GetProperty("offset").GetInt64()==offset,"upload offset");
                        if(uri.AbsolutePath.EndsWith("/append_v2")){Check(bytes.Length<=8*1024*1024,"chunk size");offset+=bytes.Length;Appends++;}
                        else {Check(offset==9*1024*1024+3&&bytes.Length==0,"finish length");Check(arg.RootElement.GetProperty("commit").GetProperty("mode").GetString()=="overwrite","idempotent commit");Finishes++;}
                    }
                }
            }
            return new HttpResponseMessage(status){Content=new StringContent(output,Encoding.UTF8,"application/json")};
        }
    }
}
