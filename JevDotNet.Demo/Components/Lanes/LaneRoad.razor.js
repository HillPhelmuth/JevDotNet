// Match the rendered widths, but collide only with the front half of the car.
// The car faces up: its front half spans center - 40px through center.
const COLLISION_X = (23 + 38) / 120;
const COLLISION_FRONT = 1 - (40 + 11) / 400;
const COLLISION_BACK = 1 + 11 / 400;
// Coordinates use lane centers 1..3 and road travel 0..1 (car at 1).
export class LaneSimulation {
    constructor(random = Math.random) { this.random = random; this.reset(0, 3); }
    reset(sessionId, seconds) {
        this.sessionId = sessionId; this.seconds = seconds; this.running = false;
        this.crashed = false; this.carLane = 2; this.elapsedMs = 0; this.passed = 0;
        this.rows = []; this.nextSpawn = 0; this.move = null;
    }
    spawn() {
        const open = 1 + Math.floor(this.random() * 3);
        const blocked = [1, 2, 3].filter(l => l !== open);
        if (this.random() < 0.5) blocked.splice(Math.floor(this.random() * 2), 1);
        this.rows.push({ y: 0, lanes: blocked, counted: false });
    }
    start(id) { this.sessionId = id; this.running = true; }
    steer(id, lane) {
        if (id !== this.sessionId || !this.running || this.crashed || ![1, 2, 3].includes(lane)) return false;
        // Repeated choices do not restart an unfinished transition.
        if (this.move?.to !== lane && (!this.move || this.carLane !== lane))
            this.move = { from: this.carLane, to: lane, elapsed: 0 };
        return true;
    }
    advance(ms) {
        // Swept updates in <= 10ms segments include spawning and lateral crossings.
        while (ms > 0 && this.running) {
            const dt = Math.min(ms, 10); ms -= dt;
            if (this.elapsedMs >= this.nextSpawn) { this.spawn(); this.nextSpawn += this.seconds * 500; }
            const oldX = this.carLane;
            if (this.move) {
                this.move.elapsed += dt;
                this.carLane = this.move.from + (this.move.to - this.move.from) * Math.min(1, this.move.elapsed / 200);
                if (this.move.elapsed >= 200) this.move = null;
            }
            for (const row of this.rows) {
                const oldY = row.y; row.y += dt / (this.seconds * 1000);
                if (oldY <= COLLISION_BACK && row.y >= COLLISION_FRONT && row.lanes.some(l =>
                    Math.min(oldX, this.carLane) <= l + COLLISION_X && Math.max(oldX, this.carLane) >= l - COLLISION_X)) {
                    this.crashed = true; this.running = false; break;
                }
                if (!row.counted && row.y > COLLISION_BACK) { row.counted = true; this.passed += row.lanes.length; }
            }
            this.elapsedMs += dt;
            this.rows = this.rows.filter(r => r.y < 1.25);
        }
        return this.snapshot();
    }
    snapshot() {
        return { sessionId: this.sessionId, running: this.running, crashed: this.crashed,
            carLane: this.carLane, passed: this.passed, elapsedMs: this.elapsedMs,
            obstacles: this.rows.filter(r => r.y <= COLLISION_BACK).flatMap(r => r.lanes.map(lane =>
                ({ lane, timeToCollisionMs: Math.max(0, (COLLISION_FRONT - r.y) * this.seconds * 1000) }))) };
    }
}

const ON_EVENT = 'OnRoadEvent';
export function initialize(canvas, reference) {
    const sim = new LaneSimulation();
    const ctx = canvas.getContext('2d');
    const reduced = matchMedia('(prefers-reduced-motion: reduce)');
    let frame, last = performance.now(), notified = false, disposed = false;
    async function notify(reason) {
        try { await reference.invokeMethodAsync(ON_EVENT, { sessionId: sim.sessionId, reason, snapshot: sim.snapshot() }); }
        catch { sim.running = false; }
    }
    function draw() {
        const w = canvas.width, h = canvas.height, laneWidth = w / 3;
        ctx.clearRect(0, 0, w, h); ctx.fillStyle = '#edf1f9'; ctx.fillRect(0, 0, w, h);
        ctx.strokeStyle = '#b9c3d6'; ctx.lineWidth = 2; ctx.setLineDash([24, 30]);
        ctx.lineDashOffset = reduced.matches ? 0 : -sim.elapsedMs / 15;
        for (let i = 1; i <= 2; i++) { ctx.beginPath(); ctx.moveTo(i * laneWidth, 50); ctx.lineTo(i * laneWidth, h); ctx.stroke(); }
        ctx.setLineDash([]); ctx.fillStyle = '#6c7689'; ctx.font = '18px sans-serif'; ctx.textAlign = 'center';
        for (let l = 1; l <= 3; l++) ctx.fillText(l, (l - .5) * laneWidth, 30);
        for (const row of sim.rows) for (const l of row.lanes) {
            const x = (l - .5) * laneWidth, y = 55 + row.y * (h - 140);
            ctx.fillStyle = '#e0aa45'; ctx.fillRect(x - 38, y - 11, 76, 22);
            ctx.save(); ctx.beginPath(); ctx.rect(x - 34, y - 8, 68, 16); ctx.clip();
            ctx.strokeStyle = '#614b37'; ctx.lineWidth = 9;
            for (let j = -40; j < 50; j += 20) { ctx.beginPath(); ctx.moveTo(x + j, y + 10); ctx.lineTo(x + j + 12, y - 10); ctx.stroke(); }
            ctx.restore(); ctx.fillStyle = '#957e59'; ctx.fillRect(x - 29, y + 11, 3, 8); ctx.fillRect(x + 26, y + 11, 3, 8);
        }
        const x = (sim.carLane - .5) * laneWidth, y = h - 85;
        ctx.fillStyle = sim.crashed ? '#dc5656' : '#527ff0'; ctx.beginPath(); ctx.roundRect(x - 23, y - 40, 46, 80, 15); ctx.fill();
        ctx.fillStyle = '#233955'; ctx.beginPath(); ctx.roundRect(x - 16, y - 22, 32, 22, 5); ctx.fill(); ctx.fillRect(x - 15, y + 15, 30, 12);
    }
    function tick(now) {
        const delta = now - last; last = now;
        sim.advance(delta); draw();
        if (sim.crashed && !notified) { notified = true; void notify('crash'); }
        if (!disposed) frame = requestAnimationFrame(tick);
    }
    const hidden = () => { if (document.hidden && sim.running) { sim.running = false; void notify('hidden'); } };
    document.addEventListener('visibilitychange', hidden);
    frame = requestAnimationFrame(tick);
    return {
        reset(id, seconds) { sim.reset(id, seconds); notified = false; last = performance.now(); draw(); return sim.snapshot(); },
        start(id) { if (document.hidden || sim.crashed) return false; last = performance.now(); sim.start(id); return true; },
        pause(id) { if (id === sim.sessionId) sim.running = false; return sim.snapshot(); },
        snapshot() { return sim.snapshot(); },
        steer(id, lane) { return sim.steer(id, lane); },
        dispose() { disposed = true; sim.running = false; cancelAnimationFrame(frame); document.removeEventListener('visibilitychange', hidden); }
    };
}
