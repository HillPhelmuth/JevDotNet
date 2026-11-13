const width = 400;
const gravity = 1800;
const launchSpeed = 800;
const steerSpeed = 280;
const landingRadius = 45;
const stepSeconds = 1 / 120;
const colors = ["#6235c3", "#176363"];
const askMethod = "OnHop";
const updateMethod = "OnRaceUpdate";

function wrap(x) {
    return ((x % width) + width) % width;
}

export function wrappedDistance(from, to) {
    return wrap(to - from + width / 2) - width / 2;
}

export function makeTower(seed = 1977) {
    const random = () => {
        seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
        return seed / 4294967296;
    };
    const tower = [{ id: 0, x: width / 2, y: 0, bone: false }];
    while (tower.at(-1).y < 5300) {
        const previous = tower.at(-1);
        const id = tower.length;
        const direction = random() < 0.5 ? -1 : 1;
        tower.push({
            id,
            x: wrap(previous.x + direction * (60 + Math.floor(random() * 46))),
            y: previous.y + 70 + Math.floor(random() * 14),
            bone: id % 4 === 0
        });
    }
    return tower;
}

function stepFlight(body, target, platforms, dt) {
    const oldX = body.x;
    const oldY = body.y;
    let move = 0;
    if (target) {
        const delta = wrappedDistance(oldX, target.x);
        move = Math.sign(delta) * Math.min(Math.abs(delta), steerSpeed * dt);
        if (move) body.facing = Math.sign(move);
    }
    body.x = wrap(oldX + move);
    body.y += body.vy * dt - gravity * dt * dt / 2;
    body.vy -= gravity * dt;
    if (body.vy >= 0 || body.y >= oldY) return undefined;

    return platforms.findLast(p => p.y <= oldY && p.y >= body.y &&
        Math.abs(wrappedDistance(wrap(oldX + move * (oldY - p.y) / (oldY - body.y)), p.x)) <= landingRadius);
}

function canLandOn(dog, target, platforms) {
    const preview = { x: dog.x, y: dog.origin.y, vy: launchSpeed };
    // The actual jump uses the same steering, fixed time step, and first landing.
    while (preview.vy >= 0 || preview.y >= target.y) {
        const landing = stepFlight(preview, target, platforms, stepSeconds);
        if (landing) return landing.id === target.id;
    }
    return false;
}

export class RacePhysics {
    constructor(run, goal, ask, tower = makeTower()) {
        this.run = run;
        this.goal = goal;
        this.ask = ask;
        this.tower = tower;
        this.dogs = [0, 1].map(racer => ({
            racer, x: tower[0].x, y: tower[0].y, vy: launchSpeed, origin: tower[0],
            target: null, pending: null, queued: null, collected: new Set(),
            score: 0, hops: 0, waits: 0, misses: 0, dazed: 0, facing: racer === 0 ? 1 : -1
        }));
        this.nextId = 0;
        this.transitions = [];
        this.winner = null;
        this.dirty = true;
    }

    start() {
        for (const dog of this.dogs) this.request(dog);
    }

    request(dog) {
        const maxClimb = launchSpeed ** 2 / (2 * gravity);
        const options = this.tower.slice(dog.origin.id + 1, dog.origin.id + 6)
            .filter(p => p.y - dog.origin.y <= maxClimb && canLandOn(dog, p, this.tower))
            .map(p => ({ id: p.id, climb: p.y - dog.origin.y, horizontal: Math.round(wrappedDistance(dog.x, p.x)) }));
        if (!options.length) return;
        const id = ++this.nextId;
        dog.pending = { id, origin: dog.origin.id, options };
        this.ask({ run: this.run, racer: dog.racer, id, score: dog.score,
            opponentScore: this.dogs[1 - dog.racer].score, options });
    }

    queue(racer, id, target) {
        const dog = this.dogs[racer];
        const pending = dog.pending;
        if (pending?.id !== id) return false;
        dog.pending = null;
        if (dog.origin.id !== pending.origin || !pending.options.some(option => option.id === target)) return false;
        dog.queued = { id, target, origin: pending.origin };
        return true;
    }

    bounce(dog) {
        this.dirty = true;
        dog.vy = launchSpeed;
        const queued = dog.queued;
        if (queued) {
            dog.queued = null;
            const used = queued.origin === dog.origin.id;
            this.transitions.push({ id: queued.id, used });
            if (used) {
                dog.hops++;
                dog.target = this.tower[queued.target];
                return;
            }
            // Send the drop before asking for another decision.
            return;
        }
        dog.waits++;
        if (!dog.pending) this.request(dog);
    }

    settle(dog, platform) {
        if (dog.target && platform?.id !== dog.target.id) dog.misses++;
        dog.target = null;
        if (!platform) {
            const lower = this.tower[Math.max(0, dog.origin.id - 3)];
            dog.origin = lower;
            dog.x = lower.x;
            dog.y = lower.y;
            dog.dazed = 1;
        } else {
            dog.origin = platform;
            dog.y = platform.y;
            dog.score = Math.min(this.goal, Math.max(dog.score, Math.floor(platform.y / 10)));
            if (platform.bone) dog.collected.add(platform.id);
        }
        this.dirty = true;
    }

    advance(dt) {
        for (const dog of this.dogs) {
            if (dog.dazed > 0) {
                dog.dazed -= dt;
                if (dog.dazed <= 0) this.bounce(dog);
                continue;
            }
            // Idle hops stay on their current platform even if another platform overlaps it.
            const platforms = dog.target ? this.tower : [dog.origin];
            const landing = stepFlight(dog, dog.target, platforms, dt);
            if (landing) {
                this.settle(dog, landing);
                if (dog.score < this.goal) this.bounce(dog);
            } else if (dog.y < Math.max(-90, dog.origin.y - 530)) {
                this.settle(dog, null);
            }
        }
        const finishers = this.dogs.filter(dog => dog.score >= this.goal);
        if (finishers.length) this.winner = finishers.length === 2 ? -1 : finishers[0].racer;
    }

    update(hidden = false) {
        this.dirty = false;
        return {
            run: this.run,
            racers: this.dogs.map(dog => ({
                score: dog.score, hops: dog.hops, waits: dog.waits, misses: dog.misses, bones: dog.collected.size
            })),
            transitions: this.transitions.splice(0),
            winner: this.winner,
            hidden
        };
    }
}

const boards = new WeakMap();

export function initialize(canvas, callback, goal) {
    const board = new Board(canvas, callback, goal);
    boards.set(canvas, board);
    board.draw();
}

export function start(canvas, run) { return boards.get(canvas).start(run); }
export function stop(canvas, run) { boards.get(canvas)?.stop(run); }
export function reset(canvas) { boards.get(canvas)?.reset(); }
export function queue(canvas, run, racer, id, target) {
    const board = boards.get(canvas);
    return board?.running && board.game.run === run ? board.game.queue(racer, id, target) : false;
}
export function dispose(canvas) {
    boards.get(canvas)?.dispose();
    boards.delete(canvas);
}

class Board {
    constructor(canvas, callback, goal) {
        this.canvas = canvas;
        this.context = canvas.getContext("2d");
        this.callback = callback;
        this.goal = goal;
        this.camera = 0;
        this.running = false;
        this.frame = 0;
        this.game = new RacePhysics(0, goal, () => {});
        this.events = new AbortController();
        document.addEventListener("visibilitychange", () => {
            if (!document.hidden || !this.running) return;
            this.stop(this.game.run);
            this.send(updateMethod, this.game.update(true));
        }, { signal: this.events.signal });
    }

    start(run) {
        if (document.hidden) return false;
        this.stop(this.game.run);
        this.camera = 0;
        this.game = new RacePhysics(run, this.goal, hop => this.send(askMethod, hop));
        this.running = true;
        this.last = performance.now();
        this.accumulated = 0;
        this.game.start();
        this.frame = requestAnimationFrame(time => this.tick(time));
        return true;
    }

    async send(method, data) {
        try {
            await this.callback.invokeMethodAsync(method, data);
        } catch {
            this.stop(data.run);
        }
    }

    tick(time) {
        if (!this.running) return;
        this.accumulated += Math.min(0.05, Math.max(0, (time - this.last) / 1000));
        this.last = time;
        while (this.accumulated >= stepSeconds && this.game.winner === null) {
            this.game.advance(stepSeconds);
            this.accumulated -= stepSeconds;
            if (this.game.dirty) this.send(updateMethod, this.game.update());
        }
        this.draw();
        if (this.game.winner === null) this.frame = requestAnimationFrame(next => this.tick(next));
        else this.running = false;
    }

    stop(run) {
        if (this.game.run !== run) return;
        this.running = false;
        cancelAnimationFrame(this.frame);
    }

    reset() {
        this.stop(this.game.run);
        this.camera = 0;
        this.game = new RacePhysics(0, this.goal, () => {});
        this.draw();
    }

    dispose() {
        this.events.abort();
        this.stop(this.game.run);
    }

    draw() {
        const ctx = this.context;
        const height = this.canvas.height;
        const lead = Math.max(...this.game.dogs.map(dog => dog.y));
        this.camera += (Math.max(0, lead - height * 0.53) - this.camera) * 0.08;
        const screenY = y => height - 78 + this.camera - y;
        const sky = ctx.createLinearGradient(0, 0, 0, height);
        sky.addColorStop(0, "#8299e7");
        sky.addColorStop(1, "#d7e6fb");
        ctx.fillStyle = sky;
        ctx.fillRect(0, 0, width, height);
        for (let i = 0; i < 8; i++) {
            const x = (i * 83 + 31) % width;
            const y = ((i * 159 + this.camera * 0.35) % (height + 100)) - 45;
            cloud(ctx, x, y, 0.65 + (i % 3) * 0.22);
        }
        const finish = screenY(this.goal * 10);
        if (finish > 0 && finish < height) {
            ctx.fillStyle = "#263459";
            for (let i = 0; i < 40; i++) ctx.fillRect(i * 10, finish + (i % 2) * 7, 10, 7);
        }
        for (const p of this.game.tower) {
            const y = screenY(p.y);
            if (y < -20 || y > height + 20) continue;
            const targets = this.game.dogs.filter(dog => dog.target?.id === p.id);
            for (const offset of [-width, 0, width]) {
                const x = p.x + offset;
                pill(ctx, x - 35, y, 70, 13, "#5d39a7", 5);
                pill(ctx, x - 35, y, 70, 5, "#d7ef8b", 3);
                if (targets.length) {
                    pill(ctx, x - 18, y + 14, 36, 16, "#172445", 4);
                    ctx.fillStyle = "#fff";
                    ctx.font = "bold 11px system-ui";
                    ctx.textAlign = "center";
                    ctx.fillText(`P${p.id}`, x, y + 26);
                }
            }
            const remaining = this.game.dogs.filter(dog => !dog.collected.has(p.id));
            if (p.bone && remaining.length) drawBone(ctx, p.x, y - 16, remaining.length === 2 ? "#fff4c2" : colors[remaining[0].racer]);
        }
        const [left, right] = this.game.dogs;
        const overlap = Math.abs(wrappedDistance(left.x, right.x)) < 26 && Math.abs(left.y - right.y) < 26;
        for (const dog of this.game.dogs) {
            const x = wrap(dog.x + (overlap ? (dog.racer === 0 ? -12 : 12) : 0));
            const y = screenY(dog.y);
            if (y > height - 4) {
                pill(ctx, x - 10, height - 23, 20, 19, colors[dog.racer], 5);
                ctx.fillStyle = "#fff";
                ctx.font = "bold 13px system-ui";
                ctx.textAlign = "center";
                ctx.fillText("↓", x, height - 9);
            } else {
                for (const offset of [-width, 0, width]) drawDog(ctx, x + offset, y, dog);
            }
        }
    }
}

function pill(ctx, x, y, w, h, color, radius) {
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.roundRect(x, y, w, h, radius);
    ctx.fill();
}

function dot(ctx, x, y, radius, color) {
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.arc(x, y, radius, 0, Math.PI * 2);
    ctx.fill();
}

function cloud(ctx, x, y, scale) {
    ctx.save();
    ctx.translate(x, y);
    ctx.scale(scale, scale);
    ctx.globalAlpha = 0.72;
    dot(ctx, -25, 3, 20, "#fff");
    dot(ctx, 0, -8, 30, "#fff");
    dot(ctx, 30, 5, 22, "#fff");
    pill(ctx, -40, 1, 85, 22, "#fff", 11);
    ctx.restore();
}

function drawBone(ctx, x, y, color) {
    pill(ctx, x - 9, y - 2, 18, 4, color, 2);
    for (const dx of [-8, 8])
        for (const dy of [-2, 2]) dot(ctx, x + dx, y + dy, 3, color);
}

function drawDog(ctx, x, y, dog) {
    const fur = dog.racer === 0 ? "#e5a362" : "#eff1f5";
    const ear = dog.racer === 0 ? "#985328" : "#64718a";
    ctx.save();
    ctx.translate(x, y - 1);
    ctx.scale(dog.facing, 1);
    ctx.globalAlpha = dog.dazed > 0 ? 0.6 : 1;
    ctx.strokeStyle = ear;
    ctx.lineWidth = 4;
    ctx.lineCap = "round";
    ctx.beginPath();
    ctx.moveTo(-10, -12);
    ctx.quadraticCurveTo(-20, -12, -18, -20);
    ctx.stroke();
    pill(ctx, -13, -16, 24, 13, fur, 5);
    pill(ctx, -10, -8, 5, 8, fur, 2);
    pill(ctx, 5, -8, 5, 8, fur, 2);
    pill(ctx, 5, -17, 5, 13, colors[dog.racer], 2);
    dot(ctx, 12, -19, 8, fur);
    pill(ctx, 7, -25, 6, 11, ear, 3);
    pill(ctx, 14, -19, 9, 6, fur, 3);
    dot(ctx, 21, -17, 2, "#263045");
    dot(ctx, 16, -21, 1.5, "#263045");
    ctx.restore();
}
