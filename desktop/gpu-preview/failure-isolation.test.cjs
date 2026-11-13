'use strict';
const {test}=require('node:test');const assert=require('node:assert/strict');
const {isolateGpuPreviewStream}=require('./failure-isolation.cjs');
test('unknown completion disconnects and retires only the owned stream without ACK, release or reuse',async()=>{
  let close=0,detach=0,abort=0,unblock=0;
  const reference={release(){assert.fail('unknown imported reference must remain owned');}};
  const stream={connection:{},imports:new Map([['lease',{imported:reference}]]),pending:{},executing:{},resolveStop(){unblock++;}};
  const hooks={closeConnection(){close++;},detachRenderer(){detach++;},async abortWorker(){abort++;return true;}};
  const first=isolateGpuPreviewStream(stream,hooks);
  assert.equal(first,isolateGpuPreviewStream(stream,hooks));
  assert.equal(stream.connection,null);assert.equal(stream.imports.size,1);assert.equal(stream.imports.get('lease').imported,reference);
  assert.equal(stream.stopped,undefined);assert.equal(stream.isolated,true);assert.equal(stream.stopping,true);
  assert.equal(stream.pending,null);assert.equal(stream.executing,null);
  assert.equal(await first,true);assert.equal(stream.workerTerminationConfirmed,true);
  assert.deepEqual({close,detach,abort,unblock},{close:1,detach:1,abort:1,unblock:1});
});
test('failed host termination keeps unknown references and reports no successful drain',async()=>{
  const stream={imports:new Map([['unknown',{}]])};
  assert.equal(await isolateGpuPreviewStream(stream,{closeConnection(){},detachRenderer(){},abortWorker(){throw new Error('unknown process state');}}),false);
  assert.equal(stream.imports.size,1);assert.equal(stream.workerTerminationConfirmed,false);assert.equal(stream.stopped,undefined);
});
