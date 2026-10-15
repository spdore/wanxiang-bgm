const fs = require('fs');
const vm = require('vm');
const { chromium } = require(process.env.BGM_PLAYWRIGHT_MODULE || 'playwright');

const htmlPath = process.argv[2];
if (!htmlPath) throw new Error('usage: node ui_regression.cjs <absolute-index.html>');
const html = fs.readFileSync(htmlPath, 'utf8');
for (const [index, match] of [...html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi)].entries())
  new vm.Script(match[1], { filename: `index.html script ${index}` });

const baseState = {
  type: 'state', tracks: [{ id: 'song_test', name: '测试歌曲', file: 'review.wav', key: 'I', playing: false, start: 0, end: null }],
  head: 80, cable: 80, randomKey: 'R', randomMode: 'normal', continuousActive: false,
  selected: ['review.wav'], files: ['review.wav'], microphones: ['测试麦克风'], cables: ['CABLE Input'],
  microphone: '', cableDevice: '', microphoneResolved: '默认麦克风', cableResolved: 'CABLE Input',
  playing: false, status: '检查状态', summary: '', probe: '', probing: false,
  micPeak: 0, musicPeak: 0, headphoneMusicPeak: 0, outputPeak: 0, musicSpectrum: Array(32).fill(0),
  scenes: [{ id: 'scene_test', name: '默认场景', trackCount: 1, keyCount: 1, randomCount: 1 }], activeScene: 'scene_test',
  packBusy: false, packProgress: '', songImport: null,
  playback: { sessionId: 1, generation: 1, trackId: '', trackName: '', fileName: '', state: 'stopped', positionSeconds: 0, durationSeconds: 7200, startSeconds: 0, endSeconds: null, error: '', canSeek: false, playbackMode: 'normal' },
  editor: { id: 'song_test', requestId: 1, loading: false, error: '', warning: '', duration: 7200, start: 0, end: null }
};

function assert(condition, message) {
  if (!condition) throw new Error(`FAIL: ${message}`);
  console.log(`PASS: ${message}`);
}

(async () => {
  const launchOptions = { headless: true };
  if (process.env.BGM_UI_EXECUTABLE_PATH && fs.existsSync(process.env.BGM_UI_EXECUTABLE_PATH)) launchOptions.executablePath = process.env.BGM_UI_EXECUTABLE_PATH;
  const browser = await chromium.launch(launchOptions);
  try {
    const page = await browser.newPage({ viewport: { width: 1120, height: 820 } });
    page.on('pageerror', error => { throw error; });
    await page.addInitScript(() => {
      window.__messages = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        postMessage(value) { window.__messages.push(JSON.parse(value)); },
        addEventListener(_, callback) { window.__hostMessage = callback; }
      };
    });
    await page.goto('file:///' + htmlPath.replace(/\\/g, '/'));
    const push = async state => page.evaluate(next => window.__hostMessage({ data: next }), state);
    await push(baseState);
    await page.getByRole('button', { name: '设置 测试歌曲 的快捷键播放起止点' }).click();
    await push(baseState);

    const start = page.locator('#startTimeInput');
    await start.fill('0:15.250');
    for (let i = 0; i < 5; i++) await push(structuredClone(baseState));
    assert(await start.inputValue() === '0:15.250', 'anchor text survives repeated 100 ms style state updates');
    await start.press('Enter');
    assert(await page.evaluate(() => editorStart) === 15.25, 'Enter commits the start draft');

    const end = page.locator('#endTimeInput');
    await end.fill('1:01.250');
    await push(structuredClone(baseState));
    assert(await end.inputValue() === '1:01.250', 'pasted end draft is retained while focused');
    await end.press('Enter');
    assert(await page.evaluate(() => editorEnd) === 61.25, 'mm:ss.mmm end input parses');

    const boundary = await page.evaluate(() => [59.999, 60, 3599.999, 3600.25].map(seconds => {
      const text = formatTime(seconds, true), parsed = parseAnchorTime(text);
      return { text, parsed, roundTrip: Math.round(parsed * 1000) === Math.round(seconds * 1000) };
    }));
    assert(boundary.every(item => item.roundTrip), '59.999, 60, 3599.999, and 3600.250 format and parse round-trip');
    assert(Number.isNaN(await page.evaluate(() => parseAnchorTime('1:60.000'))) && Number.isNaN(await page.evaluate(() => parseAnchorTime('1:60:00.000'))), 'minute and second fields reject values at or above 60');

    await start.fill('0:20.500');
    await page.locator('#saveAnchors').click();
    const pendingRequest = await page.evaluate(() => anchorSaveRequest);
    await page.evaluate(requestId => handleAnchorSaveResult({ requestId, trackId: 'song_test', sceneId: 'scene_test', success: false, error: '模拟保存失败' }), pendingRequest);
    assert(await start.inputValue() === '0:20.500' && (await page.locator('#anchorStatus').textContent()).includes('模拟保存失败'), 'failed save preserves typed draft and displays the error');

    await page.evaluate(() => { window.__messages = []; });
    await start.fill('0:30.000');
    await page.locator('#previewAnchors').click();
    const preview = await page.evaluate(() => window.__messages.find(message => message.action === 'previewAnchors'));
    assert(preview && preview.startSeconds === 30 && preview.endSeconds === 61.25, 'preview commits both pending fields and uses the draft');

    await end.fill('1:60.000');
    await page.locator('#previewAnchors').click();
    assert(await end.inputValue() === '1:60.000' && (await page.locator('#anchorStatus').textContent()).includes('格式无效'), 'invalid input remains visible with a clear validation error');

    await end.fill('1:01.250');
    await page.locator('#saveAnchors').click();
    const successRequest = await page.evaluate(() => anchorSaveRequest);
    await page.evaluate(requestId => handleAnchorSaveResult({ requestId, trackId: 'song_test', sceneId: 'scene_test', success: true }), successRequest);
    const savedState = structuredClone(baseState);
    savedState.tracks[0].start = 30.5; savedState.tracks[0].end = 61.25;
    savedState.editor.start = 30.5; savedState.editor.end = 61.25;
    await push(savedState);
    await page.getByRole('button', { name: '设置 测试歌曲 的快捷键播放起止点' }).click();
    await push(savedState);
    assert(await start.inputValue() === '0:30.500' && await end.inputValue() === '1:01.250', 'saved anchors display their persisted values after reopening the editor');

    await page.evaluate(() => closeSegment());
    await page.getByRole('button', { name: '播放整首 测试歌曲' }).focus();
    const nodeBefore = await page.evaluate(() => { window.__focusedPlayNode = document.activeElement; return document.activeElement.getAttribute('aria-label'); });
    const playingState = structuredClone(baseState);
    playingState.tracks[0].playing = true;
    playingState.playing = true;
    playingState.playback = { ...baseState.playback, trackId: 'song_test', trackName: '测试歌曲', fileName: 'review.wav', state: 'playing', canSeek: true };
    await push(playingState);
    assert(await page.evaluate(() => document.activeElement === window.__focusedPlayNode), 'playback state update reuses the focused song button node');

    const large = structuredClone(baseState);
    large.tracks = Array.from({ length: 1000 }, (_, i) => ({ id: `song_${i}`, name: `歌曲 ${i}`, file: `song-${i}.wav`, key: '', playing: false, start: 0, end: null }));
    large.files = large.tracks.map(track => track.file); large.selected = [...large.files];
    await push(large);
    const perf = await page.evaluate(() => {
      const node = document.querySelector('[data-track-id="song_0"] .play');
      window.__largeNode = node;
      const next = structuredClone(state); next.tracks[0].playing = true;
      const start = performance.now(); window.__hostMessage({ data: next }); document.body.offsetHeight;
      return { elapsedMs: performance.now() - start, reused: document.querySelector('[data-track-id="song_0"] .play') === window.__largeNode };
    });
    assert(perf.reused && perf.elapsedMs < 250, `1000-song playback toggle reuses its DOM node (${perf.elapsedMs.toFixed(1)} ms)`);

    await page.evaluate(() => { window.__messages = []; showPage('random'); showPage('home'); });
    assert(!(await page.evaluate(() => window.__messages.some(message => message.action === 'refreshFiles'))), 'page navigation does not trigger a library refresh that interrupts continuous playback');

    const micText = await page.locator('#mic').locator('option').first().textContent();
    assert(micText.includes('自动选择') && micText.includes('默认麦克风'), 'empty saved device selection shows the resolved default route');

    await page.evaluate(() => { window.__messages = []; $('search').focus(); });
    await page.evaluate(() => showPackPreview({ token: 'review', name: '场景包', tracks: 1, assets: 1, bytes: 1000, warnings: [] }));
    const opened = await page.evaluate(() => window.__messages.filter(message => message.action === 'capture' && message.reason === 'pack-preview'));
    assert(opened.some(message => message.active === true), 'pack preview acquires its own keyboard capture reason');
    await page.keyboard.press('Escape');
    const afterEscape = await page.evaluate(() => window.__messages.filter(message => message.action === 'capture' && message.reason === 'pack-preview'));
    assert(afterEscape.some(message => message.active === false) && await page.evaluate(() => window.__messages.some(message => message.action === 'cancelPackImport')), 'Escape releases preview capture and cancels the pending import');

    await page.evaluate(() => {
      window.__messages = [];
      showPackPreview({ token: 'review-confirm', name: '场景包', tracks: 1, assets: 1, bytes: 1000, warnings: [] });
      confirmPackPreview();
    });
    const confirmed = await page.evaluate(() => window.__messages);
    assert(confirmed.some(message => message.action === 'confirmPackImport') && confirmed.some(message => message.action === 'capture' && message.reason === 'pack-preview' && message.active === false), 'confirm closes the modal and releases only the pack-preview capture reason');

    console.log('UI regression checks passed.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
