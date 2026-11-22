// Port of SourceCardTouch.Motion (TouchStateRotation): the card follows the finger up
// to its tilt limit, then eases back to the front when released.
const DEGREES = 180 / Math.PI;

const multiply = ([ax, ay, az, aw], [bx, by, bz, bw]) => [
    aw * bx + ax * bw + ay * bz - az * by,
    aw * by - ax * bz + ay * bw + az * bx,
    aw * bz + ax * by - ay * bx + az * bw,
    aw * bw - ax * bx - ay * by - az * bz];

const angleAxis = (degrees, [x, y, z]) => {
    const half = degrees / DEGREES / 2, s = Math.sin(half);
    return [x * s, y * s, z * s, Math.cos(half)];
};

// Unity's Quaternion.Euler order: Y, then X, then Z.
const euler = (x, y) => multiply(angleAxis(y, [0, 1, 0]), angleAxis(x, [1, 0, 0]));

const eulerXY = ([x, y, z, w]) => {
    const sinX = 2 * (w * x - y * z);
    if (Math.abs(sinX) > 0.9999) return [Math.sign(sinX) * 90, 2 * Math.atan2(y, w) * DEGREES];
    return [Math.asin(sinX) * DEGREES, Math.atan2(2 * (x * z + w * y), 1 - 2 * (x * x + y * y)) * DEGREES];
};

const angleFromIdentity = q => 2 * Math.acos(Math.min(Math.abs(q[3]), 1)) * DEGREES;

const slerpFromIdentity = (q, t) => {
    const w = q[3] < 0 ? q.map(v => -v) : q;
    const angle = Math.acos(Math.min(w[3], 1));
    if (angle < 1e-5) return [0, 0, 0, 1];
    const a = Math.sin((1 - t) * angle) / Math.sin(angle), b = Math.sin(t * angle) / Math.sin(angle);
    return [w[0] * b, w[1] * b, w[2] * b, a + w[3] * b];
};

const clamp = value => Math.max(-1, Math.min(1, value));

// A resting pose, in degrees: turned to the side, then up or down.
export const restingPose = (horizontal, vertical) => euler(vertical, horizontal);
export const combine = multiply;

export class CardTouch {
    constructor(maxTilt) {
        this.maxTilt = maxTilt;
        this.rotation = [0, 0, 0, 1];
        this.dragging = false;
        this.previous = null;
        this.pointer = null;
    }

    begin(point, now) {
        this.previous = point;
        this.pointer = null;
        this.movement = [0, 0];
        this.started = now;
        this.dragging = true;
    }

    // Unity samples the pointer once per frame: only the latest position counts.
    move(point, size) {
        if (this.dragging) this.pointer = { point, size };
    }

    // Motion.End: a short, nearly horizontal swipe flips the card. Returns its direction, or null.
    end(now) {
        if (!this.dragging) return null;
        this.dragging = false;
        const [x, y] = this.movement;
        let angle = Math.abs(Math.atan2(y, x)) * DEGREES;
        if (angle > 90) angle = 180 - angle;
        return (now - this.started) / 1000 <= .2 && angle * .5 <= 60 * .5 && x * x + y * y >= 90 * 90 ? x <= 0 : null;
    }

    tick(deltaTime) {
        if (!this.dragging) {
            this.rotation = slerpFromIdentity(this.rotation, Math.max(0, 1 - Math.min(1, deltaTime * 10)));
        } else if (this.pointer) {
            const { point, size } = this.pointer;
            const yaw = (Math.acos(clamp(point[0] / (size[0] / 2))) - Math.acos(clamp(this.previous[0] / (size[0] / 2)))) * DEGREES;
            const pitch = (Math.acos(clamp(point[1] / (size[1] / 2))) - Math.acos(clamp(this.previous[1] / (size[1] / 2)))) * DEGREES;
            const pending = multiply(angleAxis(yaw, [0, 1, 0]), angleAxis(pitch, [-1, 0, 0]));
            const [x, y] = eulerXY(multiply(this.rotation, pending));
            const target = euler(x, y);
            const distance = angleFromIdentity(target);
            this.rotation = distance > 0 ? slerpFromIdentity(target, Math.min(this.maxTilt / distance, 1)) : target;
            this.movement[0] += point[0] - this.previous[0];
            this.movement[1] += point[1] - this.previous[1];
            this.previous = point;
            this.pointer = null;
        }
    }

    reset() {
        this.rotation = [0, 0, 0, 1];
        this.dragging = false;
        this.pointer = null;
    }

    get moving() {
        return this.dragging || angleFromIdentity(this.rotation) > .01;
    }
}

// GyroManager and RotateGyro: the device's turn rate pushes the card, which returns once the device rests.
// Constructor values of 1.8.1; the game's own instance and where it activates it are not known.
const ROTATION_POWER = .35, MAX_ROTATION = 30, RATE_THRESHOLD = .5, TIME_STEP = .0005, WAITING_TIME = .3;

export class CardGyro {
    constructor() {
        this.rotation = [0, 0, 0, 1];
        this.waiting = 0;
        this.rate = [0, 0];
        this.listener = event => {
            const r = event.rotationRate;
            if (r) this.rate = [(r.beta ?? 0) / DEGREES, (r.gamma ?? 0) / DEGREES];
        };
    }

    async start() {
        if (typeof DeviceMotionEvent === 'undefined') return false;
        if (DeviceMotionEvent.requestPermission && await DeviceMotionEvent.requestPermission() !== 'granted') return false;
        addEventListener('devicemotion', this.listener);
        return true;
    }

    stop() {
        removeEventListener('devicemotion', this.listener);
    }

    tick(deltaTime) {
        const [x, y] = this.rate;
        if (Math.abs(x) < RATE_THRESHOLD && Math.abs(y) < RATE_THRESHOLD) {
            this.waiting += deltaTime;
            if (this.waiting > WAITING_TIME) this.rotation = slerpToward(this.rotation, Math.max(0, Math.min(1, this.waiting + TIME_STEP)));
        }
        // The native code multiplies the rate by the power without the frame's duration.
        const push = multiply(angleAxis(-y * ROTATION_POWER, [0, 1, 0]), angleAxis(-x * ROTATION_POWER, [1, 0, 0]));
        const [ex, ey] = eulerXY(multiply(this.rotation, push));
        const limit = v => Math.max(-MAX_ROTATION, Math.min(MAX_ROTATION, v));
        this.rotation = euler(limit(ex), limit(ey));
        // RotateGyro: the x angle is negated, the y angle kept.
        const [ox, oy] = eulerXY(this.rotation);
        return euler(-ox, oy);
    }
}

const slerpToward = (q, t) => slerpFromIdentity(q, 1 - t);
