using VirtualDesktop.Unlock;
namespace SteamVRLibrary;

public sealed class LaunchPresetsForm : Form
{
    public LaunchPresetsForm(Game game,LaunchPresetStore store)
    {
        var config=store.Load();
        if(!uint.TryParse(game.Id,out var id)||id==0)throw new InvalidDataException("Invalid Steam app ID.");
        bool hasPreset=config.Games.TryGetValue(id,out var preset);preset??=new LaunchPreset(90,2);
        var background=Color.FromArgb(14,20,31);var muted=Color.FromArgb(159,176,199);
        Text=Localizer.T("Launch Preset — Virtual Desktop Unlock");BackColor=background;ForeColor=Color.White;Font=new Font("Segoe UI",10);
        ClientSize=new Size(720,420);MinimumSize=new Size(640,450);StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;AutoScaleMode=AutoScaleMode.Dpi;MaximizeBox=false;
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=2,RowCount=6};
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,30));root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,70));
        foreach(var height in new[]{42,42,34,85,85,58})root.RowStyles.Add(new RowStyle(SizeType.Absolute,height));Controls.Add(root);
        var title=new Label{Text=game.Name,Dock=DockStyle.Fill,AutoEllipsis=true,Font=new Font(Font.FontFamily,19,FontStyle.Bold),AccessibleName="Preset game"};root.Controls.Add(title,0,0);root.SetColumnSpan(title,2);
        var note=new Label{Text=Localizer.T("Apply this preset before launching this game from VD."),Dock=DockStyle.Fill,ForeColor=muted};root.Controls.Add(note,0,1);root.SetColumnSpan(note,2);
        var enabled=new CheckBox{Text=Localizer.T("Use preset"),Checked=hasPreset,Dock=DockStyle.Fill,AccessibleName="Enable launch preset"};root.Controls.Add(enabled,0,2);root.SetColumnSpan(enabled,2);
        TrackBar Slider(int max,int value,string name)=>new(){Minimum=0,Maximum=max,Value=value,TickFrequency=1,SmallChange=1,LargeChange=1,AutoSize=false,Dock=DockStyle.Fill,Enabled=hasPreset,AccessibleName=name,RightToLeft=RightToLeft.No};
        var quality=Slider(8,preset.Quality+1,"Quality / resolution");var fps=Slider(LaunchPresetConfiguration.FrameRates.Length-1,Array.IndexOf(LaunchPresetConfiguration.FrameRates,preset.Fps),"FPS preset");
        void AddSlider(TrackBar slider,int row,string key,Func<string> caption)
        {
            root.Controls.Add(new Label{Text=Localizer.T(key),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,row);
            var panel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(0,8,0,4)};panel.RowStyles.Add(new RowStyle(SizeType.Absolute,25));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var value=new Label{Text=caption(),Dock=DockStyle.Fill,ForeColor=hasPreset?Color.White:muted};panel.Controls.Add(value,0,0);panel.Controls.Add(slider,0,1);root.Controls.Add(panel,1,row);
            slider.ValueChanged+=(_,_)=>value.Text=caption();enabled.CheckedChanged+=(_,_)=>{slider.Enabled=enabled.Checked;value.ForeColor=enabled.Checked?Color.White:muted;};
        }
        AddSlider(quality,3,"Quality / resolution",()=>Localizer.T(LaunchPresetConfiguration.QualityNames[quality.Value]));
        AddSlider(fps,4,"FPS preset",()=>Localizer.T("{0} FPS",LaunchPresetConfiguration.FrameRates[fps.Value]));
        var footer=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Padding=new Padding(0,12,0,0)};
        var save=new Button{Text=Localizer.T("Save preset"),AutoSize=true,Height=36,Padding=new Padding(14,0,14,0),BackColor=Color.FromArgb(108,218,188),ForeColor=background,FlatStyle=FlatStyle.Flat,AccessibleName="Save launch preset"};
        var cancel=new Button{Text=Localizer.T("Cancel"),AutoSize=true,Height=36,DialogResult=DialogResult.Cancel,Padding=new Padding(14,0,14,0)};
        save.Click+=(_,_)=>{try{if(enabled.Checked)config.Games[id]=new LaunchPreset(LaunchPresetConfiguration.FrameRates[fps.Value],quality.Value-1);else config.Games.Remove(id);store.Save(config);DialogResult=DialogResult.OK;Close();}catch(Exception e){LocalizedMessageBox.Show(this,e.Message,"Could not save launch presets",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
        footer.Controls.AddRange([save,cancel]);root.Controls.Add(footer,0,5);root.SetColumnSpan(footer,2);AcceptButton=save;CancelButton=cancel;
        RightToLeft=Localizer.IsRtl?RightToLeft.Yes:RightToLeft.No;RightToLeftLayout=Localizer.IsRtl;
    }
}
