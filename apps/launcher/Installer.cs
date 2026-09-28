using System.Diagnostics;
using System.Text.Json;
namespace Gonio.Launcher;
public static class Installer
{
    public static void Run()
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows専用です。");
        var source=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GonioWeb","app");
        Directory.CreateDirectory(root);
        using(var mutex=new FileStream(Path.Combine(root,"launcher.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
        {
            if(File.Exists(Path.Combine(root,"state.json")))throw new IOException("導入済みです。デスクトップのGonio Webから起動してください。");
            string version=Updates.ValidVersion(File.ReadAllText(Path.Combine(source,"version.txt")).Trim());
            var config=Updates.Read<UpdateConfig>(Path.Combine(source,"update-config.json"));
            Updates.ValidateRepository(config.Repository);
            var target=Path.Combine(root,"versions",version);
            if(Directory.Exists(target))throw new IOException("前回のインストールフォルダが残っています: "+target);
            var staging=Path.Combine(root,"install-"+Guid.NewGuid().ToString("N"));
            try {
                CopyTree(source,staging,true);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);Directory.Move(staging,target);
                CopyTree(Path.Combine(source,"launcher"),Path.Combine(root,"launcher"),false);
                File.Copy(Path.Combine(source,"update-config.json"),Path.Combine(root,"update-config.json"),true);
                // Application data is stored outside versions. No history or raw logs are copied or deleted.
                Updates.AtomicJson(Path.Combine(root,"state.json"),new InstallState(version));
            }finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
        }
        try {
            var type=Type.GetTypeFromProgID("WScript.Shell")!;dynamic shell=Activator.CreateInstance(type)!;
            dynamic shortcut=shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Gonio Web.lnk"));
            shortcut.TargetPath=Path.Combine(root,"launcher","GonioLauncher.exe");shortcut.WorkingDirectory=root;shortcut.Save();
        }catch {Console.WriteLine("ショートカットを作成できません。起動先: "+Path.Combine(root,"launcher","GonioLauncher.exe"));}
        Console.WriteLine("インストール完了。次回からデスクトップのGonio Webを使用してください。");
        Process.Start(new ProcessStartInfo(Path.Combine(root,"launcher","GonioLauncher.exe")){UseShellExecute=true});
    }
    static void CopyTree(string source,string target,bool appOnly)
    {
        Directory.CreateDirectory(target);
        foreach(var file in Directory.GetFiles(source)) {
            var name=Path.GetFileName(file);
            if(appOnly&&(name.StartsWith("Install-Gonio")||name=="update-config.json"))continue;
            File.Copy(file,Path.Combine(target,name),true);
        }
        foreach(var folder in Directory.GetDirectories(source)) {
            if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new IOException("Links are not allowed in installation");
            if(appOnly&&Path.GetFileName(folder)=="launcher")continue;
            CopyTree(folder,Path.Combine(target,Path.GetFileName(folder)),false);
        }
    }
}
