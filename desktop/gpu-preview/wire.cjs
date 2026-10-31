'use strict';
const decimal=value=>typeof value==='string'&&/^(0|[1-9]\d*)$/.test(value)&&BigInt(value)<=0xffffffffffffffffn;
function validateWorkerMessage(m,s,config){
  if(!m||typeof m!=='object'||Array.isArray(m))throw new Error('Invalid Worker packet');
  if(m.type==='clock-ping'){
    if(!decimal(m.workerMonotonicNs))throw new Error('Invalid clock ping');return;
  }
  if(m.type==='clock-calibrated'){
    if(!/^-?(0|[1-9]\d*)$/.test(m.workerMinusSenderNs)||!decimal(m.uncertaintyNs)||m.calibrated!==true)throw new Error('Invalid calibrated clock');return;
  }
  const identity={protocolVersion:1,streamId:s.streamId,hostInstanceId:config.hostInstanceId,producerEpoch:s.producerEpoch,inputGeneration:s.expectedInputGeneration,consumerEpoch:'1'};
  if(Object.keys(identity).some(key=>m[key]!==identity[key]))throw new Error('Worker response identity mismatch');
  if(m.type==='hello-accepted'){
    if(m.consumerId!==config.consumerId||m.poolGeneration!==s.poolGeneration||m.echoSenderMonotonicNs!==s.helloNs||!decimal(m.workerMonotonicNs)||m.width!==s.size.width||m.height!==s.size.height||m.slotCount!==3||!Number.isSafeInteger(m.allocationBytes)||m.allocationBytes<1||m.allocationBytes>64*1024**2)throw new Error('Invalid Worker hello');
    return;
  }
  if(['frame','state','control-applied'].includes(m.type)&&m.poolGeneration!==s.poolGeneration)throw new Error('Worker pool identity mismatch');
  if(!['frame','state','control-applied','stopped','failed'].includes(m.type))throw new Error('Unknown Worker response');
}
module.exports={validateWorkerMessage};
