// <pokemon-card card="PK_10_000010_00" lang="en" base="/web-cards/"></pokemon-card>
// The element's box is the card. The canvas around it leaves room for the tilt.
import { CardRenderer, packageFiles } from './engine.js';
import { CardGyro, CardTouch, combine, restingPose } from './touch.js';

const LANGUAGES = { fr: 'fr_FR', en: 'en_US', ja: 'ja_JP', it: 'it_IT', de: 'de_DE', es: 'es_ES', pt: 'pt_BR', ko: 'ko_KR', zh: 'zh_TW' };
// UICardView's height: swipe thresholds are in these units, whatever the element's size.
const LOGICAL_HEIGHT = 880, MAX_CANVAS = 4096;
// Every card draws with the same context, in one animation frame: past this many
// milliseconds the cards left wait for the next frame, so the page stays fluid.
const FRAME_BUDGET_MS = 10;
// The game's own settings on a phone: its 1403 px target, without antialiasing, then scaled to
// the screen. A phone has neither the power nor the need for a target the size of its display.
const GAME_TARGET = 1403;
const phone = () => matchMedia('(pointer: coarse)').matches;
// idle: a slow sway, in degrees and radians per second.
const SWAY = { horizontal: 6, vertical: 3, horizontalSpeed: .6, verticalSpeed: .45 };
// loading="lazy": the card starts loading this far before it reaches the screen.
const LAZY_MARGIN = '300px';
const waiting = new Set(), preloaded = new Map();
let frameRequested = false;

function drawWaitingCards(now) {
    frameRequested = false;
    // A card being touched first, then the one that waited longest.
    const cards = [...waiting].sort((a, b) => b.urgent - a.urgent || a.drawnAt - b.drawnAt);
    waiting.clear();
    const start = performance.now();
    for (const card of cards) {
        if (!card.urgent && performance.now() - start > FRAME_BUDGET_MS) schedule(card);
        else card.frame(now);
    }
}

function schedule(card) {
    waiting.add(card);
    if (!frameRequested) {
        frameRequested = true;
        requestAnimationFrame(drawWaitingCards);
    }
}
const STYLE = `
:host { display: inline-block; position: relative; width: 300px; aspect-ratio: 630 / 880; touch-action: none; user-select: none; }
img { position: absolute; inset: 0; width: 100%; height: 100%; z-index: 2; pointer-events: none; }
img[hidden] { display: none; }
canvas { position: absolute; left: 50%; top: 50%; transform: translate(-50%, -50%); pointer-events: none; z-index: 1; }
`;

class PokemonCard extends HTMLElement {
    static observedAttributes = ['card', 'lang', 'gold'];

    // Fetches a card's files into the browser's cache, nothing more: the card then shows at once.
    // A page calls it for the cards it will show next; a file is only fetched once.
    static preload(id, { lang = 'en', base = '/web-cards/', gold = false } = {}) {
        const language = LANGUAGES[lang] ?? lang, folder = new URL(`${base}${id}/`, location.href);
        const short = Object.keys(LANGUAGES).find(key => LANGUAGES[key] === language) ?? lang;
        const file = url => {
            if (!preloaded.has(url)) preloaded.set(url, fetch(url).then(response => response.arrayBuffer()).catch(() => preloaded.delete(url)));
            return preloaded.get(url);
        };
        file(new URL(`../_posters/${id}/${short}${gold ? '-gold' : ''}.webp`, folder).href);
        return fetch(new URL('card.json', folder)).then(response => response.json())
            .then(card => Promise.all(packageFiles(card, folder, language, gold).map(file))).then(() => undefined);
    }

    constructor() {
        super();
        const root = this.attachShadow({ mode: 'open' });
        root.innerHTML = `<style>${STYLE}</style><img alt="" hidden><canvas></canvas>`;
        this.canvas = root.querySelector('canvas');
        this.poster = root.querySelector('img');
        this.poster.addEventListener('error', () => { this.poster.hidden = true; });
        this.timing = {};
        this.drawnAt = 0;
    }

    connectedCallback() {
        this.addEventListener('pointerdown', this.onDown);
        this.addEventListener('pointermove', this.onMove);
        this.addEventListener('pointerup', this.onUp);
        this.addEventListener('pointercancel', this.onUp);
        this.observer = new ResizeObserver(() => this.layout());
        this.observer.observe(this);
        this.start();
    }

    disconnectedCallback() {
        this.observer.disconnect();
        this.lazyObserver?.disconnect();
        this.lazyObserver = null;
        waiting.delete(this);
        this.request = null;
    }

    attributeChangedCallback() {
        if (this.isConnected) this.start();
    }

    // The card's first frame, drawn in advance by build-web-posters, until the real one is there.
    showPoster() {
        const id = this.getAttribute('card'), language = this.getAttribute('lang') ?? 'en';
        if (!id || this.getAttribute('poster') === 'none' || this.getAttribute('side') === 'back' || this.renderer?.draws) return;
        const short = Object.keys(LANGUAGES).find(key => key === language || LANGUAGES[key] === language) ?? 'en';
        // The gold frame has its own poster: the image never changes once the card is drawn.
        this.poster.src = this.getAttribute('poster') || `${this.getAttribute('base') ?? '/web-cards/'}_posters/${id}/${short}${this.hasAttribute('gold') ? '-gold' : ''}.webp`;
        this.poster.hidden = false;
    }

    // Released while loading, a card simply stops.
    start() {
        this.showPoster();
        if (this.getAttribute('loading') === 'lazy' && !this.seen) {
            this.lazyObserver ??= new IntersectionObserver(entries => {
                if (!entries.some(entry => entry.isIntersecting)) return;
                this.seen = true;
                this.lazyObserver.disconnect();
                this.lazyObserver = null;
                this.start();
            }, { rootMargin: LAZY_MARGIN });
            this.lazyObserver.observe(this);
            return;
        }
        this.loadCard().catch(error => {
            if (!this.renderer) return;
            console.error(error);
            this.dispatchEvent(new CustomEvent('cardfailed', { detail: error }));
        });
    }

    // A page showing many cards gives back what this one holds in the shared context.
    release() {
        waiting.delete(this);
        this.request = null;
        this.renderer?.dispose();
        this.renderer = null;
    }

    // A card nobody sees is not drawn: it keeps its image and picks up where it was when shown again.
    set offscreen(on) {
        if (this.concealed === on) return;
        this.concealed = on;
        if (on) {
            waiting.delete(this);
            this.request = null;
        } else this.wake();
    }

    // motion="reduce" or "full" chooses; by default the system's "reduce motion" setting.
    // Reduced, a card only moves while it is touched, turned or playing its sequence.
    get reducedMotion() {
        const motion = this.getAttribute('motion');
        return motion ? motion === 'reduce' : matchMedia('(prefers-reduced-motion: reduce)').matches;
    }

    get swaying() {
        return this.hasAttribute('idle') && !this.reducedMotion;
    }

    // tilt="horizontal vertical", in degrees, then the sway of idle.
    restRotation(now) {
        const [horizontal = 0, vertical = 0] = (this.getAttribute('tilt') ?? '').split(/[\s,]+/).filter(Boolean).map(Number);
        let pose = restingPose(horizontal, vertical);
        if (this.swaying) {
            const time = now / 1000;
            pose = combine(pose, restingPose(SWAY.horizontal * Math.sin(time * SWAY.horizontalSpeed), SWAY.vertical * Math.sin(time * SWAY.verticalSpeed)));
        }
        return pose;
    }

    // quality="game" or "max" chooses; by default the game's settings on a phone, the most elsewhere.
    get gameQuality() {
        const quality = this.getAttribute('quality');
        return quality ? quality === 'game' : phone();
    }

    get fixedSize() {
        if (this.hasAttribute('target')) return +this.getAttribute('target');
        return this.gameQuality ? GAME_TARGET : null;
    }

    // Touched, turning or playing its sequence: drawn every frame, whatever the budget.
    get urgent() {
        return !!(this.touch?.moving || this.renderer?.flipClip || this.immersive?.busy || this.gyro || !this.hasAttribute('preview'));
    }

    async loadCard() {
        const id = this.getAttribute('card');
        if (!id) return;
        const language = LANGUAGES[this.getAttribute('lang') ?? 'en'] ?? this.getAttribute('lang');
        const base = this.getAttribute('base') ?? '/web-cards/';
        const started = performance.now();
        // An attribute cannot change the context of a canvas: each load keeps the first renderer's samples.
        this.renderer ??= new CardRenderer(this.canvas, { samples: +(this.getAttribute('samples') ?? (this.gameQuality ? 1 : 4)), dedicated: !this.hasAttribute('preview') });
        this.renderer.fixedTarget = this.fixedSize !== null;
        this.layout();
        await this.renderer.load(`${base}${id}/`, language, { gold: this.hasAttribute('gold') });
        this.touch = new CardTouch(+(this.getAttribute('max-tilt') ?? this.renderer.card.maxTilt));
        this.layout();
        if (this.getAttribute('side') === 'back') {
            await this.renderer.lazyLoad();
            this.renderer.flipped = true;
        }
        this.renderer.rotation = this.restRotation(performance.now());
        this.describe(language);
        this.renderer.render();
        this.poster.hidden = true;
        this.timing.firstFrame = performance.now() - started;
        this.gyroEnabled = this.hasAttribute('gyro');
        const plan = this.renderer.card.immersive;
        if (plan && !this.hasAttribute('preview')) {
            this.renderer.onZoom = () => this.layout();
            Promise.all([import('./immersive.js'), this.renderer.loadImmersiveScenes()]).then(([{ ImmersivePlayer }]) => {
                this.immersive = new ImmersivePlayer(this, this.renderer, plan, `${base}${id}/`);
                // Browsers only start a film by themselves without sound.
                if (this.hasAttribute('autoplay') && !this.reducedMotion && !this.renderer.flipped) {
                    this.immersive.video.muted = true;
                    this.immersive.video.preload = 'auto';
                    this.immersive.beginPlayback();
                    this.wake();
                }
            });
        }
        // Glitter, marble and the like move with the card at rest.
        if ((this.renderer.tick(0) && !this.hasAttribute('paused') && !this.reducedMotion) || this.swaying) this.wake();
        this.dispatchEvent(new CustomEvent('cardready', { detail: this.timing }));
        const renderer = this.renderer;
        await renderer.lazyLoad();
        if (this.renderer === renderer) this.wake();
    }

    // For screen readers: the card's name unless label says otherwise; aria-label of the page wins.
    describe(language) {
        this.setAttribute('role', 'img');
        if (this.hasAttribute('aria-label') && !this.labelled) return;
        this.labelled = true;
        this.setAttribute('aria-label', this.getAttribute('label') ?? this.renderer.card.names?.[language] ?? this.getAttribute('card'));
    }

    layout() {
        this.canvas.style.background = this.getAttribute('background') ?? '';
        this.style.touchAction = this.hasAttribute('no-touch') ? 'auto' : '';
        // verticalPercentage: share of the square target the front card occupies.
        const fraction = this.renderer?.card?.verticalPercentage ?? .73;
        const box = this.getBoundingClientRect();
        const side = box.height / fraction * (this.renderer?.zoom ?? 1);
        const fixed = this.fixedSize !== null;
        const pixels = fixed ? this.fixedSize : Math.min(Math.round(side * devicePixelRatio), MAX_CANVAS);
        // One canvas pixel per screen pixel, on the grid: otherwise the browser resamples the whole image.
        const shown = fixed ? side : pixels / devicePixelRatio;
        this.canvas.style.width = this.canvas.style.height = `${shown}px`;
        const onGrid = centre => (Math.round((centre - shown / 2) * devicePixelRatio) / devicePixelRatio + shown / 2 - centre).toFixed(4);
        this.canvas.style.transform = `translate(calc(-50% + ${onGrid(box.left + box.width / 2)}px), calc(-50% + ${onGrid(box.top + box.height / 2)}px))`;
        if (pixels > 0 && this.canvas.width !== pixels) {
            this.canvas.width = this.canvas.height = pixels;
            if (this.renderer?.draws) {
                this.renderer.resize();
                this.renderer.render();
            }
        }
    }

    localPoint(event) {
        const box = this.getBoundingClientRect();
        // Unity's rectangle: origin at the centre, y up.
        const scale = LOGICAL_HEIGHT / box.height;
        return { point: [(event.clientX - box.left - box.width / 2) * scale, (box.top + box.height / 2 - event.clientY) * scale], size: [box.width * scale, LOGICAL_HEIGHT] };
    }

    onDown(event) {
        if (!this.touch || this.hasAttribute('no-touch')) return;
        this.setPointerCapture(event.pointerId);
        const { point } = this.localPoint(event);
        if (this.gyroEnabled && !this.gyro) { this.gyro = new CardGyro(); this.gyro.start().then(started => { if (started) this.wake(); else this.gyro = null; }); }
        if (this.immersive?.down(point)) return;
        this.touch.begin(point, performance.now());
        this.wake();
    }

    onMove(event) {
        if (!this.touch || this.hasAttribute('no-touch')) return;
        const { point, size } = this.localPoint(event);
        if (this.immersive?.move(point)) return;
        this.touch.move(point, size);
    }

    onUp(event) {
        if (this.immersive?.up()) return;
        const right = this.touch?.end(performance.now());
        if (right === null || right === undefined || this.hasAttribute('no-flip')) return;
        // Scaled to the game's rectangle: its thresholds are in 630 x 880 logical units.
        this.touch.rotation = [0, 0, 0, 1];
        this.renderer.flip(right).then(() => this.wake());
    }

    // Nothing is drawn while the card rests: an idle card costs no GPU.
    wake() {
        if (this.request || !this.renderer || this.concealed) return;
        this.last = performance.now();
        this.request = true;
        schedule(this);
    }

    frame(now) {
        // A frame can be asked for while the card still loads: its touch only exists once loaded.
        if (!this.renderer || this.concealed || !this.touch) return;
        // fps: a ceiling; the card waits for its turn while staying scheduled.
        const fps = +this.getAttribute('fps');
        if (fps > 0 && now - this.drawnAt < 1000 / fps - 2) {
            schedule(this);
            return;
        }
        this.drawnAt = now;
        // Unity's Time.maximumDeltaTime: a slow frame loses no more time than in the game.
        const deltaTime = Math.min((now - this.last) / 1000, 1 / 3);
        this.last = now;
        this.touch.tick(deltaTime);
        this.renderer.rotation = combine(this.restRotation(now), this.touch.rotation);
        if (this.gyro) this.renderer.gyroRotation = this.gyro.tick(deltaTime);
        const sequence = this.immersive?.tick(deltaTime);
        const animating = this.renderer.tick(deltaTime);
        const busy = sequence || !!this.gyro || !!this.renderer.flipClip || this.swaying || (animating && !this.reducedMotion);
        this.renderer.render();
        this.request = this.touch.moving || busy;
        if (this.request) schedule(this);
    }
}

customElements.define('pokemon-card', PokemonCard);
