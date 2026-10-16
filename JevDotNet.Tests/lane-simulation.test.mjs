import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import { test } from 'node:test';
const source = await readFile(new URL('../JevDotNet.Demo/Components/Lanes/LaneRoad.razor.js', import.meta.url), 'utf8');
const { LaneSimulation, initialize } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
function random(seed = 17) { return () => ((seed = (seed * 1664525 + 1013904223) >>> 0) / 4294967296); }
test('seeded obstacle rows always leave an open lane', () => {
    const sim = new LaneSimulation(random());
    for (let i = 0; i < 1000; i++) sim.spawn();
    assert(sim.rows.every(r => r.lanes.length >= 1 && r.lanes.length <= 2 && new Set(r.lanes).size === r.lanes.length));
});
test('5/3/2 second speeds, half-interval spawning and large-frame collision', () => {
    for (const seconds of [5, 3, 2]) {
        const sim = new LaneSimulation(() => .99); sim.reset(1, seconds); sim.start(1);
        sim.advance(seconds * 500 + 20); assert.equal(sim.rows.length, 2);
        sim.advance(seconds * 1000); assert.equal(sim.crashed, true);
        assert(Math.abs(sim.elapsedMs - seconds * 1000 * .8725) <= 20);
    }
});
test('safe lane passes barriers without local steering', () => {
    const sim = new LaneSimulation(() => 0); sim.reset(1, 2); sim.start(1); sim.steer(1, 1);
    sim.advance(2300); assert.equal(sim.crashed, false); assert.equal(sim.passed, 1); assert.equal(sim.carLane, 1);
});
test('snapshot timing uses visible collision edges and counts each barrier', () => {
    const sim = new LaneSimulation(() => .99); sim.reset(1, 2); sim.start(1); sim.steer(1, 3);
    sim.advance(10); assert(Math.abs(sim.snapshot().obstacles[0].timeToCollisionMs - 1735) < .001);
    sim.advance(2290); assert.equal(sim.passed, 2); assert.equal(sim.crashed, false);
});
test('lateral movement collides with intermediate-lane barrier', () => {
    const sim = new LaneSimulation(() => 0); sim.reset(1, 5); sim.carLane = 1;
    sim.rows = [{ y: .97, lanes: [2], counted: false }]; sim.nextSpawn = Infinity;
    sim.start(1); sim.steer(1, 3); sim.advance(200); assert.equal(sim.crashed, true);
});
test('lane change ignores a barrier overlapping only the rear half', () => {
    const sim = new LaneSimulation(() => 0); sim.reset(1, 3);
    // Barrier center is 20px below the car center; its top is in the rear half.
    sim.rows = [{ y: 1.05, lanes: [2], counted: false }]; sim.nextSpawn = Infinity;
    assert.equal(sim.snapshot().obstacles.length, 0);
    sim.start(1); sim.steer(1, 3); sim.advance(200);
    assert.equal(sim.crashed, false); assert.equal(sim.carLane, 3); assert.equal(sim.passed, 1);
});
test('front-half overlap still crashes before a late lane change escapes', () => {
    const sim = new LaneSimulation(() => 0); sim.reset(1, 3);
    sim.rows = [{ y: .95, lanes: [2], counted: false }]; sim.nextSpawn = Infinity;
    sim.start(1); sim.steer(1, 3); sim.advance(200);
    assert.equal(sim.crashed, true);
});
test('pause/resume preserves positions and transitions; old responses cannot steer', () => {
    const sim = new LaneSimulation(() => 0); sim.start(1); sim.steer(1, 1); sim.advance(50);
    sim.running = false; const paused = sim.snapshot(); sim.advance(9000); assert.deepEqual(sim.snapshot(), paused);
    sim.start(2); assert.equal(sim.steer(1, 3), false); sim.advance(150); assert.equal(sim.carLane, 1);
    sim.reset(3, 3); assert.equal(sim.steer(2, 3), false); assert.equal(sim.passed, 0); assert.equal(sim.carLane, 2);
});
test('repeated choices do not restart lane transition; crash is terminal', () => {
    const sim = new LaneSimulation(() => 0); sim.start(1); sim.steer(1, 1); sim.advance(100); sim.steer(1, 1); sim.advance(100);
    assert.equal(sim.carLane, 1);
    sim.steer(1, 3); sim.advance(4000); assert.equal(sim.crashed, true);
    assert.equal(sim.steer(1, 1), false); const end = sim.snapshot(); sim.advance(10000); assert.deepEqual(sim.snapshot(), end);
});
test('animation wrapper pauses hidden tabs and cleans up callbacks on disposal', () => {
    const listeners = new Map(), events = []; let tick, canceled = false;
    globalThis.document = { hidden: false, addEventListener: (name, fn) => listeners.set(name, fn), removeEventListener: name => listeners.delete(name) };
    globalThis.matchMedia = () => ({matches:true});
    globalThis.requestAnimationFrame = fn => { tick = fn; return 1; };
    globalThis.cancelAnimationFrame = () => { canceled = true; };
    const context = new Proxy({}, { get: () => () => {} });
    const road = initialize({width:360,height:540,getContext:() => context}, {invokeMethodAsync: async (name, value) => events.push([name, value])});
    road.reset(8,5); assert.equal(road.start(8),true); tick(performance.now()+100);
    document.hidden = true; listeners.get('visibilitychange')();
    assert.equal(road.snapshot().running,false); assert.equal(events[0][1].reason,'hidden'); assert.equal(events[0][1].sessionId,8);
    assert.equal(road.start(9),false); road.dispose(); assert.equal(canceled,true); assert.equal(listeners.size,0);
    delete globalThis.document; delete globalThis.matchMedia; delete globalThis.requestAnimationFrame; delete globalThis.cancelAnimationFrame;
});
