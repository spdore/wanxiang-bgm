<div align="center">
  <img src="app/assets/brand.svg" width="72" alt="Wanxiang BGM Player">
  <h1>Wanxiang BGM Player</h1>
  <p>A Windows soundboard for voice chat.</p>
</div>

Wanxiang BGM Player plays local audio from global keyboard shortcuts. It mixes the selected physical microphone with this application's music and sends that mix to VB-CABLE. Headphone music and voice-chat music have independent volume controls.

**Current version: 1.8.4** · Windows 10 version 1903 or later / Windows 11 · x64 · Chinese interface · Empty library in the installer

## Download and install

Download the [1.8.4 Windows installer](https://github.com/spdore/wanxiang-bgm/releases/download/v1.8.4/WanxiangBgmSetup-1.8.4.exe). Run it, choose an installation folder, and continue through the setup wizard. The installer contains the official Microsoft WebView2 Evergreen offline runtime and the official VB-CABLE driver package. An existing WebView2 runtime or VB-CABLE installation is reused. Installing the driver requires Windows administrator approval and may require a restart; follow the signed driver's prompts.

The package is for 64-bit Intel/AMD Windows 10 version 1903 or later, or Windows 11, and requires .NET Framework 4.8 or later. The installer starts with an empty music library. Add your own files after installation.

## Features

- Import MP3, WAV, M4A, AAC, WMA, FLAC, Ogg Vorbis, Opus, and AIFF files supported by the current Windows media decoders.
- Assign a global shortcut to each library track. Only one BGM track can play at a time; pressing an assigned track key while music is loading or playing stops it.
- Set a start point and optional end point for single-track shortcut playback.
- Play the full song with the regular Play button or either random-playback entry point.
- Seek anywhere across the full song. Seeking during anchored playback removes that session's range limit, keeps saved anchor markers visible, and leaves the saved anchors unchanged.
- Build a random playlist from every supported audio file in the application's `bgm` folder, including tracks without individual shortcuts. Search, bulk-select visible results, and keep the selection across restarts.
- Mix only the selected physical microphone and this application's BGM into the virtual cable. Other applications' audio is not captured.
- Adjust headphone music and virtual-channel music independently. Removing a song from the library does not delete its source file.

## Quick start

1. Open **My Library** and choose **Add songs**. The file picker starts in the application's `bgm` folder.
2. Click a track's shortcut field and press a letter, number, or function key. Press Escape to cancel.
3. Press the assigned key or the card's **Play** button. The Play button starts the full song; the global shortcut uses that track's saved shortcut range.
4. To send music to chat, select your physical microphone and **CABLE Input** in the player. In QQ or the game, select **CABLE Output** as the microphone/recording device.

## Playback ranges and seeking

Choose **Shortcut playback range** to save a start point and an optional end point for a track. These points apply only when that track's single-track global shortcut starts playback. With no start point, playback starts at 00:00. With no end point, it continues to the end of the song.

The regular Play button and random playback always play the full track. The progress bar always spans the full song. If you drag it during shortcut-range playback, the current session continues from the chosen position to the song end, even when the position is outside the saved range. The saved anchor markers remain visible, and the next shortcut press still uses the saved range.

## Audio formats

The file picker accepts MP3, WAV, M4A, AAC, WMA, FLAC, Ogg Vorbis, Opus, and AIFF extensions. Windows Media Foundation natively supports MP3, WAVE, AAC, and WMA on supported Windows versions. Other formats depend on compatible system codecs; the application does not bundle third-party codecs, so they are not guaranteed on a clean installation. A file can appear as an import candidate and still fail if the installed decoder cannot open it.

MP3 seeking decodes forward and discards sample frames to avoid incorrect variable-bitrate seek-index results. Seeking far into a long MP3 can take longer than seeking an uncompressed WAV file.

## Audio routing

```text
Selected physical microphone ─┐
                              ├─> CABLE Input ─> CABLE Output ─> QQ / game chat
Application BGM ──────────────┘
```

The player mixes the selected microphone and its own BGM into **CABLE Input**. In the chat application, choose **CABLE Output** as the recording device. The player does not capture other applications' audio and does not change the chat application's settings for you.

The two music sliders are separate digital gains. Their UI range is 0–100%, mapped to an application gain range of 0–30%; these are not calibrated sound-pressure levels. Music controls do not change the selected microphone level.

## Build from source

Run PowerShell on a Windows development machine with the .NET Framework C# compiler installed:

```powershell
.\build.ps1 -OutputName 'BgmHotkey_1.8.4.exe'
```

The app uses C# 5, .NET Framework 4.8, Windows Forms, WinMM, Media Foundation, and WebView2. The interface is an embedded HTML/CSS/SVG document hosted by WebView2. The build copies the WebView2 SDK assemblies and architecture-specific loader beside the executable.

To build a share installer, download the official x64 Evergreen Standalone WebView2 Runtime installer from the [Microsoft WebView2 download page](https://developer.microsoft.com/microsoft-edge/webview2/) and place it at `vendor/webview2/runtime-installer/MicrosoftEdgeWebView2RuntimeInstallerX64.exe`. The signed VB-CABLE driver archive is included at `vendor/vb-cable/VBCABLE_Driver_Pack45.zip`. Then run:

```powershell
.\packaging\build-share.ps1 -OutputName 'WanxiangBgmSetup-1.8.4-local.exe'
```

The packaging script validates the Microsoft and VB-Audio Authenticode signatures and refuses to overwrite an existing installer. The WebView2 Evergreen runtime is omitted from the source tree because the offline installer is large; it is embedded in the downloadable release installer. The official Evergreen Standalone installer can be installed without an internet connection and is kept up to date through Microsoft's Evergreen update process. See Microsoft's [WebView2 distribution guidance](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution).

## Project layout and privacy

See [Project Structure](docs/PROJECT_STRUCTURE.md), [Installation Notes](docs/INSTALLATION.md), and [Changelog](CHANGELOG.md). Third-party notices are in [`licenses/`](licenses/).

The public source and release contain no personal music library, recordings, promotional video, workstation logs, or local machine paths. The share installer starts with an empty library. User settings and imported files stay in the current Windows user's local application data folder; uninstalling the player preserves them and does not remove the shared VB-CABLE driver or WebView2 runtime.

## Third-party components

The installer includes the official VB-CABLE driver by VB-Audio and the Microsoft WebView2 Evergreen Runtime offline installer. See the relevant third-party notices and distribution terms before redistributing modified packages. This project's installer is not digitally signed.
