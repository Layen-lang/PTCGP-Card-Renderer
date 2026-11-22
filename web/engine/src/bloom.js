// The card-studio sheet bloom: seven original passes and TextureSheetBuilder's geometry (SourceImmersiveBloom, SourceBloomAtlas).
// Source profile: buffer 256, 5 reductions, scatter .5, intensity 1, on a 455 px base at the game's target.
const DEFAULT_BASE = 455, LEVELS = 5, SCATTER = .5, INTENSITY = 1, MAX_LEVELS = 7, MARGIN = 9;
const FLIP_Y = new Float32Array([1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
const IDENTITY = new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);

const buildAtlas = BASE => {
    const size = [BASE + (BASE >> 1) + 4 * MARGIN, BASE + 2 * MARGIN];
    const quad = [[-1, -1], [1, -1], [-1, 1], [1, 1]], uv = [[0, 0], [1, 0], [0, 1], [1, 1]];
    const vertexCount = (MAX_LEVELS + 1) * 4;
    const imagePositions = new Float32Array(vertexCount * 3), imageUvs = new Float32Array(vertexCount * 3);
    const sheetPositions = new Float32Array(vertexCount * 3), sheetUvs = new Float32Array(vertexCount * 3), sheetColors = new Float32Array(vertexCount * 4);
    for (let q = 0; q <= MAX_LEVELS; q++) for (let v = 0; v < 4; v++) {
        const at = q * 4 + v;
        imagePositions.set([quad[v][0], quad[v][1], 0], at * 3);
        sheetPositions.set([quad[v][0], quad[v][1], 0], at * 3);
        imageUvs.set([uv[v][0], uv[v][1], 0], at * 3);
        sheetUvs.set([uv[v][0], uv[v][1], 0], at * 3);
        sheetColors.set(q === 0 ? [0, 0, 0, 0] : [1, 1, 1, 1], at * 4);
    }
    const triangles = [];
    for (let q = 0; q <= LEVELS; q++) { const v = q * 4; triangles.push(v, v + 2, v + 1, v + 2, v + 3, v + 1); }
    let sum = 0;
    for (let i = 0; i < LEVELS; i++) sum += SCATTER ** i;
    const inverseWidth = 1 / size[0], inverseHeight = 1 / size[1], marginX = inverseWidth * MARGIN, marginY = inverseHeight * MARGIN;
    let offsetX = 0, offsetY = 0, width = BASE, height = BASE;
    for (let i = 1; i <= LEVELS; i++) {
        const x = marginX + offsetX, y = marginY + offsetY, extentX = inverseWidth * width, extentY = inverseHeight * height;
        const right = extentX + x, top = y + extentY, weight = INTENSITY * (SCATTER ** (i - 1) / sum);
        for (let v = 0; v < 4; v++) {
            const u = v % 2 === 0 ? x : right, w = v < 2 ? y : top, at = (i * 4 + v) * 3;
            imagePositions.set([u + u - 1, w + w - 1, 0], at);
            imageUvs.set([uv[v][0], uv[v][1], i], at);
            sheetUvs.set([u, w, weight], at);
        }
        if (i & 1) offsetX += marginX + marginX + extentX;
        else offsetY += marginY + marginY + extentY;
        width >>= 1;
        height >>= 1;
    }
    return { size, imagePositions, imageUvs, sheetPositions, sheetUvs, sheetColors, triangles: new Uint16Array(triangles) };
};

export class Bloom {
    constructor(renderer, passes, base = DEFAULT_BASE) {
        const BASE = this.base = base;
        this.renderer = renderer;
        const gl = this.gl = renderer.gl;
        this.programs = passes.map(pass => ({ ...renderer.reflect(renderer.linkProgram(pass.vertex, pass.fragment)), blend: pass.blend, attributes: pass.attributes }));
        // Half floats need this extension, which nearly every WebGL2 device has; bytes clip the glow otherwise.
        this.format = gl.getExtension('EXT_color_buffer_float') || gl.getExtension('EXT_color_buffer_half_float') ? [gl.RGBA16F, gl.RGBA, gl.HALF_FLOAT] : [gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE];
        const atlas = buildAtlas(BASE);
        this.prepared = this.target(BASE * 2, BASE * 2);
        this.levels = Array.from({ length: LEVELS }, (_, i) => this.target(BASE >> i, BASE >> i));
        this.sheet = this.target(...atlas.size);
        this.blur = this.target(...atlas.size);
        this.reconstructed = this.target(BASE, BASE);
        this.image = this.mesh(this.programs[2], { position: [atlas.imagePositions, 3], uv0: [atlas.imageUvs, 3] }, atlas.triangles);
        this.sheetMesh = this.mesh(this.programs[5], { position: [atlas.sheetPositions, 3], uv0: [atlas.sheetUvs, 3], color: [atlas.sheetColors, 4] }, atlas.triangles);
        this.count = atlas.triangles.length;
        this.empty = gl.createVertexArray();
    }

    dispose() {
        const gl = this.gl;
        for (const program of this.programs) gl.deleteProgram(program.program);
        for (const target of [this.prepared, ...this.levels, this.sheet, this.blur, this.reconstructed]) {
            gl.deleteTexture(target.texture);
            gl.deleteFramebuffer(target.framebuffer);
        }
        for (const vao of [this.image, this.sheetMesh, this.empty]) gl.deleteVertexArray(vao);
    }

    target(width, height) {
        const gl = this.gl, texture = gl.createTexture(), framebuffer = gl.createFramebuffer();
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texImage2D(gl.TEXTURE_2D, 0, this.format[0], width, height, 0, this.format[1], this.format[2], null);
        for (const [name, value] of [[gl.TEXTURE_MIN_FILTER, gl.LINEAR], [gl.TEXTURE_MAG_FILTER, gl.LINEAR], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]])
            gl.texParameteri(gl.TEXTURE_2D, name, value);
        gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
        return { texture, framebuffer, width, height };
    }

    mesh(program, channels, indices) {
        const gl = this.gl, vao = gl.createVertexArray();
        gl.bindVertexArray(vao);
        for (const [location, channel] of Object.entries(program.attributes)) {
            const [data, size] = channels[channel];
            gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
            gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
            gl.enableVertexAttribArray(+location);
            gl.vertexAttribPointer(+location, size, gl.FLOAT, false, 0, 0);
        }
        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, indices, gl.STATIC_DRAW);
        gl.bindVertexArray(null);
        return vao;
    }

    draw(index, framebuffer, width, height, textures, uniforms, vao) {
        const gl = this.gl, program = this.programs[index], r = this.renderer;
        gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
        gl.viewport(0, 0, width, height);
        gl.useProgram(program.program);
        const [src, dst, srcAlpha, dstAlpha] = program.blend;
        gl.blendFuncSeparate(gl[r.blendName(src)], gl[r.blendName(dst)], gl[r.blendName(srcAlpha)], gl[r.blendName(dstAlpha)]);
        for (const [name, value] of Object.entries(uniforms)) if (program.uniforms[name]) r.setUniform(program.uniforms[name], value);
        let unit = 0;
        for (const [name, texture] of Object.entries(textures)) {
            if (!program.samplers[name]) continue;
            gl.activeTexture(gl.TEXTURE0 + unit);
            gl.bindTexture(gl.TEXTURE_2D, texture);
            gl.uniform1i(program.samplers[name], unit++);
        }
        if (vao) {
            gl.bindVertexArray(vao);
            gl.drawElements(gl.TRIANGLES, this.count, gl.UNSIGNED_SHORT, 0);
        } else {
            gl.bindVertexArray(this.empty);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
        }
        gl.bindVertexArray(null);
    }

    blit(index, source, destination, property = '_BlitTexture') {
        this.draw(index, destination.framebuffer, destination.width, destination.height, { [property]: source.texture }, {
            _BlitTexture_TexelSize: [1 / source.width, 1 / source.height, source.width, source.height], _BlitScaleBias: [1, 1, 0, 0], _GlobalMipBias: [0, 0] });
    }

    clear(target) {
        const gl = this.gl;
        gl.bindFramebuffer(gl.FRAMEBUFFER, target.framebuffer);
        gl.clearColor(0, 0, 0, 1);
        gl.clear(gl.COLOR_BUFFER_BIT);
    }

    // Adds the glow of the emission target to the card target.
    apply(emission, color, side) {
        const gl = this.gl;
        gl.disable(gl.DEPTH_TEST);
        gl.disable(gl.STENCIL_TEST);
        gl.disable(gl.CULL_FACE);
        gl.enable(gl.BLEND);
        gl.colorMask(true, true, true, true);
        gl.frontFace(gl.CCW);
        this.blit(0, { texture: emission, width: side, height: side }, this.prepared);
        let input = this.prepared;
        const reductions = {};
        this.levels.forEach((level, i) => {
            this.blit(1, input, level);
            input = level;
            reductions[`_DownSampling${i + 1}Tex`] = level.texture;
        });
        const matrices = { unity_ObjectToWorld: IDENTITY, unity_MatrixVP: FLIP_Y, _GlobalMipBias: [0, 0] };
        this.clear(this.sheet);
        this.draw(2, this.sheet.framebuffer, this.sheet.width, this.sheet.height, reductions, matrices, this.image);
        this.blit(3, this.sheet, this.blur);
        this.blit(4, this.blur, this.sheet);
        this.clear(this.reconstructed);
        this.draw(5, this.reconstructed.framebuffer, this.base, this.base, { _MainTex: this.sheet.texture }, matrices, this.sheetMesh);
        this.blit(6, this.reconstructed, { framebuffer: color, width: side, height: side }, '_BloomTex');
        gl.disable(gl.BLEND);
    }
}
