using VirtualDesktop.Unlock;

namespace SteamVRLibrary;

internal static class LaunchPresetTests
{
    public static void Run(string root,Action<bool,string> check)
    {
        string dir=Path.Combine(root,"LaunchPresets");Directory.CreateDirectory(dir);
        string cache=Path.Combine(dir,"GameSettings.json"),path=Path.Combine(dir,"LaunchPresets.xml");
        File.WriteAllText(cache,"{\"SteamProductInfos\":{\"111\":{\"OpenXRSupport\":false},\"999\":{\"OpenXRSupport\":true}}}");
        var cached=LaunchPresetStore.CachedGames(cache,[new Game("111","Cached application",""),new Game("952060","Not cached","")]);
        check(cached.Select(g=>g.Id).ToHashSet().SetEquals(["111","999"])&&cached.Single(g=>g.Id=="111").Name=="Cached application","Launch presets list actual cache entries without adding uncached library selections");
        var config=new LaunchPresetConfiguration();config.Games[2981220]=new LaunchPreset(144,0);config.Games[123]=new LaunchPreset(96,2);config.Write(path);
        IEnumerable<Control> Children(Control c)=>c.Controls.Cast<Control>().SelectMany(x=>new[]{x}.Concat(Children(x)));
        using(var form=new LaunchPresetsForm(new Game("2981220","Forefront",""),new LaunchPresetStore(path)))
        {
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();
            var controls=Children(form).ToArray();var active=controls.OfType<CheckBox>().Single(c=>c.AccessibleName=="Enable launch preset");
            var sliders=controls.OfType<TrackBar>().Where(c=>c.AccessibleName is "Quality / resolution" or "FPS preset").ToArray();
            check(active.Checked&&sliders.Length==2&&sliders[0].Value==1&&sliders[1].Value==6&&controls.Single(c=>c.AccessibleName=="Preset game").Text=="Forefront","Selected-game editor loads only Forefront and its Low / 144 FPS preset");
            sliders[0].Value=3;sliders[1].Value=2;
            var fov=controls.OfType<CheckBox>().Single(c=>c.AccessibleName=="Enable FOV override");
            var horizontal=controls.OfType<TrackBar>().Single(c=>c.AccessibleName=="Horizontal FOV tangent");var vertical=controls.OfType<TrackBar>().Single(c=>c.AccessibleName=="Vertical FOV tangent");
            check(!fov.Checked&&!horizontal.Enabled&&!vertical.Enabled,"Existing presets leave FOV overrides disabled and global FOV unchanged");
            fov.Checked=true;horizontal.Value=40;vertical.Value=50;
            var renderOverride=controls.OfType<CheckBox>().Single(c=>c.AccessibleName=="Enable render resolution override");var sharpeningOverride=controls.OfType<CheckBox>().Single(c=>c.AccessibleName=="Enable sharpening override");
            var render=controls.OfType<TrackBar>().Single(c=>c.AccessibleName=="VDXR render resolution");var sharpening=controls.OfType<TrackBar>().Single(c=>c.AccessibleName=="Sharpening strength");
            check(!renderOverride.Checked&&!sharpeningOverride.Checked&&!render.Enabled&&!sharpening.Enabled,"New advanced overrides start disabled for existing presets");
            renderOverride.Checked=true;render.Value=75;sharpeningOverride.Checked=true;sharpening.Value=45;
            check(render.Enabled&&sharpening.Enabled,"Opt-in enables render and sharpening controls");
            using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(root,"launch-presets.png"));}
            controls.OfType<Panel>().Single(c=>c.AutoScroll).ScrollControlIntoView(sharpening);Application.DoEvents();
            using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(root,"launch-presets-advanced.png"));}
            controls.OfType<Button>().Single(c=>c.AccessibleName=="Save launch preset").PerformClick();
        }
        var saved=LaunchPresetConfiguration.Read(path);
        check(saved.Games[2981220].Fps==90&&saved.Games[2981220].Quality==2&&saved.Games[123].Fps==96,"Saving a selected game's sliders preserves every other game's preset");
        check(saved.Games[2981220].HorizontalFov==0.8f&&saved.Games[2981220].VerticalFov==0.9f&&!saved.Games[123].HorizontalFov.HasValue,"FOV percentages persist without adding overrides to other games");
        check(saved.Games[2981220].RenderResolution==1.25f&&saved.Games[2981220].UseSharpening==true&&saved.Games[2981220].Sharpening==0.5f&&!saved.Games[123].RenderResolution.HasValue,"Advanced presets save in format 3 and preserve other games");
        string before=File.ReadAllText(path);
        using(var cancel=new LaunchPresetsForm(new Game("2981220","Forefront",""),new LaunchPresetStore(path)))
        {Children(cancel).OfType<CheckBox>().Single(c=>c.AccessibleName=="Enable launch preset").Checked=false;cancel.Close();}
        check(File.ReadAllText(path)==before,"Cancelling the preset window leaves saved settings unchanged");
        var first=new LaunchPresetStore(path);var second=new LaunchPresetStore(path);var a=first.Load();var b=second.Load();a.Games.Remove(123);first.Save(a);
        bool stale=false;try{second.Save(b);}catch(IOException){stale=true;}
        check(stale&&!LaunchPresetConfiguration.Read(path).Games.ContainsKey(123),"Stale preset window cannot overwrite another save");
        using(var off=new LaunchPresetsForm(new Game("2981220","Forefront",""),new LaunchPresetStore(path))){
            off.ShowInTaskbar=false;off.StartPosition=FormStartPosition.Manual;off.Location=new Point(-20000,-20000);off.Show();Application.DoEvents();
            var controls=Children(off).ToArray();var sharpeningOverride=controls.OfType<CheckBox>().Single(c=>c.AccessibleName=="Enable sharpening override");
            check(sharpeningOverride.Checked&&controls.OfType<TrackBar>().Single(c=>c.AccessibleName=="VDXR render resolution").Value==75,"Saved advanced settings reload into the editor");
            sharpeningOverride.Checked=false;check(!controls.OfType<TrackBar>().Single(c=>c.AccessibleName=="Sharpening strength").Enabled,"Clearing the sharpening override disables its strength slider");
            controls.OfType<Button>().Single(c=>c.AccessibleName=="Save launch preset").PerformClick();
        }
        check(!LaunchPresetConfiguration.Read(path).Games[2981220].UseSharpening.HasValue&&!LaunchPresetConfiguration.Read(path).Games[2981220].Sharpening.HasValue,"Clearing the sharpening override leaves global sharpening unchanged");
        var advanced=LaunchPresetConfiguration.Read(path);advanced.Games[2981220].RenderResolution=null;advanced.Games[2981220].UseSharpening=null;advanced.Games[2981220].Sharpening=null;advanced.Write(path);
        check(File.ReadAllText(path).Contains("version=\"2\""),"Removing advanced overrides returns to the FOV-only format");
        foreach(string attributes in new[]{"renderResolution=\"NaN\"","renderResolution=\"0.49\"","renderResolution=\"2.01\"","renderResolution=\"1,25\"","useSharpening=\"true\"","sharpening=\"0.5\"","useSharpening=\"invalid\" sharpening=\"0.5\"","useSharpening=\"false\" sharpening=\"Infinity\"","useSharpening=\"true\" sharpening=\"0.04\""}){
            File.WriteAllText(path,"<LaunchPresets version=\"3\"><Game platform=\"Steam\" appId=\"1\" fps=\"90\" quality=\"2\" "+attributes+"/></LaunchPresets>");bool rejected=false;try{LaunchPresetConfiguration.Read(path);}catch(InvalidDataException){rejected=true;}check(rejected,"Invalid advanced preset rejected: "+attributes);
        }
        foreach(string value in new[]{"NaN","Infinity","0,8","0.3","1.1"}){
            File.WriteAllText(path,"<LaunchPresets version=\"2\"><Game platform=\"Steam\" appId=\"1\" fps=\"90\" quality=\"2\" horizontalFov=\""+value+"\"/></LaunchPresets>");bool rejected=false;try{LaunchPresetConfiguration.Read(path);}catch(InvalidDataException){rejected=true;}check(rejected,"Invalid FOV value rejected: "+value);
        }
    }
}
