using System.Collections.Generic;
using VirtualDesktop.Unlock;
namespace SteamVRLibrary;

public sealed class LaunchPresetsForm : Form
{
    private TableLayoutPanel? root;private Panel? scroll;private FlowLayoutPanel? footer;private Label? note;private readonly Size baseClient;
    public LaunchPresetsForm(Game game,LaunchPresetStore store)
    {
        var config=store.Load();
        if(!uint.TryParse(game.Id,out var id)||id==0)throw new InvalidDataException("Invalid Steam app ID.");
        bool hasPreset=config.Games.TryGetValue(id,out var preset);preset??=new LaunchPreset(90,2);
        var background=Color.FromArgb(14,20,31);var muted=Color.FromArgb(159,176,199);
        Text=Localizer.T("Launch Preset — Virtual Desktop Unlock");BackColor=background;ForeColor=Color.White;Font=new Font("Segoe UI",10);
        ClientSize=new Size(760,740);MinimumSize=new Size(700,630);StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;AutoScaleMode=AutoScaleMode.Dpi;MaximizeBox=false;
        scroll=new Panel{Dock=DockStyle.Fill,AutoScroll=true};Controls.Add(scroll);
        root=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(24),ColumnCount=2,RowCount=12};
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,30));root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,70));
        for(int i=0;i<12;i++)root.RowStyles.Add(new RowStyle(SizeType.AutoSize));scroll.Controls.Add(root);
        var title=new Label{Text=game.Name,Dock=DockStyle.Fill,AutoEllipsis=true,Font=new Font(Font.FontFamily,19,FontStyle.Bold),AccessibleName="Preset game"};root.Controls.Add(title,0,0);root.SetColumnSpan(title,2);
        note=new Label{Text=Localizer.T("Apply this preset before launching this game from VD."),Dock=DockStyle.Top,AutoSize=true,ForeColor=muted};root.Controls.Add(note,0,1);root.SetColumnSpan(note,2);
        var enabled=new CheckBox{Text=Localizer.T("Use preset"),Checked=hasPreset,Dock=DockStyle.Top,AutoSize=true,AccessibleName="Enable launch preset"};root.Controls.Add(enabled,0,2);root.SetColumnSpan(enabled,2);
        var fovEnabled=new CheckBox{Text=Localizer.T("Override FOV tangent (experimental)"),Checked=preset.HorizontalFov.HasValue||preset.VerticalFov.HasValue,Enabled=hasPreset,Dock=DockStyle.Top,AutoSize=true,AccessibleName="Enable FOV override"};root.Controls.Add(fovEnabled,0,5);root.SetColumnSpan(fovEnabled,2);
        CheckBox Override(string text,string name,int row,bool selected)
        {
            var box=new CheckBox{Text=Localizer.T(text),Checked=selected,Enabled=hasPreset,Dock=DockStyle.Top,AutoSize=true,AccessibleName=name};root.Controls.Add(box,0,row);root.SetColumnSpan(box,2);return box;
        }
        var renderEnabled=Override("Override VDXR render resolution (experimental)","Enable render resolution override",8,preset.RenderResolution.HasValue);
        var sharpeningOverride=Override("Override additional sharpening (experimental)","Enable sharpening override",10,preset.UseSharpening==true);
        void EnableOverrides(){fovEnabled.Enabled=renderEnabled.Enabled=sharpeningOverride.Enabled=enabled.Checked;}
        enabled.CheckedChanged+=(_,_)=>EnableOverrides();sharpeningOverride.CheckedChanged+=(_,_)=>EnableOverrides();EnableOverrides();
        TrackBar Slider(int max,int value,string name)=>new(){Minimum=0,Maximum=max,Value=value,TickFrequency=1,SmallChange=1,LargeChange=1,AutoSize=false,Dock=DockStyle.Fill,Enabled=hasPreset,AccessibleName=name,RightToLeft=RightToLeft.No};
        var quality=Slider(8,preset.Quality+1,"Quality / resolution");var fps=Slider(LaunchPresetConfiguration.FrameRates.Length-1,Array.IndexOf(LaunchPresetConfiguration.FrameRates,preset.Fps),"FPS preset");
        var horizontal=Slider(60,(int)Math.Round((preset.HorizontalFov??1f)*100)-40,"Horizontal FOV tangent");var vertical=Slider(60,(int)Math.Round((preset.VerticalFov??1f)*100)-40,"Vertical FOV tangent");
        var render=Slider(150,(int)Math.Round((preset.RenderResolution??1f)*100)-50,"VDXR render resolution");
        var sharpening=Slider(95,(int)Math.Round((preset.Sharpening??0.7f)*100)-5,"Sharpening strength");
        foreach(var slider in new[]{horizontal,vertical,render,sharpening}){slider.TickFrequency=10;slider.LargeChange=5;}
        void AddSlider(TrackBar slider,int row,string key,Func<string> caption,Func<bool>? condition=null)
        {
            root.Controls.Add(new Label{Text=Localizer.T(key),Dock=DockStyle.Top,AutoSize=true,TextAlign=ContentAlignment.MiddleLeft},0,row);
            var panel=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,RowCount=2,Padding=new Padding(0,8,0,4)};panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var value=new Label{Text=caption(),Dock=DockStyle.Top,AutoSize=true};panel.Controls.Add(value,0,0);panel.Controls.Add(slider,0,1);root.Controls.Add(panel,1,row);
            void UpdateEnabled(){slider.Enabled=enabled.Checked&&(condition?.Invoke()??true);value.ForeColor=slider.Enabled?Color.White:muted;}
            UpdateEnabled();slider.ValueChanged+=(_,_)=>value.Text=caption();enabled.CheckedChanged+=(_,_)=>UpdateEnabled();fovEnabled.CheckedChanged+=(_,_)=>UpdateEnabled();renderEnabled.CheckedChanged+=(_,_)=>UpdateEnabled();sharpeningOverride.CheckedChanged+=(_,_)=>UpdateEnabled();
        }
        AddSlider(quality,3,"Quality / resolution",()=>Localizer.T(LaunchPresetConfiguration.QualityNames[quality.Value]));
        AddSlider(fps,4,"FPS preset",()=>Localizer.T("{0} FPS",LaunchPresetConfiguration.FrameRates[fps.Value]));
        AddSlider(horizontal,6,"Horizontal FOV tangent",()=>Localizer.T("{0}%",horizontal.Value+40),()=>fovEnabled.Checked);
        AddSlider(vertical,7,"Vertical FOV tangent",()=>Localizer.T("{0}%",vertical.Value+40),()=>fovEnabled.Checked);
        AddSlider(render,9,"VDXR render resolution",()=>Localizer.T("{0}%",render.Value+50),()=>renderEnabled.Checked);
        AddSlider(sharpening,11,"Sharpening strength",()=>Localizer.T("{0}%",sharpening.Value+5),()=>sharpeningOverride.Checked);
        footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Padding=new Padding(24,12,24,12)};
        var save=new Button{Text=Localizer.T("Save preset"),AutoSize=true,Padding=new Padding(14,8,14,8),BackColor=Color.FromArgb(108,218,188),ForeColor=background,FlatStyle=FlatStyle.Flat,AccessibleName="Save launch preset"};
        var cancel=new Button{Text=Localizer.T("Cancel"),AutoSize=true,Padding=new Padding(14,8,14,8),DialogResult=DialogResult.Cancel};
        save.Click+=(_,_)=>{try{if(enabled.Checked)config.Games[id]=new LaunchPreset(LaunchPresetConfiguration.FrameRates[fps.Value],quality.Value-1,fovEnabled.Checked?(float?)((horizontal.Value+40)/100f):null,fovEnabled.Checked?(float?)((vertical.Value+40)/100f):null,renderEnabled.Checked?(float?)((render.Value+50)/100f):null,sharpeningOverride.Checked?(bool?)true:null,sharpeningOverride.Checked?(float?)((sharpening.Value+5)/100f):null);else config.Games.Remove(id);store.Save(config);DialogResult=DialogResult.OK;Close();}catch(Exception e){LocalizedMessageBox.Show(this,e.Message,"Could not save launch presets",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
        footer.Controls.AddRange([save,cancel]);Controls.Add(footer);AcceptButton=save;CancelButton=cancel;
        RightToLeft=Localizer.IsRtl?RightToLeft.Yes:RightToLeft.No;RightToLeftLayout=Localizer.IsRtl;
        baseClient=ClientSize;
        Shown+=(_,_)=>FitContent();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e){base.OnDpiChanged(e);FitContent();}
    static IEnumerable<Control> Descendants(Control c){foreach(Control g in c.Controls){yield return g;foreach(var d in Descendants(g))yield return d;}}
    void FitContent()
    {
        if(root is null||scroll is null||footer is null||!IsHandleCreated)return;
        foreach(var tr in Descendants(root).OfType<TrackBar>())tr.Height=Math.Max(35,TextRenderer.MeasureText("Ag",tr.Font).Height+16);
        var area=Screen.FromHandle(Handle).WorkingArea;
        int maxW=(int)(area.Width*0.94),maxH=(int)(area.Height*0.92);
        var pref=root.GetPreferredSize(new Size(Math.Max(scroll.ClientSize.Width,baseClient.Width),0));
        int w=Math.Min(Math.Max(pref.Width,baseClient.Width),maxW);
        ClientSize=new Size(w,ClientSize.Height);
        int avail=Math.Max(200,scroll.ClientSize.Width-root.Padding.Horizontal);
        foreach(var lbl in root.Controls.OfType<Label>())lbl.MaximumSize=new Size(root.GetColumnSpan(lbl)>1?avail:(int)(avail*0.28),0);
        var pref2=root.GetPreferredSize(new Size(Math.Max(scroll.ClientSize.Width,200),0));
        int h=Math.Min(Math.Max(pref2.Height+footer.Height,baseClient.Height),maxH);
        ClientSize=new Size(w,h);
    }
}
