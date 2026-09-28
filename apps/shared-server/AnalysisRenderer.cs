using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Globalization;
namespace Gonio.Shared;
public static class AnalysisRenderer
{
    public static async Task Render(string dir,CancellationToken ct)
    {
        var data=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir,"session.json"),ct))!;
        var points=data["samples"]!.AsArray().Select(s=>(Time:s!["elapsed_ms"]!.GetValue<double>()/1000,Angle:s["smoothed_angle_deg"]!.GetValue<double>())).ToArray();if(points.Length==0)throw new IOException("角度データがありません。");
        var raw=Path.Combine(dir,File.Exists(Path.Combine(dir,"raw_video.mp4"))?"raw_video.mp4":"raw_video.webm");
        using var probe=Start(Environment.GetEnvironmentVariable("FFPROBE_PATH")??"ffprobe",["-v","error","-protocol_whitelist","file,pipe","-format_whitelist","mov,matroska,webm","-show_entries","format=duration","-of","default=noprint_wrappers=1:nokey=1",raw],false);using var probeCancellation=ct.Register(()=>{try{if(!probe.HasExited)probe.Kill(true);}catch(InvalidOperationException){}});var probeError=probe.StandardError.ReadToEndAsync(ct);var durationText=await probe.StandardOutput.ReadToEndAsync(ct);await probe.WaitForExitAsync(ct);await probeError;
        if(probe.ExitCode!=0||!double.TryParse(durationText.Trim(),CultureInfo.InvariantCulture,out var duration)||!double.IsFinite(duration)||duration<=0||duration>36000)throw new IOException("録画の長さを確認できません。FFprobeと録画ファイルを確認してください。");probe.Dispose();
        double offset=0;if(DateTimeOffset.TryParse((string?)data["cameraStartedAt"],out var camera)&&DateTimeOffset.TryParse((string?)data["recordGateAt"],out var record))offset=Math.Max(0,(record-camera).TotalSeconds);
        var settings=data["graphPreferences"];var window=settings?["window"]?.GetValue<int>()??10;window=new[]{10,20,30}.Contains(window)?window:10;
        var fixedAxis=(string?)settings?["axis"]=="fixed";var min=fixedAxis?settings!["min"]!.GetValue<double>():Math.Min(0,points.Min(p=>p.Angle))-5;var max=fixedAxis?settings!["max"]!.GetValue<double>():Math.Max(100,points.Max(p=>p.Angle))+5;if(!double.IsFinite(min)||!double.IsFinite(max)||min>=max)throw new IOException("グラフ軸が不正です。");
        var output=Path.Combine(dir,"analysis_video.pending.mp4");
        using var ffmpeg=Start(Environment.GetEnvironmentVariable("FFMPEG_PATH")??"ffmpeg",["-y","-v","error","-protocol_whitelist","file,pipe","-format_whitelist","mov,matroska,webm","-i",raw,"-f","rawvideo","-pixel_format","rgb24","-video_size","960x240","-framerate","30","-i","pipe:0","-filter_complex","[0:v]scale=960:540:force_original_aspect_ratio=decrease,pad=960:540:(ow-iw)/2:(oh-ih)/2,setsar=1[cam];[cam][1:v]vstack=inputs=2[out]","-map","[out]","-map","0:a?","-c:v","libx264","-preset","veryfast","-crf","23","-pix_fmt","yuv420p","-c:a","aac","-movflags","+faststart","-shortest",output],true);
        var errors=ffmpeg.StandardError.ReadToEndAsync(ct);var stdout=ffmpeg.StandardOutput.ReadToEndAsync(ct);
        try {
            const int w=960,h=240;var frame=new byte[w*h*3];
            void Pixel(int x,int y,byte r,byte g,byte b){if(x<0||x>=w||y<0||y>=h)return;int i=(y*w+x)*3;frame[i]=r;frame[i+1]=g;frame[i+2]=b;}
            void Line(int x0,int y0,int x1,int y1,byte r,byte g,byte b){int dx=Math.Abs(x1-x0),sx=x0<x1?1:-1,dy=-Math.Abs(y1-y0),sy=y0<y1?1:-1,e=dx+dy;for(int safe=0;safe<3000;safe++){Pixel(x0,y0,r,g,b);if(x0==x1&&y0==y1)break;int e2=2*e;if(e2>=dy){e+=dy;x0+=sx;}if(e2<=dx){e+=dx;y0+=sy;}}}
            var digits=new[]{"111101101101111","010110010010111","111001111100111","111001111001111","101101111001001","111100111001111","111100111101111","111001001001001","111101111101111","111101111001111"};
            void Text(string text,int x,int y){foreach(char c in text){var glyph=c>='0'&&c<='9'?digits[c-'0']:c=='-'?"000000111000000":c=='.'?"000000000000010":c=='d'?"001001111101111":c=='e'?"000111111100111":c=='g'?"000111101111001":c=='s'?"000111100011110":"000000000000000";for(int j=0;j<15;j++)if(glyph[j]=='1')for(int a=0;a<2;a++)for(int b=0;b<2;b++)Pixel(x+(j%3)*2+a,y+(j/3)*2+b,70,85,85);x+=8;}}
            for(int f=0;f<(int)Math.Ceiling(duration*30);f++) {
                ct.ThrowIfCancellationRequested();Array.Fill(frame,(byte)250);double t=Math.Max(0,f/30.0-offset),start=Math.Clamp(t-window/2.0,0,Math.Max(0,points[^1].Time-window));
                Text("deg",3,2);Text("s",930,222);for(int i=0;i<=4;i++){int y=15+i*48;Line(52,y,945,y,220,228,228);Text((max-i*(max-min)/4).ToString("0",CultureInfo.InvariantCulture),3,y);}
                for(int i=0;i<=4;i++)Text((start+i*window/4.0).ToString("0.0",CultureInfo.InvariantCulture),52+i*210,222);
                int previousY=0;bool previous=false;for(int x=52;x<=945;x++){double at=start+(x-52)/893.0*window;if(at>points[^1].Time){previous=false;continue;}int index=Math.Clamp((int)Math.Round(at*100),0,points.Length-1);int y=15+(int)(Math.Clamp((max-points[index].Angle)/(max-min),0,1)*192);if(previous)Line(x-1,previousY,x,y,22,134,117);previousY=y;previous=true;}
                int cursor=52+(int)(Math.Clamp((t-start)/window,0,1)*893);Line(cursor,10,cursor,210,210,40,50);Text(t.ToString("0.00",CultureInfo.InvariantCulture),820,3);
                await ffmpeg.StandardInput.BaseStream.WriteAsync(frame,ct);
            }
            ffmpeg.StandardInput.Close();await ffmpeg.WaitForExitAsync(ct);var error=await errors;await stdout;if(ffmpeg.ExitCode!=0)throw new IOException("解析動画生成に失敗しました: "+error[..Math.Min(error.Length,500)]);File.Move(output,Path.Combine(dir,"analysis_video.mp4"),true);
        }finally{if(!ffmpeg.HasExited)ffmpeg.Kill(true);if(File.Exists(output))File.Delete(output);}
    }
    static Process Start(string executable,string[] arguments,bool input){var info=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardInput=input,RedirectStandardError=true,RedirectStandardOutput=true,CreateNoWindow=true};foreach(var arg in arguments)info.ArgumentList.Add(arg);return Process.Start(info)??throw new IOException("動画処理を起動できません。");}
}
