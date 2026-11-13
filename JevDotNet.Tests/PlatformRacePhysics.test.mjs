import { test } from "node:test";
import assert from "node:assert/strict";
import { RacePhysics, makeTower, wrappedDistance } from "../JevDotNet.Demo/Components/Race/RaceBoard.razor.js";

const step = 1 / 120;

test("the shared seeded tower is repeatable and steering uses the short path across an edge", () => {
    assert.deepEqual(makeTower(), makeTower());
    assert.notDeepEqual(makeTower(), makeTower(7));
    assert.equal(wrappedDistance(390, 10), 20);
    assert.equal(wrappedDistance(10, 390), -20);
});

test("a delayed reply costs bounces while the other dog progresses to a win", () => {
    const hops = [];
    let tick = 0;
    const game = new RacePhysics(1, 500, hop => hops.push({ hop, due: tick + (hop.racer === 0 ? 126 : 0) }));
    game.start();
    assert.deepEqual(hops[0].hop.options, hops[1].hop.options);
    for (; tick < 120 * 120 && game.winner === null; tick++) {
        for (const reply of hops.splice(0)) {
            const { hop, due } = reply;
            if (due > tick) hops.push(reply);
            else assert.equal(game.queue(hop.racer, hop.id, hop.options[0].id), true);
        }
        game.advance(step);
    }
    const update = game.update();
    assert.equal(update.winner, 1);
    assert.equal(update.racers[1].score, 500);
    assert.ok(update.racers[1].bones > 0);
    assert.ok(update.racers[0].score > 0 && update.racers[0].score < 500);
    assert.ok(update.racers[0].waits > update.racers[1].waits);
    assert.ok(update.racers.every(racer => racer.misses === 0));
});

test("the model is never offered a stay, a platform above the apex, or a jump too far sideways", () => {
    const tower = [
        { id: 0, x: 200, y: 0, bone: false },
        { id: 1, x: 310, y: 100, bone: true },
        { id: 2, x: 0, y: 175, bone: false },
        { id: 3, x: 200, y: 195, bone: false }
    ];
    const hops = [];
    const game = new RacePhysics(1, 500, hop => hops.push(hop), tower);
    game.start();
    assert.deepEqual(hops[0].options, [{ id: 1, climb: 100, horizontal: 110 }]);
    assert.equal(game.queue(0, hops[0].id, 0), false);
    assert.equal(game.queue(1, hops[1].id, 2), false);
});

test("both choices lead to the promised platform from every rung, including edge landings", () => {
    const tower = makeTower();
    for (const origin of tower.filter(p => p.y < 5000)) {
        for (const offset of [-45, 0, 45]) {
            for (const optionIndex of [0, 1]) {
                const hops = [];
                const game = new RacePhysics(1, 500, hop => hops.push(hop), tower);
                const dog = game.dogs[0];
                Object.assign(dog, { origin, x: (origin.x + offset + 400) % 400, y: origin.y });
                game.request(dog);
                const hop = hops[0];
                assert.equal(hop?.options.length, 2, `choices from P${origin.id}, offset ${offset}`);
                const target = hop.options[optionIndex].id;
                assert.equal(game.queue(0, hop.id, target), true);
                for (let i = 0; i < 240 && dog.origin === origin; i++) game.advance(step);
                assert.equal(dog.origin.id, target, `P${origin.id} to P${target}, offset ${offset}`);
                assert.equal(dog.misses, 0);
            }
        }
    }
});
