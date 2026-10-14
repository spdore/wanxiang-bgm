# Third-Party Notices

## Encrypted local audio converter

The local converter helper is built from the pinned MIT-licensed `music-geshizhuanhuan` decoder subset and bundles CPython 3.14.3, OpenSSL 3.0.18 runtime DLLs, PyInstaller 6.22.3, and PyCryptodome 3.20.0. Source provenance and local modifications are documented in [`vendor/music-geshizhuanhuan/UPSTREAM.md`](../vendor/music-geshizhuanhuan/UPSTREAM.md). The complete notices shipped beside and embedded in the player are in [`MusicConverter-NOTICES.txt`](MusicConverter-NOTICES.txt); the component license texts are also kept individually in this directory.

The QMC/KGM decoder uses only key material embedded in a selected local file and the bundled public KGM key asset. No client, online service, or external key database is included.

- **Tabler Icons**: the application icon assets include Tabler artwork. See [`Tabler-LICENSE.txt`](Tabler-LICENSE.txt).
- **Microsoft WebView2**: the application uses the WebView2 SDK and distributes the official Evergreen Runtime installer in the Windows release package. See [`WebView2-LICENSE.txt`](WebView2-LICENSE.txt) and [`WebView2-NOTICE.txt`](WebView2-NOTICE.txt). Microsoft's runtime is updated through its Evergreen servicing channel.
- **VB-CABLE**: the release package contains the official signed VB-CABLE driver distribution archive. VB-Audio's distribution and donation terms are available at [vb-audio.com/Services/licensing.htm](https://vb-audio.com/Services/licensing.htm). Use and redistribute the driver only under the vendor's applicable terms.

The application's installer is not digitally signed by this project.
