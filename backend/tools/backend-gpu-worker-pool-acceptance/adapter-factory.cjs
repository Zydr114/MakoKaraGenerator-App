'use strict';
const fs = require('node:fs');
const path = require('node:path');
const {createHash} = require('node:crypto');

// Execute the unmodified transport/claim/import/ACK/gate functions from the
// current production main in two independent closures. Only startup/profile/UX
// code is excluded. IPC registration is routed by the exact sender webContents.
function sourceFor(root) {
  const file=path.join(root,'desktop/main.cjs'),source=fs.readFileSync(file,'utf8');
  const start=source.indexOf('const decimal = value =>'),end=source.indexOf("for(const signal of ['SIGINT','SIGTERM'])");
  if(start<0||end<=start||source.indexOf('async function attach(streamId,expectedInputGeneration)')<start||source.indexOf('async function detach()')<start)
    throw new Error('Production adapter extraction anchors changed; inspect before acceptance');
  return {file,sha256:createHash('sha256').update(source).digest('hex'),body:source.slice(start,end),
    boundary:'Exact production main transport/claim/native import/ACK/control functions; separate closure per webContents; production startup/UX excluded'};
}

function createRouter(ipcMain) {
  const surfaces=new Map(),handles=new Set(),events=new Set();
  const local=()=>{
    const handlers=new Map(),listeners=new Map();
    return {handlers,listeners,ipcMain:{
      handle(name,handler){
        handlers.set(name,handler);
        if(!handles.has(name)){handles.add(name);ipcMain.handle(name,(event,...args)=>{
          const surface=surfaces.get(event.sender.id),target=surface?.handlers.get(name);
          if(!target)throw new Error('Unexpected dual harness IPC sender');return target(event,...args);
        });}
      },
      on(name,handler){
        listeners.set(name,handler);
        if(!events.has(name)){events.add(name);ipcMain.on(name,(event,...args)=>{
          const surface=surfaces.get(event.sender.id),target=surface?.listeners.get(name);
          if(!target)throw new Error('Unexpected dual harness IPC sender');return target(event,...args);
        });}
      },
    }};
  };
  return {local,bind(window,surface){if(surfaces.has(window.webContents.id))throw new Error('Duplicate surface');surfaces.set(window.webContents.id,surface);}};
}

function createAdapter({root,config,electron,broker,router}) {
  if(new URL(config.pageUrl).hostname!=='127.0.0.1'||new URL(config.baseUrl).hostname!=='127.0.0.1')throw new Error('Dual surfaces must be loopback');
  const source=sourceFor(root),local=router.local();
  const bindings={app:electron.app,dialog:electron.dialog,sharedTexture:electron.sharedTexture,ipcMain:local.ipcMain,
    config,broker,fs,path,performanceDiagnostics:null,measured:(_type,action)=>action(),
    importAndTransferFrame:require(path.join(root,'desktop/gpu-preview/import-consumer.cjs')).importAndTransferFrame,
    validateWorkerMessage:require(path.join(root,'desktop/gpu-preview/wire.cjs')).validateWorkerMessage};
  const factory=new Function('bindings',`'use strict';const {app,dialog,sharedTexture,ipcMain,config,broker,fs,path,performanceDiagnostics,measured,importAndTransferFrame,validateWorkerMessage}=bindings;\n${source.body}\nreturn {setSurface(value,info){window=value;gpuInfo=info;},attach,detach,failStop};`);
  const adapter=factory(bindings);
  return {...adapter,provenance:{file:source.file,sha256:source.sha256,boundary:source.boundary},
    bind(window,gpuInfo){adapter.setSurface(window,gpuInfo);router.bind(window,local);}};
}

module.exports={sourceFor,createRouter,createAdapter};
