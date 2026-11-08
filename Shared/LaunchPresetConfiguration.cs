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
        public float? RenderResolution; public bool? UseSharpening; public float? Sharpening;
        public LaunchPreset(int fps,int quality,float? horizontalFov=null,float? verticalFov=null,float? renderResolution=null,bool? useSharpening=null,float? sharpening=null){Fps=fps;Quality=quality;HorizontalFov=horizontalFov;VerticalFov=verticalFov;RenderResolution=renderResolution;UseSharpening=useSharpening;Sharpening=sharpening;}
    }
    public sealed class LaunchPresetConfiguration
    {
        public static readonly int[] FrameRates={72,80,90,96,100,120,144};
        public static readonly string[] QualityNames={"Potato","Low","Medium","High","Ultra","Godlike","Monster","Rampage","Unstoppable"};
        public static string DefaultPath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Virtual Desktop Unlock","LaunchPresets.xml");}}
        public readonly SortedDictionary<uint,LaunchPreset> Games=new SortedDictionary<uint,LaunchPreset>();
        public static bool ValidFov(float value){return !Single.IsNaN(value)&&!Single.IsInfinity(value)&&value>=0.4f&&value<=1f;}
        public static bool ValidRender(float value){return FiniteRange(value,0.5f,2f);}
        public static bool ValidSharpening(float value){return FiniteRange(value,0.05f,1f);}
        private static bool FiniteRange(float value,float min,float max){return !Single.IsNaN(value)&&!Single.IsInfinity(value)&&value>=min&&value<=max;}
        private static float? Number(XmlReader reader,string name,Func<float,bool> valid)
        {
            var text=reader.GetAttribute(name);if(text==null)return null;float value;
            if(!Single.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)||!valid(value))throw new InvalidDataException("Invalid launch-preset value: "+name);return value;
        }
        private static bool? Boolean(XmlReader reader,string name)
        {
            var text=reader.GetAttribute(name);if(text==null)return null;bool value;
            if(!System.Boolean.TryParse(text,out value))throw new InvalidDataException("Invalid launch-preset value: "+name);return value;
        }
        public static bool ValidAdvanced(LaunchPreset preset){return (!preset.RenderResolution.HasValue||ValidRender(preset.RenderResolution.Value))&&preset.UseSharpening.HasValue==preset.Sharpening.HasValue&&(!preset.Sharpening.HasValue||ValidSharpening(preset.Sharpening.Value));}
        private static void FloatAttr(XmlWriter w,string name,float? value){if(value.HasValue)w.WriteAttributeString(name,value.Value.ToString("R",CultureInfo.InvariantCulture));}
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
                r.MoveToContent();if(r.Name!="LaunchPresets"||(r.GetAttribute("version")!="1"&&r.GetAttribute("version")!="2"&&r.GetAttribute("version")!="3"))throw new InvalidDataException("Unsupported launch-preset format.");
                while(r.Read())if(r.NodeType==XmlNodeType.Element){
                    uint id;int fps,quality;
                    if(r.Depth!=1||r.Name!="Game"||r.GetAttribute("platform")!="Steam"||!UInt32.TryParse(r.GetAttribute("appId"),out id)||id==0||!Int32.TryParse(r.GetAttribute("fps"),out fps)||Array.IndexOf(FrameRates,fps)<0||!Int32.TryParse(r.GetAttribute("quality"),out quality)||quality< -1||quality>7||config.Games.Count>=1000||config.Games.ContainsKey(id))throw new InvalidDataException("Invalid or duplicate launch preset.");
                    var preset=new LaunchPreset(fps,quality,Fov(r,"horizontalFov"),Fov(r,"verticalFov"),Number(r,"renderResolution",ValidRender),Boolean(r,"useSharpening"),Number(r,"sharpening",ValidSharpening));
                    if(!ValidAdvanced(preset))throw new InvalidDataException("Sharpening preset requires both enabled state and strength.");
                    config.Games.Add(id,preset);
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
                    bool fov=false,advanced=false;foreach(var preset in Games.Values){if(preset.HorizontalFov.HasValue||preset.VerticalFov.HasValue)fov=true;if(preset.RenderResolution.HasValue||preset.UseSharpening.HasValue)advanced=true;}
                    w.WriteStartElement("LaunchPresets");w.WriteAttributeString("version",advanced?"3":fov?"2":"1");
                    foreach(var item in Games){
                        if(item.Key==0||Array.IndexOf(FrameRates,item.Value.Fps)<0||item.Value.Quality< -1||item.Value.Quality>7||Games.Count>1000)throw new InvalidDataException("Invalid launch preset.");
                        if(!ValidAdvanced(item.Value))throw new InvalidDataException("Invalid render resolution or sharpening preset.");
                        if((item.Value.HorizontalFov.HasValue&&!ValidFov(item.Value.HorizontalFov.Value))||(item.Value.VerticalFov.HasValue&&!ValidFov(item.Value.VerticalFov.Value)))throw new InvalidDataException("Invalid FOV tangent.");
                        w.WriteStartElement("Game");w.WriteAttributeString("platform","Steam");w.WriteAttributeString("appId",item.Key.ToString(CultureInfo.InvariantCulture));w.WriteAttributeString("fps",item.Value.Fps.ToString(CultureInfo.InvariantCulture));w.WriteAttributeString("quality",item.Value.Quality.ToString(CultureInfo.InvariantCulture));
                        if(item.Value.HorizontalFov.HasValue)w.WriteAttributeString("horizontalFov",item.Value.HorizontalFov.Value.ToString("R",CultureInfo.InvariantCulture));
                        if(item.Value.VerticalFov.HasValue)w.WriteAttributeString("verticalFov",item.Value.VerticalFov.Value.ToString("R",CultureInfo.InvariantCulture));FloatAttr(w,"renderResolution",item.Value.RenderResolution);
                        if(item.Value.UseSharpening.HasValue)w.WriteAttributeString("useSharpening",item.Value.UseSharpening.Value?"true":"false");
                        FloatAttr(w,"sharpening",item.Value.Sharpening);w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }
                if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
}
