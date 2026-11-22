// Unity's Shuriken for the modules the cards use: emission, shapes, lifetime curves, noise, sub-emitters, custom data.
// Random numbers come from a seeded generator: the same character as the game, not the same particles.
import { Curve, perlin } from './effects.js';

const DEG = Math.PI / 180;
const curveOf = cache => (data) => {
    let curve = cache.get(data);
    if (!curve) {
        curve = data.m_Curve.length ? new Curve({ keys: data.m_Curve, preWrap: data.m_PreInfinity, postWrap: data.m_PostInfinity }) : null;
        cache.set(data, curve);
    }
    return curve;
};
const curves = curveOf(new Map());

// ParticleSystemCurveMode: constant, curve, two curves, two constants.
export const evaluate = (c, t, random) => {
    if (!c) return 0;
    switch (c.minMaxState) {
        case 0: return c.scalar;
        case 1: return (curves(c.maxCurve)?.evaluate(t) ?? 1) * c.scalar;
        case 2: { const a = curves(c.minCurve)?.evaluate(t) ?? 0, b = curves(c.maxCurve)?.evaluate(t) ?? 0; return (a + (b - a) * random) * c.scalar; }
        default: return c.minScalar + (c.scalar - c.minScalar) * random;
    }
};

const gradientAt = (g, t) => {
    const stops = (count, name) => { const out = []; for (let i = 0; i < count; i++) out.push(g[`${name}${i}`] / 65535); return out; };
    const colorTimes = stops(g.m_NumColorKeys, 'ctime'), alphaTimes = stops(g.m_NumAlphaKeys, 'atime');
    const pick = (times, read) => {
        if (t <= times[0]) return read(0);
        for (let i = 1; i < times.length; i++) if (t <= times[i]) {
            if (g.m_Mode === 1) return read(i);
            const f = (t - times[i - 1]) / Math.max(times[i] - times[i - 1], 1e-6), a = read(i - 1), b = read(i);
            return a.map((v, k) => v + (b[k] - v) * f);
        }
        return read(times.length - 1);
    };
    const [r, g2, b] = pick(colorTimes, i => { const k = g[`key${i}`]; return [k.r, k.g, k.b]; });
    const [a] = pick(alphaTimes, i => [g[`key${i}`].a]);
    return [r, g2, b, a];
};

// ParticleSystemGradientMode: color, gradient, two colors, two gradients.
const color = (c, t, random) => {
    const of = k => [k.r, k.g, k.b, k.a], mix = (a, b) => a.map((v, i) => v + (b[i] - v) * random);
    switch (c.minMaxState) {
        case 0: return of(c.maxColor);
        case 1: return gradientAt(c.maxGradient, t);
        case 2: return mix(of(c.minColor), of(c.maxColor));
        default: return mix(gradientAt(c.minGradient, t), gradientAt(c.maxGradient, t));
    }
};

const multiply = (a, b) => {
    const o = new Float32Array(16);
    for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++)
        o[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
    return o;
};
const point = (m, v) => [0, 1, 2].map(r => m[r] * v[0] + m[4 + r] * v[1] + m[8 + r] * v[2] + m[12 + r]);
const direction = (m, v) => [0, 1, 2].map(r => m[r] * v[0] + m[4 + r] * v[1] + m[8 + r] * v[2]);
const normalize = v => { const l = Math.hypot(...v) || 1; return v.map(c => c / l); };
const rotateEuler = ([x, y, z], v) => {
    // Unity's order: Z, then X, then Y.
    const rz = [v[0] * Math.cos(z) - v[1] * Math.sin(z), v[0] * Math.sin(z) + v[1] * Math.cos(z), v[2]];
    const rx = [rz[0], rz[1] * Math.cos(x) - rz[2] * Math.sin(x), rz[1] * Math.sin(x) + rz[2] * Math.cos(x)];
    return [rx[0] * Math.cos(y) + rx[2] * Math.sin(y), rx[1], -rx[0] * Math.sin(y) + rx[2] * Math.cos(y)];
};

class Random {
    constructor(seed) { this.state = (seed >>> 0) || 1; }
    next() {
        // xorshift32, Unity's family of generators.
        let x = this.state;
        x ^= x << 13; x ^= x >>> 17; x ^= x << 5;
        this.state = x >>> 0;
        return this.state / 4294967296;
    }
}

export class ParticleSystem {
    constructor(node, mesh) {
        this.node = node;
        this.definition = node.system;
        this.config = node.system.config;
        this.mesh = mesh;
        this.particles = [];
        this.playing = false;
        this.emitting = false;
        this.time = 0;
        this.emission = 0;
        this.bursts = [];
        this.world = new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
        this.previous = null;
        this.rateOverride = null;
        this.subs = [];
        this.seed(this.config.randomSeed);
    }

    seed(value) { this.random = new Random(value); }

    play() {
        this.playing = this.emitting = true;
        this.time = 0;
        this.emission = 0;
        this.delay = evaluate(this.config.startDelay, 0, this.random.next());
        this.bursts = (this.config.EmissionModule?.m_Bursts ?? []).map(b => ({ ...b, cycles: 0, next: b.time }));
    }

    stop(clear) {
        this.emitting = false;
        if (clear) { this.particles = []; this.playing = false; }
    }

    // Shape module: where a particle starts and where it goes, in the emitter's space.
    shapeSample() {
        const s = this.config.ShapeModule, r = () => this.random.next();
        if (!s) return { position: [0, 0, 0], direction: [0, 0, 1] };
        const radius = s.radius?.value ?? 1, thickness = s.radiusThickness ?? 1;
        const inRadius = () => radius * (1 - thickness + thickness * Math.sqrt(r()));
        let position, dir;
        switch (s.type) {
            case 0: case 1: case 2: case 3: {
                const z = (s.type >= 2 ? r() : r() * 2 - 1), a = r() * Math.PI * 2, q = Math.sqrt(1 - z * z);
                dir = [q * Math.cos(a), q * Math.sin(a), z];
                const d = radius * (1 - thickness + thickness * Math.cbrt(r()));
                position = dir.map(v => v * d);
                break;
            }
            case 4: case 8: {
                const arc = (s.arc?.value ?? 360) * DEG, a = r() * arc, d = inRadius(), ca = Math.cos(a), sa = Math.sin(a);
                const spread = Math.sin(s.angle * DEG) * (radius > 0 ? d / radius : 1);
                dir = normalize([ca * spread, sa * spread, Math.cos(s.angle * DEG)]);
                position = [ca * d, sa * d, 0];
                if (s.type === 8) position = position.map((v, i) => v + dir[i] * s.length * r());
                break;
            }
            case 5: case 15: case 16: {
                position = [r() - .5, r() - .5, r() - .5];
                dir = [0, 0, 1];
                break;
            }
            case 6: case 13: case 14: {
                const mesh = this.mesh?.(s.mesh), material = this.meshMaterialIndex ?? s.m_MeshMaterialIndex ?? 0;
                if (mesh) {
                    const [first, count] = mesh.submeshes[material % mesh.submeshes.length], at = first + Math.floor(r() * count / 3) * 3;
                    const corner = i => { const k = mesh.indices[at + i], p = mesh.vertices.position.data; return [p[k * 3], p[k * 3 + 1], p[k * 3 + 2]]; };
                    const a = corner(0), b = corner(1), c = corner(2);
                    let u = r(), v = r();
                    if (u + v > 1) { u = 1 - u; v = 1 - v; }
                    position = a.map((x, i) => x + (b[i] - x) * u + (c[i] - x) * v);
                    const e1 = b.map((x, i) => x - a[i]), e2 = c.map((x, i) => x - a[i]);
                    dir = normalize([e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0]]);
                } else { position = [0, 0, 0]; dir = [0, 0, 1]; }
                break;
            }
            default: {
                const a = r() * Math.PI * 2, d = inRadius();
                dir = [Math.cos(a), Math.sin(a), 0];
                position = dir.map(v => v * d);
            }
        }
        const scale = s.m_Scale ?? { x: 1, y: 1, z: 1 }, angles = [s.m_Rotation.x * DEG, s.m_Rotation.y * DEG, s.m_Rotation.z * DEG];
        position = rotateEuler(angles, [position[0] * scale.x, position[1] * scale.y, position[2] * scale.z]);
        position = [position[0] + s.m_Position.x, position[1] + s.m_Position.y, position[2] + s.m_Position.z];
        dir = rotateEuler(angles, dir);
        if (s.randomDirectionAmount) {
            const a = r() * Math.PI * 2, z = r() * 2 - 1, q = Math.sqrt(1 - z * z), t = s.randomDirectionAmount;
            dir = normalize(dir.map((v, i) => v + ([q * Math.cos(a), q * Math.sin(a), z][i] - v) * t));
        }
        if (s.sphericalDirectionAmount) dir = normalize(dir.map((v, i) => v + (normalize(position)[i] - v) * s.sphericalDirectionAmount));
        return { position, direction: dir };
    }

    spawn(count, at = null, inherited = null) {
        const m = this.config.InitialModule, t = this.config.lengthInSec ? (this.time % this.config.lengthInSec) / this.config.lengthInSec : 0, r = () => this.random.next();
        for (let i = 0; i < count && this.particles.length < m.maxNumParticles; i++) {
            const { position, direction: dir } = this.shapeSample(), speed = evaluate(m.startSpeed, t, r());
            const lifetime = Math.max(evaluate(m.startLifetime, t, r()), 1e-3);
            const size = evaluate(m.startSize, t, r()), rotation = evaluate(m.startRotation, t, r());
            const local = this.config.moveWithTransform === 0 && !at;
            const start = at ?? (local ? position : point(this.world, position));
            const velocity = dir.map(v => v * speed);
            const p = { position: [...start], velocity: local ? velocity : direction(this.world, velocity), age: 0, lifetime, randoms: [r(), r(), r(), r()], rotation,
                size: m.size3D ? [size, evaluate(m.startSizeY, t, r()), evaluate(m.startSizeZ, t, r())] : [size, size, size], color: color(m.startColor, t, r()), inherited: inherited ?? [0, 0, 0] };
            const sign = m.randomizeRotationDirection > r() ? -1 : 1;
            p.rotation *= sign;
            this.particles.push(p);
            for (const sub of this.subs) if (sub.type === 0) sub.system.spawn(1, this.worldOf(p));
        }
    }

    worldOf(p) { return this.config.moveWithTransform === 0 ? point(this.world, p.position) : p.position; }

    step(delta, world) {
        if (!this.playing) return;
        // ParticleSystemScalingMode.Local: the parents' scale does not reach the simulation.
        this.world = this.config.scalingMode === 0 ? world : this.withoutParentScale(world);
        const speed = this.config.simulationSpeed, dt = delta * speed;
        const position = [world[12], world[13], world[14]];
        const emitterVelocity = this.previous && delta > 0 ? position.map((v, i) => (v - this.previous[i]) / delta) : [0, 0, 0];
        this.previous = position;
        if (this.emitting && this.delay > 0) this.delay -= dt;
        else if (this.emitting) {
            const length = this.config.lengthInSec, t = (this.time % length) / length, e = this.config.EmissionModule;
            if (this.config.looping || this.time < length) {
                const rate = this.rateOverride ?? evaluate(e?.rateOverTime, t, this.random.next());
                this.emission += rate * dt;
                const whole = Math.floor(this.emission);
                this.emission -= whole;
                if (whole > 0) this.spawn(whole, null, this.inheritedVelocity(emitterVelocity));
                for (const b of this.bursts) {
                    while (this.time >= b.next && (b.cycleCount === 0 || b.cycles < b.cycleCount)) {
                        if (this.random.next() <= b.probability) this.spawn(Math.round(evaluate(b.countCurve, t, this.random.next())), null, this.inheritedVelocity(emitterVelocity));
                        b.cycles++;
                        b.next += b.repeatInterval;
                    }
                }
            }
            this.time += dt;
            if (this.config.looping && length > 0 && this.time >= length) {
                this.time -= length;
                for (const b of this.bursts) { b.cycles = 0; b.next = b.time; }
            } else if (!this.config.looping && this.time >= length) this.emitting = false;
        }
        const live = [];
        for (const p of this.particles) {
            p.age += dt;
            if (p.age >= p.lifetime) {
                for (const sub of this.subs) if (sub.type === 1 && this.random.next() <= sub.probability) sub.system.spawn(1, this.worldOf(p));
                continue;
            }
            live.push(p);
            const u = p.age / p.lifetime;
            let added = [0, 0, 0];
            const v = this.config.VelocityModule;
            if (v) {
                const raw = [evaluate(v.x, u, p.randoms[0]), evaluate(v.y, u, p.randoms[1]), evaluate(v.z, u, p.randoms[2])];
                const modifier = v.speedModifier ? evaluate(v.speedModifier, u, p.randoms[3]) : 1;
                added = raw.map(c => c * modifier);
                if (!v.inWorldSpace && this.config.moveWithTransform !== 0) added = direction(world, added);
            }
            const f = this.config.ForceModule;
            if (f) {
                const force = [evaluate(f.x, u, p.randoms[0]), evaluate(f.y, u, p.randoms[1]), evaluate(f.z, u, p.randoms[2])];
                for (let i = 0; i < 3; i++) p.velocity[i] += force[i] * dt;
            }
            const gravity = evaluate(this.config.InitialModule.gravityModifier, u, p.randoms[0]);
            if (gravity) p.velocity[1] -= 9.81 * gravity * dt;
            const noise = this.config.NoiseModule;
            let drift = [0, 0, 0];
            if (noise) {
                const strength = evaluate(noise.strength, u, p.randoms[0]) * (noise.remapEnabled ? 1 : 1), frequency = noise.frequency ?? 1;
                const scroll = evaluate(noise.scrollSpeed, u, p.randoms[1]) * p.age;
                const sample = o => (perlin(p.position[0] * frequency + o + scroll, p.position[1] * frequency + p.randoms[2] * 10) - .5) * 2 * strength;
                drift = [sample(0), sample(31.4), sample(77.7)];
            }
            const clamped = this.config.ClampVelocityModule;
            if (clamped) {
                const limit = evaluate(clamped.magnitude, u, p.randoms[0]), total = p.velocity.map((c, i) => c + added[i]), size = Math.hypot(...total);
                if (size > limit && size > 0) {
                    const dampen = 1 - (clamped.dampen ?? 0) * 60 * dt >= 0 ? clamped.dampen ?? 0 : 1, keep = (size - (size - limit) * dampen) / size;
                    for (let i = 0; i < 3; i++) p.velocity[i] *= keep;
                }
            }
            for (let i = 0; i < 3; i++) p.position[i] += (p.velocity[i] + added[i] + drift[i] + p.inherited[i]) * dt;
            const rotation = this.config.RotationModule;
            if (rotation) p.rotation += evaluate(rotation.curve, u, p.randoms[3]) * dt;
        }
        this.particles = live;
        if (!this.emitting && !live.length) this.playing = false;
    }

    withoutParentScale(world) {
        const own = this.node.matrix, out = new Float32Array(world);
        for (let c = 0; c < 3; c++) {
            const w = Math.hypot(world[c * 4], world[c * 4 + 1], world[c * 4 + 2]) || 1, l = Math.hypot(own[c * 4], own[c * 4 + 1], own[c * 4 + 2]);
            for (let r = 0; r < 3; r++) out[c * 4 + r] = world[c * 4 + r] / w * l;
        }
        return out;
    }

    inheritedVelocity(emitter) {
        const module = this.config.InheritVelocityModule;
        if (!module || module.m_Mode === 1) return null;
        const k = evaluate(module.m_Curve, 0, this.random.next());
        return emitter.map(v => v * k);
    }

    customData(index, p, u) {
        const m = this.config.CustomDataModule;
        if (!m || !m[`mode${index}`]) return [0, 0, 0, 0];
        if (m[`mode${index}`] === 1) return [0, 1, 2, 3].map(i => evaluate(m[`vector${index}_${i}`], u, p.randoms[i]));
        return color(m[`color${index}`], u, p.randoms[0]);
    }

    // Quads or mesh copies in world space, in the streams this material's vertex shader reads.
    build(arrays, view, geometryScale = 1) {
        const d = this.definition, size = this.config.SizeModule, colors = this.config.ColorModule, c = this.config.InitialModule;
        const ordered = d.sortMode === 3 ? [...this.particles].reverse() : this.particles;
        const right = d.alignment === 0 ? [view[0], view[4], view[8]] : d.alignment === 2 ? normalize(direction(this.world, [1, 0, 0])) : [1, 0, 0];
        const up = d.alignment === 0 ? [view[1], view[5], view[9]] : d.alignment === 2 ? normalize(direction(this.world, [0, 1, 0])) : [0, 1, 0];
        const normal = [right[1] * up[2] - right[2] * up[1], right[2] * up[0] - right[0] * up[2], right[0] * up[1] - right[1] * up[0]];
        const mesh = d.renderMode === 4 ? this.mesh?.(d.mesh) : null;
        // Local alignment turns a mesh with its emitter, as Unity does: the frame lines' emitters face the
        // camera through a half turn, without which their triangles were seen from behind and culled.
        const axes = mesh && d.alignment === 2 ? [[1, 0, 0], [0, 1, 0], [0, 0, 1]].map(axis => normalize(direction(this.world, axis))) : null;
        const scale = this.config.scalingMode === 0 ? Math.hypot(this.world[0], this.world[1], this.world[2]) : 1;
        let vertices = 0;
        let center = [0, 0, 0];
        const put = (position, n, tint, custom1, custom2, u, v) => {
            arrays.position.push(position[0], position[1], position[2]);
            arrays.normal.push(n[0], n[1], n[2]);
            arrays.color.push(tint[0], tint[1], tint[2], tint[3]);
            arrays.custom1.push(...custom1);
            arrays.custom2.push(...custom2);
            arrays.texcoord.push(u, v, u, v);
            arrays.center.push(center[0], center[1], center[2]);
            vertices++;
        };
        for (const p of ordered) {
            const u = p.age / p.lifetime, gain = size ? evaluate(size.curve, u, p.randoms[0]) : 1;
            const tint = colors ? color(colors.gradient, u, p.randoms[1]).map((v, i) => v * p.color[i]) : p.color;
            center = this.worldOf(p);
            // SourceImmersiveParticleCanvas: baked UI geometry is read in the Canvas's own units.
            if (geometryScale !== 1) center = [0, 1, 2].map(i => this.world[12 + i] + (center[i] - this.world[12 + i]) * geometryScale);
            const custom1 = this.customData(0, p, u), custom2 = this.customData(1, p, u);
            const extent = [p.size[0] * gain * scale * geometryScale, p.size[1] * gain * scale * geometryScale, p.size[2] * gain * scale * geometryScale];
            if (mesh) {
                const positions = mesh.vertices.position.data, uvs = mesh.vertices.uv0?.data, base = vertices, ca = Math.cos(p.rotation), sa = Math.sin(p.rotation);
                for (let k = 0; k < positions.length / 3; k++) {
                    const x = positions[k * 3] * extent[0], y = positions[k * 3 + 1] * extent[1], z = positions[k * 3 + 2] * extent[2];
                    const local = [ca * x - sa * y, sa * x + ca * y, z];
                    const offset = axes ? [0, 1, 2].map(i => axes[0][i] * local[0] + axes[1][i] * local[1] + axes[2][i] * local[2]) : local;
                    put([0, 1, 2].map(i => center[i] + offset[i]), normal, tint, custom1, custom2, uvs ? uvs[k * 2] : 0, uvs ? uvs[k * 2 + 1] : 0);
                }
                for (const index of mesh.indices) arrays.indices.push(base + index);
                continue;
            }
            const ca = Math.cos(-p.rotation), sa = Math.sin(-p.rotation), base = vertices;
            for (const [qx, qy] of [[-.5, -.5], [.5, -.5], [.5, .5], [-.5, .5]]) {
                const x = qx * extent[0], y = qy * extent[1], rx = ca * x - sa * y, ry = sa * x + ca * y;
                put([0, 1, 2].map(i => center[i] + right[i] * rx + up[i] * ry), normal, tint, custom1, custom2, qx + .5, qy + .5);
            }
            arrays.indices.push(base, base + 2, base + 1, base, base + 3, base + 2);
        }
    }
}

// A scene: its node tree and systems, with world matrices recomputed from a root matrix.
export class ParticleScene {
    constructor(data, mesh) {
        this.data = data;
        this.systems = [];
        this.byPath = new Map();
        const visit = (node, path) => {
            this.byPath.set(path.join('/'), node);
            node.override = null;
            if (node.system && node.system.material !== null && node.system.material !== undefined) {
                node.particle = new ParticleSystem(node, mesh);
                this.systems.push(node.particle);
            }
            node.children.forEach((child, i) => visit(child, [...path, i]));
        };
        visit(data.root, []);
        const byId = new Map(this.systems.map(s => [s.definition.id, s]));
        for (const system of this.systems) for (const sub of system.config.SubModule?.subEmitters ?? []) {
            const target = byId.get(String(sub.emitter));
            if (target) { system.subs.push({ type: sub.type, probability: sub.emitProbability, system: target }); target.isSub = true; target.playing = true; }
        }
    }

    node(indices) { return this.byPath.get(indices.join('/')); }

    systemsBelow(indices) {
        const out = [], visit = n => { if (n.particle) out.push(n.particle); n.children.forEach(visit); };
        const node = this.node(indices);
        if (node) visit(node);
        return out;
    }

    step(delta, root) {
        const visit = (node, parent) => {
            const local = node.override ? Object.assign(new Float32Array(node.matrix), { 12: node.override[0], 13: node.override[1], 14: node.override[2] }) : node.matrix;
            const world = multiply(parent, local);
            if (node.particle) node.particle.step(delta, world);
            for (const child of node.children) visit(child, world);
        };
        visit(this.data.root, root);
    }

    get active() { return this.systems.some(s => s.playing && (s.emitting || s.particles.length)); }

    clear() { for (const s of this.systems) s.stop(true); }
}
