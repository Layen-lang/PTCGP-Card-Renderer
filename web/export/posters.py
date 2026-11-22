"""Still images of each card, front and at rest, per language: what <pokemon-card> shows while it loads.

The browser engine itself draws them, in Chromium, so a poster is the card's first frame
cropped to the card: 734 x 1024 at the game's 1403 px target, the size of the L textures.
"""
import base64
import functools
import http.server
import json
import threading
from .paths import LANGUAGES, WEB_CARDS
from .server import SiteHandler, serve

POSTERS = WEB_CARDS/'_posters'
# The engine on the GPU through Direct3D, as the bench runs it.
BROWSER_ARGUMENTS = ['--use-angle=d3d11', '--enable-gpu', '--ignore-gpu-blocklist']
# Lossy: a poster is on screen for the second the card takes to load.
QUALITY = .9
PAGE = '''<!doctype html><meta charset="utf-8"><body style="margin:0">
<script type="module" src="/src/pokemon-card.js"></script></body>'''
CAPTURE = '''async ([id, language, quality, gold]) => {
    await customElements.whenDefined('pokemon-card');
    const card = document.createElement('pokemon-card');
    for (const [name, value] of Object.entries({ base: '/web-cards/', lang: language, preview: '', target: '1403', samples: '4', motion: 'reduce', poster: 'none' })) card.setAttribute(name, value);
    if (gold) card.setAttribute('gold', '');
    card.style.height = '600px';
    const ready = new Promise((ok, fail) => {
        card.addEventListener('cardready', ok, { once: true });
        card.addEventListener('cardfailed', event => fail(new Error(String(event.detail))), { once: true });
    });
    card.setAttribute('card', id);
    document.body.append(card);
    // A card that never shows is a failure, never a wait without end.
    await Promise.race([ready, new Promise((_, fail) => setTimeout(() => fail(new Error('No first frame after 60 s')), 60000))]);
    // The previous card goes once this one holds the same files: the next language reuses them.
    window.previousCard?.release();
    window.previousCard?.remove();
    window.previousCard = card;
    const renderer = card.renderer;
    renderer.rotation = [0, 0, 0, 1];
    const target = renderer.readTarget(), side = target.width;
    const height = Math.round(side * renderer.card.verticalPercentage), width = Math.round(height * renderer.card.cardAspect);
    const left = Math.round((side - width) / 2), top = Math.round((side - height) / 2);
    const pixels = new Uint8ClampedArray(width * height * 4);
    for (let y = 0; y < height; y++) {
        const row = target.pixels.subarray(((top + y) * side + left) * 4, ((top + y) * side + left + width) * 4);
        pixels.set(row, y * width * 4);
    }
    // The target's alpha is what still shows the background: the page needs coverage.
    for (let i = 3; i < pixels.length; i += 4) pixels[i] = 255 - pixels[i];
    const canvas = new OffscreenCanvas(width, height);
    canvas.getContext('2d').putImageData(new ImageData(pixels, width, height), 0, 0);
    const blob = await canvas.convertToBlob({ type: 'image/webp', quality });
    const bytes = new Uint8Array(await blob.arrayBuffer());
    let text = '';
    for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return btoa(text);
}'''
SHORT = {'fr_FR': 'fr', 'en_US': 'en', 'ja_JP': 'ja', 'it_IT': 'it', 'de_DE': 'de', 'es_ES': 'es', 'pt_BR': 'pt', 'ko_KR': 'ko', 'zh_TW': 'zh'}


class Handler(SiteHandler):
    def do_GET(self):
        if self.path.split('?', 1)[0] == '/poster.html':
            body = PAGE.encode()
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        super().do_GET()


def poster_path(card_id, language, gold=False):
    return POSTERS/card_id/f"{SHORT[language]}{'-gold' if gold else ''}.webp"


def is_current(card_id, language, gold=False):
    """A poster is kept while it is newer than the package it shows."""
    path = poster_path(card_id, language, gold)
    return path.is_file() and path.stat().st_mtime >= (WEB_CARDS/card_id/'card.json').stat().st_mtime


def catalog_cards():
    return [row['id'] for row in json.loads((WEB_CARDS/'catalog.json').read_text(encoding='utf-8'))['cards']]


def build_posters(card_ids=None, languages=LANGUAGES, log=lambda *line: print(*line, flush=True), force=False, gold=False):
    """gold: also the gold frame's look, on the cards that have one (about 3 GB more for every card)."""
    from playwright.sync_api import sync_playwright
    card_ids = card_ids or catalog_cards()
    server, address = serve(Handler)
    failures = {}
    try:
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch(args=BROWSER_ARGUMENTS, headless=True)
            page = browser.new_page(viewport=dict(width=800, height=800))
            page.goto(address+'poster.html')
            for done, card_id in enumerate(card_ids, 1):
                package = json.loads((WEB_CARDS/card_id/'card.json').read_text(encoding='utf-8'))
                # The gold frame's look only when asked: without its poster, a card shows nothing while it loads.
                for language, gold in [(language, look) for look in ((False, True) if gold else (False,)) for language in languages]:
                    texts = package.get('goldText', {}) if gold else package['dynamicText']
                    if language not in texts or (not force and is_current(card_id, language, gold)):
                        continue
                    # The local server sometimes drops a connection under load: a second try is enough.
                    for attempt in range(3):
                        try:
                            data = base64.b64decode(page.evaluate(CAPTURE, [card_id, SHORT[language], QUALITY, gold]))
                            break
                        except Exception as error:
                            data = None
                            if attempt == 2:
                                failures[f"{card_id} {language}{' gold' if gold else ''}"] = str(error)[:300]
                    if data is None:
                        continue
                    path = poster_path(card_id, language, gold)
                    path.parent.mkdir(parents=True, exist_ok=True)
                    temporary = path.with_suffix('.tmp')
                    temporary.write_bytes(data)
                    temporary.replace(path)
                log(done, len(card_ids), card_id)
            browser.close()
    finally:
        server.shutdown()
    return failures
