<div align="center">
  <img src="brand.svg" width="72" alt="Wanxiang BGM Player">
  <h1>Wanxiang BGM Player</h1>
  <p>A simple soundboard for your voice chat.</p>
  <p><a href="https://github.com/spdore/wanxiang-bgm/raw/refs/heads/main/WanxiangBgmSetup.exe">Download for Windows</a></p>
</div>

Play local MP3 files with global keyboard shortcuts and share your music with teammates through VB-CABLE. Adjust the music in your headphones and voice chat independently.

**Current version: 1.5.2.** The installer includes no songs or preset shortcuts. The application interface is currently available in Chinese; the control names below are English descriptions.

## Features

- Import and search your own MP3 files.
- Assign a global shortcut to each song.
- Select songs from the music folder for random playback, even without individual shortcuts.
- Mix your physical microphone with music from this application.
- Control headphone music and voice-chat music volume separately.
- Switch between light and dark themes.
- Close the window to stop the application.

## Installation

1. Download [the Windows installer](https://github.com/spdore/wanxiang-bgm/raw/refs/heads/main/WanxiangBgmSetup.exe).
2. Run the installer. If VB-CABLE is missing, select the option to install the bundled official driver.
3. Follow the driver installer instructions and restart Windows if requested.
4. Open the player using its desktop or Start menu shortcut.

You need Windows 10 or 11, .NET Framework 4.8, and [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). The full WebView2 Runtime is not bundled. VB-CABLE driver installation requires administrator permission. The application installer is unsigned.

## Playing Music

1. Use the add-song button in the upper-right corner to import an MP3.
2. Select the song's shortcut field and press a letter, number, or F1 through F24. Press Esc to cancel.
3. Click the song's play button or press its assigned shortcut.
4. While music is playing, press any bound song shortcut to stop it.

Only one song can play at a time. Removing a library entry keeps its audio file. Settings save automatically.

### Random Playback

Open the random-playback tab, refresh the music-folder list, and select the songs you want to include. Assign an unused shortcut to random playback.

Candidates can include any MP3 in the application's music folder; they do not need individual library entries or shortcuts. If music is already playing, the random shortcut stops it. Press the shortcut again to choose a random song.

### Volume Controls

The two sliders independently control music in your headphones and music sent to voice chat. Each displayed range of 0 to 100 percent maps to an actual music gain of 0 to 30 percent. Changing voice-chat music volume does not change microphone volume.

Headphone playback uses the Windows default output device. These percentages describe digital gain, not calibrated sound pressure.

## Connecting Voice Chat

```text
Physical microphone + App music
                 |
            CABLE Input
                 |
            CABLE Output
                 |
         QQ or game voice chat
```

1. Open the voice-settings tab in the player.
2. Select your physical microphone.
3. Select **CABLE Input** as the sending device.
4. In QQ or your game, select **CABLE Output** as the microphone.

The virtual channel receives your selected microphone and this application's music. Other computer audio is not captured. Music also plays through your default headphones.

### If Teammates Cannot Hear Music

- Refresh devices and confirm both cable endpoint selections.
- Raise the voice-chat music slider.
- Play a song and use the audio-channel check in the voice-settings tab.
- Check the chat application's input meter and noise suppression settings.
- Reselect your physical microphone if the saved device is unavailable.

## Local Data

The distribution stores settings in `%LOCALAPPDATA%\BgmHotkeyShare` and imported songs in its `bgm` subfolder. No personal songs, device settings, recordings, logs, screenshots, credentials, or developer workstation paths are included in the download.

Uninstalling the player preserves user songs and settings. It does not uninstall the shared VB-CABLE driver.

## Building from Source

Run these commands in PowerShell from the repository root on Windows:

```powershell
.\build.ps1
.\build-share.ps1
```

The first command builds the empty-library player and prepares its WebView2 dependencies. The second creates the distribution installer; its output location is printed when the build completes.

Keep the player EXE, its configuration file, the WebView2 assemblies, and the loader together when running a source build.

The application uses C# 5, .NET Framework 4.8, Windows Forms, and an embedded offline HTML/CSS/SVG interface hosted in WebView2. Native MP3 playback provides headphone audio. Media Foundation and WinMM provide the music and microphone mix for the virtual channel. No Node.js server is needed at runtime.

VB-CABLE is donationware from [VB-Audio](https://vb-audio.com/Cable/). The bundled standard driver package is unmodified and retains the vendor's terms. WebView2 and Tabler notices accompany their components.
