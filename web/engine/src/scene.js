// Draws a particle scene: materials, gradients made as GradationMapComponent makes them, streaming geometry.
import { ParticleScene } from './particles.js';

const CHANNEL_SIZE = { position: 3, normal: 3, color: 4 };
// ParticleSystemVertexStream values that take a TEXCOORD: channel, first component, size.
const STREAM_CHANNEL = { 4: ['texcoord', 0, 2], 5: ['texcoord', 2, 2], 34: ['custom1', 0, 4], 38: ['custom2', 0, 4], 10: ['center', 0, 3] };
const SOURCE_SIZE = { texcoord: 4, custom1: 4, custom2: 4, center: 3 };
// Unity packs these streams without gaps, four components per TEXCOORD: UV.xy Custom1.xy | Custom1.zw Custom2.xy | Custom2.zw.
const packedLayout = streams => streams.filter(s => s in STREAM_CHANNEL).map(s => STREAM_CHANNEL[s]);
const packedSize = layout => layout.reduce((sum, [, , size]) => sum + size, 0);
const packStreams = (arrays, layout) => {
    const count = arrays.position.length / 3, stride = packedSize(layout), out = new Float32Array(count * stride);
    let offset = 0;
    for (const [channel, first, size] of layout) {
        const source = arrays[channel], step = SOURCE_SIZE[channel];
        for (let v = 0; v < count; v++) for (let c = 0; c < size; c++) out[v * stride + offset + c] = source[v * step + first + c];
        offset += size;
    }
    return out;
};

const gradientColor = (g, t) => {
    const pick = (keys, read, size) => {
        if (t <= keys[0].time) return read(keys[0]);
        for (let i = 1; i < keys.length; i++) if (t <= keys[i].time) {
            if (g.mode === 1) return read(keys[i]);
            const f = (t - keys[i - 1].time) / Math.max(keys[i].time - keys[i - 1].time, 1e-6), a = read(keys[i - 1]), b = read(keys[i]);
            return a.map((v, k) => v + (b[k] - v) * f);
        }
        return read(keys[keys.length - 1]);
    };
    const [r, gg, b] = pick(g.colors, k => [k.color.r, k.color.g, k.color.b]);
    const [a] = pick(g.alphas, k => [k.alpha]);
    return [r, gg, b, a];
};

export class SceneRenderer {
    constructor(renderer, data, images) {
        this.renderer = renderer;
        const gl = this.gl = renderer.gl;
        this.programs = data.programs.map(row => ({ ...renderer.reflect(renderer.linkProgram(row.vertex, row.fragment)), attributes: row.attributes, emits: row.fragment.includes('layout(location = 1)') }));
        this.textures = data.textures.map((row, i) => renderer.uploadTexture(row, images[i]));
        this.scene = new ParticleScene(data, index => (index === null || index === undefined ? null : data.meshes[index]));
        this.materials = data.materials.map(material => this.prepareMaterial(material));
        this.buffers = Object.fromEntries([...Object.keys(CHANNEL_SIZE), 'packed'].map(channel => [channel, gl.createBuffer()]));
        this.indexBuffer = gl.createBuffer();
    }

    prepareMaterial(material) {
        const gl = this.gl, renderer = this.renderer, textures = {};
        for (const [name, value] of Object.entries(material.textures)) textures[name] = typeof value === 'number' ? this.textures[value] : renderer.defaultTexture(value);
        for (const [name, gradient] of Object.entries(material.gradients)) {
            const rows = gradient.keys.length, width = gradient.resolution, pixels = new Uint8Array(width * rows * 4);
            gradient.keys.forEach((keys, y) => {
                for (let x = 0; x < width; x++) pixels.set(gradientColor(keys, x / width).map(v => Math.round(Math.max(0, Math.min(1, v)) * 255)), (y * width + x) * 4);
            });
            const texture = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, texture);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, width, rows, 0, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
            const filter = gradient.filter > 0 ? gl.LINEAR : gl.NEAREST;
            for (const [p, v] of [[gl.TEXTURE_MIN_FILTER, filter], [gl.TEXTURE_MAG_FILTER, filter], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) gl.texParameteri(gl.TEXTURE_2D, p, v);
            textures[name] = { texture, target: gl.TEXTURE_2D };
        }
        const program = this.programs[material.program], vao = gl.createVertexArray();
        return { ...material, textures, reflected: program, vao, wired: false, layout: packedLayout(material.streams) };
    }

    dispose() {
        const gl = this.gl;
        for (const program of this.programs) gl.deleteProgram(program.program);
        for (const { texture } of this.textures) gl.deleteTexture(texture);
        for (const material of this.materials) {
            gl.deleteVertexArray(material.vao);
            for (const [name, { texture }] of Object.entries(material.textures)) if (name in material.gradients) gl.deleteTexture(texture);
        }
        for (const buffer of [...Object.values(this.buffers), this.indexBuffer]) gl.deleteBuffer(buffer);
    }

    wire(material) {
        const gl = this.gl;
        gl.bindVertexArray(material.vao);
        const stride = packedSize(material.layout);
        for (const [location, attribute] of Object.entries(material.reflected.attributes)) {
            const packed = attribute.startsWith('uv'), start = packed ? +attribute.slice(2) * 4 : 0;
            if (packed && start >= stride) continue;
            gl.bindBuffer(gl.ARRAY_BUFFER, this.buffers[packed ? 'packed' : attribute]);
            gl.enableVertexAttribArray(+location);
            if (packed) gl.vertexAttribPointer(+location, Math.min(4, stride - start), gl.FLOAT, false, stride * 4, start * 4);
            else gl.vertexAttribPointer(+location, CHANNEL_SIZE[attribute], gl.FLOAT, false, 0, 0);
        }
        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.indexBuffer);
        material.wired = true;
    }

    // Queue first, then the renderer's sorting fudge: a larger fudge draws earlier.
    draw(globals, view, fromQueue = -Infinity, toQueue = Infinity, twoTargets = false, coverage = false) {
        const gl = this.gl, renderer = this.renderer, identity = renderer.identity;
        const systems = this.scene.systems.filter(s => {
            if (!s.particles.length || s.definition.material === null) return false;
            const queue = this.materials[s.definition.material].queue;
            return queue > fromQueue && queue <= toQueue;
        });
        systems.sort((a, b) => this.materials[a.definition.material].queue - this.materials[b.definition.material].queue || b.definition.fudge - a.definition.fudge);
        for (const system of systems) {
            const material = this.materials[system.definition.material];
            const arrays = { position: [], normal: [], color: [], custom1: [], custom2: [], texcoord: [], center: [], indices: [] };
            system.build(arrays, view, material.canvasGeometry ? this.renderer.uiWorldPerLogical * material.uiScale : 1);
            if (!arrays.indices.length) continue;
            if (!material.wired) this.wire(material);
            gl.bindVertexArray(material.vao);
            for (const channel of Object.keys(CHANNEL_SIZE)) {
                gl.bindBuffer(gl.ARRAY_BUFFER, this.buffers[channel]);
                gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(arrays[channel]), gl.STREAM_DRAW);
            }
            gl.bindBuffer(gl.ARRAY_BUFFER, this.buffers.packed);
            gl.bufferData(gl.ARRAY_BUFFER, packStreams(arrays, material.layout), gl.STREAM_DRAW);
            gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint32Array(arrays.indices), gl.STREAM_DRAW);
            const { program, uniforms, samplers } = material.reflected;
            gl.useProgram(program);
            // WebGL refuses a draw when an attachment has no matching shader output.
            if (twoTargets) gl.drawBuffers(material.reflected.emits ? [gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1] : [gl.COLOR_ATTACHMENT0, gl.NONE]);
            // The interface layer is composited later: its alpha has to hold what the particles cover.
            // An additive particle (destination factor One) covers nothing: it only adds light, as over
            // the game's opaque screen. Counted as cover, it dimmed the page and its glows turned dark blue.
            const [source, destination] = material.state.blend;
            renderer.applyState(coverage ? { ...material.state, blend: [source, destination, ...(destination === 1 ? [0, 1] : [1, 10])] } : material.state);
            if (uniforms.unity_ObjectToWorld) renderer.setUniform(uniforms.unity_ObjectToWorld, identity);
            if (uniforms.unity_WorldToObject) renderer.setUniform(uniforms.unity_WorldToObject, identity);
            for (const [name, value] of Object.entries(globals)) if (uniforms[name]) renderer.setUniform(uniforms[name], value);
            for (const [name, value] of Object.entries(material.uniforms)) if (uniforms[name]) renderer.setUniform(uniforms[name], value);
            let unit = 0;
            for (const [name, { texture, target }] of Object.entries(material.textures)) {
                if (!samplers[name]) continue;
                gl.activeTexture(gl.TEXTURE0 + unit);
                gl.bindTexture(target, texture);
                gl.uniform1i(samplers[name], unit++);
            }
            gl.drawElements(gl.TRIANGLES, arrays.indices.length, gl.UNSIGNED_INT, 0);
        }
        gl.bindVertexArray(null);
    }
}
