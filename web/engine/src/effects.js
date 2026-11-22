// Ports of the card controllers (Source*State in the Unity player, CardXObject in the game).
// Each one reads the card's world orientation and writes constants on renderers, as a MaterialPropertyBlock.
const RAD = 180 / Math.PI;
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const clamp01 = v => clamp(v, 0, 1);
// ARM64 FCMP/FCSEL clamps NaN to zero; a plain clamp propagates it.
const clamp01Native = v => v >= 0 ? (v <= 1 ? v : 1) : 0;
const length = ([x, y, z]) => Math.hypot(x, y, z);
const distance3 = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2]);
const normalized = v => { const l = length(v); return l > 1e-5 ? v.map(c => c / l) : [0, 0, 0]; };
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const tiltRadians = f => Math.atan2(Math.hypot(f[0], f[1]), -f[2]);
const rotateZ = (degrees, [x, y, z]) => {
    const r = degrees / RAD, c = Math.cos(r), s = Math.sin(r);
    return [c * x - s * y, s * x + c * y, z];
};
const DIAGONAL_A = normalized([1, 1, 0]), DIAGONAL_B = normalized([1, -1, 0]);

// Mathf.PerlinNoise: Perlin's improved noise at z = 0, remapped by (n + .69) / (.793 + .69).
// Checked against 3000 samples from the Unity player: within 1.4e-5 for positive coordinates, the only ones the cards use.
const PERMUTATION_BASE = [151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225,140,36,103,30,69,142,8,99,37,240,21,10,23,190,6,148,247,120,234,75,0,26,197,62,94,252,219,203,117,35,11,32,57,177,33,88,237,149,56,87,174,20,125,136,171,168,68,175,74,165,71,134,139,48,27,166,77,146,158,231,83,111,229,122,60,211,133,230,220,105,92,41,55,46,245,40,244,102,143,54,65,25,63,161,1,216,80,73,209,76,132,187,208,89,18,169,200,196,135,130,116,188,159,86,164,100,109,198,173,186,3,64,52,217,226,250,124,123,5,202,38,147,118,126,255,82,85,212,207,206,59,227,47,16,58,17,182,189,28,42,223,183,170,213,119,248,152,2,44,154,163,70,221,153,101,155,167,43,172,9,129,22,39,253,19,98,108,110,79,113,224,232,178,185,112,104,218,246,97,228,251,34,242,193,238,210,144,12,191,179,162,241,81,51,145,235,249,14,239,107,49,192,214,31,181,199,106,157,184,84,204,176,115,121,50,45,127,4,150,254,138,236,205,93,222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180];
const PERMUTATION = [...PERMUTATION_BASE, ...PERMUTATION_BASE];
export const perlin = (x, y = 0) => {
    const fade = t => t * t * t * (t * (t * 6 - 15) + 10), lerp = (a, b, t) => a + t * (b - a);
    const grad = (hash, a, b) => {
        const h = hash & 15, u = h < 8 ? a : b, v = h < 4 ? b : h === 12 || h === 14 ? a : 0;
        return (h & 1 ? -u : u) + (h & 2 ? -v : v);
    };
    const fx = x - Math.floor(x), fy = y - Math.floor(y), X = Math.floor(x) & 255, Y = Math.floor(y) & 255, u = fade(fx), v = fade(fy);
    const A = PERMUTATION[X] + Y, B = PERMUTATION[X + 1] + Y;
    const noise = lerp(lerp(grad(PERMUTATION[PERMUTATION[A]], fx, fy), grad(PERMUTATION[PERMUTATION[B]], fx - 1, fy), u),
        lerp(grad(PERMUTATION[PERMUTATION[A + 1]], fx, fy - 1), grad(PERMUTATION[PERMUTATION[B + 1]], fx - 1, fy - 1), u), v);
    return (noise + .69) / (.793 + .69);
};

// AnimationCurve.Evaluate: Hermite segments, Bezier when a key is weighted, serialized wrap modes.
const WRAP_PING_PONG = 0, WRAP_REPEAT = 1;
export class Curve {
    constructor({ keys, preWrap, postWrap }) {
        this.keys = keys;
        this.preWrap = preWrap;
        this.postWrap = postWrap;
    }

    wrap(time) {
        const { keys } = this, first = keys[0].time, last = keys[keys.length - 1].time, span = last - first;
        if (span <= 0) return first;
        const mode = time < first ? this.preWrap : time > last ? this.postWrap : -1;
        if (mode === WRAP_REPEAT) return first + ((time - first) % span + span) % span;
        if (mode === WRAP_PING_PONG) {
            const t = ((time - first) % (2 * span) + 2 * span) % (2 * span);
            return first + (t <= span ? t : 2 * span - t);
        }
        return clamp(time, first, last);
    }

    evaluate(input) {
        const { keys } = this;
        if (keys.length === 0) return 0;
        if (keys.length === 1) return keys[0].value;
        const time = this.wrap(input);
        let i = 0;
        while (i < keys.length - 2 && time >= keys[i + 1].time) i++;
        const a = keys[i], b = keys[i + 1], dt = b.time - a.time;
        if (dt <= 0) return a.value;
        if (!isFinite(a.outSlope) || !isFinite(b.inSlope)) return a.value;
        const t = (time - a.time) / dt;
        // weightedMode: 1 in, 2 out, 3 both.
        const outWeighted = (a.weightedMode & 2) !== 0, inWeighted = (b.weightedMode & 1) !== 0;
        if (outWeighted || inWeighted) return bezier(a, b, dt, t, outWeighted ? a.outWeight : 1 / 3, inWeighted ? b.inWeight : 1 / 3);
        const m0 = a.outSlope * dt, m1 = b.inSlope * dt, t2 = t * t, t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * a.value + (t3 - 2 * t2 + t) * m0 + (-2 * t3 + 3 * t2) * b.value + (t3 - t2) * m1;
    }
}

const bezier = (a, b, dt, t, w1, w2) => {
    // The time polynomial is solved for its parameter, then the value polynomial read at it.
    const x1 = w1, x2 = 1 - w2;
    let s = t;
    for (let n = 0; n < 12; n++) {
        const r = 1 - s, x = 3 * r * r * s * x1 + 3 * r * s * s * x2 + s * s * s;
        const d = 3 * r * r * x1 + 6 * r * s * (x2 - x1) + 3 * s * s * (1 - x2);
        if (Math.abs(d) < 1e-6) break;
        s = clamp01(s - (x - t) / d);
    }
    const r = 1 - s, y1 = a.value + a.outSlope * dt * w1, y2 = b.value - b.inSlope * dt * w2;
    return r * r * r * a.value + 3 * r * r * s * y1 + 3 * r * s * s * y2 + s * s * s * b.value;
};

const curvesOf = effect => Object.fromEntries((effect.curves ?? []).map(c => [c.property, new Curve(c)]));

class Controller {
    constructor(effect, context) {
        this.settings = effect;
        this.context = context;
        this.curves = curvesOf(effect);
        this.front = context.front(effect);
    }

    // Renderers below this controller with one of these SearchTags, as SourceCardEffects.Collect.
    collect(...tags) {
        const prefix = this.settings.indices;
        return this.context.renderers.filter(r => prefix.every((v, i) => r.path[i] === v) && tags.some(t => r.tags.includes(t)));
    }

    renderer(indices) {
        return indices && this.context.renderers.find(r => r.path.length === indices.length && r.path.every((v, i) => v === indices[i]));
    }

    set(renderers, values) {
        for (const r of renderers) if (r) Object.assign(r.block, values);
    }

    // True while this controller still changes the image with the card at rest.
    get animating() { return false; }
    step() {}
}

// CardUVShadow and CardMReliefSRObject.
class ShadowRelief extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.shadows = this.collect('Card-UVShadow');
        this.reliefs = effect.kind === 'CardMReliefSRObject' ? this.collect('MRR-ChangeColor-Lighting') : [];
        this.emits = effect.kind === 'CardMReliefSRObject' ? this.collect('Card-Effect-Emit') : [];
        this.names = (effect.curves ?? []).map(c => c.property);
        this.lastTilt = 0;
        this.elapsed = 0;
        this.playing = false;
        this.values = {};
        this.evaluate(0);
        this.step(this.front, 0);
    }

    evaluate(progress) {
        for (const name of this.names) this.values[name] = this.curves[name].evaluate(progress);
    }

    step(front, delta) {
        const light = normalized(this.settings.lightDir ? [this.settings.lightDir.x, this.settings.lightDir.y, this.settings.lightDir.z] : [0, 0, 0]);
        const f = normalized(front), n = [-light[0], -light[1], -light[2]], d = dot(n, f);
        const projected = [n[0] - f[0] * d, n[1] - f[1] * d, n[2] - f[2] * d];
        const local = this.context.toLocal(this.settings, projected);
        const offset = this.settings.translateOffset ?? { x: 0, y: 0 };
        const translation = [local[0] + offset.x, local[1] + offset.y, 0, 0];
        if (this.names.length) {
            const tilt = tiltRadians(front) * RAD, s = this.settings;
            if (!this.playing && this.lastTilt < s.animStartDegree && tilt > s.animStartDegree) { this.playing = true; this.elapsed = 0; }
            if (this.playing) {
                this.elapsed += delta;
                this.evaluate(this.elapsed / s.animTimeScale);
                this.playing = this.elapsed <= s.animDuration * s.animTimeScale;
            }
            this.lastTilt = tilt;
        }
        this.set(this.shadows, { _ShadowUVTranslate: translation });
        const pick = (from, count) => Object.fromEntries(this.names.slice(from, from + count).map(n => [n, this.values[n]]));
        this.set(this.reliefs, pick(0, 4));
        this.set(this.shadows, pick(4, 2));
        this.set(this.emits, pick(5, 4));
    }

    get animating() { return this.playing; }
}

// CardURObject.
class UltraRare extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.targets = this.collect('Opaque-UR-Oklab', 'Frame-2Layer-UR');
        this.step(this.front);
    }

    step(front) {
        const sine = Math.sin(3 * Math.atan2(Math.hypot(front[0], front[1]), front[2]));
        this.set(this.targets, { _Front: [front[0], front[1], front[2], 0], _Tilt: sine * sine });
    }
}

// GlitterFlowMaps.
class GlitterFlow extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.target = this.renderer(effect.rendererIndices);
        this.speed = [effect.initFlowSpeed, effect.initFlowSpeed];
        this.offset = [0, 0];
        this.light = 0;
        this.rotationA = effect.flowAInitialRotation;
        this.rotationB = effect.flowBInitialRotation;
        this.step(this.front, 0);
    }

    step(back, delta) {
        const s = this.settings, forward = normalized([-back[0], -back[1], -back[2]]), frac = v => v - Math.floor(v);
        const shape = v => { const x = 2 * v - v * v; return x * x; };
        // Acceleration and resistance are per update, not per second.
        this.speed[0] -= forward[0] * s.accelIntensity;
        this.speed[1] -= forward[1] * s.accelIntensity;
        let magnitude = Math.hypot(...this.speed);
        if (magnitude > s.maxFlowSpeed) this.speed = this.speed.map(v => v * s.maxFlowSpeed / magnitude);
        this.speed = this.speed.map(v => v * (1 - s.resistance));
        magnitude = Math.hypot(...this.speed);
        if (magnitude > 0 && magnitude < s.minFlowSpeed) this.speed = this.speed.map(v => v * s.minFlowSpeed / magnitude);
        magnitude = Math.hypot(...this.speed);
        const direction = magnitude > 1e-5 ? this.speed.map(v => v / magnitude) : [0, 0];
        const t = (direction[0] * .5821118950843811 + direction[1] * .8131087422370911 + 1) * .5;
        this.offset[0] = frac(this.offset[0] + (s.minTiltPower + (1 - s.minTiltPower) * shape(t)) * delta * magnitude);
        this.offset[1] = frac(this.offset[1] + (s.minTiltPower + (1 - s.minTiltPower) * shape(1 - t)) * delta * magnitude);
        this.light = frac(this.light + delta * s.lightSpeed);
        const blend = clamp01((magnitude - s.minFlowSpeed) / (s.maxFlowSpeed - s.minFlowSpeed));
        const lerp = (a, b) => a + (b - a) * blend;
        this.rotationA = (this.rotationA + delta * lerp(s.flowAMinRotateSpeed, s.flowAMaxRotateSpeed) / 20) % (2 * Math.PI);
        this.rotationB = (this.rotationB + delta * lerp(s.flowBMinRotateSpeed, s.flowBMaxRotateSpeed) / 20) % (2 * Math.PI);
        this.set([this.target], { _FlowParams: [this.offset[0], -this.offset[1], this.light, 0, this.rotationA, this.rotationB, 0, 0] });
    }

    get animating() { return true; }
}

// CardRGBObject: one renderer-wide block, later materials overwrite earlier colours.
const srgbToLinear = v => v < .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4;
const cubeRoot = v => v < 0 ? -((-v) ** .3333333432674408) : v ** .3333333432674408;
const oklab = ([sr, sg, sb]) => {
    const r = srgbToLinear(sr), g = srgbToLinear(sg), b = srgbToLinear(sb);
    const l = cubeRoot(r * .4121656119823456 + g * .5362752079963684 + b * .051457565277814865);
    const m = cubeRoot(r * .2118591070175171 + g * .6807189583778381 + b * .10740657895803452);
    const s = cubeRoot(r * .08830979466438293 + g * .28184741735458374 + b * .6302613615989685);
    return [l * .21045425534248352 + m * .7936177849769592 + s * .004072046838700771,
        l * 1.9779984951019287 + m * -2.4285922050476074 + s * .4505937099456787,
        l * .025904037058353424 + m * .7827717661857605 + s * -.8086757659912109, 0];
};
class RgbCard extends Controller {
    constructor(effect, context) {
        super(effect, context);
        if (!effect.enabled) return;
        const vectors = { _AreaAColor: '_AreaAColorOklab', _AreaBColor: '_AreaBColorOklab', _UIColorR: '_UIColorROklab', _UIColorG: '_UIColorGOklab' };
        for (const renderer of this.collect('Card-RGB'))
            for (const colors of renderer.colors)
                for (const [name, value] of Object.entries(colors)) renderer.block[vectors[name]] = oklab(value);
    }
}

// CardFutureObject.
class Future extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.leds = this.collect('Parallax-Led');
        this.updateGoal(this.front);
        this.frame = this.goal;
        this.apply();
    }

    updateGoal(front) {
        const n = normalized(front), s = this.settings;
        this.tiltDot = dot(n, DIAGONAL_A) + .5 * dot(n, DIAGONAL_B);
        // Integer division in the source.
        this.goal = Math.trunc(clamp(Math.round(this.tiltDot * 10 + 5), 0, 10) * (s.animationFrameCount - 1) / 10);
    }

    step(front, delta) {
        if (delta === 0) return;
        const s = this.settings;
        this.updateGoal(front);
        let distance = Math.abs(this.goal - this.frame);
        if (distance < .00009999999747378752) this.frame = this.goal;
        else {
            const direction = this.goal - this.frame >= 0 ? 1 : -1;
            if (distance > s.skipAnimThreshold) { this.frame += (distance - s.skipAnimThreshold) * direction; distance = Math.abs(this.goal - this.frame); }
            this.frame += s.accellRatio * distance * direction * delta;
            this.frame = direction > 0 ? Math.min(this.frame, this.goal) : Math.max(this.frame, this.goal);
        }
        this.apply();
    }

    apply() {
        this.set(this.leds, { _AnimFrame: this.frame + this.settings.animFrameOffset, _TiltDot: this.tiltDot });
    }

    get animating() { return this.frame !== this.goal; }
}

// KiraPuyoObject: fixed LightDir (.5, .5), no clock.
class KiraPuyo extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.surface = this.renderer(effect.rendererIndices);
        this.step(this.front);
    }

    step(f) {
        const s = this.settings, animation = .5 * (f[0] * .5 + .5) + .5 * (f[1] * .5 + .5);
        const phase = s.scaleAnimationOffset + s.vertScaleSpeed * animation;
        this.set([this.surface], { _RampRepeat: s.rampRepeat, _ScrollScale: s.scrollScale, _ScrollOffset: s.scrollOffset,
            _KiraScale: this.curves.Curve.evaluate(clamp01(phase - Math.floor(phase))), _Anim: animation });
    }
}

// CardMRRObject.
class Mrr extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.surfaces = this.collect('MRR-ChangeColor-Lighting', 'Frame-Holo-2Layer');
        this.effects = this.collect('Card-Effect-Emit');
        this.flashes = this.collect('MRR-Parallax-Flash');
        const radial = this.curves.FlashRadialScaling.keys;
        this.minRadial = radial[0].value;
        this.maxRadial = radial[radial.length - 1].value;
        this.distances = [];
        this.lastFront = this.front;
        this.startFront = [0, 0, 0];
        this.lastTilt = 0;
        this.elapsed = 0;
        this.adjusted = 1;
        this.playing = false;
        this.clock = 0;
        this.progress = 0;
        this.evaluate(0);
        this.apply();
    }

    evaluate(value) {
        this.progress = value;
        const s = this.settings, offset = s.flashRadialStartOffset, range = this.maxRadial - this.minRadial, v = n => this.value(n);
        this.layer = v(this.startFront[0] > 0 ? 'Layer2UVXTranslateByTiltingRight' : 'Layer2UVXTranslateByTiltingLeft');
        this.radialScaling = this.minRadial + offset * range + (range * (1 - offset) * (v('FlashRadialScaling') - this.minRadial)) / range;
        this.radialAnimation = offset + (1 - offset) * v('FlashRadialAnim');
    }

    value(name) { return this.curves[name].evaluate(this.progress); }

    step(front, delta) {
        const s = this.settings;
        this.clock += delta;
        const tilt = tiltRadians(front) * RAD;
        this.distances.push([distance3(front, this.lastFront), delta, this.clock]);
        while (this.distances.length && this.clock - this.distances[0][2] > s.recordingTime) this.distances.shift();
        this.lastFront = front;
        if (!this.playing && this.lastTilt < s.animStartDegree && tilt > s.animStartDegree) {
            this.playing = true;
            this.elapsed = 0;
            this.startFront = front;
            const distance = this.distances.reduce((a, d) => a + d[0], 0), interval = this.distances.reduce((a, d) => a + d[1], 0);
            const speed = clamp(distance / interval, s.minTiltSpeed, s.maxTiltSpeed);
            this.adjusted = s.minAnimSpeed + (s.maxAnimSpeed - s.minAnimSpeed) * clamp01((speed - s.minTiltSpeed) / (s.maxTiltSpeed - s.minTiltSpeed));
        }
        if (this.playing) {
            this.elapsed += delta * (s.useSpeedAdjust ? this.adjusted : 1);
            this.evaluate(this.elapsed / s.animTimeScale);
            this.playing = this.elapsed <= s.animDuration * s.animTimeScale;
        }
        this.lastTilt = tilt;
        this.apply();
    }

    apply() {
        const v = n => this.value(n);
        this.set(this.surfaces, { _ChangeColor: v('ChangeColorCurve'), _LightColorIntensity: v('LightColorIntensityCurve'), _LightEmitIntensity: v('LightEmitIntensityCurve'),
            _LightPower: v('LightPower'), _Layer2UVTranslate: [this.layer, 0, 0, 0], _Layer2EmissiveIntensity: v('Layer2EmissiveIntensity'), _Layer2ColorPower: v('Layer2ColorPower') });
        this.set(this.effects, { _Switch: v('EffSwitchColor'), _AdditiveIntensity: v('EffAdditiveIntensity'), _Color3Blend: v('EffColor3Blend'), _EmissiveIntensity: v('EffEmissiveIntensity') });
        this.set(this.flashes, { _FlashIntensity: v('FlashIntensity'), _RadialScaling: this.radialScaling, _RadialAnim: this.radialAnimation });
    }

    get animating() { return this.playing; }
}

// CardDualMRRObject: its own transition and icon curves, no speed history.
class DualMrr extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.surfaces = this.collect('MRR-ChangeColor-Lighting', 'Frame-Holo-2Layer');
        this.effects = this.collect('Card-Effect-Emit');
        this.flashes = this.collect('MRR-Parallax-Flash');
        this.transitions = this.collect('Card-Dual-Transition');
        this.typeIcons = this.collect('Dual-ShadowBoxUI-TypeIcon');
        const radial = this.curves.FlashRadialScaling.keys;
        this.minRadial = radial[0].value;
        this.maxRadial = radial[radial.length - 1].value;
        this.startFront = [0, 0, 0];
        this.elapsed = this.lastTilt = this.bounds = this.range = this.textureSwitch = 0;
        this.playing = false;
        this.evaluate(0);
        this.step(this.front, 0);
    }

    value(name) { return this.curves[name].evaluate(this.progress); }

    evaluate(value) {
        this.progress = value;
        const offset = this.settings.flashRadialStartOffset, range = this.maxRadial - this.minRadial, right = this.startFront[0] > 0;
        this.layer = this.value(right ? 'Layer2UVXTranslateByTiltingRight' : 'Layer2UVXTranslateByTiltingLeft');
        this.radialScaling = this.minRadial + offset * range + (range * (1 - offset) * (this.value('FlashRadialScaling') - this.minRadial)) / range;
        this.radialAnimation = offset + (1 - offset) * this.value('FlashRadialAnim');
        this.highlight = this.value(right ? 'TypeIconHighlightTranslateByTiltingRight' : 'TypeIconHighlightTranslateByTiltingLeft');
    }

    advance(delta) {
        const s = this.settings;
        this.elapsed += delta;
        this.evaluate(this.elapsed / s.animTimeScale);
        this.playing = this.elapsed <= s.animDuration * s.animTimeScale;
    }

    step(f, delta) {
        const s = this.settings, tilt = tiltRadians(f) * RAD, rotated = rotateZ(-s.transitionAngle, f);
        this.toBoundary = Math.atan2(rotated[1], Math.hypot(rotated[0], rotated[2])) * RAD;
        if (!s.isAnimationStopped) {
            const amount = Math.abs(this.toBoundary) / 30, sign = this.toBoundary < 0 ? 1 : -1;
            this.textureSwitch = sign * this.curves.TypeEffectTransitionSwitchCurve.evaluate(amount);
            this.bounds = sign * this.curves.TransitionBoundsCurve.evaluate(amount);
            this.range = this.curves.TransitionRangeCurve.evaluate(amount);
        }
        if (this.playing) this.advance(delta);
        else if (this.lastTilt < s.animStartDegree && tilt > s.animStartDegree) {
            this.playing = true;
            this.elapsed = 0;
            this.startFront = f;
            this.advance(delta);
        }
        this.lastTilt = tilt;
        const v = n => this.value(n);
        this.set(this.surfaces, { _ChangeColor: v('ChangeColorCurve'), _LightColorIntensity: v('LightColorIntensityCurve'), _LightEmitIntensity: v('LightEmitIntensityCurve'),
            _LightPower: v('LightPower'), _Layer2UVTranslate: [this.layer, 0, 0, 0], _Layer2EmissiveIntensity: v('Layer2EmissiveIntensity'), _Layer2ColorPower: v('Layer2ColorPower') });
        this.set(this.effects, { _Switch: v('EffSwitchColor'), _AdditiveIntensity: v('EffAdditiveIntensity'), _Color3Blend: v('EffColor3Blend'), _EmissiveIntensity: v('EffEmissiveIntensity') });
        this.set(this.flashes, { _FlashIntensity: v('FlashIntensity'), _RadialScaling: this.radialScaling, _RadialAnim: this.radialAnimation });
        this.set(this.transitions, { _IsLeft: this.toBoundary >= 0 ? 1 : 0, _GradationBoundsOffset: this.bounds, _GradationRange: this.range, _TextureSwitch: this.textureSwitch,
            _MainColorModMultiplier: 1, _SubColorModMultiplier: 1 });
        this.set(this.typeIcons, { _HighlightTranslate: [this.highlight, 0, 0, 0] });
    }

    get animating() { return this.playing; }
}

// CardMSRObject: source update order, spring denominators and partial stop semantics.
class Msr extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.auras = this.collect('Card-Aura');
        this.parallaxes = this.collect('Card_Parallax_Transparent_Translate');
        this.reflections = this.collect('ShadowBox_MSR');
        this.distances = [];
        this.lastFront = [0, 0, 0];
        this.lastDegree = this.previousDelta = this.elapsedTime = 0;
        this.parallaxTilt = this.parallaxSpeed = this.parallaxTransparency = this.parallaxTranslate = 0;
        this.noiseOffset = this.flipSpeed = this.flipAcceleration = this.flipBlend = this.flipAnim = this.animElapsed = this.timeParam = 0;
        this.animState = this.reflectionState = 0;
        this.evaluate(0);
        this.step(this.front, 0);
        this.parallaxSpeed = 0;
        this.parallaxTilt = Math.sin(tiltRadians(this.front) * 3);
        // The game's card enters rotating, so its first resting view is past this animation (measured on PK_20_011180_00).
        this.evaluate(effect.animDuration);
        this.apply();
    }

    evaluate(time) {
        const v = n => this.curves[n].evaluate(time);
        this.aura = v('AuraTransparency');
        this.center = v('OutlineReflectCenterX');
        this.noise = v('ColorIntensityNoiseStrength');
        this.intensity = v('OutlineReflectColorIntensity');
    }

    step(f, delta) {
        const s = this.settings;
        this.elapsedTime += this.previousDelta;
        this.previousDelta = delta;
        const angle = tiltRadians(f), degree = angle * RAD, tilt = Math.sin(angle * 3);
        this.distances.push([distance3(f, this.lastFront), delta, this.elapsedTime]);
        while (this.distances.length && this.elapsedTime - this.distances[0][2] > s.checkRotatingTime) this.distances.shift();
        this.lastFront = f;
        if (!s.isAnimationStopped) {
            const difference = tilt - this.parallaxTilt, equal = difference === 0 || Number.isNaN(difference);
            const stiffness = equal ? s.appearBane : s.disappearBane, damping = equal ? s.appearNensei : s.disappearNensei;
            this.parallaxSpeed += delta * (stiffness * (difference - delta * this.parallaxSpeed) - this.parallaxSpeed * damping) / (delta * s.disappearNensei + 1);
            this.parallaxTilt = clamp01(this.parallaxTilt + delta * this.parallaxSpeed);
            this.parallaxTransparency = this.curves.ParallaxTransparency.evaluate(this.parallaxTilt);
            this.parallaxTranslate = this.curves.ParallaxTranslate.evaluate(this.parallaxTilt);
        }
        if (this.animState === 2) {
            this.timeParam += delta;
            if (degree < s.animStartDegree) { this.animState = 1; this.reflectionState = 1; }
        } else if (this.animState === 0 && this.lastDegree < s.animStartDegree && degree > s.animStartDegree) {
            this.animState = 1;
            this.reflectionState = 1;
        }
        if (this.animState === 1) {
            this.animElapsed += delta;
            this.timeParam += delta;
            this.evaluate(this.animElapsed / s.animTimeScale);
            if (this.animElapsed > s.animDuration * s.animTimeScale) { this.animState = 0; this.animElapsed = 0; this.timeParam = 0; this.reflectionState = 2; }
        }
        this.lastDegree = degree;
        this.noiseOffset = this.animState === 0 ? 0 : perlin(this.elapsedTime * s.intensityNoiseSpeed) * this.noise;
        const distance = this.distances.reduce((a, d) => a + d[0], 0), interval = this.distances.reduce((a, d) => a + d[1], 0), average = distance / interval;
        if (this.reflectionState === 0 && average > s.rotatingStartThreshold && tilt > .004999999888241291) this.reflectionState = 1;
        if ((this.reflectionState === 1 && this.animState !== 1 || this.reflectionState === 2) && average < s.rotatingEndThreshold) this.reflectionState = 0;
        if (this.reflectionState < 2) {
            const on = this.reflectionState === 1, stiffness = on ? s.reflectStartBane : s.reflectEndBane, damping = on ? s.reflectStartNensei : s.reflectEndNensei;
            this.flipAcceleration += delta * (stiffness * (((on ? 1 : 0) - this.flipSpeed) - delta * this.flipAcceleration) - this.flipAcceleration * damping) / (delta * damping + 1);
            this.flipSpeed += delta * this.flipAcceleration;
        }
        this.flipBlend = this.flipSpeed;
        this.flipAnim += this.flipSpeed * s.reflectFlipBookMaxSpeed * delta;
        this.apply();
    }

    apply() {
        this.set(this.auras, { _Transparency: this.aura, _TimeParam: this.settings.timeOffset + this.timeParam });
        this.set(this.parallaxes, { _Transparency: this.parallaxTransparency, _Translate: this.parallaxTranslate });
        this.set(this.reflections, { _ReflectCenterX: this.center, _ReflectIntensity: this.intensity + this.noiseOffset, _FlipAnim: this.flipAnim, _FlipBlend: this.flipBlend });
    }

    get animating() { return this.animState !== 0 || this.reflectionState !== 0 || Math.abs(this.flipSpeed) > 1e-3 || Math.abs(this.parallaxSpeed) > 1e-3; }
}

// CardDualMSRObject: separate flash and outline clocks, transition and reflection springs.
class DualMsr extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.shadowboxes = this.collect('ShadowBox_MSR');
        this.transitions = this.collect('Card-Dual-Transition');
        this.frameTransitions = this.collect('Card-Dual-Transition-Frame');
        this.typeIcons = this.collect('Dual-ShadowBoxUI-TypeIcon');
        this.effects = this.collect('Card-Effect-Emit');
        this.distances = [];
        this.startFront = this.lastFront = [0, 0, 0];
        this.elapsedTime = this.previousDelta = this.lastDegree = this.flashElapsed = this.outlineElapsed = 0;
        this.flipAnim = this.flipSpeed = this.flipBlend = this.flipPd = this.transitionTilt = this.transitionSpeed = this.noiseOffset = 0;
        this.flashState = this.outlineState = this.reflectionState = 0;
        this.frameBandWidth = 0;
        this.isLeft = false;
        this.evaluateFlash(0);
        this.evaluateOutline(0);
        this.step(this.front, 0);
        // As for CardMSRObject: the game's card enters rotating, so its first resting view is past these animations.
        this.evaluateFlash(effect.flashAnimDuration);
        this.evaluateOutline(effect.outlineAnimDuration);
        this.apply();
    }

    value(name, at) { return this.curves[name].evaluate(at); }

    evaluateFlash(p) {
        this.holoPower = this.value('HoloPowerCurve', p);
        this.highlight = this.value(this.startFront[0] > 0 ? 'TypeIconHighlightTranslateByTiltingRight' : 'TypeIconHighlightTranslateByTiltingLeft', p);
        this.effectSwitch = this.value('EffSwitchColor', p);
        this.effectAdditive = this.value('EffAdditiveIntensity', p);
        this.effectColor = this.value('EffColor3Blend', p);
        this.effectEmission = this.value('EffEmissiveIntensity', p);
    }

    evaluateOutline(p) {
        this.center = this.value('OutlineReflectCenterX', p);
        this.noise = this.value('ColorIntensityNoiseStrength', p);
        this.intensity = this.value('OutlineReflectColorIntensity', p);
    }

    step(f, delta) {
        const s = this.settings;
        this.elapsedTime += this.previousDelta;
        this.previousDelta = delta;
        const angle = tiltRadians(f), degree = angle * RAD, tilt = Math.sin(angle * 3);
        this.distances.push([distance3(f, this.lastFront), delta, this.elapsedTime]);
        while (this.distances.length && this.elapsedTime - this.distances[0][2] > s.checkRotatingTime) this.distances.shift();
        this.lastFront = f;
        const crossed = this.lastDegree < s.animStartDegree && degree > s.animStartDegree;
        if (this.flashState === 0 && crossed) { this.flashState = 1; this.startFront = f; }
        if (this.flashState === 1) {
            this.flashElapsed += delta;
            this.evaluateFlash(this.flashElapsed / s.flashAnimTimeScale);
            if (this.flashElapsed > s.flashAnimDuration * s.flashAnimTimeScale) { this.flashState = 0; this.flashElapsed = 0; }
        }
        if (this.outlineState === 0 && crossed) this.outlineState = this.reflectionState = 1;
        if (this.outlineState === 1) {
            this.outlineElapsed += delta;
            this.evaluateOutline(this.outlineElapsed / s.outlineAnimTimeScale);
            if (this.outlineElapsed > s.outlineAnimDuration * s.outlineAnimTimeScale) { this.outlineState = 0; this.outlineElapsed = 0; this.reflectionState = 2; }
        }
        if (!s.isAnimationStopped) {
            const rotated = rotateZ(-s.transitionAngle, f), boundary = Math.atan2(rotated[1], Math.hypot(rotated[0], rotated[2])) * RAD;
            const target = ((30 - boundary) / 60) * 2 - 1;
            this.transitionSpeed += delta * (s.transitionBane * ((target - this.transitionTilt) - delta * this.transitionSpeed) - this.transitionSpeed * s.transitionNensei) / (delta * s.transitionNensei + 1);
            this.transitionTilt = clamp(this.transitionTilt + delta * this.transitionSpeed, -1, 1);
            this.frameBandWidth = this.value('FrameBandWidthCurve', Math.abs(this.transitionTilt));
            this.isLeft = this.transitionTilt < 0;
        }
        // Noise is sampled before the reflection state changes.
        this.noiseOffset = this.reflectionState === 0 ? 0 : perlin(this.elapsedTime * s.intensityNoiseSpeed) * this.noise;
        const distance = this.distances.reduce((a, d) => a + d[0], 0), interval = this.distances.reduce((a, d) => a + d[1], 0), average = distance / interval;
        if (this.reflectionState === 0 && average > s.rotatingStartThreshold && tilt > .004999999888241291) this.reflectionState = 1;
        if ((this.reflectionState === 1 && this.outlineState !== 1 || this.reflectionState === 2) && average < s.rotatingEndThreshold) this.reflectionState = 0;
        if (this.reflectionState < 2) {
            const on = this.reflectionState === 1, bane = on ? s.reflectStartBane : s.reflectEndBane, nensei = on ? s.reflectStartNensei : s.reflectEndNensei;
            this.flipPd += delta * (bane * (((on ? 1 : 0) - this.flipSpeed) - delta * this.flipPd) - this.flipPd * nensei) / (delta * nensei + 1);
            this.flipSpeed += delta * this.flipPd;
        }
        this.flipBlend = this.flipSpeed;
        this.flipAnim += this.flipSpeed * s.reflectFlipBookMaxSpeed * delta;
        this.apply();
        this.lastDegree = degree;
    }

    apply() {
        this.set(this.shadowboxes, { _ReflectCenterX: this.center, _ReflectIntensity: this.noiseOffset + this.intensity, _FlipAnim: this.flipAnim, _FlipBlend: this.flipBlend });
        this.set(this.transitions, { _TransitionSwitch: this.transitionTilt, _IsLeft: this.isLeft ? 1 : 0, _TransitionDiffractionPower: this.holoPower });
        this.set(this.frameTransitions, { _TransitionSwitch: this.transitionTilt, _BandWidth: this.frameBandWidth });
        this.set(this.typeIcons, { _HighlightTranslate: [this.highlight, 0, 0, 0] });
        this.set(this.effects, { _Switch: this.effectSwitch, _AdditiveIntensity: this.effectAdditive, _Color3Blend: this.effectColor, _EmissiveIntensity: this.effectEmission });
    }

    get animating() { return this.flashState !== 0 || this.outlineState !== 0 || this.reflectionState !== 0 || Math.abs(this.flipSpeed) > 1e-3 || Math.abs(this.transitionSpeed) > 1e-3; }
}

// CardDualNoParallaxObject: only the dual type icon's highlight.
class DualNoParallax extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.typeIcons = this.collect('Dual-ShadowBoxUI-TypeIcon');
        this.startFront = [0, 0, 0];
        this.elapsed = this.lastDegree = 0;
        this.playing = false;
        this.evaluate(0);
        this.step(this.front, 0);
    }

    evaluate(p) {
        this.highlight = this.curves[this.startFront[0] > 0 ? 'TypeIconHighlightTranslateByTiltingRight' : 'TypeIconHighlightTranslateByTiltingLeft'].evaluate(p);
    }

    step(f, delta) {
        const s = this.settings, degree = tiltRadians(f) * RAD;
        const advance = () => {
            this.elapsed += delta;
            this.evaluate(this.elapsed / s.animTimeScale);
            this.playing = this.elapsed <= s.animDuration * s.animTimeScale;
        };
        if (this.playing) advance();
        else if (this.lastDegree < s.animStartDegree && degree > s.animStartDegree) { this.playing = true; this.elapsed = 0; this.startFront = f; advance(); }
        this.lastDegree = degree;
        this.set(this.typeIcons, { _HighlightTranslate: [this.highlight, 0, 0, 0] });
    }

    get animating() { return this.playing; }
}

// CardMSSRObject.
class Mssr extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.targets = this.collect('Card-Effect-Emit');
        this.distances = [];
        this.lastFront = this.front;
        this.lastTilt = this.elapsed = this.clock = 0;
        this.adjusted = 1;
        this.playing = false;
        this.evaluate(0);
        this.step(this.front, 0);
    }

    evaluate(elapsed) {
        const at = elapsed / this.settings.animTimeScale, v = n => this.curves[n].evaluate(at);
        this.values = { _Switch: v('EffSwitchColor'), _AdditiveIntensity: v('EffAdditiveIntensity'), _Color3Blend: v('EffColor3Blend'), _EmissiveIntensity: v('EffEmissiveIntensity') };
    }

    step(f, delta) {
        const s = this.settings, tilt = tiltRadians(f) * RAD;
        this.clock += delta;
        this.distances.push([distance3(f, this.lastFront), delta, this.clock]);
        while (this.distances.length && this.clock - this.distances[0][2] > s.recordingTime) this.distances.shift();
        this.lastFront = f;
        if (!this.playing && this.lastTilt < s.animStartDegree && tilt > s.animStartDegree) {
            this.playing = true;
            this.elapsed = 0;
            const distance = this.distances.reduce((a, d) => a + d[0], 0), interval = this.distances.reduce((a, d) => a + d[1], 0);
            const speed = clamp(distance / interval, s.minTiltSpeed, s.maxTiltSpeed);
            this.adjusted = s.minAnimSpeed + (s.maxAnimSpeed - s.minAnimSpeed) * clamp01((speed - s.minTiltSpeed) / (s.maxTiltSpeed - s.minTiltSpeed));
        }
        if (this.playing) {
            // The source evaluates before the increment; even the terminating update keeps this sample.
            this.evaluate(this.elapsed);
            this.elapsed += delta * (s.useSpeedAdjust ? this.adjusted : 1);
            this.playing = this.elapsed <= s.animDuration * s.animTimeScale;
        }
        this.lastTilt = tilt;
        this.set(this.targets, this.values);
    }

    get animating() { return this.playing; }
}

// CardMarbleLayer.
class Marble extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.target = this.renderer(effect.rendererIndices);
        this.fronts = [];
        this.clock = 0;
        this.tilt = this.rotation = 0;
        this.currentTilt = [0, 0, 0];
        this.goalTilt = [0, 0, 0];
        this.tiltSpeed = [0, 0, 0];
        const start = p => [p.DefaultPosition.x, p.DefaultPosition.y, p.DefaultForce];
        this.current = effect.points.map(start);
        this.goal = effect.points.map(start);
        this.speeds = effect.points.map(() => [0, 0, 0]);
        if (this.target) this.target.blockTextures[effect.remapLabel] = this.remapTexture();
        this.step(this.front, 0);
    }

    remapTexture() {
        // ldexpf(1, Resolution), R16, no mips, clamped: half floats here, the only filterable 16-bit format of WebGL2.
        const s = this.settings, size = 2 ** s.remapResolution, pixels = new Float32Array(size * size);
        for (let y = 0; y < size; y++) {
            const blend = clamp01(this.curves.RemapRemapCurve.evaluate(y / size));
            for (let x = 0; x < size; x++) {
                const normal = this.curves.DefaultRemapCurve.evaluate(x / size), tilted = this.curves.TiltRemapCurve.evaluate(x / size);
                pixels[x + y * size] = normal + blend * (tilted - normal);
            }
        }
        return this.context.floatTexture(size, pixels);
    }

    updateGoals(front) {
        const s = this.settings, degree = Math.atan2(front[1], front[0]) * RAD;
        s.points.forEach((p, i) => {
            const moved = rotateZ(degree * p.RotationWithTilt, [p.TiltMovePosition.x, p.TiltMovePosition.y, 0]);
            this.goal[i] = [p.DefaultPosition.x + moved[0] * front[2] * s.pointMoveByTilt, p.DefaultPosition.y + moved[1] * front[2] * s.pointMoveByTilt,
                p.DefaultForce + front[2] * p.TiltForce * s.pointForceChangeByTilt];
        });
    }

    advance(acceleration, speed, position, destination, delta) {
        const s = this.settings, difference = destination.map((v, i) => v - position[i]), distance = length(difference);
        if (distance < s.goalThreshold) { speed.fill(0); return; }
        const direction = distance > .000009999999747378752 ? difference.map(v => v / distance) : [0, 0, 0];
        for (let i = 0; i < 3; i++) speed[i] += direction[i] * acceleration * delta;
        if (distance <= s.dorodoroDistance) {
            const resistance = (1 - distance / s.dorodoroDistance) ** s.resistancePower, factor = s.minDorodoroCoef + (1 - resistance) * (1 - s.minDorodoroCoef);
            for (let i = 0; i < 3; i++) speed[i] *= factor;
        }
        const magnitude = length(speed);
        if (magnitude > s.maxPointSpeed) for (let i = 0; i < 3; i++) speed[i] *= s.maxPointSpeed / magnitude;
        else if (magnitude < s.minPointSpeed) for (let i = 0; i < 3; i++) speed[i] = direction[i] * s.minPointSpeed;
        for (let i = 0; i < 3; i++) position[i] += speed[i] * delta;
    }

    step(f, delta) {
        const s = this.settings;
        this.clock += delta;
        this.tilt = clamp01(Math.sin(Math.atan2(Math.hypot(f[0], f[1]), f[2]) * 3) ** s.tiltPower);
        if (s.useMarbleDelay) {
            this.fronts.push([f[0], f[1], this.tilt, this.clock]);
            let removed = false;
            while (this.fronts.length && this.clock - this.fronts[0][3] > s.delayTime2) {
                const old = this.fronts.shift();
                this.goalTilt = [old[0], old[1], old[2]];
                removed = true;
            }
            if (removed) this.updateGoals(this.goalTilt);
        }
        this.rotation = (Math.atan2(f[1], f[0]) / Math.PI + 1) * .5;
        if (s.useMarbleDelay) {
            this.current.forEach((position, i) => this.advance(s.pointAccel, this.speeds[i], position, this.goal[i], delta));
            this.advance(s.shearAccel, this.tiltSpeed, this.currentTilt, this.goalTilt, delta);
        }
        this.set([this.target], { _PointCount: this.current.length, _Attributes: this.current.flatMap(p => [p[0], p[1], p[2], 0]), _Tilt: this.tilt,
            _Front: [this.currentTilt[0], this.currentTilt[1], 0, 0], _WorldFront: [f[0], f[1], f[2], 0], _TiltRotation: this.rotation });
    }

    // The pattern keeps creeping long after the other animations end (fidelity bench, 25 s).
    get animating() { return true; }
}

// CardAncientObject; the sand particles are not driven here, see unportedParticleSystems.
class Ancient extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.targets = this.collect('Parallax-Strata');
        this.strata = Array.from({ length: 6 }, () => ({ current: 0, lastDir: 0, elapsed: 0, changeTime: 0, startDelay: 0, speed: 0, state: 0 }));
        this.remapped = new Array(6).fill(0);
        this.elapsedTime = 0;
        this.volumes = new Array(4).fill(0);
        this.step(this.front, 0);
    }

    noise(value) {
        const scaled = perlin(value) * this.settings.noiseScale, fraction = scaled - Math.floor(scaled);
        return fraction + fraction - 1;
    }

    advance(s, goal, delta) {
        const p = this.settings, distance = Math.abs(goal - s.current), random = (a, b) => a + Math.random() * (b - a);
        if (distance < p.goalThreshold) { s.current = goal; s.elapsed = 0; s.speed = 0; s.state = 0; return false; }
        const direction = goal - s.current >= 0 ? 1 : -1;
        if (s.state === 0 || s.lastDir !== direction) {
            s.elapsed = 0; s.speed = 0; s.state = 1;
            s.changeTime = random(p.changeRangeStart, p.changeRangeEnd);
            s.startDelay = random(p.animStartDelayRangeA, p.animStartDelayRangeB);
        }
        const t = Math.max(s.elapsed - s.startDelay, 0);
        s.state = t < s.changeTime ? 1 : distance > p.zuzuGoalAnimThreshold ? 2 : 3;
        s.speed = this.curves[s.state === 1 ? 'ZuzuA' : s.state === 2 ? 'Zzzzz' : 'ZuzuGoal'].evaluate(t) * (direction * p.animCurveScale);
        if (s.state === 2) s.speed *= Math.max(p.accellRatio * Math.abs(goal - s.current), 1);
        s.lastDir = direction;
        s.elapsed += delta;
        s.current = clamp(s.current + s.speed * delta, -1, 1);
        return true;
    }

    step(f, delta) {
        const p = this.settings, n = normalized(f);
        this.elapsedTime += delta;
        this.goal = dot(n, DIAGONAL_A) + p.dot2Multiply * dot(n, DIAGONAL_B);
        this.strata.forEach((s, i) => { if (this.advance(s, this.goal, delta)) this.remapped[i] = s.current * p.scrolls[i] * p.scrollLength; });
        const clock = this.elapsedTime * p.shakeSpeed, first = this.strata[0];
        const a = [this.noise(clock * p.shakeAFrequency.x), this.noise(clock * p.shakeAFrequency.y + .5)];
        const b = [this.noise(clock * p.shakeBFrequency.x), this.noise(clock * p.shakeBFrequency.y + .5)];
        const gain = this.curves.ShakeIntensity.evaluate(first.elapsed) * clamp01(p.diffOffset + Math.abs(this.goal - first.current));
        const shake = [(a[0] * p.shakeAIntensity.x * .1 + b[0] * p.shakeBIntensity.x * .1) * gain, (a[1] * p.shakeAIntensity.y * .1 + b[1] * p.shakeBIntensity.y * .1) * gain, 0, 0];
        this.set(this.targets, { _StrataFaults: [...this.remapped], _Shake: shake });
        this.updateSand();
    }

    // Sand falls where strata rub: emission follows their speed difference, the shape's sub-mesh changes with noise.
    updateSand() {
        const p = this.settings, scene = this.context.particles?.();
        if (!scene) return;
        const of = paths => paths.map(path => scene.node(path.indices)?.particle);
        const sand = of(p.sandParticlePaths), sand2 = of(p.sand2ParticlePaths);
        for (let i = 0; i < 4; i++) {
            const noise = perlin(i * 10 + this.elapsedTime * p.shapeChangeSpeed) * 5, index = Math.floor((noise + noise) % 5);
            this.volumes[i] = clamp(p.frictionScale * Math.abs(this.strata[4 - i].speed * p.scrolls[4 - i] - this.strata[5 - i].speed * p.scrolls[5 - i]), 0, p.maxFriction);
            const state = this.strata[i].state, base = state === 1 ? p.startSandBaseEmissionRate : state === 2 ? p.middleSandBaseEmissionRate : state === 3 ? p.endSandBaseEmissionRate : 0;
            const rate = p.isAnimationStopped ? 0 : base * this.volumes[i];
            for (const [list, mesh] of [[sand, true], [sand2, false]]) {
                const system = list[i];
                if (!system) continue;
                if (!system.playing) system.play();
                system.emitting = true;
                system.rateOverride = rate;
                if (mesh) system.meshMaterialIndex = index;
            }
        }
    }

    get animating() { return this.strata.some(s => s.state !== 0) || !!this.context.particles?.()?.active; }
}

// CircularKiraObject.
class CircularKira extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.prims = effect.primitives.map(p => ({ ...p, Speed: 0, Angle: p.StartAngle }));
        this.movingA = this.renderer(effect.movingKiraARendererPath?.indices);
        this.movingB = this.renderer(effect.movingKiraBRendererPath?.indices);
        this.trailA = this.renderer(effect.trailKiraARendererPath?.indices);
        this.trailB = this.renderer(effect.trailKiraBMeshFilterPath?.indices);
        this.trail = this.trailB && context.dynamicUv(this.trailB);
        const n = this.prims.length;
        this.properties = Object.fromEntries(['_PrimAngles', '_PrimBaseScales', '_PrimBaseIntensities', '_PrimMinIntensities', '_PrimMaxIntensities', '_PrimFlickerScaling',
            '_PrimFlickerAnimOffsets', '_PrimTypes', '_PrimMorphing'].map(name => [name, new Array(n).fill(0)]));
        this.prims.forEach((p, i) => {
            this.properties._PrimBaseScales[i] = p.BaseScale;
            this.properties._PrimBaseIntensities[i] = p.BaseIntensity;
            this.properties._PrimMinIntensities[i] = p.MinIntensity;
            this.properties._PrimMaxIntensities[i] = p.MaxIntensity;
            this.properties._PrimFlickerScaling[i] = p.FlickerScaling;
            this.properties._PrimTypes[i] = p.PrimType;
        });
        this.brightnesses = new Array(effect.meshDivideCount).fill(0);
        this.temp = new Array(effect.meshDivideCount * 2).fill(0);
        this.morphOffset = Math.random() * 10000;
        this.defaultAngle = [effect.defaultCircularAngleManual, 50, 132, 35][effect.defaultCircularAnglePattern] ?? effect.defaultCircularAngleManual;
        this.isTilt = false;
        this.changeElapsed = this.time = 0;
        this.speedState = 0;
        this.updateTilt(this.front, 0);
        this.moveAngle = this.evaluateMoveAngle(this.front);
        this.lastIndex = this.index(this.moveAngle);
        if (!effect.isAnimationStopped) this.step(this.front, 0);
    }

    index(angle) { return Math.round(((angle + 360) * this.settings.meshDivideCount) / 360); }

    evaluateMoveAngle(f) { return -(((f[0] * .5 + .5) * .5 + (f[1] * .5 + .5) * .5 - .5) * this.settings.moveAngleScale); }

    updateTilt(f, delta) {
        const s = this.settings;
        this.tilt = clamp01Native(Math.sin(Math.atan2(Math.hypot(f[0], f[1]), f[2]) * 3) ** s.tiltPower);
        const next = this.tilt > s.tiltThreshold;
        if (this.isTilt !== next) {
            this.changeElapsed += delta;
            if (this.changeElapsed <= s.tiltStateChangeDelay) return;
            this.isTilt = next;
        }
        this.changeElapsed = 0;
    }

    symmetry(type) {
        const s = this.settings;
        return type === 0 ? s.primTypeASymmetryCount : type === 1 ? s.primTypeBSymmetryCount : type === 2 ? s.primTypeCSymmetryCount : 1;
    }

    updatePrimitives(delta) {
        const s = this.settings, resetBrake = () => { for (const p of this.prims) { p.BreakingTime = p.SpeedAtBrakeStart = p.BreakGoalAngle = p.BreakStartAngle = 0; } };
        if (this.speedState === 0) {
            if (this.isTilt) { this.speedState = 1; resetBrake(); }
        } else if (this.speedState === 1) {
            for (const p of this.prims) p.Speed = clamp(p.Speed + p.MaxRotateSpeed * s.rotateAccel * delta, 0, p.MaxRotateSpeed);
            if (!this.isTilt) {
                this.speedState = 2;
                for (const p of this.prims) {
                    p.SpeedAtBrakeStart = p.Speed;
                    const period = 360 / this.symmetry(p.PrimType);
                    let goal = p.Angle + (period - (p.Angle - p.StartAngle) % period);
                    const distance = p.Speed * s.brakeDuration * Math.PI * .25;
                    while (goal - p.Angle < distance) goal += period;
                    p.BreakGoalAngle = goal;
                    p.BreakStartAngle = goal - distance;
                }
            }
        } else {
            for (const p of this.prims) {
                if (p.Angle >= p.BreakGoalAngle) { p.Speed = 0; p.Angle = p.BreakGoalAngle; }
                else if (p.Angle >= p.BreakStartAngle) {
                    const value = Math.sin(Math.acos(-clamp01Native(p.BreakingTime / s.brakeDuration))) * p.SpeedAtBrakeStart;
                    p.Speed = value <= .05 ? .05 : value;
                    p.BreakingTime += delta;
                }
            }
            if (this.isTilt) { this.speedState = 1; resetBrake(); }
        }
        this.prims.forEach((p, i) => {
            this.properties._PrimFlickerAnimOffsets[i] += delta * p.FlickerSpeed;
            p.Angle += p.Speed * delta;
            this.properties._PrimAngles[i] = p.ReverseRotation ? -p.Angle : p.Angle;
        });
        this.morphOffset += delta;
        this.prims.forEach((p, i) => {
            if (!p.UseMorphing) return;
            const phase = this.morphOffset * p.MorphingSpeed, noise = p.UseMorphingNoise ? perlin(phase) : Math.sin(phase);
            this.properties._PrimMorphing[i] = clamp01Native((noise - .5) * p.MorphingClearly + .5) * clamp01Native(p.Speed / p.MaxRotateSpeed);
        });
    }

    updateTrail(delta) {
        const s = this.settings, t = this.temp, current = this.index(this.moveAngle), edges = [0, 0];
        let edgeCount = 0, previous = 0;
        for (let i = 0; i < t.length; i++) {
            const distance = Math.abs(i - current);
            if (!((i < current || this.lastIndex < i) && (current < i || i < this.lastIndex))) t[i] = s.centerIntensity;
            else if (distance <= s.expandLength) {
                const center = s.centerIntensity * this.tilt;
                t[i] = Math.max(t[i], (center + clamp01(distance / s.expandLength) * (0 - center)) ** s.expandPower);
            }
            if (s.useLengthLimit) {
                if (edgeCount > 1) break; // The source stops before fading later elements this update.
                const value = t[i];
                if (i === 0 || i === t.length - 1) { if (value > 0) edges[edgeCount++] = i; }
                else if (value !== 0 && previous === 0) edges[edgeCount++] = i;
                else if (value === 0 && previous !== 0) edges[edgeCount++] = i - 1;
                previous = value;
            }
            let fade = s.fadeOut;
            if (s.useDistanceFadeOut) {
                const size = s.limitLengthRatio * this.brightnesses.length;
                fade *= s.distanceFadeOutSpeed * (distance / (size + size)) ** s.distanceFadeOutCurvePower + 1;
            }
            t[i] = t[i] < s.fadeOutEnd ? 0 : t[i] - fade * delta;
        }
        if (s.useLengthLimit) {
            let size = edges[1] - edges[0] + 1;
            const limit = Math.trunc(s.limitLengthRatio * this.brightnesses.length);
            while (size > limit) {
                const side = t[edges[1]] <= t[edges[0]] ? 1 : 0, index = edges[side], value = t[index];
                edges[side] += side === 0 ? 1 : -1;
                t[index] = value * (1 - s.LimitAdjustSpeed) + s.LimitAdjustSpeed * Math.min(value, 0);
                size--;
            }
            for (let i = 0; i < size; i++) {
                const index = edges[0] + i, distance = Math.abs(current - index), range = index < current ? current - edges[0] : edges[1] - current;
                const cap = s.centerIntensity * (1 - (distance / range) ** s.LimitAdjustCurvePower), value = t[index];
                t[index] = value * (1 - s.LimitAdjustSpeed) + s.LimitAdjustSpeed * (cap <= value ? cap : value);
            }
        }
        for (let i = 0; i < this.brightnesses.length; i++) this.brightnesses[i] = Math.max(t[i], t[i + this.brightnesses.length]);
        this.lastIndex = current;
    }

    applyVertices() {
        if (!this.trail) return;
        const uv = this.trail.data, count = uv.length / 2;
        this.brightnesses.forEach((value, i) => {
            const index = count - 2 - i * 2;
            uv[index * 2 + 1] = value;
            uv[(index + 1) * 2 + 1] = value;
        });
        uv[1] = uv[7];
        uv[3] = uv[(count - 1) * 2 + 1];
        uv[5] = uv[(count - 1) * 2 + 1];
        this.trail.upload();
    }

    step(f, delta) {
        if (this.settings.isAnimationStopped) return;
        this.time += delta;
        this.updateTilt(f, delta);
        this.moveAngle = this.evaluateMoveAngle(f);
        this.updatePrimitives(delta);
        this.updateTrail(delta);
        this.applyVertices();
        const moving = { _CircularDefaultAngle: this.defaultAngle, _MoveAngle: this.moveAngle, _Tilt: this.tilt };
        for (const [name, values] of Object.entries(this.properties)) moving[name] = [...values];
        this.set([this.movingA, this.movingB], moving);
        this.set([this.trailA, this.trailB], { ...moving, _NoiseTime: this.time + this.time });
    }

    get animating() { return !this.settings.isAnimationStopped; }
}

// CardImmersiveObject: its values come from the prefab, the movie controller changes them.
class Immersive extends Controller {
    constructor(effect, context) {
        super(effect, context);
        this.illustration = this.collect('Card_Parallax_Immersive');
        this.text = this.collect('Text_Alpha');
        this.emission = this.collect('Frame-Holo-Immersive');
        Object.assign(this, { IllustAlpha: effect.IllustAlpha, TextAlpha: effect.TextAlpha, EmitSwitch: effect.EmitSwitch, EmitIntensity: effect.EmitIntensity, Darkness: effect.Darkness });
        this.apply();
    }

    apply() {
        this.set(this.illustration, { _IllustAlpha: this.IllustAlpha });
        this.set(this.text, { _Alpha: this.TextAlpha });
        this.set(this.emission, { _Switch: this.EmitSwitch, _OnIntensity: this.EmitIntensity, _Darkness: this.Darkness });
    }

    step() { this.apply(); }
}

const CONTROLLERS = { CardUVShadow: ShadowRelief, CardMReliefSRObject: ShadowRelief, CardURObject: UltraRare, GlitterFlowMaps: GlitterFlow, CardRGBObject: RgbCard,
    CardFutureObject: Future, KiraPuyoObject: KiraPuyo, CardMRRObject: Mrr, CardDualMRRObject: DualMrr, CardMSRObject: Msr, CardDualMSRObject: DualMsr,
    CardDualNoParallaxObject: DualNoParallax, CardMSSRObject: Mssr, CardMarbleLayer: Marble, CardAncientObject: Ancient, CircularKiraObject: CircularKira, CardImmersiveObject: Immersive };

export const createControllers = (effects, context) => effects.map(effect => {
    const Type = CONTROLLERS[effect.kind];
    if (!Type) throw new Error(`Effect not ported: ${effect.kind}`);
    return new Type(effect, context);
});
