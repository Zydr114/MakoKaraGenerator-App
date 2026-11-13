'use strict';

// Retain the exact failed stream, including unknown imported references. Closing
// its transport is not consumer completion: never release, ACK, or clear imports.
// The controller disables new attachments for the remainder of this process.
function isolateGpuPreviewStream(stream,{closeConnection,detachRenderer,abortWorker}) {
  if(stream.isolationPromise)return stream.isolationPromise;
  stream.isolated=true;stream.stopping=true;stream.pending=null;stream.executing=null;
  clearInterval(stream.poll);stream.clearStop?.();
  const connection=stream.connection;stream.connection=null;
  if(connection)try{closeConnection(connection);}catch{}
  try{detachRenderer();}catch{}
  stream.rejectHello?.(new Error('GPU preview isolated'));
  stream.isolationPromise=Promise.resolve().then(abortWorker).then(terminated=>{
    stream.workerTerminationConfirmed=terminated===true;return stream.workerTerminationConfirmed;
  },()=>{stream.workerTerminationConfirmed=false;return false;});
  // Unblock an already requested detach without inventing a Worker stopped/drain
  // packet. Callers of detach must separately await isolationPromise.
  stream.resolveStop?.();
  return stream.isolationPromise;
}
module.exports={isolateGpuPreviewStream};
