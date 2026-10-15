<div align="center">
  <img src="app/assets/brand.svg" width="72" alt="Wanxiang BGM Player">
  <h1>Wanxiang BGM Player</h1>
  <p>A Windows soundboard for voice chat.</p>
</div>

Wanxiang BGM Player plays local audio from global keyboard shortcuts. It mixes the selected physical microphone with this application's music and sends that mix to VB-CABLE. Headphone music and voice-chat music have independent volume controls.

**Current source version: 1.9.8** · Windows 10 version 1903 or later / Windows 11 · x64 · Chinese-language interface · Empty library in the installer

## Download and install

Download the [1.9.8 installer](https://github.com/spdore/wanxiang-bgm/releases/download/v1.9.8/WanxiangBgmSetup-1.9.8.exe) from the [GitHub release page](https://github.com/spdore/wanxiang-bgm/releases/tag/v1.9.8). Choose an installation folder and continue through the setup wizard. The installer checks for the Microsoft WebView2 Runtime and reuses it when present. If it is missing, the included official Microsoft Bootstrapper downloads and installs it, so an internet connection is required in that case. The official VB-CABLE driver package is included. Installing the driver requires Windows administrator approval and may require a restart; follow the signed driver's prompts.

The package is for 64-bit Intel/AMD Windows 10 version 1903 or later, or Windows 11, and requires .NET Framework 4.8 or later. The installer starts with an empty music library. Add your own files after installation.

## Features

- Import MP3, WAV, M4A, AAC, WMA, FLAC, Ogg Vorbis, Opus, and AIFF files supported by the current Windows media decoders.
- Convert selected local NetEase NCM, traditional QQ QMCv1, embedded-key QQ QMCv2, and Kugou KGM/VPR files offline. QQ `.mflac`/`.mgg` conversion is limited to recognized QMCv2 variants with an embedded EKey; MusicEx/STag files without one return `KeyUnavailable`. KGM/VPR support is limited to crypto v3/v4. See the [encrypted format support matrix](docs/ENCRYPTED_FORMAT_SUPPORT.md); real platform samples are still needed before claiming compatibility across current downloads.
- Assign a global shortcut to each library track. Only one BGM track can play at a time; pressing an assigned track key while music is loading or playing stops it.
- Set a start point and optional end point for single-track shortcut playback.
- Audition unsaved range edits through the Windows default audio output. Audition never saves the draft or sends it to CABLE Input; the editor timeline shows only its editable start and end markers.
- Play the full song with the regular Play button or either random-playback entry point.
- Seek anywhere across the full song. Seeking during anchored playback removes that session's range limit, keeps saved anchor markers visible, and leaves the saved anchors unchanged.
- Build a random playlist from every supported audio file in the application's `bgm` folder, including tracks without individual shortcuts. Search, bulk-select visible results, and keep the selection across restarts.
- Use a non-repeating shuffle bag for random playback. Songs do not repeat within a round, and when the pool has at least two songs the next round does not start with the previous round's last song. Each scene keeps its own queue while switching between scenes.
- Manage scene presets on their own top-level page. Create, copy, rename, remove, and switch scenes; each scene stores its song membership, shortcut keys, shortcut ranges, and random playlist. Audio devices and volume remain global.
- Analyze integrated loudness in the background using BS.1770 / EBU R128. On first run, the first measurable song becomes the reference baseline and the existing library is analyzed once. Later runs reuse cached measurements; newly imported audio is analyzed against the saved baseline. Positive gain is capped at +11 dB and each track is kept below a -1 dBFS sample peak. The gain ceiling keeps native MP3 playback within MCI's 0–1000 volume-factor range, so headphone and virtual-channel playback can use the same per-track gain. Silent or unreadable files keep unity gain. Analysis runs without a progress card or per-song technical text.
- Apply a -1 dBFS sample-peak limiter after mixing the selected microphone and BGM for the virtual channel. This is a sample-peak ceiling, not a true-peak guarantee.
- Export or import a `.wbgm` sound pack containing one scene and its referenced shortcut and random-playlist audio. Packs omit absolute paths, global device/volume settings, and unrelated user data. Import validates paths and SHA-256 hashes, reuses identical local files, avoids overwriting name conflicts, and creates a new scene after confirmation.
- Mix only the selected physical microphone and this application's BGM into the virtual cable. Other applications' audio is not captured.
- Adjust headphone music and virtual-channel music independently. Removing a song from the library does not delete its source file.
- Use the graphite-and-amber console styling in dark mode and a matching warm graphite-and-amber palette in light mode. Both retain the existing top navigation and page layouts.
- Disable WebView2 zoom controls, including Ctrl+mouse wheel, so the interface scale stays fixed.

## Version history

This README records the project history available in the repository, from 1.8.4 through the current 1.9.8 source. See [CHANGELOG.md](CHANGELOG.md) for the detailed change log.

- **1.9.8** — Stability release. Reorganizes the C# application by functional module and groups tests by unit, UI, and integration scope. Standardizes the versioned player and installer output under `release/1.9.8/`; playback and import behavior is intended to remain unchanged.
- **1.9.7** — Adds offline import for supported NCM, QQ QMCv1/QMCv2, and Kugou KGM/VPR variants; bounded, cancellable conversion and audio validation; themed per-file import results; headphone-only audition of unsaved playback ranges; and per-scene continuous random playback. The range editor now shows only the adjustable start/end markers. Continuous playback remains active across page navigation and no-op library refreshes.
- **1.9.6** — Replaces the large offline WebView2 runtime package with Microsoft's official Evergreen Bootstrapper and reuses an installed runtime.
- **1.9.5** — Aligns the playback thumb with its seek rail and disables WebView2 zoom controls.
- **1.9.4** — Restores the warm GRAPHITE light theme, improves volume-meter and progress styling, and adds a live BGM-only spectrum.
- **1.9.3** — Introduces the GRAPHITE console styling, top navigation, independent vertical output faders, and route-specific music meters.
- **1.9.2** — Refines keyboard focus, closes the range editor after a successful save, and adds a themed scene-delete dialog.
- **1.9.1** — Adds the dedicated scene-management page and persisted loudness baseline; simplifies track cards and removes loudness controls from Voice Settings.
- **1.9.0** — Adds per-scene libraries and shuffle bags, integrated-loudness matching, a virtual-channel limiter, portable .wbgm scene packs, and an empty-library installer.
- **1.8.4** — Adds multi-format audio import, per-track shortcut ranges, full-song seeking, a searchable random playlist, and light/dark themes.

## Quick start

1. Open **My Library** and choose **Add songs**. The file picker starts in the application's `bgm` folder.
2. Click a track's shortcut field and press a letter, number, or function key. Press Escape to cancel.
3. Press the assigned key or the card's **Play** button. The Play button starts the full song; the global shortcut uses that track's saved shortcut range.
4. To send music to chat, select your physical microphone and **CABLE Input** in the player. In QQ or the game, select **CABLE Output** as the microphone/recording device.

## Scenes

Use the **Scenes** page in the main navigation to create an empty preset, copy the current scene, rename or delete a scene, or switch the active scene. Scene changes stop the current BGM and replace its song list and shortcuts; the microphone connection, output routing, and volume sliders remain shared.

## Playback ranges and seeking

Choose **Shortcut playback range** to save a start point and an optional end point for a track. These points apply only when that track's single-track global shortcut starts playback. With no start point, playback starts at 00:00. With no end point, it continues to the end of the song.

The regular Play button and random playback always play the full track. The progress bar always spans the full song. If you drag it during shortcut-range playback, the current session continues from the chosen position to the song end, even when the position is outside the saved range. The saved anchor markers remain visible, and the next shortcut press still uses the saved range.

## Audio formats

The file picker accepts MP3, WAV, M4A, AAC, WMA, FLAC, Ogg Vorbis, Opus, and AIFF extensions. Windows Media Foundation natively supports MP3, WAVE, AAC, and WMA on supported Windows versions. Other formats depend on compatible system codecs; the application does not bundle third-party codecs, so they are not guaranteed on a clean installation. A file can appear as an import candidate and still fail if the installed decoder cannot open it.

MP3 seeking decodes forward and discards sample frames to avoid incorrect variable-bitrate seek-index results. Seeking far into a long MP3 can take longer than seeking an uncompressed WAV file.

## Encrypted local audio

Version 1.9.7 adds an offline conversion helper for NCM, QMCv1/QMCv2, and KGM/VPR. The helper uses a pinned, MIT-licensed decoder subset and is bundled in the player; users do not need Python. It does not log in, download files, inspect client processes, or read cookies, tickets, or external key databases. It accepts only keys embedded in supported files. Output type is determined from the decrypted bytes, and the existing player decoder must read the complete converted file before the library imports it. Some outputs depend on Windows codec availability. The offline converter caps encrypted input at 256 MiB and decoded output at 512 MiB. Conversion still materializes payloads in memory, so these caps bound memory demand but do not make the decoder streaming. Import inspection, conversion, Media Foundation validation, deduplication, and large-file copying run in owned child processes with bounded deadlines and cancellable process-tree cleanup. A per-file timeout is reported separately and does not cancel the remaining batch. See the [support matrix](docs/ENCRYPTED_FORMAT_SUPPORT.md) for exact variants, timeout defaults, and sample status.

Version 1.9.7 introduced the song-import modal, unsaved-range audition, and per-scene continuous random playback. The 1.9.8 source retains those behaviors: audition uses the Windows default audio output and does not save anchors or send music to CABLE Input; continuous playback advances only after natural completion and uses the scene's no-repeat shuffle bag. Navigation and refreshes that leave the selected candidate set unchanged do not cancel a run. Missing or undecodable files are skipped or reported without an unbounded retry loop.

## Loudness normalization

The first measurable audio file in the existing library becomes the persisted reference baseline. If the library is empty, its first analyzable import becomes the baseline. The application analyzes the current library once in the background, then reuses cached loudness measurements on later launches. Newly imported files are analyzed against the saved baseline; the baseline measurement itself is reused from its cache. New tracks are matched to the baseline's integrated loudness, subject to the +11 dB gain cap and -1 dBFS per-track sample-peak ceiling. No audio file is rewritten. The final virtual-channel mix also uses a -1 dBFS sample-peak limiter after microphone and BGM are mixed; this is not a true-peak guarantee.

## Audio routing

```text
Selected physical microphone ─┐
                              ├─> CABLE Input ─> CABLE Output ─> QQ / game chat
Application BGM ─────────────┘
```

The player mixes the selected microphone and its own BGM into **CABLE Input**. In the chat application, choose **CABLE Output** as the recording device. The player does not capture other applications' audio and does not change the chat application's settings for you.

The two home-page controls are separate vertical digital faders: one for headphone BGM and one for voice-chat BGM. Their UI range is 0–100%, mapped to an application gain range of 0–30%; these are not calibrated sound-pressure levels. Each narrow segmented meter reflects the measured music signal after that route's gain. Music controls do not change the selected microphone level.

The player timeline keeps its full-song seeking behavior and saved anchor markers. It shows a separate seek rail and progress fill beneath live spectral bars computed from this application's BGM PCM stream. Bars run from low frequencies at the left to high frequencies at the right; microphone audio is excluded. They are a live spectrum display, not a precomputed waveform, and do not change seek positions or anchors.

## Build from source

Run PowerShell on a Windows development machine with CPython 3.14.3 x64, the pinned helper build packages, and the .NET Framework C# compiler installed. Python is a build-time dependency only; the output contains the helper runtime.

```powershell
.\tools\build-encrypted-audio-helper.ps1 `
  -OutputArchive 'local-only\build-staging\encrypted-audio-helper-1.9.8.zip' `
  -PythonPath 'C:\path\to\python.exe'
.\build.ps1 -PublicRelease -OutputDirectory 'release\1.9.8'
.\packaging\build-installer.ps1 `
  -PlayerPath 'release\1.9.8\BgmHotkey_1.9.8.exe' `
  -CryptoHelperArchive 'local-only\build-staging\encrypted-audio-helper-1.9.8.zip'
```

The helper build requires PyInstaller 6.22.3, PyCryptodome 3.20.0, and the exact supporting package versions checked by `tools/build-encrypted-audio-helper.ps1`. The app uses C# 5, .NET Framework 4.8, Windows Forms, WinMM, Media Foundation, and WebView2. The interface is an embedded HTML/CSS/SVG document hosted by WebView2. The distributable build starts with an empty library. Generated release files are ignored by Git.

Run the isolated regression suite from the project root with `.\tests\scripts\run_project_regressions.ps1`. It compiles into `local-only/test-build/` and covers core playback/settings, browser UI, import process isolation, encrypted import fixtures, and installer file transactions.

To build a share installer, download the official Microsoft Evergreen WebView2 Bootstrapper from [Microsoft](https://go.microsoft.com/fwlink/p/?LinkId=2124703) and save it as `vendor/webview2/bootstrapper/MicrosoftEdgeWebview2Setup.exe`. The signed VB-CABLE driver archive is included at `vendor/vb-cable/VBCABLE_Driver_Pack45.zip`. Then run:

```powershell
.\packaging\build-installer.ps1 `
  -PythonPath 'C:\path\to\python.exe' `
  -PythonDependencyPath 'C:\path\to\python\Lib\site-packages'
```

`PythonDependencyPath` is optional when packages are already available to the selected Python interpreter. The packaging script validates the Microsoft Bootstrapper and VB-Audio Authenticode signatures and refuses to overwrite an existing installer. The small Bootstrapper is embedded in the release package; the full Evergreen Runtime is downloaded from Microsoft only if it is missing. Setup therefore needs internet access only for that missing-runtime case. The Bootstrapper uses Microsoft's documented `/silent /install` command and setup verifies the Runtime after installation. This package is not a fully offline installer. See Microsoft's [WebView2 distribution guidance](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution).

## Project layout

See [Project Structure](docs/PROJECT_STRUCTURE.md), [Architecture](docs/ARCHITECTURE.md), [Encrypted Format Support](docs/ENCRYPTED_FORMAT_SUPPORT.md), [Installation Notes](docs/INSTALLATION.md), and [Changelog](CHANGELOG.md). Third-party notices are in [`licenses/`](licenses/).

New installations start with an empty music library. Add your own audio files after setup.

## Third-party components

The installer includes the offline converter helper, CPython/OpenSSL runtime libraries, the official VB-CABLE driver by VB-Audio, and Microsoft's official WebView2 Evergreen Bootstrapper. Converter source, checksums, and third-party terms are recorded in `vendor/music-geshizhuanhuan/UPSTREAM.md` and `licenses/MusicConverter-NOTICES.txt`. The full converter notice is embedded in the player and installed beside it. See all notices and distribution terms before redistributing modified packages. This project's installer is not digitally signed.

