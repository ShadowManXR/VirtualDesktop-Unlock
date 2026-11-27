using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;
using System.Drawing;
using System.Xml;
using Microsoft.Win32;
using System.Text;
using System.Threading;
using VirtualDesktop.Unlock;

[assembly: AssemblyVersion("0.3.7.0")]
[assembly: AssemblyFileVersion("0.3.7.0")]
[assembly: AssemblyProduct("Virtual Desktop Unlock")]

internal static class StreamerSetup
{
    private const string Title = "Virtual Desktop Unlock Installer - 0.3.7-alpha";
    internal const string ProductName = "Virtual Desktop Unlock";
    internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VirtualDesktopUnlock";
    internal const string InstalledFilesName = "installed-files.xml";
    internal static string InstallDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),ProductName); } }
    private const string Installer = "HelperInstaller.ps1";
    private static readonly string[] Payload =
    {
        "VirtualDesktop.LibraryOverrides.dll", "VirtualDesktop.Streamer.exe.config", "manifest.json"
    };

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length>0 && args[0]=="--remove-app-worker") return RemoveApplicationWorker(args);
        if(args.Contains("--remove-app")) return BeginRemoveApplication();
        bool analyze = args.Length > 0 && args[0] == "--analyze";
        bool uninstall = args.Contains("--uninstall");
        bool integrationOnly = args.Contains("--integration-only") || uninstall;
        try
        {
            string executable = analyze ? Path.GetFullPath(args[1]) : SelectExecutable();
            if (executable == null) return 1;
            string report = uninstall ? "Remove Virtual Desktop Unlock integration from:\n"+executable : (analyze ? Analyze(executable,false) : RunWithProgress("Checking Virtual Desktop", update => {update("Analyzing Streamer methods and dependencies...",-1);return Analyze(executable,true);},false));
            if(analyze) { File.WriteAllText(args[2], report); return 0; }
            if(!uninstall && profile!=null) report="Streamer "+profile.Version+"\n"+executable+"\nAll "+profile.Tokens.Count+" integration symbols resolved.\nDependencies verified.\nDetailed report: %ProgramData%\\Virtual Desktop Unlock\\compatibility-report.txt";
            if(MessageBox.Show(report + "\n\n" + (uninstall ? "Remove integration?" :
                "Install/update Virtual Desktop Unlock and restart Streamer? Disconnect the headset first."), Title,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return 1;
            string applicationDirectory=null;
            string output=RunWithProgress(uninstall?"Removing integration":"Installing Virtual Desktop Unlock",update=>{
                string result=RunInstaller(Path.GetDirectoryName(executable),uninstall,update);
                if(!integrationOnly)applicationDirectory=InstallApplication(update);
                return result;
            },false);
            if(!integrationOnly) ShowCompletion(applicationDirectory);
            else MessageBox.Show(output, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception error)
        {
            if(analyze && args.Length > 2) File.WriteAllText(args[2], error.ToString());
            else MessageBox.Show(error.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    internal static Form CreateProgressForm(string heading)
    {
        var form=new Form {Text=Title,ClientSize=new Size(560,180),Font=new Font("Segoe UI",9F),
            FormBorderStyle=FormBorderStyle.FixedDialog,ControlBox=false,StartPosition=FormStartPosition.CenterScreen};
        form.Controls.Add(new Label {Text=heading,AutoSize=true,Location=new Point(20,20),Font=new Font("Segoe UI",11F,FontStyle.Bold)});
        form.Controls.Add(new Label {Name="status",Text="Preparing...",Location=new Point(22,60),Size=new Size(516,42)});
        form.Controls.Add(new ProgressBar {Name="progress",Location=new Point(22,118),Size=new Size(516,24),Style=ProgressBarStyle.Marquee,MarqueeAnimationSpeed=25});
        return form;
    }

    internal static T RunWithProgress<T>(string heading,Func<Action<string,int>,T> work,bool offscreen)
    {
        T result=default(T);Exception failure=null;bool running=true;
        using(var form=CreateProgressForm(heading))
        {
            if(offscreen){form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);}
            Action<string,int> update=(message,percent)=>{
                form.BeginInvoke(new Action(delegate {
                    form.Controls["status"].Text=message;
                    var bar=(ProgressBar)form.Controls["progress"];
                    bar.Style=percent<0?ProgressBarStyle.Marquee:ProgressBarStyle.Continuous;
                    if(percent>=0)bar.Value=Math.Max(0,Math.Min(100,percent));
                }));
            };
            form.FormClosing+=(sender,e)=>{if(running && e.CloseReason==CloseReason.UserClosing)e.Cancel=true;};
            form.Shown+=(sender,e)=>{
                var worker=new Thread(delegate() {
                    try{result=work(update);}catch(Exception error){failure=error;}
                    form.BeginInvoke(new Action(delegate {running=false;form.DialogResult=failure==null?DialogResult.OK:DialogResult.Abort;}));
                });
                worker.SetApartmentState(ApartmentState.STA);worker.IsBackground=true;worker.Start();
            };
            form.ShowDialog();
        }
        if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private static StreamerProfile profile;
    private static string Analyze(string executable, bool saveReport)
    {
        // The legacy family retains its exact-hash compatibility path.
        if(StreamerProfile.Hash(executable)==StreamerProfile.Original13423)
            return "Verified legacy Streamer 1.34.23 (limited feature support).\n" + executable;
        List<SymbolSignature> baseline;
        using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream(StreamerSymbols.BaselineResource))
            baseline=CompatibilitySignatures.Read(input);
        var result=CompatibilityAnalysis.Analyze(executable,baseline);
        string report="Virtual Desktop Streamer " + result.Version + "\n" + executable + "\nSHA-256: " + result.Sha256 + "\n" + String.Join("\n",result.Messages);
        string logs=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Virtual Desktop Unlock");
        if(saveReport) {Directory.CreateDirectory(logs); File.WriteAllText(Path.Combine(logs,"compatibility-report.txt"),report);}
        if(!result.Compatible) throw new NotSupportedException(report + "\n\nNo integration was installed. This Streamer requires updated compatibility signatures.");
        profile=new StreamerProfile { Sha256=result.Sha256, Version=result.Version, Mvid=result.Mvid };
        foreach(var pair in result.Tokens) profile.Tokens.Add(pair.Key,pair.Value);
        return report;
    }

    private static string InstallApplication(Action<string,int> update=null)
    {
        string destination=InstallDirectory;
        string staging=CreatePrivateDirectory("VDUnlockApp-");
        var changed=new Dictionary<string,byte[]>();
        try
        {
            if(update!=null)update("Extracting application and bundled runtime...",-1);
            using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("Application.zip")) ExtractApplication(input,staging);
            if(update!=null)update("Closing previous application versions...",-1);
            StopApplication(true);
            Directory.CreateDirectory(destination);
            int copied=0,total=Directory.GetFiles(staging,"*",SearchOption.AllDirectories).Length;
            changed=DeployFiles(staging,destination,file=>{if(update!=null)update("Installing application files...",(int)(100L*copied++/Math.Max(1,total)));});
            if(update!=null)update("Creating shortcuts and registering the application...",-1);
            File.WriteAllText(Path.Combine(destination,"installed.txt"),"0.3.7-alpha");
            string installer=Path.Combine(destination,"Virtual Desktop Unlock Installer.exe");
            if(!String.Equals(installer,Assembly.GetExecutingAssembly().Location,StringComparison.OrdinalIgnoreCase))
                File.Copy(Assembly.GetExecutingAssembly().Location,installer,true);
            WriteInstalledFiles(destination,Directory.GetFiles(staging,"*",SearchOption.AllDirectories).Select(file=>file.Substring(staging.Length+1)));
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),"Programs",ProductName+".lnk"),destination);
            using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
            using(var registration=machine.CreateSubKey(UninstallKey))
                foreach(var value in RegistrationValues(destination)) registration.SetValue(value.Key,value.Value,value.Value is int?RegistryValueKind.DWord:RegistryValueKind.String);
            return destination;
        }
        catch { foreach(var entry in changed) {if(entry.Value==null) File.Delete(entry.Key);else File.WriteAllBytes(entry.Key,entry.Value);} throw; }
        finally { foreach(string file in Directory.GetFiles(staging,"*",SearchOption.AllDirectories)) File.Delete(file); foreach(string dir in Directory.GetDirectories(staging,"*",SearchOption.AllDirectories).OrderByDescending(x=>x.Length)) Directory.Delete(dir);Directory.Delete(staging); }
    }

    private static void StopApplication(bool migrateData)
    {
        foreach(var process in Process.GetProcessesByName("Virtual Desktop Unlock")) using(process)
            {
                // Close only this product, including portable older copies.
                string file=VDUnlockInstaller.ProcessControl.ExecutablePath(process.Id);
                if(file==null)continue;
                if(FileVersionInfo.GetVersionInfo(file).ProductName!="Virtual Desktop Unlock") continue;
                if(process.SessionId!=Process.GetCurrentProcess().SessionId) throw new IOException("Close Virtual Desktop Unlock in other Windows sessions before upgrading.");
                process.CloseMainWindow(); if(!process.WaitForExit(3000)) {process.Kill();process.WaitForExit(5000);}
                if(!migrateData) continue;
                string oldData=Path.Combine(Path.GetDirectoryName(file),"Data");
                if(!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Virtual Desktop Unlock","library.json")) && Directory.Exists(oldData))
                    foreach(string data in Directory.GetFiles(oldData,"*",SearchOption.AllDirectories)) {string target=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Virtual Desktop Unlock",data.Substring(oldData.Length+1)); Directory.CreateDirectory(Path.GetDirectoryName(target));if(!File.Exists(target)) File.Copy(data,target,false);}
        }
    }

    internal static Dictionary<string,object> RegistrationValues(string directory)
    {
        return new Dictionary<string,object> {
            {"DisplayName",ProductName},{"DisplayVersion","0.3.7-alpha"},{"Publisher","ShadowManXR"},
            {"InstallLocation",directory},{"DisplayIcon",Path.Combine(directory,ProductName+".exe")},
            {"UninstallString","\""+Path.Combine(directory,ProductName+" Installer.exe")+"\" --remove-app"},
            {"URLInfoAbout","https://github.com/ShadowManXR/VirtualDesktop-Unlock"},
            {"InstallDate",DateTime.Today.ToString("yyyyMMdd")},{"NoModify",1},{"NoRepair",1}
        };
    }

    internal static void CreateShortcut(string shortcut,string directory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut));
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic link=shell.CreateShortcut(shortcut);link.TargetPath=Path.Combine(directory,ProductName+".exe");
        link.WorkingDirectory=directory;link.IconLocation=link.TargetPath+",0";link.Save();
    }

    internal static void RemoveShortcut(string shortcut,string directory)
    {
        if(!File.Exists(shortcut))return;
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic link=shell.CreateShortcut(shortcut);
        if(String.Equals((string)link.TargetPath,Path.Combine(directory,ProductName+".exe"),StringComparison.OrdinalIgnoreCase))File.Delete(shortcut);
    }

    internal static Form CreateCompletionForm(string directory)
    {
        var form=new Form { Text=Title,ClientSize=new Size(560,238),Font=new Font("Segoe UI",9F),
            FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,StartPosition=FormStartPosition.CenterScreen };
        form.Controls.Add(new Label { Text="Virtual Desktop Unlock has been installed.",AutoSize=true,Location=new Point(20,20),Font=new Font("Segoe UI",11F,FontStyle.Bold) });
        form.Controls.Add(new Label { Text=directory,AutoEllipsis=true,Location=new Point(20,55),Size=new Size(520,40) });
        form.Controls.Add(new CheckBox { Name="desktopShortcut",Text="Create a desktop shortcut",Checked=true,AutoSize=true,Location=new Point(22,108) });
        form.Controls.Add(new CheckBox { Name="launchApplication",Text="Launch Virtual Desktop Unlock",Checked=true,AutoSize=true,Location=new Point(22,140) });
        var finish=new Button { Text="Finish",DialogResult=DialogResult.OK,Location=new Point(440,192),Size=new Size(100,30) };
        form.Controls.Add(finish);form.AcceptButton=finish;return form;
    }

    private static void ShowCompletion(string directory)
    {
        using(var form=CreateCompletionForm(directory))
        {
            if(form.ShowDialog()!=DialogResult.OK)return;
            if(((CheckBox)form.Controls["desktopShortcut"]).Checked)
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),ProductName+".lnk"),directory);
            if(((CheckBox)form.Controls["launchApplication"]).Checked) LaunchApplication(directory);
        }
    }

    private static void LaunchApplication(string directory)
    {
        // Delegate to the existing desktop shell so the app runs as the signed-in
        // user, rather than inheriting the installer's administrator token.
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
        dynamic windows=shell.Windows();object location=0,root=null;int handle;
        dynamic desktop=windows.FindWindowSW(ref location,ref root,8,out handle,1);
        if(desktop==null)throw new IOException("Installed successfully. Open Virtual Desktop Unlock from the Start menu to launch it.");
        desktop.Document.Application.ShellExecute(Path.Combine(directory,ProductName+".exe"),"",directory,"open",1);
    }

    internal static void WriteInstalledFiles(string directory,IEnumerable<string> payloadFiles)
    {
        var files=payloadFiles.Concat(new[]{"installed.txt",ProductName+" Installer.exe",InstalledFilesName}).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using(var writer=XmlWriter.Create(Path.Combine(directory,InstalledFilesName),new XmlWriterSettings{Indent=true}))
        {
            writer.WriteStartElement("InstalledFiles");writer.WriteAttributeString("product",ProductName);
            foreach(string file in files){ManagedPath(directory,file);writer.WriteElementString("File",file);}
            writer.WriteEndElement();
        }
    }

    internal static string ManagedPath(string directory,string relative)
    {
        string root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if(String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(":"))throw new InvalidDataException("Unsafe installed-file path.");
        string path=Path.GetFullPath(Path.Combine(root,relative));
        if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Installed file escapes the application folder.");
        for(string parent=Path.GetDirectoryName(path);parent!=null && parent.Length>=root.Length;parent=Path.GetDirectoryName(parent))
            if(Directory.Exists(parent) && (File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)throw new IOException("Application folders must not contain junctions or symbolic links during removal.");
        return path;
    }

    internal static void RemoveInstalledFiles(string directory)
    {
        string manifest=Path.Combine(directory,InstalledFilesName);
        var files=new List<string>();
        using(var reader=XmlReader.Create(manifest,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024 }))
        {
            reader.MoveToContent();if(reader.Name!="InstalledFiles" || reader.GetAttribute("product")!=ProductName)throw new InvalidDataException("No valid application installation record. Reinstall the app before removing it.");
            reader.Read();
            while(!reader.EOF)
            {
                if(reader.NodeType==XmlNodeType.Element){if(reader.Name!="File" || reader.Depth!=1)throw new InvalidDataException("Invalid installed-file record.");files.Add(ManagedPath(directory,reader.ReadElementContentAsString()));}
                else reader.Read();
            }
        }
        // Validate the complete list before deleting anything; retain user-created
        // files and preferences, and remove only empty directories we used.
        foreach(string file in files.Where(f=>!String.Equals(f,manifest,StringComparison.OrdinalIgnoreCase)))if(File.Exists(file))File.Delete(file);
        File.Delete(manifest);
        var parents=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string file in files)for(string folder=Path.GetDirectoryName(file);folder!=null && folder.Length>=directory.Length;folder=Path.GetDirectoryName(folder))parents.Add(folder);
        foreach(string folder in parents.OrderByDescending(p=>p.Length))if(Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())Directory.Delete(folder);
    }

    private static int BeginRemoveApplication()
    {
        try
        {
            string directory=InstallDirectory;
            if(!File.Exists(Path.Combine(directory,InstalledFilesName)))throw new IOException("No managed app installation found. Run the installer again to repair the installation record.");
            if(MessageBox.Show("Remove Virtual Desktop Unlock and its Streamer integration?\n\nDisconnect Virtual Desktop first. Your saved settings and presets will be kept.",Title,MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return 1;
            string temporary=CreatePrivateDirectory("VDUnlockRemove-");string worker=Path.Combine(temporary,ProductName+" Installer.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location,worker);
            Process.Start(new ProcessStartInfo(worker,"--remove-app-worker \""+directory+"\" "+Process.GetCurrentProcess().Id) {UseShellExecute=false,CreateNoWindow=true});
            return 0;
        }
        catch(Exception error){MessageBox.Show(error.Message,Title,MessageBoxButtons.OK,MessageBoxIcon.Warning);return 1;}
    }

    private static int RemoveApplicationWorker(string[] args)
    {
        try
        {
            if(args.Length!=3 || !String.Equals(Path.GetFullPath(args[1]),InstallDirectory,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid application removal location.");
            int parentId=Int32.Parse(args[2]);
            try{using(var parent=Process.GetProcessById(parentId))if(!parent.WaitForExit(30000))throw new IOException("Close the original uninstaller window and retry.");}catch(ArgumentException){}
            StopApplication(false);
            string streamer=StreamerLocation.FindInstalledHelperTarget();
            if(File.Exists(streamer) && File.Exists(Path.Combine(Path.GetDirectoryName(streamer),"VirtualDesktop.LibraryOverrides.install.json")))RunInstaller(Path.GetDirectoryName(streamer),true);
            using(var startup=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true))
                if(startup!=null && String.Equals(startup.GetValue(ProductName) as string,"\""+Path.Combine(args[1],ProductName+".exe")+"\"",StringComparison.OrdinalIgnoreCase))startup.DeleteValue(ProductName,false);
            RemoveInstalledFiles(args[1]);
            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),"Programs",ProductName+".lnk"),args[1]);
            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),ProductName+".lnk"),args[1]);
            using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))machine.DeleteSubKeyTree(UninstallKey,false);
            MessageBox.Show("Virtual Desktop Unlock has been removed. Your saved settings and presets were kept.",Title,MessageBoxButtons.OK,MessageBoxIcon.Information);
            return 0;
        }
        catch(Exception error){MessageBox.Show(error.Message,Title,MessageBoxButtons.OK,MessageBoxIcon.Warning);return 1;}
        finally{ScheduleWorkerCleanup();}
    }

    private static void ScheduleWorkerCleanup()
    {
        string executable=Assembly.GetExecutingAssembly().Location,folder=Path.GetDirectoryName(executable);
        // These exact paths are generated in our administrator-only temp folder.
        if(!Path.GetFileName(folder).StartsWith("VDUnlockRemove-",StringComparison.Ordinal))return;
        string fileLiteral="'"+executable.Replace("'","''")+"'",folderLiteral="'"+folder.Replace("'","''")+"'";
        string script="$ErrorActionPreference='Stop'; for($attempt=0;$attempt -lt 120;$attempt++){try{[IO.File]::Delete("+fileLiteral+");[IO.Directory]::Delete("+folderLiteral+");exit}catch{Start-Sleep -Milliseconds 500}}";
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe"),"-NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) {UseShellExecute=false,CreateNoWindow=true};
        try{Process.Start(info);}catch{ /* Removal result remains available to the user. */ }
    }

    internal static void ExtractApplication(Stream input,string staging)
    {
        staging=Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar);
        using(var archive=new ZipArchive(input,ZipArchiveMode.Read,true))
        foreach(var entry in archive.Entries)
        {
            string path=Path.GetFullPath(Path.Combine(staging,entry.FullName));
            if(!path.StartsWith(staging+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe payload path.");
            if(entry.Name.Length==0) {Directory.CreateDirectory(path);continue;}
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using(var source=entry.Open()) using(var output=File.Create(path)) source.CopyTo(output);
        }
    }

    internal static Dictionary<string,byte[]> DeployFiles(string staging,string destination,Action<string> beforeCopy)
    {
        var changed=new Dictionary<string,byte[]>();
        try
        {
            foreach(string file in Directory.GetFiles(staging,"*",SearchOption.AllDirectories).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))
            {
                string target=Path.Combine(destination,file.Substring(staging.Length+1));
                if(beforeCopy!=null) beforeCopy(target);
                changed[target]=File.Exists(target)?File.ReadAllBytes(target):null;
                Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(file,target,true);
            }
            return changed;
        }
        catch { foreach(var entry in changed) {if(entry.Value==null) File.Delete(entry.Key);else File.WriteAllBytes(entry.Key,entry.Value);} throw; }
    }

    private static string SelectExecutable()
    {
        string found = StreamerLocation.Find();
        if (found.Length != 0)
        {
            var choice = MessageBox.Show("Use this Virtual Desktop Streamer installation?\n\n" + found +
                "\n\nChoose No to browse for another installation.", Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Yes) return found;
            if (choice == DialogResult.Cancel) return null;
        }
        using (var picker = new OpenFileDialog
        {
            Title = "Select VirtualDesktop.Streamer.exe", FileName = StreamerLocation.ExecutableName,
            Filter = "Virtual Desktop Streamer|VirtualDesktop.Streamer.exe", CheckFileExists = true, RestoreDirectory = true
        })
        {
            if (picker.ShowDialog() != DialogResult.OK) return null;
            string selected = StreamerLocation.ValidatePath(picker.FileName);
            if (selected.Length == 0) throw new IOException("Select VirtualDesktop.Streamer.exe in the Streamer installation folder.");
            return selected;
        }
    }

    private static string CreatePrivateDirectory(string prefix)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var account in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(account, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),prefix+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory,security); return directory;
    }

    private static string RunInstaller(string folder, bool uninstall,Action<string,int> update=null)
    {
        if(update!=null)update(uninstall?"Preparing integration removal...":"Preparing Streamer integration...",-1);
        string temporary=CreatePrivateDirectory("VDUnlockSetup-");
        try
        {
            Extract(Installer, Path.Combine(temporary, Installer));
            Extract("InstallerProcessControl.cs",Path.Combine(temporary,"InstallerProcessControl.cs"));
            string payload = Path.Combine(temporary, "runtime-override");
            Directory.CreateDirectory(payload);
            foreach (string name in Payload) Extract(name, Path.Combine(payload, name));
            if(!uninstall && profile!=null) profile.Write(Path.Combine(payload,StreamerSymbols.ProfileFileName));

            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo(powershell,
                "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(temporary, Installer) +
                "\" -StreamerDirectory \"" + (folder.EndsWith("\\", StringComparison.Ordinal) ? folder + "\\" : folder) +
                "\"" + (uninstall ? " -Uninstall" : ""))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = temporary
            };
            using (var process = Process.Start(info))
            {
                if(update!=null)update(uninstall?"Removing integration and restarting Streamer...":"Applying integration and restarting Streamer...",-1);
                var output=System.Threading.Tasks.Task.Factory.StartNew(delegate {
                    var text=new StringBuilder();string line;
                    while((line=process.StandardOutput.ReadLine())!=null){
                        text.AppendLine(line);
                        if(update!=null){
                            if(line.StartsWith("Pausing the Virtual Desktop service",StringComparison.Ordinal))update("Pausing the Virtual Desktop service...",-1);
                            else if(line.StartsWith("Stopping installed Streamer PID",StringComparison.Ordinal))update("Stopping Streamer...",-1);
                            else if(line.StartsWith("Integration files are unlocked",StringComparison.Ordinal))update("Updating Streamer integration files...",-1);
                            else if(line.StartsWith("Restored the Virtual Desktop service",StringComparison.Ordinal))update("Restoring the Virtual Desktop service...",-1);
                        }
                    }
                    return text.ToString();
                });
                var errors = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new IOException((errors.Result + "\n" + output.Result).Trim() +
                        "\n\nSee %ProgramData%\\Virtual Desktop Unlock\\installer.log for details.");
                return output.Result.Trim();
            }
        }
        finally
        {
            try
            {
                foreach (string name in Payload) File.Delete(Path.Combine(temporary, "runtime-override", name));
                File.Delete(Path.Combine(temporary,"runtime-override",StreamerSymbols.ProfileFileName));
                string payload = Path.Combine(temporary, "runtime-override");
                if (Directory.Exists(payload)) Directory.Delete(payload);
                File.Delete(Path.Combine(temporary, Installer));
                File.Delete(Path.Combine(temporary,"InstallerProcessControl.cs"));
                Directory.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Extract(string name, string destination)
    {
        using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (var output = File.Open(destination, FileMode.CreateNew))
        {
            if (input == null) throw new IOException("Missing installer component: " + name);
            input.CopyTo(output);
        }
    }
}
