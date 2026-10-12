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
  private string _webEditorTrackId = "";
  private double _webEditorDuration;
  private bool _webEditorLoading;
  private string _webEditorError = "";
  private string _webEditorWarning = "";
  private long _webEditorFileLength;
  private DateTime _webEditorLastWriteUtc;
  private int _webEditorRequest;
  private bool _analysisRunning;
  private readonly object _analysisQueueGate = new object();
  private readonly Queue<string> _pendingAnalysisFiles = new Queue<string>();
  private readonly HashSet<string> _pendingAnalysisSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  private bool _analysisInitializationPending;
  private bool _packBusy;
  private string _pendingImportToken="";
  private readonly JavaScriptSerializer _webJson = new JavaScriptSerializer { MaxJsonLength = 4194304 };

  private sealed class LoudnessSnapshot
  {
   public bool Initialized;
   public bool BaselineReady;
   public string BaselineFileName;
   public string BaselineFingerprint;
   public double BaselineLufs;
   public string[] Files;
   public string[] TrackFiles;
   public LoudnessAnalysis[] Analyses;
  }

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
    _web.CoreWebView2.Settings.IsZoomControlEnabled = false;
    bool interfaceNavigationAccepted = false;
    _web.CoreWebView2.NavigationStarting += delegate(object o, CoreWebView2NavigationStartingEventArgs e) { if(!interfaceNavigationAccepted && e.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)) { interfaceNavigationAccepted = true; return; } if(e.Uri != "about:blank") e.Cancel = true; };
    _web.CoreWebView2.WebMessageReceived += WebMessage;
    _web.CoreWebView2.NavigationCompleted += delegate(object o,CoreWebView2NavigationCompletedEventArgs e) { if(e.IsSuccess) { _webReady = true; RefreshWebFiles(); PublishWebState(); } else { ShowStartupFailure("界面加载失败："+e.WebErrorStatus); } };
    using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Interface"))
    using(StreamReader reader=new StreamReader(stream))
    { _web.NavigateToString(reader.ReadToEnd()); }
   } catch(Exception ex) {
    AppPaths.Log("网页界面启动失败："+ex);
    _webReady=false;
    if(_web!=null){_web.Dispose();_web=null;}
    ShowStartupFailure("界面启动失败。请检查 Microsoft Edge WebView2 Runtime 是否已安装。\n安装后重新启动播放器。\n\n微软官方下载：developer.microsoft.com/microsoft-edge/webview2/");
   }
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
   try {
    string folder=AppPaths.TrackPath("");
    _webFiles=RandomFileCatalog.Scan(folder);
    string[] previous=_settings.ActiveRandomFiles??new string[0];
    string[] retained=RandomFileCatalog.RetainAvailable(previous,_webFiles);
    if(retained.Length!=previous.Length){_settings.ActiveRandomFiles=retained;string saveError;if(!SettingsStore.TrySave(_settings,out saveError)){_settings.ActiveRandomFiles=previous;AppPaths.Log("清理随机曲库中已移除的歌曲失败："+saveError);}}
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
     tracks.Add(new {id=t.Id,name=t.Name,file=t.FileName,key=FormatKey(_settings.GetKey(t.Id))=="未设置"?"":FormatKey(_settings.GetKey(t.Id)),playing=active,start=t.StartSeconds,end=t.EndSeconds});
    }
    foreach(string file in _settings.ActiveRandomFiles??new string[0])playing|=_engine.IsTrackPlaying("random_file:"+file);
    List<string> mics=new List<string>();foreach(AudioDevice d in _devices.Microphones)mics.Add(d.Name);
    List<string> cables=new List<string>();foreach(AudioDevice d in _devices.Cables)cables.Add(d.Name);
    PlaybackSnapshot playback=_engine.GetPlaybackSnapshot();
    object playbackState=new {sessionId=playback.SessionId,generation=playback.Generation,trackId=playback.TrackId,trackName=playback.TrackName,fileName=playback.FileName,state=playback.State,positionSeconds=playback.PositionSeconds,durationSeconds=playback.DurationSeconds,startSeconds=playback.StartSeconds,endSeconds=playback.EndSeconds,error=playback.Error,canSeek=playback.CanSeek};
    SavedTrack editorTrack=_settings.FindActiveTrack(_webEditorTrackId);
    object editor=new {id=_webEditorTrackId,loading=_webEditorLoading,error=_webEditorError,warning=_webEditorWarning,duration=_webEditorDuration,start=editorTrack==null?0:editorTrack.StartSeconds,end=editorTrack==null?(double?)null:editorTrack.EndSeconds};
    List<object> scenes=new List<object>();foreach(ScenePreset scene in _settings.Scenes??new ScenePreset[0])
    {
     int keyCount=0;foreach(TrackBinding binding in scene.Bindings??new TrackBinding[0])if(binding!=null&&!String.IsNullOrWhiteSpace(binding.Key))keyCount++;
     int randomCount=(scene.RandomFileNames??new string[0]).Length;
     scenes.Add(new {id=scene.Id,name=scene.Name,trackCount=(scene.Tracks??new SceneTrack[0]).Length,keyCount=keyCount,randomCount=randomCount});
    }
    _web.CoreWebView2.PostWebMessageAsJson(_webJson.Serialize(new {type="state",tracks=tracks,head=_settings.HeadphoneVolume,cable=_settings.CableVolume,randomKey=_settings.ActiveRandomKey,selected=_settings.ActiveRandomFiles??new string[0],files=_webFiles,formats=AudioFormatCatalog.SupportedExtensionsText,microphones=mics,cables=cables,microphone=_settings.MicrophoneDeviceName,cableDevice=_settings.CableDeviceName,playing=playing||playback.State=="playing"||playback.State=="loading",summary=_engine.PlaybackSummary,status=_status.Text,playback=playbackState,editor=editor,probe=_webProbe,probing=_webProbing,micPeak=AudioRouteProbe.MeterValue(_engine.MicrophonePeak),musicPeak=AudioRouteProbe.MeterValue(_engine.MusicPeak),headphoneMusicPeak=AudioRouteProbe.MeterValue(_engine.HeadphoneMusicPeak),outputPeak=AudioRouteProbe.MeterValue(_engine.OutputPeak),musicSpectrum=_engine.MusicSpectrum,scenes=scenes,activeScene=_settings.ActiveSceneId,packBusy=_packBusy,packProgress=SoundPackService.ProgressText}));
   }catch(InvalidOperationException){}
  }

  private void InspectTrackForAnchors(string id)
  {
   TrackDefinition track=TrackCatalog.Find(id);if(track==null)return;
   int request=++_webEditorRequest;
   _webEditorTrackId=track.Id;_webEditorDuration=0;_webEditorLoading=true;_webEditorError="";_webEditorWarning="";_webEditorFileLength=0;_webEditorLastWriteUtc=DateTime.MinValue;PublishWebState();
   string path=AppPaths.TrackPath(track.FileName);
   System.Threading.ThreadPool.QueueUserWorkItem(delegate {
    double duration=0;string error="";long length=0;DateTime lastWrite=DateTime.MinValue;
    try{
     FileInfo before=new FileInfo(path);if(!before.Exists)throw new FileNotFoundException("音频文件不存在。",path);long beforeLength=before.Length;DateTime beforeWrite=before.LastWriteTimeUtc;
     duration=AudioFormatCatalog.ProbeDurationSeconds(path);
     FileInfo after=new FileInfo(path);if(!after.Exists||after.Length!=beforeLength||after.LastWriteTimeUtc!=beforeWrite)throw new IOException("读取时音频文件发生了变化，请重新打开片段编辑。 ");
     length=after.Length;lastWrite=after.LastWriteTimeUtc;
    }catch(Exception ex){error=ex.Message;}
    if(IsDisposed||!IsHandleCreated)return;
    try{BeginInvoke(new MethodInvoker(delegate {
     if(IsDisposed||request!=_webEditorRequest)return;
     _webEditorLoading=false;_webEditorDuration=duration;_webEditorError=error;_webEditorWarning="";_webEditorFileLength=length;_webEditorLastWriteUtc=lastWrite;
     SavedTrack saved=_settings.FindActiveTrack(id);
     if(error.Length==0&&saved!=null&&(saved.StartSeconds>=duration||(saved.EndSeconds.HasValue&&(saved.EndSeconds.Value>duration||saved.EndSeconds.Value<=saved.StartSeconds))))
      _webEditorWarning="当前音频时长已短于保存的播放锚点，请调整或清除越界时间后保存。";
     if(error.Length>0)NotifyWeb("无法编辑播放片段："+error);
     PublishWebState();
    }));}catch(InvalidOperationException){}
   });
  }

  private void SendAnchorSaveResult(string requestId,string trackId,string sceneId,bool success,string error)
  {
   if(!_webReady||_web==null||IsDisposed)return;
   try{_web.CoreWebView2.PostWebMessageAsJson(_webJson.Serialize(new {type="anchorSaveResult",requestId=requestId,trackId=trackId,sceneId=sceneId,success=success,error=error??""}));}catch(InvalidOperationException){}catch(System.Runtime.InteropServices.COMException){}
  }
  private void RejectAnchorSave(string requestId,string trackId,string sceneId,string error)
  {SendAnchorSaveResult(requestId,trackId,sceneId,false,error);}
  private void SavePlaybackAnchors(Dictionary<string,object> message,string id)
  {
   string requestId=WebString(message,"requestId"),sceneId=WebString(message,"sceneId");
   if(!String.Equals(sceneId,_settings.ActiveSceneId,StringComparison.OrdinalIgnoreCase))
   {RejectAnchorSave(requestId,id,sceneId,"场景已切换，请重新打开歌曲的起止点设置。");return;}
   if(!String.Equals(id,_webEditorTrackId,StringComparison.OrdinalIgnoreCase)||_webEditorLoading||_webEditorDuration<=0)
   {RejectAnchorSave(requestId,id,sceneId,"请等待音频时长读取完成。");return;}
   try{
    TrackDefinition current=TrackCatalog.Find(id);if(current==null){RejectAnchorSave(requestId,id,sceneId,"这首歌已从曲库移除。");return;}
    FileInfo currentFile=new FileInfo(AppPaths.TrackPath(current.FileName));
    if(!currentFile.Exists||currentFile.Length!=_webEditorFileLength||currentFile.LastWriteTimeUtc!=_webEditorLastWriteUtc)
    {InspectTrackForAnchors(id);RejectAnchorSave(requestId,id,sceneId,"音频文件已改变，正在重新读取时长，请检查后再次保存。");return;}
   }catch(Exception ex){RejectAnchorSave(requestId,id,sceneId,"重新检查音频文件失败："+ex.Message);return;}
   SceneTrack saved=_settings.FindSceneTrack(id);
   if(saved==null){RejectAnchorSave(requestId,id,sceneId,"找不到这首歌的场景设置，请重新打开后重试。");return;}
   object rawStart;double start;
   if(!message.TryGetValue("startSeconds",out rawStart)||!Double.TryParse(Convert.ToString(rawStart,System.Globalization.CultureInfo.InvariantCulture),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out start)||Double.IsNaN(start)||Double.IsInfinity(start)||start<0||start>=_webEditorDuration)
   {RejectAnchorSave(requestId,id,sceneId,"开始锚点必须在歌曲时长以内。");return;}
   double? end=null;object rawEnd;
   if(message.TryGetValue("endSeconds",out rawEnd)&&rawEnd!=null)
   {
    double parsed;
    if(!Double.TryParse(Convert.ToString(rawEnd,System.Globalization.CultureInfo.InvariantCulture),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out parsed)||Double.IsNaN(parsed)||Double.IsInfinity(parsed)||parsed<=start||parsed>_webEditorDuration)
    {RejectAnchorSave(requestId,id,sceneId,"结束锚点必须大于开始锚点且不能超过歌曲时长。");return;}
    end=parsed;
   }
   double oldStart=saved.StartSeconds;double? oldEnd=saved.EndSeconds;
   saved.StartSeconds=start;saved.EndSeconds=end;
   string error;
   if(!SettingsStore.TrySave(_settings,out error))
   {
    saved.StartSeconds=oldStart;saved.EndSeconds=oldEnd;_settings.Normalize();
    RejectAnchorSave(requestId,id,sceneId,"保存失败："+error);return;
   }
   _webEditorError="";_webEditorWarning="";PublishWebState();SendAnchorSaveResult(requestId,id,sceneId,true,"");
  }

  private void InitializeLoudnessNormalization()
  {
   if(_settings.LoudnessNormalizationInitialized)return;
   lock(_analysisQueueGate)_analysisInitializationPending=true;
   StartLoudnessWorker();
  }

  private void QueueLoudnessAnalysis(IEnumerable<string> fileNames)
  {
   if(fileNames==null)return;
   lock(_analysisQueueGate)
   {
    foreach(string file in fileNames)
     if(AppSettings.IsAudioFileName(file)&&File.Exists(AppPaths.TrackPath(file))&&_pendingAnalysisSet.Add(file))_pendingAnalysisFiles.Enqueue(file);
   }
   StartLoudnessWorker();
  }

  private void StartLoudnessWorker()
  {
   lock(_analysisQueueGate)
   {
    if(_analysisRunning||(!_analysisInitializationPending&&_pendingAnalysisFiles.Count==0))return;
    _analysisRunning=true;
   }
   System.Threading.ThreadPool.QueueUserWorkItem(delegate
   {
    try
    {
     while(true)
     {
      bool initialize=false;string file=null;
      lock(_analysisQueueGate)
      {
       if(_analysisInitializationPending){_analysisInitializationPending=false;initialize=true;}
       else if(_pendingAnalysisFiles.Count>0){file=_pendingAnalysisFiles.Dequeue();_pendingAnalysisSet.Remove(file);}
       else break;
      }
      if(initialize)ProcessInitialLoudnessNormalization();
      else if(file!=null)ProcessImportedLoudness(file);
     }
    }
    catch(Exception ex){AppPaths.Log("后台歌曲响度统一失败："+ex);}
    finally
    {
     bool restart;
     lock(_analysisQueueGate){_analysisRunning=false;restart=_analysisInitializationPending||_pendingAnalysisFiles.Count>0;}
     if(restart)StartLoudnessWorker();
    }
   });
  }

  private LoudnessSnapshot CaptureLoudnessSnapshot()
  {
   LoudnessSnapshot snapshot=new LoudnessSnapshot();
   snapshot.Initialized=_settings.LoudnessNormalizationInitialized;
   snapshot.BaselineReady=_settings.LoudnessBaselineReady;
   snapshot.BaselineFileName=_settings.LoudnessBaselineFileName??"";
   snapshot.BaselineFingerprint=_settings.LoudnessBaselineFingerprint??"";
   snapshot.BaselineLufs=_settings.LoudnessBaselineLufs;
   snapshot.Files=(string[])_webFiles.Clone();
   List<string> trackFiles=new List<string>();
   foreach(SavedTrack track in _settings.Tracks??new SavedTrack[0])if(track!=null&&!String.IsNullOrWhiteSpace(track.FileName)&&!trackFiles.Contains(track.FileName))trackFiles.Add(track.FileName);
   snapshot.TrackFiles=trackFiles.ToArray();
   List<LoudnessAnalysis> analyses=new List<LoudnessAnalysis>();
   foreach(LoudnessAnalysis item in _settings.LoudnessAnalyses??new LoudnessAnalysis[0])if(item!=null)analyses.Add(CloneAnalysis(item));
   snapshot.Analyses=analyses.ToArray();
   return snapshot;
  }

  private LoudnessSnapshot ReadLoudnessSnapshot()
  {
   return InvokeOnUi(new Func<LoudnessSnapshot>(CaptureLoudnessSnapshot));
  }

  private T InvokeOnUi<T>(Func<T> action)
  {
   if(IsDisposed||!IsHandleCreated)throw new InvalidOperationException("播放器窗口已关闭。");
   if(InvokeRequired)return (T)Invoke(action);
   return action();
  }

  private void InvokeOnUi(Action action)
  {
   if(IsDisposed||!IsHandleCreated)throw new InvalidOperationException("播放器窗口已关闭。");
   if(InvokeRequired)Invoke(new MethodInvoker(action));else action();
  }

  private void ProcessInitialLoudnessNormalization()
  {
   LoudnessSnapshot snapshot=ReadLoudnessSnapshot();
   if(snapshot.Initialized||snapshot.Files.Length==0)return;
   List<string> candidates=new List<string>();
   foreach(string file in snapshot.TrackFiles)if(!candidates.Contains(file))candidates.Add(file);
   foreach(string file in snapshot.Files)if(!candidates.Contains(file))candidates.Add(file);
   List<LoudnessAnalysis> updates=new List<LoudnessAnalysis>();
   HashSet<string> attempted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   string baselineFile=snapshot.BaselineFileName;
   string baselineFingerprint=snapshot.BaselineFingerprint;
   double target=snapshot.BaselineReady?snapshot.BaselineLufs:LoudnessAnalyzer.TargetLufs;
   bool baselineReady=snapshot.BaselineReady;
   if(!baselineReady)
   {
    foreach(string candidate in candidates)
    {
     if(!File.Exists(AppPaths.TrackPath(candidate)))continue;
     attempted.Add(candidate);
     try
     {
      LoudnessAnalysis measured=AnalyzeOrReuse(candidate,LoudnessAnalyzer.TargetLufs,snapshot.Analyses);
      if(measured==null)continue;
      updates.Add(measured);
      if(measured.IntegratedLufs>-99)
      {
       baselineFile=candidate;baselineFingerprint=measured.Fingerprint;target=measured.IntegratedLufs;baselineReady=true;
       updates[updates.Count-1]=RetargetAnalysis(measured,target);
       break;
      }
     }
     catch(Exception ex){AppPaths.Log("响度基准候选处理失败（"+candidate+"）："+ex.Message);}
    }
   }
   if(!baselineReady)target=LoudnessAnalyzer.TargetLufs;
   for(int i=0;i<updates.Count;i++)updates[i]=RetargetAnalysis(updates[i],target);
   HashSet<string> updatedNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(LoudnessAnalysis item in updates)updatedNames.Add(item.FileName);
   foreach(string file in snapshot.Files)
   {
    if(updatedNames.Contains(file)||attempted.Contains(file))continue;
    try
    {
     LoudnessAnalysis result=AnalyzeOrReuse(file,target,snapshot.Analyses);
     if(result!=null){updates.Add(RetargetAnalysis(result,target));updatedNames.Add(file);}
    }
    catch(Exception ex){AppPaths.Log("歌曲响度统一失败（"+file+"）："+ex.Message);}
   }
   ApplyLoudnessUpdates(updates.ToArray(),baselineReady,baselineFile,baselineFingerprint,target,true);
  }

  private void ProcessImportedLoudness(string file)
  {
   LoudnessSnapshot snapshot=ReadLoudnessSnapshot();
   try
   {
    double target=snapshot.BaselineReady?snapshot.BaselineLufs:LoudnessAnalyzer.TargetLufs;
    LoudnessAnalysis result=AnalyzeOrReuse(file,target,snapshot.Analyses);
    if(result==null)return;
    bool baselineReady=snapshot.BaselineReady;string baselineFile=snapshot.BaselineFileName;string baselineFingerprint=snapshot.BaselineFingerprint;
    if(!baselineReady&&result.IntegratedLufs>-99)
    {
     target=result.IntegratedLufs;baselineReady=true;baselineFile=file;baselineFingerprint=result.Fingerprint;
    }
    result=RetargetAnalysis(result,target);
    ApplyLoudnessUpdates(new LoudnessAnalysis[]{result},baselineReady,baselineFile,baselineFingerprint,target,
     snapshot.Initialized||baselineReady);
   }
   catch(Exception ex){AppPaths.Log("新导入歌曲响度统一失败（"+file+"）："+ex);}
  }

  private LoudnessAnalysis AnalyzeOrReuse(string file,double target,LoudnessAnalysis[] cachedAnalyses)
  {
   string path=AppPaths.TrackPath(file);FileInfo before=new FileInfo(path);
   if(!before.Exists)throw new FileNotFoundException("音频文件不存在。",path);
   long length=before.Length;DateTime write=before.LastWriteTimeUtc;
   string fingerprint=LoudnessAnalyzer.Fingerprint(path);
   LoudnessAnalysis cached=null;
   foreach(LoudnessAnalysis item in cachedAnalyses??new LoudnessAnalysis[0])
    if(item!=null&&String.Equals(item.FileName,file,StringComparison.OrdinalIgnoreCase)&&LoudnessAnalyzer.IsCompatibleAlgorithmVersion(item.AlgorithmVersion)) {cached=item;break;}
   if(cached!=null&&String.Equals(cached.Fingerprint,fingerprint,StringComparison.OrdinalIgnoreCase))
    return RetargetAnalysis(cached,target);
   LoudnessAnalysisResult measured=LoudnessAnalyzer.Analyze(path,target);
   FileInfo after=new FileInfo(path);
   if(!after.Exists||after.Length!=length||after.LastWriteTimeUtc!=write)throw new IOException("分析期间音频文件发生变化。");
   return new LoudnessAnalysis{FileName=file,Fingerprint=fingerprint,IntegratedLufs=measured.IntegratedLufs,
    PeakDbfs=measured.PeakDbfs,GainDb=measured.GainDb,TargetLufs=target,AlgorithmVersion=LoudnessAnalyzer.AlgorithmVersion,IsValidated=true};
  }

  private static LoudnessAnalysis RetargetAnalysis(LoudnessAnalysis source,double target)
  {
   if(source==null)return null;
   return new LoudnessAnalysis{FileName=source.FileName,Fingerprint=source.Fingerprint,IntegratedLufs=source.IntegratedLufs,
    PeakDbfs=source.PeakDbfs,GainDb=LoudnessAnalyzer.CalculateGainDb(source.IntegratedLufs,source.PeakDbfs,target),
    TargetLufs=target,AlgorithmVersion=LoudnessAnalyzer.AlgorithmVersion,IsValidated=true};
  }

  private static LoudnessAnalysis CloneAnalysis(LoudnessAnalysis source)
  {
   return new LoudnessAnalysis{FileName=source.FileName,Fingerprint=source.Fingerprint,IntegratedLufs=source.IntegratedLufs,
    PeakDbfs=source.PeakDbfs,GainDb=source.GainDb,TargetLufs=source.TargetLufs,AlgorithmVersion=source.AlgorithmVersion,IsValidated=source.IsValidated};
  }

  private void ApplyLoudnessUpdates(LoudnessAnalysis[] updates,bool baselineReady,string baselineFile,string baselineFingerprint,double target,bool markInitialized)
  {
   InvokeOnUi(delegate
   {
    if(IsDisposed)return;
    if(baselineReady)
    {
     _settings.LoudnessBaselineReady=true;_settings.LoudnessBaselineFileName=baselineFile??"";
     _settings.LoudnessBaselineFingerprint=baselineFingerprint??"";_settings.LoudnessBaselineLufs=target;
    }
    foreach(LoudnessAnalysis item in updates??new LoudnessAnalysis[0])_settings.SetAnalysis(item);
    if(markInitialized)_settings.LoudnessNormalizationInitialized=true;
    TrackCatalog.Configure(_settings);
    string error;if(!SettingsStore.TrySave(_settings,out error))AppPaths.Log("保存响度归一化结果失败："+error);
    PublishWebState();
   });
  }

  private void ExportSoundPack()
  {
   using(SaveFileDialog picker=new SaveFileDialog{Title="导出当前场景音效包",Filter="BGM 场景包 (*.wbgm)|*.wbgm",DefaultExt="wbgm",AddExtension=true,FileName=SafeSceneFileName(_settings.ActiveScene==null?"BGM场景":_settings.ActiveScene.Name)+".wbgm",OverwritePrompt=true})
   {
    _capturingKey=true;DialogResult result;try{result=picker.ShowDialog(this);}finally{_capturingKey=false;}
    if(result!=DialogResult.OK)return;
    _packBusy=true;PublishWebState();string path=picker.FileName;
    System.Threading.ThreadPool.QueueUserWorkItem(delegate
    {
     string error="";try{SoundPackService.Export(_settings,path);}catch(Exception ex){error=ex.Message;}
     if(IsDisposed||!IsHandleCreated)return;
     try{BeginInvoke(new MethodInvoker(delegate{_packBusy=false;_capturingKey=false;NotifyWeb(error.Length==0?"音效包已导出。":(error.IndexOf("cancel",StringComparison.OrdinalIgnoreCase)>=0?"导出已取消。":"导出失败："+error));PublishWebState();}));}catch(InvalidOperationException){}
    });
   }
  }

  private void BeginImportSoundPack()
  {
   using(OpenFileDialog picker=new OpenFileDialog{Title="选择要导入的 BGM 场景包",Filter="BGM 场景包 (*.zip;*.wbgm)|*.zip;*.wbgm|所有文件 (*.*)|*.*",CheckFileExists=true,Multiselect=false,RestoreDirectory=true})
   {
    _capturingKey=true;DialogResult result;try{result=picker.ShowDialog(this);}finally{_capturingKey=false;}
    if(result!=DialogResult.OK)return;
    _packBusy=true;PublishWebState();string path=picker.FileName;
    System.Threading.ThreadPool.QueueUserWorkItem(delegate
    {
     SoundPackPreview preview=null;string error="";try{preview=SoundPackService.PrepareImport(path);}catch(Exception ex){error=ex.Message;}
     if(IsDisposed||!IsHandleCreated){if(preview!=null)SoundPackService.CancelPrepared(preview.Token);return;}
     try{BeginInvoke(new MethodInvoker(delegate
     {
      _packBusy=false;_capturingKey=false;
      if(preview==null)NotifyWeb(error.IndexOf("cancel",StringComparison.OrdinalIgnoreCase)>=0?"导入预览已取消。":"无法预览音效包："+error);
      else {_pendingImportToken=preview.Token;NotifyPackPreview(preview);}
      PublishWebState();
     }));}catch(InvalidOperationException){if(preview!=null)SoundPackService.CancelPrepared(preview.Token);}
    });
   }
  }

  private void NotifyPackPreview(SoundPackPreview preview)
  {
   if(!_webReady||_web==null||IsDisposed)return;
   try{_web.CoreWebView2.PostWebMessageAsJson(_webJson.Serialize(new {type="packPreview",token=preview.Token,name=preview.SceneName,tracks=preview.TrackCount,assets=preview.AssetCount,bytes=preview.ExpandedBytes,warnings=preview.Warnings}));}catch(InvalidOperationException){}
  }

  private void ConfirmSoundPackImport()
  {
   string token=_pendingImportToken;if(String.IsNullOrWhiteSpace(token)){NotifyWeb("音效包预览已失效，请重新导入。");return;}
   _pendingImportToken="";_packBusy=true;_capturingKey=true;PublishWebState();
   SavedTrack[] assets=(SavedTrack[])_settings.Tracks.Clone();List<string> names=new List<string>();foreach(ScenePreset scene in _settings.Scenes)names.Add(scene.Name);
   string[] sceneNames=names.ToArray();
   System.Threading.ThreadPool.QueueUserWorkItem(delegate
   {
    SoundPackImportPlan plan=null;string error="";try{plan=SoundPackService.BuildImportPlan(token,assets,sceneNames);}catch(Exception ex){error=ex.Message;}
    if(IsDisposed||!IsHandleCreated){if(plan!=null)SoundPackService.RollbackFiles(plan);SoundPackService.CancelPrepared(token);return;}
    try{BeginInvoke(new MethodInvoker(delegate
    {
     _packBusy=false;_capturingKey=false;
     if(plan==null){SoundPackService.CancelPrepared(token);NotifyWeb(error.IndexOf("cancel",StringComparison.OrdinalIgnoreCase)>=0?"导入已取消。":"导入失败："+error);PublishWebState();return;}
     ApplySoundPackImport(plan,token);
    }));}catch(InvalidOperationException){if(plan!=null)SoundPackService.RollbackFiles(plan);SoundPackService.CancelPrepared(token);}
   });
  }

  private void ApplySoundPackImport(SoundPackImportPlan plan,string token)
  {
   SavedTrack[] oldAssets=_settings.Tracks;ScenePreset[] oldScenes=_settings.Scenes;LoudnessAnalysis[] oldAnalyses=_settings.LoudnessAnalyses;
   List<SavedTrack> newAssets=new List<SavedTrack>(oldAssets);foreach(SavedTrack asset in plan.NewAssets)newAssets.Add(asset);
   List<ScenePreset> newScenes=new List<ScenePreset>(oldScenes);newScenes.Add(plan.Scene);
   _settings.Tracks=newAssets.ToArray();_settings.Scenes=newScenes.ToArray();
   foreach(LoudnessAnalysis analysis in plan.NewAnalyses)_settings.SetAnalysis(analysis);
   string error;
   if(!SettingsStore.TrySave(_settings,out error))
   {
    _settings.Tracks=oldAssets;_settings.Scenes=oldScenes;_settings.LoudnessAnalyses=oldAnalyses;_settings.Normalize();
    SoundPackService.RollbackFiles(plan);SoundPackService.CancelPrepared(token);NotifyWeb("导入未完成，配置保存失败，已回滚新文件："+error);PublishWebState();return;
   }
   SoundPackService.CompleteImport(token);RefreshWebFiles();RebuildBindings();
    List<string> importedFiles=new List<string>();HashSet<string> importedFileSet=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach(SavedTrack asset in plan.NewAssets??new SavedTrack[0])
     if(asset!=null&&!String.IsNullOrWhiteSpace(asset.FileName)&&importedFileSet.Add(asset.FileName))importedFiles.Add(asset.FileName);
    foreach(string file in plan.Scene.RandomFileNames??new string[0])
     if(!String.IsNullOrWhiteSpace(file)&&importedFileSet.Add(file))importedFiles.Add(file);
    foreach(SceneTrack entry in plan.Scene.Tracks??new SceneTrack[0])
    {
     SavedTrack asset=null;
     foreach(SavedTrack candidate in plan.NewAssets??new SavedTrack[0])
      if(candidate!=null&&String.Equals(candidate.Id,entry.TrackId,StringComparison.OrdinalIgnoreCase)){asset=candidate;break;}
     if(asset==null)asset=_settings.FindAsset(entry.TrackId);
     if(asset!=null&&!String.IsNullOrWhiteSpace(asset.FileName)&&importedFileSet.Add(asset.FileName))importedFiles.Add(asset.FileName);
    }
   QueueLoudnessAnalysis(importedFiles);
   string detail=plan.Warnings==null||plan.Warnings.Length==0?"":" 提示："+String.Join("；",plan.Warnings);
   NotifyWeb("已导入场景“"+plan.Scene.Name+"”和 "+plan.Scene.Tracks.Length+" 首歌曲；新场景未自动启用，音频文件未覆盖。"+detail);PublishWebState();
  }

  private static string SafeSceneFileName(string value)
  {
   if(String.IsNullOrWhiteSpace(value))return "BGM场景";
   string result=value;foreach(char ch in Path.GetInvalidFileNameChars())result=result.Replace(ch,'_');
   result=result.Trim();return result.Length==0?"BGM场景":(result.Length>64?result.Substring(0,64):result);
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
    if(_packBusy&&action!="cancelPack"){NotifyWeb("音效包任务正在执行，完成或取消后再继续。");return;}
    if(action=="painted") { if(!_webPresented) { _webPresented=true; if(_startupTimeout!=null)_startupTimeout.Stop(); _startupSurface.Visible=false; _web.BringToFront(); ShowInTaskbar=true; Opacity=1; BeginInvoke(new MethodInvoker(delegate{ResetStartupWebFocus();})); } return; }
    if(action=="ready") {RefreshWebFiles();InitializeLoudnessNormalization();PublishWebState();return;}
    if(action=="capture") {_capturingKey=message.ContainsKey("active")&&Convert.ToBoolean(message["active"]);return;}
    if(action=="play") {TrackDefinition track=TrackCatalog.Find(id);if(track!=null)_engine.ToggleTrack(track,false);}
    else if(action=="stop")_engine.StopActiveTrack();
    else if(action=="randomPlay")PlayRandom();
    else if(action=="add") AddSongs(this,EventArgs.Empty);
    else if(action=="remove") {TrackDefinition track=TrackCatalog.Find(id);if(track!=null)DeleteSong(track);}
    else if(action=="inspectTrack") InspectTrackForAnchors(id);
    else if(action=="saveAnchors") SavePlaybackAnchors(message,id);
    else if(action=="seek") {
     object rawPosition;double seconds;
     if(message.TryGetValue("positionSeconds",out rawPosition)&&Double.TryParse(Convert.ToString(rawPosition,System.Globalization.CultureInfo.InvariantCulture),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out seconds)) {
      int session,generation;object rawSession,rawGeneration;
      if(!message.TryGetValue("sessionId",out rawSession)||!Int32.TryParse(Convert.ToString(rawSession,System.Globalization.CultureInfo.InvariantCulture),out session)||
         !message.TryGetValue("generation",out rawGeneration)||!Int32.TryParse(Convert.ToString(rawGeneration,System.Globalization.CultureInfo.InvariantCulture),out generation)||
         !_engine.Seek(session,generation,seconds))NotifyWeb("播放状态已变化，请松开进度条后重试。");
     }
    }
    else if(action=="volume") {
     _headphoneVolume.Value=Math.Max(0,Math.Min(100,Convert.ToInt32(message["head"])));
     _cableVolume.Value=Math.Max(0,Math.Min(100,Convert.ToInt32(message["cable"])));
    }
    else if(action=="bind"||action=="randomKey") {
     bool random=action=="randomKey";string normalized=AppSettings.NormalizeKey(key);
     if(normalized.Length==0 && !(random&&key.Length==0)) {NotifyWeb("请使用字母、数字或功能键。");return;}
     foreach(TrackDefinition t in TrackCatalog.All)if((random||t.Id!=id)&&normalized.Length>0&&String.Equals(_settings.GetKey(t.Id),normalized,StringComparison.OrdinalIgnoreCase)){NotifyWeb("这个按键已绑定给“"+t.Name+"”。");return;}
     if(!random&&String.Equals(_settings.ActiveRandomKey,normalized,StringComparison.OrdinalIgnoreCase)){NotifyWeb("这个按键已用于随机播放。");return;}
     if(!random&&TrackCatalog.Find(id)==null)return;
     string old=random?_settings.ActiveRandomKey:_settings.GetKey(id);
     if(random)_settings.ActiveRandomKey=normalized;else _settings.SetKey(id,normalized);
     string error;if(!SettingsStore.TrySave(_settings,out error)){if(random)_settings.ActiveRandomKey=old;else _settings.SetKey(id,old);NotifyWeb("保存失败："+error);}else {RebuildBindings();NotifyWeb("快捷键已保存。");}
    }
    else if(action=="randomFiles") {
     List<string> selected=new List<string>();object raw;
     if(message.TryGetValue("files",out raw))foreach(object item in (System.Collections.IEnumerable)raw){string file=Convert.ToString(item);if(AppSettings.IsAudioFileName(file)&&Array.Exists(_webFiles,delegate(string f){return String.Equals(f,file,StringComparison.OrdinalIgnoreCase);})&&!selected.Contains(file))selected.Add(file);}
     string[] old=_settings.ActiveRandomFiles;_settings.ActiveRandomFiles=selected.ToArray();string error;
     if(!SettingsStore.TrySave(_settings,out error)){_settings.ActiveRandomFiles=old;NotifyWeb("保存失败："+error);}
    }
    else if(action=="sceneSwitch") {
     string oldScene=_settings.ActiveSceneId;
     if(!_settings.SetActiveScene(id)){NotifyWeb("找不到这个场景。");return;}
     if(!_engine.StopActiveTrack()) { }
     string sceneError;if(!SettingsStore.TrySave(_settings,out sceneError)){_settings.SetActiveScene(oldScene);NotifyWeb("切换场景失败："+sceneError);return;}
     _webEditorTrackId="";RefreshWebFiles();RebuildBindings();NotifyWeb("已切换场景："+_settings.ActiveScene.Name);
    }
    else if(action=="sceneCreate"||action=="sceneCopy") {
     string name=WebString(message,"name");if(String.IsNullOrWhiteSpace(name))name="新场景";
     ScenePreset created=_settings.CreateScene(name,action=="sceneCopy");
     string previous=_settings.ActiveSceneId;_settings.ActiveSceneId=created.Id;TrackCatalog.Configure(_settings);_engine.StopActiveTrack();
     string sceneError;if(!SettingsStore.TrySave(_settings,out sceneError)){_settings.DeleteScene(created.Id);_settings.ActiveSceneId=previous;TrackCatalog.Configure(_settings);NotifyWeb("创建场景失败："+sceneError);return;}
     _webEditorTrackId="";RefreshWebFiles();RebuildBindings();NotifyWeb(action=="sceneCopy"?"场景已复制。":"已创建空场景。");
    }
    else if(action=="sceneRename") {
     string name=WebString(message,"name");ScenePreset active=_settings.FindScene(id);if(active==null){NotifyWeb("找不到这个场景。");return;}string oldName=active.Name;
     if(!_settings.RenameScene(id,name)){NotifyWeb("场景名称不能为空。");return;}
     string sceneError;if(!SettingsStore.TrySave(_settings,out sceneError)){active.Name=oldName;NotifyWeb("重命名失败："+sceneError);return;}NotifyWeb("场景名称已保存。");
    }
    else if(action=="sceneDelete") {
     if((_settings.Scenes??new ScenePreset[0]).Length<=1){NotifyWeb("至少保留一个场景。");return;}
     string previous=_settings.ActiveSceneId;List<ScenePreset> previousScenes=new List<ScenePreset>(_settings.Scenes);
     if(!_settings.DeleteScene(id)){NotifyWeb("找不到要删除的场景。");return;}
     if(String.Equals(previous,id,StringComparison.OrdinalIgnoreCase))_engine.StopActiveTrack();
     string sceneError;if(!SettingsStore.TrySave(_settings,out sceneError)){_settings.Scenes=previousScenes.ToArray();_settings.ActiveSceneId=previous;TrackCatalog.Configure(_settings);NotifyWeb("删除场景失败："+sceneError);return;}
     _webEditorTrackId="";RefreshWebFiles();RebuildBindings();NotifyWeb("场景已删除，音频文件仍保留。");
    }
    else if(action=="refreshFiles"){RefreshWebFiles();ResetRandomShuffleForRefresh();PublishWebState();}
    else if(action=="exportPack")ExportSoundPack();
    else if(action=="importPack")BeginImportSoundPack();
    else if(action=="confirmPackImport")ConfirmSoundPackImport();
    else if(action=="cancelPackImport") {if(!String.IsNullOrWhiteSpace(_pendingImportToken)){SoundPackService.CancelPrepared(_pendingImportToken);_pendingImportToken="";}NotifyWeb("已取消导入。");}
    else if(action=="cancelPack")SoundPackService.Cancel();
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







