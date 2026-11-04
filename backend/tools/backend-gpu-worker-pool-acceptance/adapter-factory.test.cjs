'use strict';
const test=require('node:test');
const assert=require('node:assert/strict');
const path=require('node:path');
const {sourceFor,createRouter,createAdapter}=require('./adapter-factory.cjs');
const root=path.resolve(__dirname,'../../..');
test('exact production adapter extraction compiles with claim/import/ACK/barrier boundaries intact',()=>{
  const extracted=sourceFor(root);
  for(const token of ['async function attach(streamId,expectedInputGeneration)','async function detach()',
    'importAndTransferFrame','onAllReferencesReleased','mako-gpu:barrier','window.webContents.mainFrame'])
    assert.ok(extracted.body.includes(token),token);
  assert.equal(extracted.sha256.length,64);
  assert.ok(!extracted.body.includes('new BrowserWindow'));
});
test('two separate production closures route IPC by exact webContents sender',async()=>{
  const globalHandlers=new Map(),globalListeners=new Map();
  const router=createRouter({handle:(name,fn)=>{assert.ok(!globalHandlers.has(name));globalHandlers.set(name,fn);},
    on:(name,fn)=>{assert.ok(!globalListeners.has(name));globalListeners.set(name,fn);}});
  const surface=index=>{
    const adapter=createAdapter({root,config:{pageUrl:'http://127.0.0.1:1/',baseUrl:'http://127.0.0.1:2/',consumerId:`consumer-${index}`},
      electron:{app:{commandLine:{hasSwitch:()=>false},getAppMetrics:()=>[]},dialog:{},sharedTexture:{}},broker:{},router});
    const frame={url:'http://127.0.0.1:1/'},webContents={id:index,mainFrame:frame,getOSProcessId:()=>index};
    adapter.bind({webContents,isDestroyed:()=>false},{});return {adapter,event:{sender:webContents,senderFrame:frame}};
  };
  const one=surface(1),two=surface(2),read=globalHandlers.get('mako-gpu:consumer-id');
  assert.equal(read(one.event),'consumer-1');assert.equal(read(two.event),'consumer-2');
  assert.throws(()=>read({sender:{id:3}}),/Unexpected/);
  assert.throws(()=>read({...one.event,senderFrame:{url:'http://127.0.0.1:1/'}}),/Unexpected/);
  assert.equal((await globalHandlers.get('mako-gpu:read')(one.event)).activeStream,null);
  assert.equal((await globalHandlers.get('mako-gpu:read')(two.event)).activeStream,null);
});
