'use strict';
const {performance,PerformanceObserver}=require('node:perf_hooks');
const KEYS=['streamId','hostInstanceId','producerEpoch','inputGeneration','consumerEpoch','timelineEpoch','poolGeneration','slotId','slotEpoch','frameSeq'];
function createPerformanceDiagnostics({now=()=>process.hrtime.bigint(),capacity=512,observe=true}={}) {
  let sequence=0;const events=[];
  const add=(type,start,end,detail={})=>{events.push({sequence:++sequence,type,startMonotonicNs:start.toString(),endMonotonicNs:end.toString(),durationMs:Number(end-start)/1e6,...detail});if(events.length>capacity)events.shift();};
  const identity=m=>m?Object.fromEntries(KEYS.filter(k=>m[k]!==undefined).map(k=>[k,m[k]])):undefined;
  const measure=(type,action,metadata,thresholdMs=5)=>{const start=now();try{return action();}finally{const end=now();if(Number(end-start)/1e6>=thresholdMs)add(type,start,end,metadata?{lease:identity(metadata)}:{});}};
  let timer,observer;
  if(observe){
    const origin=now()-BigInt(Math.round(performance.now()*1e6));
    observer=new PerformanceObserver(list=>{for(const e of list.getEntries())if(e.duration>=5){const start=origin+BigInt(Math.round(e.startTime*1e6));add('main-gc',start,start+BigInt(Math.round(e.duration*1e6)),{kind:e.detail?.kind});}});
    observer.observe({entryTypes:['gc']});
    let previous=now();timer=setInterval(()=>{const end=now(),expected=previous+20000000n;if(end-expected>=20000000n)add('main-event-loop-lag',expected,end);previous=end;},20);timer.unref();
  }
  return {measure,add,identity,snapshot:()=>({enabled:true,sequence,capacity,events:events.slice()}),close(){clearInterval(timer);observer?.disconnect();}};
}
module.exports={createPerformanceDiagnostics};
