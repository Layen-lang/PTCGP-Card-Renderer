// Measures where a frame's time goes on this device: each test changes one thing, the card
// turns slowly the whole time, and the report is text to copy. Only for measuring: nothing here
// changes how the cards are drawn elsewhere.
import './src/pokemon-card.js';

const query = new URLSearchParams(location.search);
const CARD = query.get('card') ?? 'PK_20_000360_02';
const BASE = query.get('base') ?? new URL('../web-cards/', location.href).pathname;
const SECONDS = +(query.get('seconds') ?? 5);
const element = id => document.getElementById(id);

// name, component attributes, then what the test removes from each frame.
const TESTS = [
    ['game', { quality: 'game' }, {}],
    ['game, effects not animated', { quality: 'game' }, { still: true }],
    ['game, no bloom', { quality: 'game' }, { noBloom: true }],
    ['game, opaque layers only', { quality: 'game' }, { opaqueOnly: true }],
    ['game, no card (presentation only)', { quality: 'game' }, { noCard: true }],
    ['game, waiting for the GPU every frame', { quality: 'game' }, { finish: true }],
    ['half (702 px target)', { target: '702', samples: '1' }, {}],
    ['half, waiting for the GPU', { target: '702', samples: '1' }, { finish: true }],
    ['game + 4x antialiasing', { target: '1403', samples: '4' }, {}],
    ['max (screen size, 4x)', { quality: 'max' }, {}],
    ['max, waiting for the GPU', { quality: 'max' }, { finish: true }],
    ['grid (shared context, game)', { quality: 'game', preview: '' }, {}],
];

const median = values => values.length ? values.slice().sort((a, b) => a - b)[values.length >> 1] : 0;
const percentile = (values, p) => values.length ? values.slice().sort((a, b) => a - b)[Math.min(values.length - 1, Math.floor(values.length * p))] : 0;
const round = value => Math.round(value * 10) / 10;

function device() {
    const gl = document.createElement('canvas').getContext('webgl2');
    const info = gl?.getExtension('WEBGL_debug_renderer_info');
    return [
        `Appareil : ${navigator.userAgent}`,
        `Screen: ${screen.width} x ${screen.height}, ratio ${devicePixelRatio}, window ${innerWidth} x ${innerHeight}`,
        `Pointeur tactile : ${matchMedia('(pointer: coarse)').matches ? 'oui' : 'non'}`,
        `WebGL2 : ${gl ? gl.getParameter(gl.VERSION) : 'absent'}`,
        `GPU : ${gl ? (info ? gl.getParameter(info.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER)) : '-'}`,
        `MSAA max : ${gl?.getParameter(gl.MAX_SAMPLES) ?? '-'}, texture max : ${gl?.getParameter(gl.MAX_TEXTURE_SIZE) ?? '-'}`,
        `Demi-flottants : ${gl?.getExtension('EXT_color_buffer_float') || gl?.getExtension('EXT_color_buffer_half_float') ? 'oui' : 'non'}`,
        `Card: ${CARD}, ${SECONDS} s per test`,
    ];
}

function loadCard(attributes) {
    return new Promise((resolve, reject) => {
        const card = document.createElement('pokemon-card');
        card.setAttribute('base', BASE);
        card.setAttribute('lang', 'en');
        for (const [name, value] of Object.entries(attributes)) card.setAttribute(name, value);
        const started = performance.now();
        card.addEventListener('cardready', () => resolve({ card, load: performance.now() - started }), { once: true });
        card.addEventListener('cardfailed', event => reject(event.detail), { once: true });
        card.setAttribute('card', CARD);
        element('stage').replaceChildren(card);
    });
}

// The test drives the card itself: the page's own scheduling stays out of the measure.
async function measure(card, options) {
    const renderer = card.renderer, gl = renderer.gl;
    card.request = true;
    const bloom = renderer.bloom, draws = renderer.draws;
    if (options.noBloom) renderer.bloom = null;
    if (options.opaqueOnly) renderer.draws = draws.filter(draw => draw.queue <= 2500);
    const renderCard = renderer.renderCard.bind(renderer);
    if (options.noCard) renderer.renderCard = () => {};
    const intervals = [], scripts = [];
    let angle = 0, last = performance.now();
    await new Promise(done => {
        const end = last + SECONDS * 1000;
        const step = now => {
            intervals.push(now - last);
            last = now;
            const start = performance.now();
            angle += .03;
            const yaw = Math.sin(angle) * .25, pitch = Math.cos(angle * .7) * .12;
            renderer.rotation = [Math.sin(pitch / 2), Math.sin(yaw / 2), 0, Math.cos(yaw / 2) * Math.cos(pitch / 2)];
            if (!options.still) renderer.tick(1 / 60);
            renderer.render();
            if (options.finish) gl.finish();
            scripts.push(performance.now() - start);
            if (now < end) requestAnimationFrame(step);
            else done();
        };
        requestAnimationFrame(step);
    });
    const drawn = renderer.draws.length;
    renderer.bloom = bloom;
    renderer.draws = draws;
    renderer.renderCard = renderCard;
    intervals.shift();
    const total = intervals.reduce((a, b) => a + b, 0);
    return {
        fps: round(intervals.length / (total / 1000)), frame: round(median(intervals)), frame95: round(percentile(intervals, .95)),
        script: round(median(scripts)), script95: round(percentile(scripts, .95)),
        canvas: card.canvas.width, target: renderer.target.side, samples: renderer.samples, draws: options.noCard ? 0 : drawn,
    };
}

async function main() {
    const lines = [...device(), ''];
    lines.push('test | fps | median frame ms | p95 frame ms | median script ms | p95 script ms | canvas | target | msaa | layers | load ms');
    element('report').value = lines.join('\n');
    for (const [index, [name, attributes, options]] of TESTS.entries()) {
        element('status').textContent = `Test ${index + 1} / ${TESTS.length} : ${name}`;
        element('progress').value = index / TESTS.length;
        try {
            const { card, load } = await loadCard(attributes);
            await new Promise(ok => setTimeout(ok, 600));
            const r = await measure(card, options);
            lines.push(`${name} | ${r.fps} | ${r.frame} | ${r.frame95} | ${r.script} | ${r.script95} | ${r.canvas} | ${r.target} | ${r.samples} | ${r.draws} | ${Math.round(load)}`);
            card.release();
        } catch (error) {
            lines.push(`${name} | erreur : ${error?.message ?? error}`);
        }
        element('stage').replaceChildren();
        element('report').value = lines.join('\n');
        await new Promise(ok => setTimeout(ok, 400));
    }
    element('progress').value = 1;
    element('status').textContent = 'Done: press “Copy the report”.';
    element('copy').disabled = false;
}

// Without HTTPS the clipboard API is refused: a selected text area copies everywhere.
element('copy').addEventListener('click', async () => {
    const area = element('report');
    try {
        await navigator.clipboard.writeText(area.value);
    } catch {
        area.focus();
        area.setSelectionRange(0, area.value.length);
        document.execCommand('copy');
    }
    element('copy').textContent = 'Copied ✓';
});

main().catch(error => { element('status').textContent = `Erreur : ${error.message}`; });
