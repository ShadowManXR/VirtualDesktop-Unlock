using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace VirtualDesktop.Unlock
{
    public sealed class LaunchPreset
    {
        public int Fps; public int Quality;
        public float? HorizontalFov; public float? VerticalFov;
        public LaunchPreset(int fps,int quality,float? horizontalFov=null,float? verticalFov=null){Fps=fps;Quality=quality;HorizontalFov=horizontalFov;VerticalFov=verticalFov;}
    }
    public sealed class LaunchPresetConfiguration
    {
        public static readonly int[] FrameRates={72,80,90,96,100,120,144};
        public static readonly string[] QualityNames={"Potato","Low","Medium","High","Ultra","Godlike","Monster","Rampage","Unstoppable"};
        public static string DefaultPath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Virtual Desktop Unlock","LaunchPresets.xml");}}
        public readonly SortedDictionary<uint,LaunchPreset> Games=new SortedDictionary<uint,LaunchPreset>();
        public static bool ValidFov(float value){return !Single.IsNaN(value)&&!Single.IsInfinity(value)&&value>=0.4f&&value<=1f;}
        private static float? Fov(XmlReader reader,string name)
        {
            var text=reader.GetAttribute(name);if(text==null)return null;float value;
            if(!Single.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)||!ValidFov(value))throw new InvalidDataException("FOV tangent must be between 40% and 100%.");return value;
        }
        public static LaunchPresetConfiguration Read(string path)
        {
            var config=new LaunchPresetConfiguration();if(!File.Exists(path))return config;
            if(new FileInfo(path).Length>65536)throw new InvalidDataException("Launch-preset file too large.");
            using(var r=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536}))
            {
                r.MoveToContent();if(r.Name!="LaunchPresets"||(r.GetAttribute("version")!="1"&&r.GetAttribute("version")!="2"))throw new InvalidDataException("Unsupported launch-preset format.");
                while(r.Read())if(r.NodeType==XmlNodeType.Element){
                    uint id;int fps,quality;
                    if(r.Depth!=1||r.Name!="Game"||r.GetAttribute("platform")!="Steam"||!UInt32.TryParse(r.GetAttribute("appId"),out id)||id==0||!Int32.TryParse(r.GetAttribute("fps"),out fps)||Array.IndexOf(FrameRates,fps)<0||!Int32.TryParse(r.GetAttribute("quality"),out quality)||quality< -1||quality>7||config.Games.Count>=1000||config.Games.ContainsKey(id))throw new InvalidDataException("Invalid or duplicate launch preset.");
                    config.Games.Add(id,new LaunchPreset(fps,quality,Fov(r,"horizontalFov"),Fov(r,"verticalFov")));
                }
            }
            return config;
        }
        public void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))??".");
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{
                using(var w=XmlWriter.Create(temp,new XmlWriterSettings{Indent=true})){
                    bool fov=false;foreach(var preset in Games.Values)if(preset.HorizontalFov.HasValue||preset.VerticalFov.HasValue)fov=true;
                    w.WriteStartElement("LaunchPresets");w.WriteAttributeString("version",fov?"2":"1");
                    foreach(var item in Games){
                        if(item.Key==0||Array.IndexOf(FrameRates,item.Value.Fps)<0||item.Value.Quality< -1||item.Value.Quality>7||Games.Count>1000)throw new InvalidDataException("Invalid launch preset.");
                        if((item.Value.HorizontalFov.HasValue&&!ValidFov(item.Value.HorizontalFov.Value))||(item.Value.VerticalFov.HasValue&&!ValidFov(item.Value.VerticalFov.Value)))throw new InvalidDataException("Invalid FOV tangent.");
                        w.WriteStartElement("Game");w.WriteAttributeString("platform","Steam");w.WriteAttributeString("appId",item.Key.ToString(CultureInfo.InvariantCulture));w.WriteAttributeString("fps",item.Value.Fps.ToString(CultureInfo.InvariantCulture));w.WriteAttributeString("quality",item.Value.Quality.ToString(CultureInfo.InvariantCulture));
                        if(item.Value.HorizontalFov.HasValue)w.WriteAttributeString("horizontalFov",item.Value.HorizontalFov.Value.ToString("R",CultureInfo.InvariantCulture));
                        if(item.Value.VerticalFov.HasValue)w.WriteAttributeString("verticalFov",item.Value.VerticalFov.Value.ToString("R",CultureInfo.InvariantCulture));w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }
                if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
}
