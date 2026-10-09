using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BgmHotkey
{
 internal sealed partial class MainMenuForm
 {
  [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
  [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam);
  private WebView2 _web;
  private bool _webReady;
  private bool _webInitialized;
  private bool _webPresented;
  private Timer _startupTimeout;
  private bool _webProbing;
  private string _webProbe = "";
  private string[] _webFiles = new string[0];
  private readonly JavaScriptSerializer _webJson = new JavaScriptSerializer { MaxJsonLength = 4194304 };

  private async void InitializeWebInterface()
  {
   if(_webInitialized || IsDisposed) return;
   _webInitialized=true;
   _startupTimeout=new Timer{Interval=20000};
   _startupTimeout.Tick+=delegate{_startupTimeout.Stop();if(!IsDisposed&&!_webPresented)ShowStartupFailure("界面加载超时。请关闭窗口后重试，并检查 WebView2 Runtime。");};
   _startupTimeout.Start();
   try {
    _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = System.Drawing.Color.FromArgb(245,245,242) };
    Controls.Add(_web); _startupSurface.BringToFront();
    CoreWebView2Environment.SetLoaderDllFolderPath(AppPaths.Root);
    CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.SettingsDirectory,"WebView"));
    if(IsDisposed) return;
    await _web.EnsureCoreWebView2Async(environment);
    if(IsDisposed) return;
    _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
    _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
    _web.CoreWebView2.Settings.AreHostObjectsAllowed = false;
    bool interfaceNavigationAccepted = false;
    _web.CoreWebView2.NavigationStarting += delegate(object o, CoreWebView2NavigationStartingEventArgs e) { if(!interfaceNavigationAccepted && e.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)) { interfaceNavigationAccepted = true; return; } if(e.Uri != "about:blank") e.Cancel = true; };
    _web.CoreWebView2.WebMessageReceived += WebMessage;
    _web.CoreWebView2.NavigationCompleted += delegate(object o, CoreWebView2NavigationCompletedEventArgs e) { if(e.IsSuccess) { _webReady = true; RefreshWebFiles(); PublishWebState(); } else { ShowStartupFailure("界面加载失败："+e.WebErrorStatus); } };
    using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Interface"))
    {
     if(stream==null)throw new InvalidOperationException("缺少界面文件。");
     using(StreamReader reader=new StreamReader(stream))_web.NavigateToString(reader.ReadToEnd());
    }
   } catch(Exception ex) {
    AppPaths.Log("网页界面启动失败："+ex);
    _webReady=false;
    if(_web!=null){_web.Dispose();_web=null;}
    ShowStartupFailure("界面启动失败。请检查 Microsoft Edge WebView2 Runtime 是否已安装。\n安装后重新启动播放器。\n\n微软官方下载：developer.microsoft.com/microsoft-edge/webview2/");
   }
  }
  private async void CaptureStartupIfRequested()
  {
   if(Array.IndexOf(Environment.GetCommandLineArgs(),"--capture-ui")<0)return;
   try{
    string folder=Path.Combine(AppPaths.Root,"diagnostics");Directory.CreateDirectory(folder);
    for(int i=1;i<=3;i++){
     await System.Threading.Tasks.Task.Delay(1000);if(IsDisposed||_web==null)return;
     using(Stream file=File.Create(Path.Combine(folder,"actual-exe-startup-"+i+".png")))await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
     string focus=await _web.CoreWebView2.ExecuteScriptAsync("JSON.stringify({active:document.activeElement.tagName,id:document.activeElement.id,keyboard:document.documentElement.classList.contains('keyboard-input'),outlined:[...document.querySelectorAll('button,input,select')].filter(e=>getComputedStyle(e).outlineStyle!=='none'&&getComputedStyle(e).outlineWidth!=='0px').map(e=>e.textContent||e.id)})");
     File.AppendAllText(Path.Combine(folder,"actual-exe-focus.txt"),DateTime.Now+" "+focus+Environment.NewLine);
    }
   }catch(Exception ex){AppPaths.Log("UI capture: "+ex.Message);}
  }
  private async void ResetStartupWebFocus()
  {
   if(IsDisposed||!_webPresented||_web==null||_web.CoreWebView2==null)return;
   try{await _web.CoreWebView2.ExecuteScriptAsync("resetStartupFocus()");}catch(InvalidOperationException){}catch(System.Runtime.InteropServices.COMException){}
  }
  private void ShowStartupFailure(string message)
  {
   if(IsDisposed)return;
   if(_startupTimeout!=null)_startupTimeout.Stop();
   FormBorderStyle=FormBorderStyle.Sizable;
   _startupMessage.Text=message; _startupSurface.Visible=true; _startupSurface.BringToFront(); ShowInTaskbar=true; Opacity=1;
  }
  private void RefreshWebFiles()
  {
   List<string> files=new List<string>();
   try {
    string folder=AppPaths.TrackPath("");
    if(Directory.Exists(folder))foreach(string path in Directory.GetFiles(folder)) {
     string name=Path.GetFileName(path);if(AppSettings.IsAudioFileName(name))files.Add(name);
    }
    files.Sort(StringComparer.CurrentCultureIgnoreCase);_webFiles=files.ToArray();
   }catch(Exception ex){NotifyWeb("读取歌曲文件夹失败："+ex.Message);}
  }
  private void NotifyWeb(string text)
  {
   if(!_webReady||_web==null||IsDisposed)return;
   try{_web.CoreWebView2.PostWebMessageAsJson(_webJson.Serialize(new {type="toast",text=text}));}catch(InvalidOperationException){}
  }
  private void PublishWebState()
  {
   if(!_webReady||_web==null||IsDisposed)return;
   try {
    List<object> tracks=new List<object>();bool playing=false;
    foreach(TrackDefinition t in TrackCatalog.All) {
     bool active=_engine.IsTrackPlaying(t.Id);playing|=active;
     tracks.Add(new {id=t.Id,name=t.Name,file=t.FileName,key=FormatKey(_settings.GetKey(t.Id))=="未设置"?"":FormatKey(_settings.GetKey(t.Id)),playing=active});
    }
    foreach(string file in _settings.RandomFileNames??new string[0])playing|=_engine.IsTrackPlaying("random_file:"+file);
    List<string> mics=new List<string>();foreach(AudioDevice d in _devices.Microphones)mics.Add(d.Name);
    List<string> cables=new List<string>();foreach(AudioDevice d in _devices.Cables)cables.Add(d.Name);
    _web.CoreWebView2.PostWebMessageAsJson(_webJson.Serialize(new {type="state",tracks=tracks,head=_settings.HeadphoneVolume,cable=_settings.CableVolume,randomKey=_settings.RandomKey,selected=_settings.RandomFileNames??new string[0],files=_webFiles,microphones=mics,cables=cables,microphone=_settings.MicrophoneDeviceName,cableDevice=_settings.CableDeviceName,playing=playing,summary=_engine.PlaybackSummary,status=_status.Text,probe=_webProbe,probing=_webProbing,micPeak=AudioRouteProbe.MeterValue(_engine.MicrophonePeak),musicPeak=AudioRouteProbe.MeterValue(_engine.MusicPeak),outputPeak=AudioRouteProbe.MeterValue(_engine.OutputPeak)}));
   }catch(InvalidOperationException){}
  }
  private static string WebString(Dictionary<string,object> message,string name)
  {object value;return message.TryGetValue(name,out value)?Convert.ToString(value):"";}
  private void WebMessage(object sender,CoreWebView2WebMessageReceivedEventArgs e)
  {
   if(e.Source!="about:blank")return;
   try {
    Dictionary<string,object> message=_webJson.Deserialize<Dictionary<string,object>>(e.TryGetWebMessageAsString());
    string action=WebString(message,"action"),id=WebString(message,"id"),key=WebString(message,"key");
    if(action=="window") {
     string operation=WebString(message,"operation");
     if(operation=="close")Close();
     else if(operation=="minimize")WindowState=FormWindowState.Minimized;
     else if(operation=="maximize"){MaximizedBounds=Screen.FromControl(this).WorkingArea;WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;}
     else if(operation=="drag"){ReleaseCapture();SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);}
     else if(operation.StartsWith("resize:")&&WindowState==FormWindowState.Normal){int edge;if(Int32.TryParse(operation.Substring(7),out edge)&&edge>=1&&edge<=8){ReleaseCapture();SendMessage(Handle,0x112,new IntPtr(0xF000+edge),IntPtr.Zero);}}
     return;
    }
    if(action=="painted") { if(!_webPresented) { _webPresented=true; if(_startupTimeout!=null)_startupTimeout.Stop(); _startupSurface.Visible=false; _web.BringToFront(); ShowInTaskbar=true; Opacity=1; BeginInvoke(new MethodInvoker(delegate{ResetStartupWebFocus();CaptureStartupIfRequested();})); } return; }
    if(action=="ready") {RefreshWebFiles();PublishWebState();return;}
    if(action=="capture") {_capturingKey=message.ContainsKey("active")&&Convert.ToBoolean(message["active"]);return;}
    if(action=="play") {TrackDefinition track=TrackCatalog.Find(id);if(track!=null)_engine.ToggleTrack(track);}
    else if(action=="stop")_engine.StopActiveTrack();
    else if(action=="randomPlay")PlayRandom();
    else if(action=="add") {AddSongs(this,EventArgs.Empty);RefreshWebFiles();}
    else if(action=="remove") {TrackDefinition track=TrackCatalog.Find(id);if(track!=null)DeleteSong(track);}
    else if(action=="volume") {
     _headphoneVolume.Value=Math.Max(0,Math.Min(100,Convert.ToInt32(message["head"])));
     _cableVolume.Value=Math.Max(0,Math.Min(100,Convert.ToInt32(message["cable"])));
    }
    else if(action=="bind"||action=="randomKey") {
     bool random=action=="randomKey";string normalized=AppSettings.NormalizeKey(key);
     if(normalized.Length==0 && !(random&&key.Length==0)) {NotifyWeb("请使用字母、数字或功能键。");return;}
     foreach(TrackDefinition t in TrackCatalog.All)if((random||t.Id!=id)&&normalized.Length>0&&String.Equals(_settings.GetKey(t.Id),normalized,StringComparison.OrdinalIgnoreCase)){NotifyWeb("这个按键已绑定给“"+t.Name+"”。");return;}
     if(!random&&String.Equals(_settings.RandomKey,normalized,StringComparison.OrdinalIgnoreCase)){NotifyWeb("这个按键已用于随机播放。");return;}
     if(!random&&TrackCatalog.Find(id)==null)return;
     string old=random?_settings.RandomKey:_settings.GetKey(id);
     if(random)_settings.RandomKey=normalized;else _settings.SetKey(id,normalized);
     string error;if(!SettingsStore.TrySave(_settings,out error)){if(random)_settings.RandomKey=old;else _settings.SetKey(id,old);NotifyWeb("保存失败："+error);}else {RebuildBindings();NotifyWeb("快捷键已保存。");}
    }
    else if(action=="randomFiles") {
     List<string> selected=new List<string>();object raw;
     if(message.TryGetValue("files",out raw))foreach(object item in (System.Collections.IEnumerable)raw){string file=Convert.ToString(item);if(AppSettings.IsAudioFileName(file)&&Array.Exists(_webFiles,delegate(string f){return String.Equals(f,file,StringComparison.OrdinalIgnoreCase);})&&!selected.Contains(file))selected.Add(file);}
     string[] old=_settings.RandomFileNames;_settings.RandomFileNames=selected.ToArray();string error;
     if(!SettingsStore.TrySave(_settings,out error)){_settings.RandomFileNames=old;NotifyWeb("保存失败："+error);}
    }
    else if(action=="refreshFiles")RefreshWebFiles();
    else if(action=="devices") {_devices.Refresh();LoadDevices();_engine.ReconfigureDevices();NotifyWeb("设备已刷新。");}
    else if(action=="device") {
     string kind=WebString(message,"kind"),name=WebString(message,"name");ComboBox combo=kind=="mic"?_microphone:_cable;
     for(int i=0;i<combo.Items.Count;i++){AudioDevice d=combo.Items[i] as AudioDevice;if(d!=null&&d.Name==name&&d.Id!=0xFFFFFFFE){combo.SelectedIndex=i;break;}}
    }
    else if(action=="system")System.Diagnostics.Process.Start("control.exe","mmsys.cpl,,1");
    else if(action=="probe"&&!_webProbing) {
     _webProbing=true;_webProbe="正在检查，请播放音乐或讲话……";
     AudioDevice receiver=_devices.FindCableRecording(_devices.FindCable(_settings.CableDeviceName));
     System.Threading.ThreadPool.QueueUserWorkItem(delegate {
      string result;try{int peak=AudioRouteProbe.MeasurePeak(receiver);result=peak>AudioRouteProbe.SignalFloor?"已收到声音（"+AudioRouteProbe.FormatPeak(peak)+"）。QQ / 游戏麦克风请选择 CABLE Output。":"没有检测到明显声音。请检查歌曲播放、发送音量和设备选择。";}catch(Exception ex){result="检查失败："+ex.Message;}
      if(IsDisposed||!IsHandleCreated)return;
      try{BeginInvoke(new MethodInvoker(delegate{_webProbing=false;_webProbe=result;PublishWebState();}));}catch(InvalidOperationException){}
     });
    }
    PublishWebState();
   }catch(Exception ex){AppPaths.Log("界面操作失败："+ex);NotifyWeb("操作失败："+ex.Message);}
  }
 }
}





