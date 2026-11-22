// One WebGL2 context for every card of the page. Browsers keep about 16 contexts, and each
// would hold its own copy of the frames, the programs and a render target: here a program,
// a texture or a mesh exists once, under its file, for as long as one card uses it.
export class SharedGpu {
    static get() {
        return SharedGpu.instance ??= new SharedGpu();
    }

    // With a canvas: a context of its own, for a card shown alone, which draws straight into it.
    constructor(canvas) {
        this.dedicated = !!canvas;
        this.canvas = canvas ?? document.createElement('canvas');
        if (!canvas) this.canvas.width = this.canvas.height = 1;
        const gl = this.gl = this.canvas.getContext('webgl2', { alpha: true, premultipliedAlpha: true, antialias: false, depth: false, stencil: false, powerPreference: 'high-performance' });
        if (!gl) throw new Error('WebGL2 indisponible');
        this.entries = new Map();
        this.defaults = {};
        this.emptyVao = gl.createVertexArray();
    }

    // Made by the first user, disposed of after the last. The value may be a promise.
    acquire(key, make, dispose) {
        let entry = this.entries.get(key);
        if (!entry) {
            entry = { value: make(), users: 0 };
            this.entries.set(key, entry);
        }
        entry.users++;
        let released = false;
        return {
            value: entry.value,
            release: () => {
                if (released) return;
                released = true;
                if (--entry.users > 0) return;
                this.entries.delete(key);
                Promise.resolve(entry.value).then(value => dispose?.(value), () => {});
            },
        };
    }

    // The drawing buffer only grows: a card draws in its bottom-left corner, then copies it out.
    reserve(width, height) {
        if (this.dedicated) return;
        if (this.canvas.width < width) this.canvas.width = width;
        if (this.canvas.height < height) this.canvas.height = height;
    }
}
