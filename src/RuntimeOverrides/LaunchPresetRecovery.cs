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
        public float? OriginalHorizontal,OriginalVertical,AppliedHorizontal,AppliedVertical;
        public bool PreserveHorizontal,PreserveVertical;
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
        public PresetRecoverySession Begin(uint id,string directory,int originalFps,int originalQuality,int appliedFps,int appliedQuality,DateTime now,float? originalHorizontal=null,float? originalVertical=null,float? appliedHorizontal=null,float? appliedVertical=null)
        {
            var session=new PresetRecoverySession{AppId=id,Directory=directory,OriginalFps=originalFps,OriginalQuality=originalQuality,AppliedFps=appliedFps,AppliedQuality=appliedQuality,StartedUtc=now,OriginalHorizontal=originalHorizontal,OriginalVertical=originalVertical,AppliedHorizontal=appliedHorizontal,AppliedVertical=appliedVertical};
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
        public void RestoreReady(Func<int[]> current,Action<int> setFps,Action<int> setQuality,Func<float[]> currentFov=null,Action<float> setHorizontal=null,Action<float> setVertical=null)
        {
            // A disconnected headset makes current()/setters throw. Keep the
            // record and retry on reconnect rather than losing remote recovery.
            while(sessions.Count>0&&sessions[sessions.Count-1].Ended){
                var session=sessions[sessions.Count-1];var selected=current();
                bool fps=!session.PreserveFps&&selected[0]==session.AppliedFps,quality=!session.PreserveQuality&&selected[1]==session.AppliedQuality;
                if(session.AppliedHorizontal.HasValue||session.AppliedVertical.HasValue){
                    if(currentFov==null||setHorizontal==null||setVertical==null)throw new InvalidOperationException("FOV recovery requires the updated helper.");
                    var fov=currentFov();
                    if(session.AppliedHorizontal.HasValue){
                        if(!session.PreserveHorizontal&&Near(fov[0],session.AppliedHorizontal.Value))setHorizontal(session.OriginalHorizontal.Value);
                        else foreach(var older in sessions)older.PreserveHorizontal=true;
                    }
                    if(session.AppliedVertical.HasValue){
                        if(!session.PreserveVertical&&Near(fov[1],session.AppliedVertical.Value))setVertical(session.OriginalVertical.Value);
                        else foreach(var older in sessions)older.PreserveVertical=true;
                    }
                }
                foreach(var older in sessions){if(!fps)older.PreserveFps=true;if(!quality)older.PreserveQuality=true;}
                if(quality)setQuality(session.OriginalQuality);
                if(fps)setFps(session.OriginalFps);
                sessions.RemoveAt(sessions.Count-1);
                try{Save();}catch{sessions.Add(session);throw;}
                log("Launch preset recovered Steam "+session.AppId+"; fps="+(fps?session.OriginalFps.ToString(CultureInfo.InvariantCulture):"manual choice preserved")+"; quality="+(quality?session.OriginalQuality.ToString(CultureInfo.InvariantCulture):"manual choice preserved"));
            }
        }
        private static bool Rate(int fps){return Array.IndexOf(new[]{0,24,30,60,72,80,90,96,100,120,144,180,200,207},fps)>=0;}
        public static bool Near(float left,float right){return !Single.IsNaN(left)&&!Single.IsInfinity(left)&&Math.Abs(left-right)<=0.0001f;}
        private static bool Axis(float? original,float? applied){return original.HasValue==applied.HasValue&&(!original.HasValue||(VirtualDesktop.Unlock.LaunchPresetConfiguration.ValidFov(original.Value)&&VirtualDesktop.Unlock.LaunchPresetConfiguration.ValidFov(applied.Value)));}
        private static void Validate(PresetRecoverySession s)
        {
            if(s.AppId==0||String.IsNullOrEmpty(s.Directory)||!Path.IsPathRooted(s.Directory)||s.Directory.Length>32767||!Rate(s.OriginalFps)||!Rate(s.AppliedFps)||s.OriginalQuality< -1||s.OriginalQuality>7||s.AppliedQuality< -1||s.AppliedQuality>7||s.StartedUtc.Kind!=DateTimeKind.Utc||!Axis(s.OriginalHorizontal,s.AppliedHorizontal)||!Axis(s.OriginalVertical,s.AppliedVertical))throw new InvalidDataException("Invalid launch-preset recovery record.");
        }
        private static int Int(XmlReader r,string name){return Int32.Parse(r.GetAttribute(name),CultureInfo.InvariantCulture);}
        private void Load()
        {
            if(!File.Exists(path))return;
            using(var r=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536})){
                r.MoveToContent();if(r.Name!="LaunchPresetRecovery"||(r.GetAttribute("version")!="1"&&r.GetAttribute("version")!="2"))throw new InvalidDataException("Unsupported recovery format.");
                while(r.Read())if(r.NodeType==XmlNodeType.Element){
                    if(r.Name!="Session"||r.Depth!=1||sessions.Count>=64)throw new InvalidDataException("Invalid recovery session.");
                    var s=new PresetRecoverySession{AppId=UInt32.Parse(r.GetAttribute("appId"),CultureInfo.InvariantCulture),Directory=r.GetAttribute("directory"),OriginalFps=Int(r,"originalFps"),OriginalQuality=Int(r,"originalQuality"),AppliedFps=Int(r,"appliedFps"),AppliedQuality=Int(r,"appliedQuality"),StartedUtc=DateTime.Parse(r.GetAttribute("startedUtc"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),SeenRunning=Boolean.Parse(r.GetAttribute("seenRunning")),Ended=Boolean.Parse(r.GetAttribute("ended"))};
                    s.PreserveFps=r.GetAttribute("preserveFps")=="True";s.PreserveQuality=r.GetAttribute("preserveQuality")=="True";
                    s.OriginalHorizontal=Float(r,"originalHorizontal");s.OriginalVertical=Float(r,"originalVertical");s.AppliedHorizontal=Float(r,"appliedHorizontal");s.AppliedVertical=Float(r,"appliedVertical");s.PreserveHorizontal=r.GetAttribute("preserveHorizontal")=="True";s.PreserveVertical=r.GetAttribute("preserveVertical")=="True";
                    Validate(s);sessions.Add(s);
                }
            }
        }
        private static void Attr(XmlWriter w,string name,object value){w.WriteAttributeString(name,Convert.ToString(value,CultureInfo.InvariantCulture));}
        private static float? Float(XmlReader r,string name){var text=r.GetAttribute(name);return text==null?(float?)null:Single.Parse(text,NumberStyles.Float,CultureInfo.InvariantCulture);}
        private static void FloatAttr(XmlWriter w,string name,float? value){if(value.HasValue)w.WriteAttributeString(name,value.Value.ToString("R",CultureInfo.InvariantCulture));}
        private void Save()
        {
            if(sessions.Count==0){if(File.Exists(path))File.Delete(path);return;}
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{
                using(var w=XmlWriter.Create(temp,new XmlWriterSettings{Indent=true})){
                    bool fov=false;foreach(var s in sessions)if(s.AppliedHorizontal.HasValue||s.AppliedVertical.HasValue)fov=true;
                    w.WriteStartElement("LaunchPresetRecovery");Attr(w,"version",fov?"2":"1");
                    foreach(var s in sessions){w.WriteStartElement("Session");Attr(w,"appId",s.AppId);Attr(w,"directory",s.Directory);Attr(w,"originalFps",s.OriginalFps);Attr(w,"originalQuality",s.OriginalQuality);Attr(w,"appliedFps",s.AppliedFps);Attr(w,"appliedQuality",s.AppliedQuality);Attr(w,"startedUtc",s.StartedUtc.ToString("O",CultureInfo.InvariantCulture));Attr(w,"seenRunning",s.SeenRunning);Attr(w,"ended",s.Ended);Attr(w,"preserveFps",s.PreserveFps);Attr(w,"preserveQuality",s.PreserveQuality);FloatAttr(w,"originalHorizontal",s.OriginalHorizontal);FloatAttr(w,"originalVertical",s.OriginalVertical);FloatAttr(w,"appliedHorizontal",s.AppliedHorizontal);FloatAttr(w,"appliedVertical",s.AppliedVertical);Attr(w,"preserveHorizontal",s.PreserveHorizontal);Attr(w,"preserveVertical",s.PreserveVertical);w.WriteEndElement();}w.WriteEndElement();
                }
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
}
