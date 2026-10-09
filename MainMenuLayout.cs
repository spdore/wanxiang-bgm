using System;
using System.Drawing;
using System.Windows.Forms;

namespace BgmHotkey
{
    internal sealed partial class MainMenuForm
    {
        // Native controls are value adapters for the existing audio/settings handlers.
        // They have no visible layout; the browser owns the entire application UI.
        private readonly Panel _stateAdapters;
        private readonly Panel _startupSurface;
        private readonly Label _startupMessage;
        private readonly Label _libraryNotice;

        public MainMenuForm(AppSettings settings, AudioDeviceCatalog devices, AudioEngine engine)
        {
            SuspendLayout();
            _settings=settings; _devices=devices; _engine=engine;
            settings.Normalize(); _initializing=true;
            Text="万象 · BGM 播放器";
            FormBorderStyle=FormBorderStyle.None;
            MaximizedBounds=Screen.FromControl(this).WorkingArea;
            try { Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition=FormStartPosition.CenterScreen;
            AutoScaleDimensions=new SizeF(96F,96F); AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(1120,820); MinimumSize=new Size(960,720);
            Font=new Font("Microsoft YaHei UI",10F);
            BackColor=Color.FromArgb(245,245,242); DoubleBuffered=true;
            Opacity=0; ShowInTaskbar=false;

            _stateAdapters=new Panel();
            _microphone=CreateCombo(); _headphone=CreateCombo(); _cable=CreateCombo();
            _headphoneVolume=CreateTrackBar(settings.HeadphoneVolume); _cableVolume=CreateTrackBar(settings.CableVolume);
            _headphoneValue=CreateValueLabel(settings.HeadphoneVolume); _cableValue=CreateValueLabel(settings.CableVolume);
            _status=new Label(); _libraryNotice=new Label(); _routeResult=new Label(); _routeCheck=new Button();
            _microphoneMeter=new ProgressBar(); _musicMeter=new ProgressBar(); _outputMeter=new ProgressBar();
            _stateAdapters.Controls.AddRange(new Control[]{_microphone,_headphone,_cable,_headphoneVolume,_cableVolume,_headphoneValue,_cableValue,_status,_libraryNotice,_routeResult,_routeCheck,_microphoneMeter,_musicMeter,_outputMeter});
            _startupSurface=new Panel{Dock=DockStyle.Fill,BackColor=BackColor};
            var heading=new Label{Text="万象 / BGM",AutoSize=true,Font=new Font("Microsoft YaHei UI",24F,FontStyle.Bold),ForeColor=Color.FromArgb(32,41,35),Location=new Point(40,36)};
            _startupMessage=new Label{Text="正在启动音乐控制台…",AutoSize=false,Size=new Size(720,140),Location=new Point(44,104),ForeColor=Color.FromArgb(119,125,120)};
            _startupSurface.Controls.Add(heading); _startupSurface.Controls.Add(_startupMessage); Controls.Add(_startupSurface);
            _headphoneVolume.ValueChanged+=VolumeChanged; _cableVolume.ValueChanged+=VolumeChanged;
            _microphone.SelectedIndexChanged+=DeviceChanged; _cable.SelectedIndexChanged+=DeviceChanged;
            _engine.StatusChanged+=EngineStatusChanged;
            LoadDevices(); _initializing=false; RefreshStatus();
            _meterTimer=new Timer{Interval=100};
            _meterTimer.Tick+=delegate{_microphoneMeter.Value=AudioRouteProbe.MeterValue(_engine.MicrophonePeak);_musicMeter.Value=AudioRouteProbe.MeterValue(_engine.MusicPeak);_outputMeter.Value=AudioRouteProbe.MeterValue(_engine.OutputPeak);PublishWebState();};
            _meterTimer.Start();
            Activated+=delegate{ResetStartupWebFocus();};
            Load+=delegate{
                FitToDisplay(true);
#if !TEST_BUILD
                InitializeWebInterface();
#endif
            };
            DpiChanged+=delegate{if(!IsDisposed)BeginInvoke(new MethodInvoker(delegate{if(!IsDisposed)FitToDisplay(false);}));};
            ResumeLayout(true);
        }

        private void FitToDisplay(bool initial)
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            float scale = CurrentAutoScaleDimensions.Width / 96F;
            int gap = Math.Max(8, (int)Math.Round(16 * scale));
            int maxWidth = Math.Max(1, area.Width - gap * 2);
            int maxHeight = Math.Max(1, area.Height - gap * 2);
            MinimumSize = new Size(Math.Min(maxWidth, (int)Math.Round(960 * scale)),
                Math.Min(maxHeight, (int)Math.Round(720 * scale)));
            if (initial)
                ClientSize = new Size((int)Math.Round(1120 * scale), (int)Math.Round(820 * scale));
            if (WindowState != FormWindowState.Normal) return;
            Size = new Size(Math.Min(Width, maxWidth), Math.Min(Height, maxHeight));
            Location = initial ? new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2)
                : new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)),
                    Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }

    }
}

