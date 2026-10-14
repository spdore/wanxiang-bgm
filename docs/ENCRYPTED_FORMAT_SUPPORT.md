# Encrypted local audio support

This feature converts user-selected local files offline. It does not download music, sign in to a platform, read another process, access cookies or client tickets, or search a client key database. Source files remain untouched. A converted file is copied into the existing library only after the player can decode the complete output.

## Support matrix

| Format family and recognized variant | Required material / content rule | Component | Current verification | Limits |
| --- | --- | --- | --- | --- |
| NetEase NCM container | NCM header and the file's embedded key metadata; output type is identified from decrypted bytes | `music-geshizhuanhuan` at `28f4ed0724f85978405d961c0e955266eed383ee`; PyCryptodome 3.20.0 | Synthetic NCM crypto round-trip and content sniff passed; no real user-authorized NCM file supplied | Only output containers recognized by the helper and decodable by Windows Media Foundation are imported |
| QQ QMCv1 traditional static-key forms | QMCv1 transform must recover a recognized audio signature; suffix does not select the algorithm | Same pinned decoder | Synthetic QMCv1 crypto round-trip and content sniff passed; no real QMCv1 file supplied | Unrecognized payloads return `UnsupportedVariant` or `InvalidFile` |
| QQ QMCv2 `QTag` / embedded EKey, Map stream | EKey is embedded in the file; derived master key is at most 300 bytes | Same pinned decoder | Synthetic Map round-trip passed with misleading `.mgg`/FLAC and `.mflac`/Ogg suffix pairings; a separate valid synthetic WAV completed host decoder validation; no real `.mflac`/`.mgg` file supplied | Actual output is sniffed; `.mflac` does not force FLAC and `.mgg` does not force Ogg |
| QQ QMCv2 `QTag` / embedded EKey, RC4 stream | EKey is embedded in the file; derived master key is longer than 300 bytes | Same pinned decoder | Synthetic RC4 crypto round-trip passed with content-based output selection; no real `.mflac`/`.mgg` file supplied | Only the upstream Map/RC4 variants selected from the parsed embedded EKey are covered |
| QQ QMCv2 `PcV1Legacy` / embedded EKey | Legacy EKey is present in the file; Map/RC4 selection follows the derived key length | Same pinned decoder | Synthetic embedded-EKey round-trip passed; no authorized real sample supplied | Key is held only by the helper process while converting |
| QQ QMCv2 `MusicEx` and `STag` | These structures do not carry a usable EKey for this offline implementation | Same pinned decoder | Synthetic missing-key cases returned `KeyUnavailable` without producing output; no real file supplied | No external key lookup or manual key input is provided |
| Other or unknown QMC variants | A recognized structure and supported decoder path are required | Same pinned decoder | Synthetic unknown/truncated files returned `UnsupportedVariant`/`InvalidFile` without producing output; no real samples supplied | Extensions alone never select QMCv1/v2 decryption |
| Kugou KGM/VPR crypto v3 and v4 | Recognized KGM/VPR header and the bundled public key asset | Same pinned decoder | Synthetic KGM and VPR v3/v4 round-trips passed; no real v3/v4 files supplied | KGM/VPR v1/v2 are explicitly unsupported |

The bundled x64 helper was built with CPython 3.14.3, OpenSSL 3.0.18, PyInstaller 6.22.3, and PyCryptodome 3.20.0. OpenSSL DLL SHA-256 values are recorded in `vendor/music-geshizhuanhuan/UPSTREAM.md`. The host compiled with the .NET Framework C# 5 compiler. These versions identify the tested build inputs, not additional platform-format support.

The checks above use generated synthetic containers and keys. They verify the implementation's test vectors, error branches, byte-based output detection, and the host's Media Foundation full-decode path. They are not platform downloads and do not establish compatibility with current real-world releases. No row claims that every file from a platform is compatible.

## Local build verification

The timeout-fix checks use generated fixtures in disposable `TEST_BUILD` directories. Nine helper tests cover NCM, QMCv1, embedded-key QMCv2 Map/RC4 and PcV1Legacy, missing-key MusicEx/STag, KGM/VPR v3/v4, unknown/truncated inputs, and input/output size limits. The C# integration smoke test inspected a Unicode-path QMCv2 `.mflac`, returned `KeyUnavailable` for a keyless MusicEx fixture, continued the same operation with a valid synthetic QMCv2 file, converted and fully decoded a two-second WAV through Media Foundation, reused a duplicate, copied a new result into the disposable library, registered it, and saved only the disposable TEST_BUILD settings.

Process fault injection covered a hung converter, user cancellation before start/during execution/near result return, child process termination, an inherited pipe held open, output flooding, exit-zero fixtures with malformed or absent result protocol output, a progress-based idle timeout, and a subsequent import after timeout/cancellation. The tests assert bounded return, child/descendant reaping, bounded output capture, batch state remaining uncancelled on per-file timeout, and ability to run the next file. These checks do not reproduce the user's real `.mflac`; no user sample was supplied. Hardware audio, actual EXE window interaction, clean-system installation, and real platform-file compatibility were not tested.

The import-timeout test results and size comparison below describe the full 1.9.7 build before the follow-up anchor-editor UI-only update. The current player still uses assembly version 1.9.7.0; the UI update removes the editor's playback-position line and does not change the converter or validation paths.

| Artifact | This build | Previous local artifact | Change |
| --- | ---: | ---: | ---: |
| Player candidate EXE | 11,330,560 | 11,293,184 | +37,376 |
| Empty-library installer EXE | 15,822,336 | 15,758,848 | +63,488 |
| Bundled helper ZIP | 10,869,133 | 10,867,850 | +1,283 |

The UI commit still calls the existing synchronous `SettingsStore.TrySave` for its small XML configuration update. Large audio copying and all decoding/hash work run outside the UI thread; a deliberately blocked settings-volume write was not injected or measured.

## Import timeout and resource limits

| Stage | Total deadline | Additional no-progress rule | Notes |
| --- | --- | --- | --- |
| Header/structure inspection | 30 seconds | None | Runs in a child process; reads bounded header/trailer data and file length. |
| Standard decoder probe | 90 seconds + 2 seconds per MiB, capped at 5 minutes | None | Child process isolates Media Foundation calls. |
| Complete converted-audio decode | 120 seconds + 30 seconds per MiB, capped at 30 minutes | None | Streams decoded blocks in the child; process termination is the hard cancellation boundary. |
| Encrypted conversion | 120 seconds + 8 seconds per MiB, capped at 20 minutes | None | Helper accepts at most 256 MiB input and rejects decoded output above 512 MiB. |
| Content reuse/deduplication | 5 minutes + 8 seconds per MiB, capped at 20 minutes | None | Hashing runs in a child because library size is independent of the selected file. |
| Library file copy | 120 seconds + 4 seconds per MiB, capped at 60 minutes | 120 seconds since the last increasing copied-byte count | Fixed 128 KiB copy buffer; source length is checked before and after copying. |
| Embedded helper extraction | 60 seconds | Cancellation checked between bounded reads | Maximum 512 MiB expanded content and 512 archive entries. |

The runner bounds stdout/stderr capture at 64 KiB/16 KiB, waits at most 1.5 seconds for each output pipe to reach EOF, and gives process-tree termination/reaping a single 2.5-second deadline. The stage deadline is monotonic and is never reset. A clean decode may take close to its absolute cap on very slow hardware; unsupported/missing-key structures return their explicit status without repeated decoder trials. Encrypted decoding uses in-memory payloads under these caps, so memory is bounded but not streaming.

Cancellation is shown immediately in the WebView modal and requests process-tree termination asynchronously. A single-file timeout is a `TimedOut` result, does not cancel the batch, and processing continues after cleanup is confirmed. If process termination cannot be confirmed, the batch stops safely and retains that operation's temporary directory rather than deleting files that may still be in use.

## Import and validation behavior

1. The file picker accepts standard audio plus the encrypted extensions. Standard supported audio continues through the existing path.
2. Encrypted candidates are sent to a bundled helper process. The helper detects NCM/KGM/VPR magic or QMC trailer/audio structure from bytes, not from the suffix.
3. The helper writes a task-local temporary output and identifies its format from its actual header. A `.mflac` or `.mgg` suffix is never used as the output format.
4. The C# host verifies the output header and asks the same Media Foundation streaming decoder used by playback to read the entire file. A successful decryption that Windows cannot decode is reported distinctly and is not added to the library.
5. The host reuses a byte-identical same-extension library file when present, then imports through `SongLibrary.Add`, the existing settings save, and loudness queue. Failed persistence restores the in-memory library and removes newly copied files. Only the current job's temporary directory is deleted.

The vendored decoder currently reads encrypted input and decoded output into memory. Inputs above 256 MiB and decoded payloads above 512 MiB are rejected. This is a bounded-memory guard, not a streaming decoder; peak memory can still reach several hundred MiB. Windows codec availability also varies; conversion success alone does not guarantee the player can decode FLAC/Ogg on a particular machine.

## Real sample validation needed

Before claiming real-world support for each variant, test a small user-authorized, non-sensitive local sample set that includes at least: one NCM file; one traditional QMCv1 file; `.mflac` and `.mgg` with embedded `QTag` keys; a `PcV1Legacy` QMCv2 file if that variant is intended to be claimed; both Map and RC4 key-length paths if present; KGM v3 and v4; and VPR v3/v4 if those are claimed. Please do not include account data, client databases, cookies, or external keys. A sample with no embedded EKey is useful to verify the `KeyUnavailable` response.
