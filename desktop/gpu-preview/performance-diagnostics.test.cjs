'use strict';
const {test}=require('node:test');const assert=require('node:assert/strict');
const {createPerformanceDiagnostics}=require('./performance-diagnostics.cjs');
test('acceptance diagnostics record only slow work with bounded sanitized lease identity',()=>{
  let t=0n;const d=createPerformanceDiagnostics({now:()=>t,capacity:2,observe:false});
  assert.equal(d.measure('quick',()=>{t+=1000000n;return 42;}),42);assert.equal(d.snapshot().sequence,0);
  const m={streamId:'s',frameSeq:3,secret:'never'};
  d.measure('slow',()=>{t+=6000000n;},m);
  assert.throws(()=>d.measure('throw',()=>{t+=9000000n;throw new Error('expected');},m),/expected/);
  d.measure('last',()=>{t+=10000000n;},m);
  const s=d.snapshot();assert.equal(s.sequence,3);assert.equal(s.events.length,2);assert.equal(s.events[0].durationMs,9);assert.deepEqual(s.events[1].lease,{streamId:'s',frameSeq:3});assert.equal(JSON.stringify(s).includes('never'),false);d.close();
});
