// The IM sequence (SourceImmersivePlayer): hold, preparation, start, movie, end, with the original controller curves and particle schedule.
const IDLE = 'idle', HOLDING = 'holding', PREPARATION = 'preparation', STARTING = 'starting', MOVIE = 'movie', ENDING = 'ending', CANCELING = 'canceling';
const MOVE_LIMIT = 12;
const LOGICAL = [630, 880];

const sample = (curve, time) => {
    let key = curve.keys[0];
    for (const next of curve.keys) { if (next.time > time) break; key = next; }
    const dt = Math.max(0, time - key.time);
    return ((key.a * dt + key.b) * dt + key.c) * dt + key.d;
};

export class ImmersivePlayer {
    constructor(element, renderer, plan, base) {
        Object.assign(this, { element, renderer, plan, phase: IDLE, hold: 0, startTime: 0, endTime: 0, cancelTime: 0, scale: 1, paused: false });
        this.state = renderer.controllers.find(c => c.settings.kind === 'CardImmersiveObject');
        this.scheduled = new Set();
        this.running = new Map();
        const root = element.shadowRoot;
        this.mask = document.createElement('div');
        this.mask.style.cssText = 'position:absolute;left:50%;top:50%;transform:translate(-50%,-50%);overflow:hidden;display:none;z-index:0;pointer-events:none';
        this.video = document.createElement('video');
        this.video.src = `${base}${plan.file}`;
        this.video.preload = 'none';
        this.video.playsInline = true;
        this.video.style.cssText = 'position:absolute;max-width:none';
        // The movie's RawImage: a fixed rectangle showing only the uv crop of the padded H.264 frame,
        // nothing beyond it, whatever the size of the mask around it.
        this.frame = document.createElement('div');
        this.frame.style.cssText = 'position:absolute;left:50%;top:50%;transform:translate(-50%,-50%);overflow:hidden';
        this.frame.append(this.video);
        this.mask.append(this.frame);
        root.prepend(this.mask);
        this.controls = document.createElement('div');
        this.controls.style.cssText = 'position:absolute;left:0;right:0;bottom:-48px;display:none;justify-content:space-between;z-index:3';
        const button = (label, action) => {
            const b = document.createElement('button');
            b.textContent = label;
            b.addEventListener('pointerdown', event => event.stopPropagation());
            b.addEventListener('click', action);
            return b;
        };
        this.pauseButton = button('Pause', () => this.togglePause());
        this.controls.append(this.pauseButton, button('Skip', () => this.skip()));
        root.append(this.controls);
        this.video.addEventListener('ended', () => { if (this.phase === MOVIE) this.beginEnd(); });
    }

    get busy() { return this.phase !== IDLE; }

    // True consumes the pointer: no rotation for it.
    down(point) {
        if (this.phase === STARTING || this.phase === MOVIE || this.phase === ENDING) return true;
        if (this.renderer.flipped) return false;
        this.pressPoint = point;
        this.hold = 0;
        this.phase = HOLDING;
        this.video.preload = 'auto';
        this.element.wake();
        return false;
    }

    move(point) {
        if (this.phase !== HOLDING && this.phase !== PREPARATION) return this.phase === STARTING || this.phase === MOVIE || this.phase === ENDING;
        if ((point[0] - this.pressPoint[0]) ** 2 + (point[1] - this.pressPoint[1]) ** 2 > MOVE_LIMIT ** 2) { this.cancel(true); return false; }
        return this.phase === PREPARATION;
    }

    up() {
        if (this.phase === STARTING || this.phase === MOVIE || this.phase === ENDING) return true;
        this.cancel(false);
        return false;
    }

    systemsIn(name) {
        const out = [], visit = node => { if (node.name === name) out.push(node); node.children.forEach(visit); };
        for (const scene of [this.renderer.uiScene, this.renderer.modelScene]) if (scene) visit(scene.scene.data.root);
        return out;
    }

    beginPreparation() {
        this.phase = PREPARATION;
        const [tap] = this.systemsIn('efp_OUT_IMS_TapPower00');
        if (tap) tap.override = this.renderer.uiPoint(this.pressPoint, tap);
        this.preparation = [];
        for (const name of ['PreparationOutside', 'PreparationInner']) for (const node of this.systemsIn(name)) {
            const visit = n => { if (n.particle) this.preparation.push(n.particle); n.children.forEach(visit); };
            visit(node);
        }
        for (const system of this.preparation) system.play();
    }

    cancel(moved) {
        if (this.phase === PREPARATION) {
            this.cancelTime = 0;
            this.canceledByMovement = moved;
            this.phase = CANCELING;
            for (const system of this.preparation) system.stop(false);
        } else if (this.phase === HOLDING) this.phase = IDLE;
    }

    beginPlayback() {
        this.element.touch?.reset();
        this.startTime = 0;
        this.movieStarted = false;
        this.scheduled.clear();
        this.running.clear();
        for (const system of this.preparation ?? []) system.stop(false);
        this.video.currentTime = 0;
        this.mask.style.display = 'block';
        this.renderer.setZoom(true);
        this.phase = STARTING;
        this.evaluate(this.plan.start, 0);
    }

    togglePause() {
        if (this.phase !== MOVIE) return;
        this.paused = !this.paused;
        if (this.paused) this.video.pause(); else this.video.play();
        this.pauseButton.textContent = this.paused ? 'Resume' : 'Pause';
    }

    skip() {
        if (this.phase !== STARTING && this.phase !== MOVIE) return;
        this.paused = false;
        this.video.pause();
        this.beginEnd(false);
    }

    get endMovieTime() { return Math.max(0, (this.video.duration || 0) - this.plan.endFramesRemaining / 24); }

    beginEnd(natural = true) {
        if (this.phase === ENDING) return;
        this.paused = false;
        this.controls.style.display = 'none';
        this.endTime = natural ? Math.max(0, this.video.currentTime - this.endMovieTime) : 0;
        this.scheduled.clear();
        this.running.clear();
        this.clearParticles();
        this.renderer.hideCard = false;
        this.phase = ENDING;
        this.evaluate(this.plan.end, this.endTime);
    }

    clearParticles() {
        this.renderer.uiScene?.scene.clear();
        this.renderer.modelScene?.scene.clear();
    }

    restore() {
        this.scale = 1;
        Object.assign(this.state, { IllustAlpha: 1, TextAlpha: 1, EmitIntensity: 1, EmitSwitch: 0, Darkness: 0 });
        this.mask.style.display = 'none';
        this.controls.style.display = 'none';
        this.renderer.hideCard = false;
        this.renderer.cardScale = 1;
        this.renderer.setZoom(false);
        this.applyMask();
    }

    assign(field, value) {
        if (field === 'CardScale') this.scale = value;
        else this.state[{ CardIllustAlpha: 'IllustAlpha', CardTextAlpha: 'TextAlpha', CardFrameEmitSwitch: 'EmitSwitch', CardFrameEmitIntensity: 'EmitIntensity', CardFrameDarkness: 'Darkness' }[field]] = value;
    }

    evaluate(clip, time) {
        for (const curve of clip.curves) this.assign(curve.field, sample(curve, time));
        for (const constant of clip.constants) this.assign(constant.field, constant.value);
        this.renderer.cardScale = this.scale;
        this.applyMask();
    }

    applyMask() {
        const box = this.element.getBoundingClientRect(), unit = box.height / LOGICAL[1], s = Math.max(this.scale - .01, .001);
        // SetupImmersive's rectangle is fixed; the H.264 frame is padded on the right and at the bottom, and
        // its uv rectangle (Unity's, v from the bottom) is the crop: placed so, the padding stays outside.
        const [rectX, rectY] = this.plan.rectSize, [u, v, uvW, uvH] = this.plan.uv;
        const width = rectX * unit / uvW, height = rectY * unit / uvH;
        Object.assign(this.mask.style, { width: `${LOGICAL[0] * s * unit}px`, height: `${LOGICAL[1] * s * unit}px`, borderRadius: `${24 * s * unit}px` });
        Object.assign(this.frame.style, { width: `${rectX * unit}px`, height: `${rectY * unit}px` });
        Object.assign(this.video.style, { width: `${width}px`, height: `${height}px`, left: `${-u * width}px`, top: `${-(1 - v - uvH) * height}px` });
    }

    schedule(entries, time) {
        const sceneOf = entry => (entry.root === 'ui' ? this.renderer.uiScene : this.renderer.modelScene)?.scene;
        for (const entry of entries) {
            const key = `${entry.root}/${entry.indices.join('/')}/${entry.start}`;
            if (time >= entry.start + entry.duration && this.running.has(key)) {
                for (const system of this.running.get(key)) system.stop(false);
                this.running.delete(key);
            }
            if (time < entry.start || this.scheduled.has(key)) continue;
            this.scheduled.add(key);
            const systems = sceneOf(entry)?.systemsBelow(entry.indices) ?? [];
            for (const system of systems) { system.seed(entry.seed); system.play(); }
            this.running.set(key, systems);
        }
    }

    effects(time) {
        for (const animation of this.plan.effectAnimations) {
            if (time < animation.start) continue;
            const local = Math.min(time - animation.start, animation.length), scene = animation.root === 'ui' ? this.renderer.uiScene : this.renderer.modelScene;
            const node = scene?.scene.node(animation.indices);
            if (node) node.override = animation.curves.map(curve => sample(curve, local));
        }
    }

    // One update; true while the sequence needs frames.
    tick(delta) {
        const p = this.plan;
        if (this.phase === HOLDING || this.phase === PREPARATION) {
            this.hold += delta;
            if (this.phase === HOLDING && this.hold >= p.holdStart) this.beginPreparation();
            if (this.phase === PREPARATION && this.hold >= p.holdStart + p.holdDuration) this.beginPlayback();
        }
        if (this.phase === CANCELING) {
            this.cancelTime += delta;
            if (this.cancelTime >= p.cancelDuration) { this.clearParticles(); this.restore(); if (!this.canceledByMovement) this.element.touch?.reset(); this.phase = IDLE; }
        }
        if (this.phase === STARTING) {
            if (!this.movieStarted) this.startTime = Math.min(this.startTime + delta, p.movieStart);
            else this.startTime = p.movieStart + this.video.currentTime;
            this.evaluate(p.start, this.startTime);
            this.effects(this.startTime);
            this.schedule(p.startParticles, this.startTime);
            if (!this.movieStarted && this.startTime >= p.movieStart) {
                this.movieStarted = true;
                this.video.play().catch(() => this.beginEnd(false));
            }
            if (this.startTime >= p.start.duration) {
                this.renderer.hideCard = true;
                this.controls.style.display = 'flex';
                this.phase = MOVIE;
            }
        }
        if (this.phase === MOVIE && !this.paused) {
            this.startTime += delta;
            this.schedule(p.startParticles, this.startTime);
            if (this.video.duration && this.video.currentTime >= this.endMovieTime) this.beginEnd();
        }
        if (this.phase === ENDING) {
            this.endTime = this.video.paused ? this.endTime + delta : Math.max(0, this.video.currentTime - this.endMovieTime);
            this.evaluate(p.end, this.endTime);
            this.schedule(p.endParticles, this.endTime);
            if (this.endTime >= p.end.duration) {
                this.video.pause();
                this.clearParticles();
                this.restore();
                this.element.touch?.reset();
                this.phase = IDLE;
            }
        }
        return this.busy;
    }
}
