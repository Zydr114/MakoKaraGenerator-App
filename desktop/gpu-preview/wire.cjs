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
// 硬失败只把稳定代码、根异常类型／抛出方法与引擎文本带进日志；格式不符的字段整体丢弃，不回显原值。
const workerToken=(value,max)=>typeof value==='string'&&value.length>0&&value.length<=max&&/^[A-Za-z0-9_.<>`|+-]+$/.test(value)?value:undefined;
function workerFailureSummary(m){
  const detail=typeof m?.failureDetail==='string'&&m.failureDetail.length<=512&&!/[\u0000-\u001f\u007f]/.test(m.failureDetail)?m.failureDetail:undefined;
  return {code:workerToken(m?.code,128),type:workerToken(m?.failureType,128),site:workerToken(m?.failureSite,256),detail,drained:m?.drained===true};
}
module.exports={validateWorkerMessage,workerFailureSummary};
