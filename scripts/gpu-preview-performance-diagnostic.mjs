// One explicitly instrumented 720p24 synthetic run; this is not formal acceptance.
import {spawn} from 'node:child_process';
import {mkdir,open,writeFile,readFile,appendFile} from 'node:fs/promises';
import {resolve,join} from 'node:path';
import {createHash} from 'node:crypto';
import {makeFixtures,createProject,connectPage} from './gpu-preview-p3.mjs';
const root=process.cwd(),name=`perfdiag-${Date.now()}`,output=join(root,'.local/gpu-preview-performance',name);await mkdir(output,{recursive:true});
const file=await open(join(output,'desktop.log'),'w',0o600),child=spawn(process.execPath,['scripts/app.mjs','desktop','--session-name',name,'--port','5185','--cdp-port','9235','--performance-diagnostics'],{cwd:root,env:{...process.env,MAKO_BACKEND_GPU_PREVIEW_BENCHMARK:'1'},stdio:['ignore',file.fd,file.fd]});
const exit=new Promise((done,reject)=>{child.once('exit',(code,signal)=>done({code,signal}));child.once('error',reject);});
const wait=ms=>new Promise(r=>setTimeout(r,ms));let cdp,page,cursor=0;const frames=new Map(),events=new Map();
const report={schemaVersion:1,name,startedAt:new Date().toISOString(),purpose:'instrumented diagnosis, not formal acceptance',seconds:180,provider:'synthetic-gpu',size:{width:1280,height:720},targetFps:24,thresholdsUnchanged:true,additionalOverhead:'main/renderer20ms lag timers, GC observer, instrumented import/read/poll/draw duration; bounded512 slow events per scope',deployment:JSON.parse(await readFile('.local/backend-current.json','utf8'))};
report.sources={};for(const p of ['desktop/main.cjs','desktop/gpu-preview/preload.cjs','desktop/gpu-preview/performance-diagnostics.cjs','scripts/gpu-preview-performance-diagnostic.mjs'])report.sources[p]=createHash('sha256').update(await readFile(p)).digest('hex');
async function sample(){const s=await page.eval(`window.makoDesktopPreview.readPreviewState(${cursor})`);cursor=s.sampleRevision;if(!s.performanceDiagnostics?.enabled||!s.rendererPerformanceDiagnostics?.enabled)throw new Error('Explicit diagnostic observers unavailable');
 for(const f of s.samples)frames.set(`${f.streamId}/${f.poolGeneration}/${f.slotId}/${f.slotEpoch}/${f.frameSeq}`,f);
 for(const scope of ['performanceDiagnostics','rendererPerformanceDiagnostics'])for(const e of s[scope].events)events.set(`${scope}/${e.sequence}`,{scope,...e});
 await appendFile(join(output,'events.jsonl'),JSON.stringify({type:'snapshot',snapshot:s})+'\n');return s;}
try{
 ({cdp,page}=await connectPage(9235));await createProject(page,await makeFixtures(output));await page.eval(`document.querySelector('[aria-label="播放"]')?.click()`);await page.waitFor('window.makoDesktopPreview.readPreviewState().then(s=>s.completedDrawnUnique>3)','Diagnostic synthetic first frames');
 const warm=Date.now()+10000;while(Date.now()<warm){await sample();await wait(1000);}report.start=await sample();
 const end=Date.now()+180000;while(Date.now()<end){await wait(Math.min(1000,end-Date.now()));await sample();}report.end=await sample();
 const sorted=[...frames.values()].filter(f=>f.drawn&&BigInt(f.completedMonotonicNs)>=BigInt(report.start.nowMonotonicNs)&&BigInt(f.completedMonotonicNs)<BigInt(report.end.nowMonotonicNs)).sort((a,b)=>Number(BigInt(a.completedMonotonicNs)-BigInt(b.completedMonotonicNs)));
 report.uniqueDrawnCompleted=sorted.length;report.slowEvents=[...events.values()];report.gaps=[];
 for(let i=1;i<sorted.length;i++){const a=sorted[i-1],b=sorted[i],gap=Number(BigInt(b.completedMonotonicNs)-BigInt(a.completedMonotonicNs))/1e6;if(gap<60)continue;
 const end=BigInt(b.completedMonotonicNs),start=BigInt(a.completedMonotonicNs);
 report.gaps.push({durationMs:gap,before:a,after:b,producerReadyGapMs:gap-b.readyToCompletionMs+a.readyToCompletionMs,nearbyEvents:report.slowEvents.filter(e=>e.startMonotonicNs&&BigInt(e.startMonotonicNs)<=end+20000000n&&BigInt(e.endMonotonicNs)>=start-20000000n)});}
 report.finishedMeasurement=true;
}catch(error){report.error=error.stack;process.exitCode=1;}finally{
 cdp?.ws.close();if(child.exitCode===null&&child.signalCode===null)child.kill('SIGTERM');report.launcherExit=await exit;await file.close();report.finishedAt=new Date().toISOString();await writeFile(join(output,'report.json'),JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify({output,measurementComplete:report.finishedMeasurement,error:report.error}));
}
