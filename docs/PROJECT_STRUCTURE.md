# Project Structure

```text
app/
  assets/                         Application icon and brand assets
  ui/                             Embedded WebView2 interface
  src/
    Core/                         Application entry point and assembly metadata
    Audio/
      Analysis/                   Loudness measurement
      Devices/                    Audio device enumeration and format catalog
      Engine/                     Mixer and Windows multimedia output
      Playback/                   MP3 decoding and playback backends
      Routing/                    Audio route probes
    Features/
      Import/                     Audio validation, conversion, and import workers
      Library/                    Song library membership and file operations
      Scenes/                     Scene playback, shuffle bags, and sound packs
    Platform/
      Hotkeys/                    Windows global shortcut integration
    Settings/                     Persistence, migration, and application paths
    UI/                           WinForms shell, WebView bridge, and theme
  app.config                      .NET runtime configuration
  app.manifest                    Windows application manifest
docs/                             Architecture, installation, format, and layout docs
licenses/                         Third-party license and notice files
packaging/
  Installer.cs                    Setup and uninstaller implementation
  build-installer.ps1             Versioned setup build entry point
  installation-guide.en.txt       Guide embedded in the setup
tests/
  unit/                           Core application regression harnesses
  ui/                             Browser UI regression suite
  integration/import/             Import fixtures and integration harnesses
  integration/installer/          Installer transaction harness
  scripts/                        Test and release verification entry points
tools/                            Helper builder and offline converter source
vendor/                           Pinned SDKs and approved third-party packages
deliverables/                     Versioned product communications
build.ps1                         Player build entry point
README.md                         Project overview and build instructions
CHANGELOG.md                      Version history
```

The C# application uses one shared namespace and is compiled with the .NET Framework compiler. The directory tree separates source by responsibility; `build.ps1` and the installer builder recursively collect C# files from `app/src` so modules do not need to be manually added to a project file.

The WebView2 Bootstrapper under `vendor/webview2/bootstrapper/` is validated before it is embedded. If WebView2 Runtime is missing, the Bootstrapper downloads it during setup.

Build the distributable player with `build.ps1 -PublicRelease -OutputDirectory release/1.9.8`, then build the installer with `packaging/build-installer.ps1`. Release outputs go in `release/1.9.8/`. The installer starts with an empty library and excludes user data.

## Application data

The application stores settings and imported music locally. A fresh installation starts with an empty library, and the test build uses isolated storage.
