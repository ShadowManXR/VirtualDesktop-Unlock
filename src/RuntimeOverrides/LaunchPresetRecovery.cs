using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace VirtualDesktop.LibraryOverrides
{
    public sealed class PresetRecoverySession
    {
        public uint AppId;
        public string Directory;
        public int OriginalFps,OriginalQuality,AppliedFps,AppliedQuality;
        public DateTime StartedUtc;
        public bool SeenRunning,Ended,PreserveFps,PreserveQuality;
        public DateTime? QuietSinceUtc;
    }

    // Access is serialized by LaunchPresetRuntime's gate, on the dispatcher.
    // Ended older sessions wait beneath newer sessions so their baseline cannot
    // overwrite a newer game's preset. Recovery is checkpointed before mutation.
    public sealed class LaunchPresetRecovery
    {
        private readonly string path;
        private readonly Action<string> log;
        private readonly List<PresetRecoverySession> sessions=new List<PresetRecoverySession>();
        public LaunchPresetRecovery(string path,Action<string> logger){this.path=path;log=logger;Load();}
        public IList<PresetRecoverySession> Sessions {get{return sessions.AsReadOnly();}}
        public PresetRecoverySession Active(uint id){return sessions.FindLast(s=>s.AppId==id&&!s.Ended);}
        public PresetRecoverySession Begin(uint id,string directory,int originalFps,int originalQuality,int appliedFps,int appliedQuality,DateTime now)
        {
            var session=new PresetRecoverySession{AppId=id,Directory=directory,OriginalFps=originalFps,OriginalQuality=originalQuality,AppliedFps=appliedFps,AppliedQuality=appliedQuality,StartedUtc=now};
            Validate(session);if(sessions.Count>=64)throw new InvalidOperationException("Too many pending game recoveries.");
            sessions.Add(session);try{Save();}catch{sessions.Remove(session);throw;}return session;
        }
        public void Fail(PresetRecoverySession session){if(session!=null){session.Ended=true;Save();}}
        public void Observe(DateTime now,Func<string,bool> running)
        {
            bool changed=false;
            foreach(var session in sessions){
                if(session.Ended)continue;
                if(running(session.Directory)){
                    if(!session.SeenRunning){session.SeenRunning=true;changed=true;log("Launch preset game observed running; Steam "+session.AppId);}
                    session.QuietSinceUtc=null;
                }else if(session.SeenRunning){
                    if(session.QuietSinceUtc==null)session.QuietSinceUtc=now;
                    if(now-session.QuietSinceUtc.Value>=TimeSpan.FromSeconds(3)){session.Ended=true;changed=true;log("Launch preset game exited; Steam "+session.AppId);}
                }else if(now-session.StartedUtc>=TimeSpan.FromMinutes(2)){
                    session.Ended=true;changed=true;log("Launch preset game did not start; Steam "+session.AppId+"; restoring previous settings.");
                }
            }
            if(changed)Save();
        }
        public void RestoreReady(Func<int[]> current,Action<int> setFps,Action<int> setQuality)
        {
            // A disconnected headset makes current()/setters throw. Keep the
            // record and retry on reconnect rather than losing remote recovery.
            while(sessions.Count>0&&sessions[sessions.Count-1].Ended){
                var session=sessions[sessions.Count-1];var selected=current();
                bool fps=!session.PreserveFps&&selected[0]==session.AppliedFps,quality=!session.PreserveQuality&&selected[1]==session.AppliedQuality;
                foreach(var older in sessions){if(!fps)older.PreserveFps=true;if(!quality)older.PreserveQuality=true;}
                if(quality)setQuality(session.OriginalQuality);
                if(fps)setFps(session.OriginalFps);
                sessions.RemoveAt(sessions.Count-1);
                try{Save();}catch{sessions.Add(session);throw;}
                log("Launch preset recovered Steam "+session.AppId+"; fps="+(fps?session.OriginalFps.ToString(CultureInfo.InvariantCulture):"manual choice preserved")+"; quality="+(quality?session.OriginalQuality.ToString(CultureInfo.InvariantCulture):"manual choice preserved"));
            }
        }
        private static bool Rate(int fps){return Array.IndexOf(new[]{0,24,30,60,72,80,90,96,100,120,144,180,200,207},fps)>=0;}
        private static void Validate(PresetRecoverySession s)
        {
            if(s.AppId==0||String.IsNullOrEmpty(s.Directory)||!Path.IsPathRooted(s.Directory)||s.Directory.Length>32767||!Rate(s.OriginalFps)||!Rate(s.AppliedFps)||s.OriginalQuality< -1||s.OriginalQuality>7||s.AppliedQuality< -1||s.AppliedQuality>7||s.StartedUtc.Kind!=DateTimeKind.Utc)throw new InvalidDataException("Invalid launch-preset recovery record.");
        }
        private static int Int(XmlReader r,string name){return Int32.Parse(r.GetAttribute(name),CultureInfo.InvariantCulture);}
        private void Load()
        {
            if(!File.Exists(path))return;
            using(var r=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536})){
                r.MoveToContent();if(r.Name!="LaunchPresetRecovery"||r.GetAttribute("version")!="1")throw new InvalidDataException("Unsupported recovery format.");
                while(r.Read())if(r.NodeType==XmlNodeType.Element){
                    if(r.Name!="Session"||r.Depth!=1||sessions.Count>=64)throw new InvalidDataException("Invalid recovery session.");
                    var s=new PresetRecoverySession{AppId=UInt32.Parse(r.GetAttribute("appId"),CultureInfo.InvariantCulture),Directory=r.GetAttribute("directory"),OriginalFps=Int(r,"originalFps"),OriginalQuality=Int(r,"originalQuality"),AppliedFps=Int(r,"appliedFps"),AppliedQuality=Int(r,"appliedQuality"),StartedUtc=DateTime.Parse(r.GetAttribute("startedUtc"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),SeenRunning=Boolean.Parse(r.GetAttribute("seenRunning")),Ended=Boolean.Parse(r.GetAttribute("ended"))};
                    s.PreserveFps=r.GetAttribute("preserveFps")=="True";s.PreserveQuality=r.GetAttribute("preserveQuality")=="True";
                    Validate(s);sessions.Add(s);
                }
            }
        }
        private static void Attr(XmlWriter w,string name,object value){w.WriteAttributeString(name,Convert.ToString(value,CultureInfo.InvariantCulture));}
        private void Save()
        {
            if(sessions.Count==0){if(File.Exists(path))File.Delete(path);return;}
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{
                using(var w=XmlWriter.Create(temp,new XmlWriterSettings{Indent=true})){
                    w.WriteStartElement("LaunchPresetRecovery");Attr(w,"version","1");
                    foreach(var s in sessions){w.WriteStartElement("Session");Attr(w,"appId",s.AppId);Attr(w,"directory",s.Directory);Attr(w,"originalFps",s.OriginalFps);Attr(w,"originalQuality",s.OriginalQuality);Attr(w,"appliedFps",s.AppliedFps);Attr(w,"appliedQuality",s.AppliedQuality);Attr(w,"startedUtc",s.StartedUtc.ToString("O",CultureInfo.InvariantCulture));Attr(w,"seenRunning",s.SeenRunning);Attr(w,"ended",s.Ended);Attr(w,"preserveFps",s.PreserveFps);Attr(w,"preserveQuality",s.PreserveQuality);w.WriteEndElement();}w.WriteEndElement();
                }
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
}
