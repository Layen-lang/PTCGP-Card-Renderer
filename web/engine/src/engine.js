// WebGL2 card renderer. It only knows the package format (card.json): every enum
// in a draw's state is the Unity value, every uniform and texture its Unity name.
// Same frame as the game: the card goes to its own square target, then on screen.
import { Bloom } from './bloom.js';
import { createControllers } from './effects.js';
import { SharedGpu } from './gpu.js';

const COMPARE = ['ALWAYS', 'NEVER', 'LESS', 'EQUAL', 'LEQUAL', 'GREATER', 'NOTEQUAL', 'GEQUAL', 'ALWAYS'];
const STENCIL_OP = ['KEEP', 'ZERO', 'REPLACE', 'INCR', 'DECR', 'INVERT', 'INCR_WRAP', 'DECR_WRAP'];
const BLEND = ['ZERO', 'ONE', 'DST_COLOR', 'SRC_COLOR', 'ONE_MINUS_DST_COLOR', 'SRC_ALPHA', 'ONE_MINUS_SRC_COLOR', 'DST_ALPHA', 'ONE_MINUS_DST_ALPHA', 'SRC_ALPHA_SATURATE', 'ONE_MINUS_SRC_ALPHA'];
const BLEND_OP = ['FUNC_ADD', 'FUNC_SUBTRACT', 'FUNC_REVERSE_SUBTRACT', 'MIN', 'MAX'];
// Unity's built-in textures: black and gray are not opaque.
const DEFAULT_TEXTURES = { white: [255, 255, 255, 255], black: [0, 0, 0, 0], gray: [128, 128, 128, 128], grey: [128, 128, 128, 128], bump: [128, 128, 255, 128], red: [255, 0, 0, 255] };
const PRESENT_VERTEX = `#version 300 es
out vec2 uv;
void main() { uv = vec2(gl_VertexID & 1, gl_VertexID >> 1) * 2.0; gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0); }`;
// The target's alpha is what still shows the background: the page expects coverage.
const PRESENT_FRAGMENT = `#version 300 es
precision highp float;
uniform sampler2D card;
uniform float shrink;
uniform bool exact;
in vec2 uv;
out vec4 color;
// Lanczos 2, widened when the target is larger than the screen: bilinear would blur or alias.
float lanczos(float x) {
    x = abs(x);
    if (x < 1e-4) return 1.0;
    if (x >= 2.0) return 0.0;
    float p = 3.14159265 * x;
    return 2.0 * sin(p) * sin(p * 0.5) / (p * p);
}
void main() {
    ivec2 size = textureSize(card, 0);
    // One target texel per screen pixel, the usual case: the filter would give the texel back.
    if (exact) {
        vec4 c = texelFetch(card, ivec2(floor(uv * vec2(size))), 0);
        color = vec4(c.rgb, 1.0 - c.a);
        return;
    }
    vec2 position = uv * vec2(size) - 0.5;
    ivec2 base = ivec2(floor(position));
    int radius = int(ceil(2.0 * shrink));
    vec4 sum = vec4(0.0);
    float total = 0.0;
    for (int y = -radius + 1; y <= radius; y++) for (int x = -radius + 1; x <= radius; x++) {
        vec2 d = (vec2(base + ivec2(x, y)) - position) / shrink;
        float w = lanczos(d.x) * lanczos(d.y);
        sum += w * texelFetch(card, clamp(base + ivec2(x, y), ivec2(0), size - 1), 0);
        total += w;
    }
    vec4 c = clamp(sum / total, 0.0, 1.0);
    color = vec4(c.rgb, 1.0 - c.a);
}`;

// SourceCardPresentationStack: the App camera's half height covers 1334 logical units at 1080 x 1920, 750 x 1334 reference.
const DEFAULT_BLOOM = 'card', MAX_TARGET = 4096, LOGICAL_HEIGHT = 1333.33, UI_BLOOM_BASE = 256, UI_REFERENCE_WIDTH = 750, ZOOM = 2.2, UI_PLANE = 100;

const multiply = (a, b) => {
    const out = new Float32Array(16);
    for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++)
        out[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
    return out;
};

const invertRigid = m => {
    // Rotation, translation and positive scale only, as every card transform.
    const out = new Float32Array(16);
    for (let c = 0; c < 3; c++) {
        const s = 1 / (m[c * 4] ** 2 + m[c * 4 + 1] ** 2 + m[c * 4 + 2] ** 2);
        for (let r = 0; r < 3; r++) out[r * 4 + c] = m[c * 4 + r] * s;
    }
    for (let r = 0; r < 3; r++) out[12 + r] = -(out[r] * m[12] + out[4 + r] * m[13] + out[8 + r] * m[14]);
    out[15] = 1;
    return out;
};

// Colour and coverage travel apart (Safari premultiplies every image): one texel each, put back together.
const JOIN_FRAGMENT = `#version 300 es
precision highp float;
uniform sampler2D color;
uniform sampler2D alpha;
out vec4 joined;
void main() {
    ivec2 texel = ivec2(gl_FragCoord.xy);
    joined = vec4(texelFetch(color, texel, 0).rgb, texelFetch(alpha, texel, 0).r);
}`;

const OVERLAY_FRAGMENT = `#version 300 es
precision mediump float;
uniform sampler2D layer;
in vec2 uv;
out vec4 color;
void main() { color = texture(layer, uv); }`;

const multiplyQuaternion = ([ax, ay, az, aw], [bx, by, bz, bw]) => [
    aw * bx + ax * bw + ay * bz - az * by,
    aw * by - ax * bz + ay * bw + az * bx,
    aw * bz + ax * by - ay * bx + az * bw,
    aw * bw - ax * bx - ay * by - az * bz];

const rotationMatrix = ([x, y, z, w]) => new Float32Array([
    1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w), 0,
    2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w), 0,
    2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y), 0,
    0, 0, 0, 1]);

// The card as shown, with its gold frame or without (SourceAdditionalGoldFrame): the gold
// renderers, or the parts they replace, are left out with the files only they need.
export function variantView(card, gold) {
    const variant = gold && card.goldText ? 'gold' : 'base';
    const shown = renderer => !renderer.variant || renderer.variant === variant;
    const draws = card.draws.filter(draw => shown(card.renderers[draw.renderer]));
    const textures = new Set(draws.flatMap(draw => Object.values(draw.textures).filter(value => typeof value === 'number')));
    return { variant, shown, draws, textures, programs: new Set(draws.map(draw => draw.program)), meshes: new Set(draws.map(draw => draw.mesh)),
             text: variant === 'gold' ? card.goldText : card.dynamicText };
}

// Every file a card loads before its first frame, in one language: what preloading fetches.
export function packageFiles(card, folder, language, gold = false) {
    const view = variantView(card, gold);
    const files = [...card.programs.filter((row, i) => view.programs.has(i)), ...card.meshes.filter((row, i) => view.meshes.has(i))].map(row => row.file);
    card.textures.forEach((row, i) => { if (!row.lazy && view.textures.has(i)) files.push(...(row.files ?? [row.file]), row.alpha); });
    for (const row of Object.values(view.text[language] ?? {})) files.push(row.file, row.alpha);
    if (card.particles) files.push('particles.json');
    if (card.emissive) files.push('../_shared/bloom.json');
    return files.filter(Boolean).map(file => new URL(file, folder).href);
}

export class CardRenderer {
    constructor(canvas, { samples = 4, dedicated = false } = {}) {
        // In a grid the page's canvas only receives the finished image: every card draws with the
        // same context. A card shown alone has its own and draws in place: copying a whole screen
        // every frame is slow, on phones above all.
        this.canvas = canvas;
        this.gpu = dedicated ? new SharedGpu(canvas) : SharedGpu.get();
        this.output = dedicated ? null : canvas.getContext('2d');
        const gl = this.gl = this.gpu.gl;
        this.samples = Math.min(samples, gl.getParameter(gl.MAX_SAMPLES));
        // Released with the card: shared entries, then what is its own.
        this.held = [];
        this.owned = [];
        this.rotation = [0, 0, 0, 1];
        this.flipped = false;
        this.flipClip = null;
        this.flipTime = 0;
        this.textures = [];
        this.defaults = {};
        this.target = null;
        this.present = this.shared('present', () => this.linkProgram(PRESENT_VERTEX, PRESENT_FRAGMENT));
        this.overlay = this.shared('overlay', () => this.linkProgram(PRESENT_VERTEX, OVERLAY_FRAGMENT));
        this.emptyVao = this.gpu.emptyVao;
        this.identity = new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
        this.cardScale = 1;
        this.zoom = 1;
        this.hideCard = false;
        this.base = null;
    }

    // A resource of the shared context, kept until this card is released.
    shared(key, make, dispose) {
        const handle = this.gpu.acquire(key, make, dispose);
        this.held.push(handle);
        return handle.value;
    }

    // Everything the card holds goes back; shared entries go when their last card goes.
    dispose() {
        const gl = this.gl;
        for (const handle of this.held) handle.release();
        for (const free of this.owned) free();
        this.targetHandle?.release();
        for (const draw of this.draws ?? []) gl.deleteVertexArray(draw.vao);
        // A slot is empty when only the other look (gold or not) draws with it.
        for (const program of this.programs ?? []) program?.owners?.delete(this);
        for (const scene of [this.modelScene, this.uiScene]) scene?.dispose();
        if (this.uiTarget) {
            for (const texture of [this.uiTarget.color, this.uiTarget.emission]) gl.deleteTexture(texture);
            for (const buffer of [this.uiTarget.buffer, this.uiTarget.colorBuffer]) gl.deleteFramebuffer(buffer);
        }
        this.uiBloom?.dispose();
        if (this.gpu.dedicated) gl.getExtension('WEBGL_lose_context')?.loseContext();
        this.held = [];
        this.owned = [];
        this.disposed = true;
    }

    async load(url, language, { gold = false } = {}) {
        const base = this.base = new URL(url, location.href);
        const response = await fetch(new URL('card.json', base));
        if (!response.ok) throw new Error(`Missing package: ${url}`);
        const card = this.card = await response.json();
        const view = this.look = variantView(card, gold);
        const text = view.text[language];
        if (!text) throw new Error(`Missing language: ${language}`);
        const image = this.imageReader(base);
        const unzipped = file => fetch(new URL(file, base)).then(r => new Response(r.body.pipeThrough(new DecompressionStream('gzip'))));
        const read = row => row.format ? unzipped(row.file).then(r => r.arrayBuffer()) : row.cube ? Promise.all(row.files.map(file => image(file))) : image(row.file, row.alpha);
        this.useMips = this.targetSide() < card.targetSize;
        const shared = name => fetch(new URL(`../_shared/${name}`, base)).then(r => r.json());
        const key = file => new URL(file, base).href;
        const gl = this.gl;
        // Everything at once, and each file once for the whole page. Programs start compiling
        // as soon as they arrive, in the background, while the images come.
        // What only the other variant draws is never fetched: its slot stays empty.
        const programs = Promise.all(card.programs.map((row, i) => view.programs.has(i) ? this.shared(`program ${key(row.file)}`, () => unzipped(row.file).then(r => r.json())
            .then(source => ({ source, program: this.startProgram(source.vertex, source.fragment) })), entry => gl.deleteProgram(entry.program)) : null));
        const meshes = Promise.all(card.meshes.map((row, i) => view.meshes.has(i) ? this.shared(`mesh ${key(row.file)}`, () => unzipped(row.file).then(r => r.arrayBuffer()).then(data => this.buildMesh(row, data)),
            mesh => this.deleteMesh(mesh)) : null));
        const textureKey = row => `texture ${row.files ? row.files.map(key).join(' ') : key(row.file)} ${row.filter} ${row.wrapU} ${row.wrapV} ${this.useMips}`;
        const deleteTexture = entry => gl.deleteTexture(entry.texture);
        // The back is only seen after a flip: the first card to need it fills it for all.
        const textures = Promise.all(card.textures.map((row, i) => view.textures.has(i) ? this.shared(textureKey(row), () => row.lazy
            ? { ...this.placeholder(row), pending: true }
            : read(row).then(data => this.uploadTexture(row, data)), deleteTexture) : null));
        const textRow = { filter: 1, wrapU: 1, wrapV: 1 };
        const surfaces = Promise.all(Object.entries(text).map(([kind, row]) => this.shared(textureKey({ ...textRow, file: row.file }),
            () => image(row.file, row.alpha).then(bitmap => this.uploadTexture(textRow, bitmap)), deleteTexture).then(texture => [kind, texture])));
        const passes = card.emissive ? shared('bloom.json') : null;
        const [started, builtMeshes, uploaded, surfaceRows, bloomPasses] = await Promise.all([programs, meshes, textures, surfaces, passes]);
        if (this.disposed) return;
        this.textures = uploaded;
        this.dynamicTextures = Object.fromEntries(surfaceRows.map(([kind, texture]) => [`dynamic${kind}`, texture]));
        this.programs = started.map(entry => {
            if (!entry) return null;
            entry.reflected ??= { ...this.reflect(this.finishProgram(entry.program)), emits: entry.source.fragment.includes('layout(location = 1)'), owners: new Map() };
            return entry.reflected;
        });
        card.programs.forEach((row, i) => { if (started[i]) row.attributes = started[i].source.attributes; });
        this.meshes = builtMeshes;
        this.renderers = card.renderers.map(row => ({ ...row, block: {}, blockTextures: {} }));
        this.draws = view.draws.map(draw => this.prepareDraw(draw));
        this.setupCamera();
        this.resize();
        this.controllers = createControllers(card.effects, this.effectContext());
        this.uiOrthographic = card.uiOrthographic;
        if (bloomPasses) this.bloom = this.shared(`bloom ${DEFAULT_BLOOM}`, () => new Bloom(this, bloomPasses), bloom => bloom.dispose());
        this.flipClips = shared('flip.json');
        if (card.particles) {
            const [{ SceneRenderer }, data] = await Promise.all([import('./scene.js'), fetch(new URL('particles.json', base)).then(r => r.json())]);
            this.modelScene = await this.loadScene(SceneRenderer, data, data.model);
            if (this.disposed) this.modelScene.dispose();
        }
        // What the card cannot show before a flip comes once it is on screen.
        this.lazyLoad = () => Promise.all(card.textures.map(async (row, i) => {
            const texture = this.textures[i];
            if (!texture?.pending) return;
            texture.pending = false;
            this.uploadTexture(row, await read(row), texture);
        }));
    }

    // An image, or its colour and its coverage when they come apart.
    imageReader(base) {
        const bitmap = file => fetch(new URL(file, base)).then(r => r.blob()).then(blob => createImageBitmap(blob, { premultiplyAlpha: 'none', colorSpaceConversion: 'none' }));
        return (file, alpha) => alpha ? Promise.all([bitmap(file), bitmap(alpha)]).then(([color, coverage]) => ({ color, alpha: coverage })) : bitmap(file);
    }

    async loadScene(SceneRenderer, data, part) {
        const image = this.imageReader(this.base);
        const images = await Promise.all(data.textures.map(row => row.format
            ? fetch(new URL(row.file, this.base)).then(r => r.body.pipeThrough(new DecompressionStream('gzip'))).then(s => new Response(s).arrayBuffer())
            : row.cube ? Promise.all(row.files.map(file => image(file))) : image(row.file, row.alpha)));
        return new SceneRenderer(this, { ...data, ...part }, images);
    }

    // IM timelines come once the card is on screen.
    async loadImmersiveScenes() {
        const data = await fetch(new URL('../_shared/immersive-scene.json', this.base)).then(r => r.json());
        const passes = await fetch(new URL('../_shared/bloom.json', this.base)).then(r => r.json());
        const { SceneRenderer } = await import('./scene.js');
        this.uiScene = await this.loadScene(SceneRenderer, data, data.ui);
        this.modelScene = await this.loadScene(SceneRenderer, data, data.model);
        if (this.disposed) return;
        this.uiBloomPasses = passes;
        // The IM particles of the card's scene glow on every card, as the game blooms them: a card
        // that emits nothing itself (a trainer) gets its emission target and bloom now.
        this.immersiveEmission = true;
        this.bloom ??= this.shared(`bloom ${DEFAULT_BLOOM}`, () => new Bloom(this, passes), bloom => bloom.dispose());
        this.resize();
    }

    // The card's second target: what its own materials emit, and its IM particles once loaded.
    get emissive() { return !!this.card.emissive || !!this.immersiveEmission; }

    setZoom(on) {
        // The card grows past its square during the IM start: the canvas grows around it.
        this.zoom = on ? ZOOM : 1;
        this.onZoom?.();
    }

    // The tap effect sits where the finger is, as tap.localPosition in the game: a point of the Canvas,
    // in its logical pixels. The Canvas root
    // already scales them to the world; scaling here too pinned the press light to the centre.
    uiPoint([x, y], node) {
        return [x, y, node.matrix[14]];
    }

    placeholder(row) {
        const gl = this.gl, texture = gl.createTexture(), target = row.cube ? gl.TEXTURE_CUBE_MAP : gl.TEXTURE_2D;
        gl.bindTexture(target, texture);
        const pixel = new Uint8Array([128, 128, 128, 255]);
        if (row.cube) for (let i = 0; i < 6; i++) gl.texImage2D(gl.TEXTURE_CUBE_MAP_POSITIVE_X + i, 0, gl.RGBA8, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
        else gl.texImage2D(target, 0, gl.RGBA8, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
        return { texture, target };
    }

    // World rotation of the card: touch, flip animation, then the Parent node's half turn (SourceCardTouch).
    rootMatrix() {
        const y = this.yaw() * Math.PI / 360;
        let q = multiplyQuaternion(this.gyroRotation ? multiplyQuaternion(this.gyroRotation, this.rotation) : this.rotation, [0, Math.sin(y), 0, Math.cos(y)]);
        if (this.flipped) q = multiplyQuaternion(q, [0, 1, 0, 0]);
        const root = rotationMatrix(q), { studio } = this.card;
        root[12] = studio[0]; root[13] = studio[1]; root[14] = studio[2];
        return root;
    }

    yaw() {
        const clip = this.flipClip;
        if (!clip) return 0;
        const position = Math.min(this.flipTime, clip.duration) * clip.rate, i = Math.floor(position), a = clip.yaw[i], b = clip.yaw[Math.min(i + 1, clip.yaw.length - 1)];
        return a + (b - a) * (position - i);
    }

    async flip(right) {
        const clips = await this.flipClips;
        this.flipClip = clips[right ? 'CardFlip_RightReverb' : 'CardFlip_LeftReverb'];
        this.flipTime = 0;
        this.flipped = !this.flipped;
    }

    // The Canvas: scaled so its logical pixels fit the App camera; the card's own camera is elsewhere.
    uiRoot() {
        const s = this.uiWorldPerLogical;
        return new Float32Array([s, 0, 0, 0, 0, s, 0, 0, 0, 0, s, 0, 0, 0, 0, 1]);
    }

    get uiWorldPerLogical() { return 2 * this.uiOrthographic / LOGICAL_HEIGHT; }

    effectContext() {
        const gl = this.gl, direct = (m, v) => [0, 1, 2].map(r => m[r] * v[0] + m[4 + r] * v[1] + m[8 + r] * v[2]);
        const inverse = (m, v) => [0, 1, 2].map(c => m[c * 4] * v[0] + m[c * 4 + 1] * v[1] + m[c * 4 + 2] * v[2]);
        return {
            // As GetComponentsInChildren: the parts the gold frame hides are inactive, the frame's own are found.
            renderers: this.renderers.filter(this.look.shown),
            particles: () => this.modelScene?.scene,
            front: effect => direct(this.rootMatrix(), direct(effect.node, [0, 0, -1])),
            toLocal: (effect, v) => inverse(effect.node, inverse(this.rootMatrix(), v)),
            floatTexture: (size, pixels) => {
                const texture = gl.createTexture(), half = new Float32Array(size * size * 4);
                pixels.forEach((value, i) => half.set([value, value, value, 1], i * 4));
                gl.bindTexture(gl.TEXTURE_2D, texture);
                gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA16F, size, size, 0, gl.RGBA, gl.FLOAT, half);
                for (const [name, value] of [[gl.TEXTURE_MIN_FILTER, gl.LINEAR], [gl.TEXTURE_MAG_FILTER, gl.LINEAR], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]])
                    gl.texParameteri(gl.TEXTURE_2D, name, value);
                this.owned.push(() => gl.deleteTexture(texture));
                return { texture, target: gl.TEXTURE_2D };
            },
            // The mesh is shared between cards: this card writes into its own copy of the UVs, which all its draws of that mesh read.
            dynamicUv: renderer => {
                const source = this.meshes[renderer.mesh].buffers.uv0, data = source.values.slice(), buffer = gl.createBuffer();
                gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
                gl.bufferData(gl.ARRAY_BUFFER, data, gl.DYNAMIC_DRAW);
                this.owned.push(() => gl.deleteBuffer(buffer));
                for (const draw of this.draws) if (draw.mesh === renderer.mesh) this.bindDraw(draw, { uv0: buffer });
                return { data, upload: () => {
                    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
                    gl.bufferSubData(gl.ARRAY_BUFFER, 0, data);
                } };
            },
        };
    }

    // One simulation step at the current pose. True while the image changes on its own.
    tick(delta) {
        if (this.flipClip) {
            this.flipTime += delta;
            if (this.flipTime >= this.flipClip.duration) this.flipClip = null;
        }
        this.time = (this.time ?? 0) + delta;
        const root = this.rootMatrix(), rotate = (m, v) => [0, 1, 2].map(r => m[r] * v[0] + m[4 + r] * v[1] + m[8 + r] * v[2]);
        for (const c of this.controllers) c.step(rotate(root, rotate(c.settings.node, [0, 0, -1])), delta);
        this.modelScene?.scene.step(delta, root);
        this.uiScene?.scene.step(delta, this.uiRoot());
        return this.controllers.some(c => c.animating) || !!this.flipClip || this.usesTime || !!this.modelScene?.scene.active || !!this.uiScene?.scene.active;
    }

    uploadTexture(row, data, existing) {
        const gl = this.gl;
        const texture = existing?.texture ?? gl.createTexture();
        const target = row.cube ? gl.TEXTURE_CUBE_MAP : gl.TEXTURE_2D;
        // The GPU keeps its copy: the decoded image would stay in the page's memory until collected.
        const release = () => (row.cube ? data : data?.alpha ? [data.color, data.alpha] : [data]).forEach(image => image.close?.());
        gl.bindTexture(target, texture);
        let mips = false;
        if (row.format === 'rgba16f') {
            let offset = 0;
            for (let level = 0; level < row.mips; level++) {
                const width = Math.max(1, row.width >> level), height = Math.max(1, row.height >> level);
                gl.texImage2D(target, level, gl.RGBA16F, width, height, 0, gl.RGBA, gl.HALF_FLOAT, new Uint16Array(data, offset, width * height * 4));
                offset += width * height * 8;
            }
            mips = row.mips > 1;
        } else if (row.format === 'r16') {
            // 16-bit normalized is not filterable in WebGL2: half floats keep the precision.
            const source = new Uint16Array(data), pixels = new Float32Array(source.length * 4);
            source.forEach((value, i) => pixels.set([value / 65535, 0, 0, 1], i * 4));
            gl.texImage2D(target, 0, gl.RGBA16F, row.width, row.height, 0, gl.RGBA, gl.FLOAT, pixels);
        } else if (row.cube) {
            data.forEach((face, i) => gl.texImage2D(gl.TEXTURE_CUBE_MAP_POSITIVE_X + i, 0, gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE, face));
        } else {
            if (data.alpha) this.joinAlpha(texture, data);
            else gl.texImage2D(target, 0, gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE, data);
            gl.bindTexture(target, texture);
            // The game shows its textures close to 1:1 and has no mips; a smaller target needs them.
            if (this.useMips && row.width > 64 && row.height > 64) { gl.generateMipmap(target); mips = true; }
        }
        const linear = row.filter > 0;
        gl.texParameteri(target, gl.TEXTURE_MAG_FILTER, linear ? gl.LINEAR : gl.NEAREST);
        gl.texParameteri(target, gl.TEXTURE_MIN_FILTER, mips ? (row.filter > 1 || this.useMips ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR_MIPMAP_NEAREST) : linear ? gl.LINEAR : gl.NEAREST);
        // TextureWrapMode: repeat, clamp, mirror; mirror-once has no WebGL equivalent and no card uses it.
        const wrap = mode => [gl.REPEAT, gl.CLAMP_TO_EDGE, gl.MIRRORED_REPEAT][mode] ?? gl.CLAMP_TO_EDGE;
        gl.texParameteri(target, gl.TEXTURE_WRAP_S, wrap(row.wrapU));
        gl.texParameteri(target, gl.TEXTURE_WRAP_T, wrap(row.wrapV));
        release();
        return { texture, target };
    }

    joinAlpha(texture, { color, alpha }) {
        const gl = this.gl, width = color.width, height = color.height;
        const join = this.gpu.join ??= this.reflect(this.linkProgram(PRESENT_VERTEX, JOIN_FRAGMENT));
        const parts = [color, alpha].map(image => {
            const part = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, part);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE, image);
            // Without mipmaps, a texture is only complete with a filter that does not need them.
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            return part;
        });
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
        const framebuffer = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
        gl.drawBuffers([gl.COLOR_ATTACHMENT0]);
        gl.viewport(0, 0, width, height);
        for (const capability of [gl.BLEND, gl.DEPTH_TEST, gl.STENCIL_TEST, gl.CULL_FACE, gl.SCISSOR_TEST]) gl.disable(capability);
        gl.colorMask(true, true, true, true);
        gl.useProgram(join.program);
        parts.forEach((part, unit) => {
            gl.activeTexture(gl.TEXTURE0 + unit);
            gl.bindTexture(gl.TEXTURE_2D, part);
        });
        gl.uniform1i(join.samplers.color, 0);
        gl.uniform1i(join.samplers.alpha, 1);
        gl.bindVertexArray(this.emptyVao);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
        gl.bindVertexArray(null);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        gl.deleteFramebuffer(framebuffer);
        parts.forEach(part => gl.deleteTexture(part));
        gl.activeTexture(gl.TEXTURE0);
    }

    defaultTexture(name) {
        const defaults = this.gpu.defaults;
        if (defaults[name]) return defaults[name];
        const gl = this.gl, texture = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(DEFAULT_TEXTURES[name] ?? DEFAULT_TEXTURES.gray));
        return defaults[name] = { texture, target: gl.TEXTURE_2D };
    }

    linkProgram(vertex, fragment) {
        return this.finishProgram(this.startProgram(vertex, fragment));
    }

    // Asking for the status is what blocks: all the programs compile side by side until then.
    startProgram(vertex, fragment) {
        const gl = this.gl, program = gl.createProgram();
        for (const [kind, source] of [[gl.VERTEX_SHADER, vertex], [gl.FRAGMENT_SHADER, fragment]]) {
            const shader = gl.createShader(kind);
            gl.shaderSource(shader, source);
            gl.compileShader(shader);
            gl.attachShader(program, shader);
            gl.deleteShader(shader);
        }
        gl.linkProgram(program);
        return program;
    }

    finishProgram(program) {
        const gl = this.gl;
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
            const shaders = gl.getAttachedShaders(program).map(s => gl.getShaderInfoLog(s)).join('\n');
            throw new Error(`Programme invalide : ${gl.getProgramInfoLog(program)}\n${shaders}`);
        }
        return program;
    }

    reflect(program) {
        // A uniform struct arrives as "vs0.unity_MatrixVP[0]": the Unity name is its last part.
        const gl = this.gl, uniforms = {}, samplers = {};
        const count = gl.getProgramParameter(program, gl.ACTIVE_UNIFORMS);
        for (let i = 0; i < count; i++) {
            const info = gl.getActiveUniform(program, i);
            const location = gl.getUniformLocation(program, info.name);
            const name = info.name.replace(/^.*\./, '').replace(/\[0\]$/, '');
            if (info.type === gl.SAMPLER_2D || info.type === gl.SAMPLER_CUBE) samplers[name] = location;
            else (uniforms[name] ??= []).push({ location, type: info.type, size: info.size });
        }
        return { program, uniforms, samplers };
    }

    buildMesh(row, data) {
        const gl = this.gl, buffers = {};
        let offset = 0;
        for (const [channel, size] of Object.entries(row.vertices)) {
            const buffer = gl.createBuffer(), length = size * row.vertexCount, values = new Float32Array(data, offset, length);
            gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
            gl.bufferData(gl.ARRAY_BUFFER, values, gl.STATIC_DRAW);
            buffers[channel] = { buffer, size, values: channel === 'uv0' ? values.slice() : null };
            offset += length * 4;
        }
        const indices = gl.createBuffer();
        gl.bindVertexArray(null);
        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indices);
        gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint32Array(data, offset, row.indexCount), gl.STATIC_DRAW);
        return { buffers, indices };
    }

    deleteMesh(mesh) {
        for (const { buffer } of Object.values(mesh.buffers)) this.gl.deleteBuffer(buffer);
        this.gl.deleteBuffer(mesh.indices);
    }

    prepareDraw(draw) {
        const row = this.card.meshes[draw.mesh];
        const textures = Object.entries(draw.textures).map(([name, value]) => [name,
            this.dynamicTextures[value] ?? (typeof value === 'number' ? this.textures[value] : this.defaultTexture(value))]);
        const [first, count] = row.submeshes[draw.submesh];
        const reflected = this.programs[draw.program];
        const node = new Float32Array(draw.model), c = row.center;
        if (reflected.uniforms._Time) this.usesTime = true;
        // Sorting position: the renderer's bounds centre.
        const center = [0, 1, 2].map(r => node[r] * c[0] + node[4 + r] * c[1] + node[8 + r] * c[2] + node[12 + r]);
        const prepared = { ...draw, vao: null, textures, first, count, reflected, node, center, owner: this.renderers[draw.renderer] };
        this.bindDraw(prepared);
        return prepared;
    }

    bindDraw(draw, replaced = {}) {
        const gl = this.gl, mesh = this.meshes[draw.mesh];
        draw.vao ??= gl.createVertexArray();
        gl.bindVertexArray(draw.vao);
        for (const [location, channel] of Object.entries(this.card.programs[draw.program].attributes)) {
            const source = mesh.buffers[channel];
            if (!source) continue;
            gl.bindBuffer(gl.ARRAY_BUFFER, replaced[channel] ?? source.buffer);
            gl.enableVertexAttribArray(+location);
            gl.vertexAttribPointer(+location, source.size, gl.FLOAT, false, 0, 0);
        }
        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, mesh.indices);
        gl.bindVertexArray(null);
    }

    setupCamera() {
        const { position, fieldOfView, near, far } = this.card.camera;
        const f = 1 / Math.tan(fieldOfView * Math.PI / 360);
        // The translated vertex shaders negate y for Vulkan: y is negated here to cancel it.
        const projection = new Float32Array([f, 0, 0, 0, 0, -f, 0, 0, 0, 0, -(far + near) / (far - near), -1, 0, 0, -2 * far * near / (far - near), 0]);
        // Unity's view matrix: right-handed, the camera looks down -z.
        this.view = new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, -position[0], -position[1], position[2], 1]);
        const viewProjection = multiply(projection, this.view);
        // Depth is the GL convention here: eye depth is 1 / (z * params.z + params.w).
        const zBuffer = [0, 0, -(far - near) / (2 * far * near), (far + near) / (2 * far * near)];
        this.globals = { unity_MatrixV: this.view, unity_MatrixVP: viewProjection, glstate_matrix_projection: projection, _WorldSpaceCameraPos: position,
            _ZBufferParams: zBuffer, _ProjectionParams: [1, near, far, 1 / far] };
    }

    targetSide() {
        const side = Math.round((this.canvas.width || this.card.targetSize) / this.zoom);
        // An explicit size is obeyed. Otherwise never less than the game's target, and more on a larger screen.
        return this.fixedTarget ? Math.min(side, this.card.targetSize) : Math.min(Math.max(side, this.card.targetSize), MAX_TARGET);
    }

    // Cards draw one after the other: all those of one size share a render target.
    resize() {
        const side = this.targetSide(), emissive = !!this.emissive;
        if (this.target?.side === side && !!this.target.emission === emissive) return;
        this.targetHandle?.release();
        this.targetHandle = this.gpu.acquire(`target ${side} ${this.samples} ${emissive}`, () => this.makeTarget(side, emissive), target => this.deleteTarget(target));
        this.target = this.targetHandle.value;
    }

    deleteTarget(target) {
        const gl = this.gl;
        for (const texture of [target.color, target.emission]) gl.deleteTexture(texture);
        for (const buffer of new Set([target.resolveBuffer, target.drawBuffer, target.colorBuffer])) gl.deleteFramebuffer(buffer);
        for (const buffer of [target.multisample, target.multisampleEmission, target.depth]) gl.deleteRenderbuffer(buffer);
    }

    makeTarget(side, emissive) {
        const gl = this.gl;
        const attachment = () => {
            const texture = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, texture);
            gl.texStorage2D(gl.TEXTURE_2D, 1, gl.RGBA8, side, side);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
            return texture;
        };
        const color = attachment(), emission = emissive ? attachment() : null;
        const resolveBuffer = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, resolveBuffer);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, color, 0);
        if (emissive) {
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT1, gl.TEXTURE_2D, emission, 0);
            gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1]);
        }
        const depth = gl.createRenderbuffer();
        gl.bindRenderbuffer(gl.RENDERBUFFER, depth);
        let drawBuffer = resolveBuffer, multisample = null, multisampleEmission = null;
        if (this.samples > 1) {
            gl.renderbufferStorageMultisample(gl.RENDERBUFFER, this.samples, gl.DEPTH24_STENCIL8, side, side);
            multisample = gl.createRenderbuffer();
            drawBuffer = gl.createFramebuffer();
            gl.bindFramebuffer(gl.FRAMEBUFFER, drawBuffer);
            gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_STENCIL_ATTACHMENT, gl.RENDERBUFFER, depth);
            gl.bindRenderbuffer(gl.RENDERBUFFER, multisample);
            gl.renderbufferStorageMultisample(gl.RENDERBUFFER, this.samples, gl.RGBA8, side, side);
            gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.RENDERBUFFER, multisample);
            if (emissive) {
                multisampleEmission = gl.createRenderbuffer();
                gl.bindRenderbuffer(gl.RENDERBUFFER, multisampleEmission);
                gl.renderbufferStorageMultisample(gl.RENDERBUFFER, this.samples, gl.RGBA8, side, side);
                gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT1, gl.RENDERBUFFER, multisampleEmission);
                gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1]);
            }
        } else {
            gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH24_STENCIL8, side, side);
            gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_STENCIL_ATTACHMENT, gl.RENDERBUFFER, depth);
        }
        if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) throw new Error('Invalid render target');
        // Bloom writes colour alone: this framebuffer has no second attachment.
        const colorBuffer = emissive ? gl.createFramebuffer() : resolveBuffer;
        if (emissive) {
            gl.bindFramebuffer(gl.FRAMEBUFFER, colorBuffer);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, color, 0);
        }
        return { side, color, emission, colorBuffer, resolveBuffer, drawBuffer, multisample, multisampleEmission, depth };
    }

    setUniform(entries, value) {
        const gl = this.gl;
        for (const { location, type, size } of entries) {
            if (size > 1) {
                if (value.length === undefined) continue;
                if (type === gl.FLOAT) gl.uniform1fv(location, value);
                else if (type === gl.INT) gl.uniform1iv(location, value);
                else gl.uniform4fv(location, value);
            } else if (type === gl.FLOAT) gl.uniform1f(location, value.length ? value[0] : value);
            else if (type === gl.INT) gl.uniform1i(location, value.length ? value[0] : value);
            else if (type === gl.UNSIGNED_INT) gl.uniform1ui(location, Math.max(0, value.length ? value[0] : value));
            else if (type === gl.FLOAT_VEC2) gl.uniform2f(location, value[0], value[1]);
            else if (type === gl.FLOAT_VEC3) gl.uniform3f(location, value[0], value[1], value[2]);
            else if (type === gl.FLOAT_VEC4) gl.uniform4fv(location, value);
            else if (type === gl.INT_VEC2) gl.uniform2i(location, value[0], value[1]);
            else if (type === gl.INT_VEC3) gl.uniform3i(location, value[0], value[1], value[2]);
            else if (type === gl.INT_VEC4) gl.uniform4i(location, value[0], value[1], value[2], value[3]);
            else if (type === gl.UNSIGNED_INT_VEC2) gl.uniform2ui(location, value[0], value[1]);
            else if (type === gl.UNSIGNED_INT_VEC3) gl.uniform3ui(location, value[0], value[1], value[2]);
            else if (type === gl.UNSIGNED_INT_VEC4) gl.uniform4ui(location, value[0], value[1], value[2], value[3]);
            else throw new Error(`Constant type not ported: ${type}`);
        }
    }

    blendName(value) { return BLEND[value]; }

    // What a card set in a shared program stays its own: another card's values are undone
    // and this card's put back, as if each had its own program.
    useProgram(reflected) {
        const gl = this.gl;
        gl.useProgram(reflected.program);
        const owners = reflected.owners;
        if (reflected.current === this) return;
        const previous = owners.get(reflected.current), mine = owners.get(this) ?? new Map();
        if (previous) for (const name of previous.keys()) if (!mine.has(name)) this.setTracked(reflected, name, null, false);
        for (const [name, value] of mine) this.setTracked(reflected, name, value, false);
        owners.set(this, mine);
        reflected.current = this;
    }

    setTracked(reflected, name, value, remember = true) {
        const gl = this.gl;
        if (remember) reflected.owners.get(this).set(name, value);
        if (name.startsWith(' ')) {
            gl.uniform1i(reflected.samplers[name.slice(1)], value ?? 0);
            return;
        }
        const entries = reflected.uniforms[name];
        if (value !== null) return this.setUniform(entries, value);
        // As long as the uniform: a single vector takes four values, an array one or four per element.
        for (const entry of entries) this.setUniform([entry], new Array(entry.size > 1 && (entry.type === gl.FLOAT || entry.type === gl.INT) ? entry.size : entry.size * 4).fill(0));
    }

    // CommonTransparent: back to front within a queue, per renderer bounds centre.
    sortedDraws(root) {
        const [cx, cy, cz] = this.card.camera.position, depth = new Map();
        const distance = draw => {
            let d = depth.get(draw.center);
            if (d === undefined) {
                const c = draw.center, x = root[0] * c[0] + root[4] * c[1] + root[8] * c[2] + root[12] - cx,
                    y = root[1] * c[0] + root[5] * c[1] + root[9] * c[2] + root[13] - cy, z = root[2] * c[0] + root[6] * c[1] + root[10] * c[2] + root[14] - cz;
                d = x * x + y * y + z * z;
                depth.set(draw.center, d);
            }
            return d;
        };
        return this.draws.map((draw, order) => [draw, order]).sort(([a, i], [b, j]) => a.queue - b.queue || (a.queue > 2500 ? distance(b) - distance(a) : 0) || i - j).map(row => row[0]);
    }

    applyState(state) {
        const gl = this.gl;
        if (state.cull === 0) gl.disable(gl.CULL_FACE);
        else {
            gl.enable(gl.CULL_FACE);
            gl.cullFace(state.cull === 1 ? gl.FRONT : gl.BACK);
        }
        gl.depthFunc(gl[COMPARE[state.zTest]]);
        gl.depthMask(state.zWrite !== 0);
        const [src, dst, srcAlpha, dstAlpha] = state.blend;
        gl.blendFuncSeparate(gl[BLEND[src]], gl[BLEND[dst]], gl[BLEND[srcAlpha]], gl[BLEND[dstAlpha]]);
        gl.blendEquationSeparate(gl[BLEND_OP[state.blendOp[0]]], gl[BLEND_OP[state.blendOp[1]]]);
        const mask = state.colorMask;
        gl.colorMask(!!(mask & 8), !!(mask & 4), !!(mask & 2), !!(mask & 1));
        for (const [face, side] of [[gl.FRONT, state.stencilFront], [gl.BACK, state.stencilBack]]) {
            gl.stencilFuncSeparate(face, gl[COMPARE[side.comp]], state.stencilRef, state.stencilReadMask);
            gl.stencilOpSeparate(face, gl[STENCIL_OP[side.fail]], gl[STENCIL_OP[side.zFail]], gl[STENCIL_OP[side.pass_]]);
        }
        gl.stencilMask(state.stencilWriteMask);
    }

    renderCard() {
        const gl = this.gl, { side, drawBuffer } = this.target, { studio } = this.card;
        gl.bindFramebuffer(gl.FRAMEBUFFER, drawBuffer);
        gl.viewport(0, 0, side, side);
        if (this.emissive) gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1]);
        gl.colorMask(true, true, true, true);
        gl.depthMask(true);
        gl.stencilMask(255);
        // The card camera clears to opaque black.
        gl.clearColor(0, 0, 0, 1);
        gl.clearDepth(1);
        gl.clearStencil(0);
        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT | gl.STENCIL_BUFFER_BIT);
        gl.enable(gl.DEPTH_TEST);
        gl.enable(gl.STENCIL_TEST);
        gl.enable(gl.BLEND);
        // Unity's left-handed meshes are clockwise on the screen.
        gl.frontFace(gl.CW);
        const root = this.rootMatrix(), t = this.time ?? 0;
        this.globals._CameraOffset = undefined;
        this.globals._Time = [t / 20, t, 2 * t, 3 * t];
        this.globals._ScreenParams = [side, side, 1 + 1 / side, 1 + 1 / side];
        const sceneGlobals = this.sceneGlobals(this.globals);
        let sceneQueue = -Infinity;
        for (const draw of this.sortedDraws(root)) {
            // Card parts' particles live in the card's own queues.
            if (this.modelScene && draw.queue > sceneQueue) {
                this.modelScene.draw(sceneGlobals, this.view, sceneQueue, draw.queue - 1, this.emissive);
                sceneQueue = draw.queue - 1;
            }
            const { uniforms, samplers } = draw.reflected, set = (name, value) => this.setTracked(draw.reflected, name, value);
            this.useProgram(draw.reflected);
            if (this.emissive) gl.drawBuffers(draw.reflected.emits ? [gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1] : [gl.COLOR_ATTACHMENT0, gl.NONE]);
            this.applyState(draw.state);
            const model = multiply(root, draw.node);
            if (uniforms.unity_ObjectToWorld) set('unity_ObjectToWorld', model);
            if (uniforms.unity_WorldToObject) set('unity_WorldToObject', invertRigid(model));
            for (const [name, value] of Object.entries(this.globals)) if (uniforms[name] && value !== undefined) set(name, value);
            for (const [name, value] of Object.entries(draw.uniforms)) if (uniforms[name]) set(name, value);
            for (const [name, value] of Object.entries(draw.owner.block)) if (uniforms[name]) set(name, value);
            let unit = 0;
            for (const [name, entry] of draw.textures) {
                const { texture, target } = draw.owner.blockTextures[name] ?? entry;
                gl.activeTexture(gl.TEXTURE0 + unit);
                gl.bindTexture(target, texture);
                if (samplers[name]) set(` ${name}`, unit);
                unit++;
            }
            gl.bindVertexArray(draw.vao);
            gl.drawElements(gl.TRIANGLES, draw.count, gl.UNSIGNED_INT, draw.first * 4);
        }
        gl.bindVertexArray(null);
        if (this.modelScene) this.modelScene.draw(sceneGlobals, this.view, sceneQueue, Infinity, this.emissive);
        gl.disable(gl.DEPTH_TEST);
        gl.disable(gl.STENCIL_TEST);
        gl.disable(gl.BLEND);
        gl.disable(gl.CULL_FACE);
        gl.colorMask(true, true, true, true);
        if (this.samples > 1) {
            gl.bindFramebuffer(gl.READ_FRAMEBUFFER, drawBuffer);
            gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, this.target.resolveBuffer);
            for (let i = 0; i < (this.emissive ? 2 : 1); i++) {
                gl.readBuffer(gl.COLOR_ATTACHMENT0 + i);
                gl.drawBuffers(i === 0 ? [gl.COLOR_ATTACHMENT0, gl.NONE] : [gl.NONE, gl.COLOR_ATTACHMENT1]);
                gl.blitFramebuffer(0, 0, side, side, 0, 0, side, side, gl.COLOR_BUFFER_BIT, gl.NEAREST);
            }
            gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, this.target.resolveBuffer);
            if (this.emissive) gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1]);
            else gl.drawBuffers([gl.COLOR_ATTACHMENT0]);
            gl.bindFramebuffer(gl.READ_FRAMEBUFFER, drawBuffer);
            gl.readBuffer(gl.COLOR_ATTACHMENT0);
        }
        if (this.bloom) this.bloom.apply(this.target.emission, this.target.colorBuffer, side);
    }

    render() {
        const gl = this.gl, width = this.canvas.width, height = this.canvas.height;
        if (!width || !height) return;
        this.renderCard();
        this.gpu.reserve(width, height);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        gl.viewport(0, 0, width, height);
        gl.enable(gl.SCISSOR_TEST);
        gl.scissor(0, 0, width, height);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.disable(gl.SCISSOR_TEST);
        if (!this.hideCard) {
            // The IM clips scale the presented card.
            const side = width / this.zoom * this.cardScale;
            gl.viewport((width - side) / 2, (height - side) / 2, side, side);
            gl.useProgram(this.present);
            gl.activeTexture(gl.TEXTURE0);
            gl.bindTexture(gl.TEXTURE_2D, this.target.color);
            gl.uniform1i(gl.getUniformLocation(this.present, 'card'), 0);
            gl.uniform1f(gl.getUniformLocation(this.present, 'shrink'), Math.min(Math.max(this.target.side / side, 1), 4));
            gl.uniform1i(gl.getUniformLocation(this.present, 'exact'), side === this.target.side ? 1 : 0);
            gl.bindVertexArray(this.emptyVao);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
            gl.bindVertexArray(null);
        }
        if (this.uiScene?.scene.active) this.renderInterfaceParticles();
        // Within the same task: the drawing buffer is still there.
        if (!this.output) return;
        const source = this.gpu.canvas;
        this.output.globalCompositeOperation = 'copy';
        this.output.drawImage(source, 0, source.height - height, width, height, 0, 0, width, height);
    }

    sceneGlobals(globals) {
        return Object.fromEntries(Object.entries(globals).filter(([, v]) => v !== undefined));
    }

    // Particles of the App camera, with their bloom: an orthographic layer over the presented card.
    renderInterfaceParticles() {
        const gl = this.gl, side = this.canvas.width;
        if (this.uiTarget?.side !== side) {
            const make = () => {
                const texture = gl.createTexture();
                gl.bindTexture(gl.TEXTURE_2D, texture);
                gl.texStorage2D(gl.TEXTURE_2D, 1, gl.RGBA8, side, side);
                for (const [p, v] of [[gl.TEXTURE_MIN_FILTER, gl.LINEAR], [gl.TEXTURE_MAG_FILTER, gl.LINEAR], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) gl.texParameteri(gl.TEXTURE_2D, p, v);
                return texture;
            };
            const color = make(), emission = make(), buffer = gl.createFramebuffer(), colorBuffer = gl.createFramebuffer();
            gl.bindFramebuffer(gl.FRAMEBUFFER, buffer);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, color, 0);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT1, gl.TEXTURE_2D, emission, 0);
            gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1]);
            gl.bindFramebuffer(gl.FRAMEBUFFER, colorBuffer);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, color, 0);
            this.uiTarget = { side, color, emission, buffer, colorBuffer };
        }
        const { buffer, colorBuffer, color, emission } = this.uiTarget;
        gl.bindFramebuffer(gl.FRAMEBUFFER, buffer);
        gl.viewport(0, 0, side, side);
        gl.clearColor(0, 0, 0, 0);
        gl.colorMask(true, true, true, true);
        gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1]);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.enable(gl.BLEND);
        gl.disable(gl.DEPTH_TEST);
        gl.disable(gl.STENCIL_TEST);
        gl.frontFace(gl.CW);
        // The canvas shows this many world units, at the card's logical scale.
        const half = this.uiWorldPerLogical * (this.card.cardHeightLogical / this.card.verticalPercentage) / 2 * this.zoom;
        const near = .3, far = 1000, z = -2 / (far - near), w = -(far + near) / (far - near);
        const projection = new Float32Array([1 / half, 0, 0, 0, 0, -1 / half, 0, 0, 0, 0, z, 0, 0, 0, w, 1]);
        const view = new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, -UI_PLANE, 1]);
        // Unity's _ZBufferParams for this camera on Vulkan (reversed depth, raw = (1 - d) / 2 of the GL depth d here),
        // rewritten for d: the Battle particle shaders pull their depth toward the camera (_CameraOffset) through
        // them. Zeros made that depth infinite, and every one of those particles was clipped.
        const reversed = (far / near - 1) / far, zBuffer = [-reversed / 2 * far, (reversed / 2 + 1 / far) * far, -reversed / 2, reversed / 2 + 1 / far];
        this.uiScene.draw({ unity_MatrixV: view, unity_MatrixVP: multiply(projection, view), glstate_matrix_projection: projection, _WorldSpaceCameraPos: [0, 0, -UI_PLANE],
            _ZBufferParams: zBuffer, _ProjectionParams: [1, near, far, 1 / far], _ScreenParams: [side, side, 1 + 1 / side, 1 + 1 / side], _Time: this.globals._Time }, view, -Infinity, Infinity, true, true);
        gl.disable(gl.CULL_FACE);
        // The game blooms its screen on 256 texels across the width of the Canvas (750 logical pixels).
        // This layer covers more of the world, the more so while the canvas grows for the IM start:
        // its bloom keeps the game's texels per world unit, or the glow spreads thin and fades.
        const base = Math.min(Math.round(UI_BLOOM_BASE * 2 * half / (UI_REFERENCE_WIDTH * this.uiWorldPerLogical)), side >> 1);
        if (this.uiBloom?.base !== base) {
            this.uiBloom?.dispose();
            this.uiBloom = new Bloom(this, this.uiBloomPasses, base);
        }
        this.uiBloom.apply(emission, colorBuffer, side);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        gl.viewport(0, 0, side, side);
        gl.enable(gl.BLEND);
        gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        gl.useProgram(this.overlay);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, color);
        gl.uniform1i(gl.getUniformLocation(this.overlay, 'layer'), 0);
        gl.bindVertexArray(this.emptyVao);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
        gl.bindVertexArray(null);
        gl.disable(gl.BLEND);
    }

    // RGBA of the card target as in the game, top row first: for measures against the player.
    readTarget() {
        const gl = this.gl, { side, colorBuffer } = this.target;
        this.renderCard();
        gl.bindFramebuffer(gl.FRAMEBUFFER, colorBuffer);
        const pixels = new Uint8Array(side * side * 4);
        gl.readPixels(0, 0, side, side, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        const flipped = new Uint8ClampedArray(pixels.length);
        for (let y = 0; y < side; y++) flipped.set(pixels.subarray(y * side * 4, (y + 1) * side * 4), (side - 1 - y) * side * 4);
        return { width: side, height: side, pixels: flipped };
    }
}
