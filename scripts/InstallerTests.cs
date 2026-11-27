using System;
using System.IO;
using System.IO.Compression;
internal static class InstallerTests
{
    static void Check(bool condition,string message) {if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            string root=Path.GetFullPath(args[0]),stage=Path.Combine(root,"stage"),installed=Path.Combine(root,"installed");
            Directory.CreateDirectory(stage);Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(stage,"app.dll"),"first");Directory.CreateDirectory(Path.Combine(stage,"runtime"));File.WriteAllText(Path.Combine(stage,"runtime","core.dll"),"runtime");
            StreamerSetup.DeployFiles(stage,installed,null);
            Check(File.ReadAllText(Path.Combine(installed,"app.dll"))=="first" && File.Exists(Path.Combine(installed,"runtime","core.dll")),"First installation copies application and nested runtime files");
            Directory.CreateDirectory(Path.Combine(installed,"Data"));File.WriteAllText(Path.Combine(installed,"Data","library.json"),"saved selections");
            File.WriteAllText(Path.Combine(stage,"app.dll"),"updated");StreamerSetup.DeployFiles(stage,installed,null);
            Check(File.ReadAllText(Path.Combine(installed,"app.dll"))=="updated" && File.ReadAllText(Path.Combine(installed,"Data","library.json"))=="saved selections","Upgrade replaces application files and preserves user data");
            File.WriteAllText(Path.Combine(stage,"app.dll"),"broken upgrade");int count=0;bool failed=false;
            try {StreamerSetup.DeployFiles(stage,installed,path=>{if(++count==2)throw new IOException("Injected copy failure");});}catch(IOException){failed=true;}
            Check(failed && File.ReadAllText(Path.Combine(installed,"app.dll"))=="updated","A failed upgrade restores overwritten files");
            using(var stream=new MemoryStream())
            {
                using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true)) using(var writer=new StreamWriter(zip.CreateEntry("../escaped.txt").Open()))writer.Write("bad");
                stream.Position=0;bool rejected=false;try{StreamerSetup.ExtractApplication(stream,Path.Combine(root,"extract"));}catch(InvalidDataException){rejected=true;}
                Check(rejected && !File.Exists(Path.Combine(root,"escaped.txt")),"Archive traversal is rejected before writing outside staging");
            }
            var values=StreamerSetup.RegistrationValues(installed);
            Check((string)values["DisplayName"]=="Virtual Desktop Unlock" && (string)values["DisplayVersion"]=="0.3.7-alpha" && (string)values["UninstallString"]=="\""+Path.Combine(installed,"Virtual Desktop Unlock Installer.exe")+"\" --remove-app", "Installed-app registration exposes the correct name, version and quoted uninstall command");
            using(var form=StreamerSetup.CreateCompletionForm(installed))
            {
                Check(((System.Windows.Forms.CheckBox)form.Controls["desktopShortcut"]).Checked && ((System.Windows.Forms.CheckBox)form.Controls["launchApplication"]).Checked,"Completion defaults select desktop shortcut and application launch");
                form.ShowInTaskbar=false;form.StartPosition=System.Windows.Forms.FormStartPosition.Manual;form.Location=new System.Drawing.Point(-30000,-30000);
                form.Show();System.Windows.Forms.Application.DoEvents();
                using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(root,"completion.png"));}
            }
            File.WriteAllText(Path.Combine(installed,"installed.txt"),"0.3.7-alpha");File.WriteAllText(Path.Combine(installed,"Virtual Desktop Unlock Installer.exe"),"installer");
            StreamerSetup.WriteInstalledFiles(installed,new[]{"app.dll","runtime\\core.dll"});
            StreamerSetup.RemoveInstalledFiles(installed);
            Check(!File.Exists(Path.Combine(installed,"app.dll")) && !Directory.Exists(Path.Combine(installed,"runtime")) && File.ReadAllText(Path.Combine(installed,"Data","library.json"))=="saved selections","Uninstall deletes managed app/runtime files and preserves user data");
            File.WriteAllText(Path.Combine(installed,"keep.txt"),"untouched");
            File.WriteAllText(Path.Combine(installed,StreamerSetup.InstalledFilesName),"<InstalledFiles product='Virtual Desktop Unlock'><File>keep.txt</File><File>../escaped.txt</File></InstalledFiles>");
            bool unsafeRejected=false;try{StreamerSetup.RemoveInstalledFiles(installed);}catch(InvalidDataException){unsafeRejected=true;}
            Check(unsafeRejected && File.Exists(Path.Combine(installed,"keep.txt")),"Uninstall validates every manifest path before deleting any file");
            string shortcut=Path.Combine(root,"app-shortcut.lnk");StreamerSetup.CreateShortcut(shortcut,installed);
            StreamerSetup.RemoveShortcut(shortcut,Path.Combine(root,"another-installation"));
            Check(File.Exists(shortcut),"Uninstall keeps shortcuts belonging to a different installation");
            StreamerSetup.RemoveShortcut(shortcut,installed);
            Check(!File.Exists(shortcut),"Uninstall removes its own application shortcut");
            int ticks=0,uiThread=System.Threading.Thread.CurrentThread.ManagedThreadId;
            using(var timer=new System.Windows.Forms.Timer {Interval=20})
            {
                timer.Tick+=(sender,e)=>ticks++;timer.Start();
                int answer=StreamerSetup.RunWithProgress("Installing Virtual Desktop Unlock",update=>{
                    if(System.Threading.Thread.CurrentThread.ManagedThreadId==uiThread)throw new Exception("Installation blocked the UI thread");
                    update("Applying Streamer integration...",-1);System.Threading.Thread.Sleep(200);
                    update("Installing application files...",50);System.Threading.Thread.Sleep(100);return 42;
                },true);
                Check(answer==42 && ticks>2,"Installation progress remains responsive while work runs on a separate thread");
            }
            bool workerError=false;
            try{StreamerSetup.RunWithProgress<int>("Installing",update=>{throw new IOException("Expected worker failure");},true);}catch(IOException error){workerError=error.Message=="Expected worker failure";}
            Check(workerError,"Progress closes and propagates installation failures to the existing error dialog");
            using(var form=StreamerSetup.CreateProgressForm("Installing Virtual Desktop Unlock"))
            {
                form.Controls["status"].Text="Applying integration and restarting Streamer...";
                Check(((System.Windows.Forms.ProgressBar)form.Controls["progress"]).Style==System.Windows.Forms.ProgressBarStyle.Marquee,"Unknown-duration installation stages show an animated activity indicator");
                form.ShowInTaskbar=false;form.StartPosition=System.Windows.Forms.FormStartPosition.Manual;form.Location=new System.Drawing.Point(-30000,-30000);form.Show();System.Windows.Forms.Application.DoEvents();
                using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(root,"progress.png"));}
            }
            Console.WriteLine("Installer deployment/completion/uninstall/progress checks passed: 13");return 0;
        }
        catch(Exception error){Console.WriteLine(error);return 1;}
    }
}
