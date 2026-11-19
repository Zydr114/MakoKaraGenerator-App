import {afterEach,expect,it} from 'vitest';
import {MakoClient,MemoryIdentityStore,staticBootstrapProvider} from '../../src/client';
import {revision} from '../../src/protocol';
import {createFakeFetch,installFakeBackend,json} from '../support/fake-fetch';
const clients:MakoClient[]=[];
afterEach(()=>{for(const client of clients.splice(0))client.dispose();});
it('GPU stream owner methods retain SDK auth and reliable metadata on the actual public routes',async()=>{
  const fake=createFakeFetch();installFakeBackend(fake);
  const client=await MakoClient.connect(staticBootstrapProvider({baseUrl:'http://127.0.0.1:4000',bootstrapToken:'boot-token'}),{fetch:fake.fetch,identityStore:new MemoryIdentityStore(),environment:null});clients.push(client);
  fake.route('POST',/\/gpu-preview-streams$/,()=>json(201,{}));
  fake.route('GET',/\/gpu-preview-streams$/,()=>json(200,{streams:[],protocolVersion:'1'}));
  fake.route('GET',/\/gpu-preview-streams\/stream$/,()=>json(200,{}));
  fake.route('PUT',/\/gpu-preview-streams\/stream\/(source|size)$/,()=>json(200,{}));
  fake.route('DELETE',/\/gpu-preview-streams\/stream$/,()=>json(200,{}));
  const source={kind:'project' as const,sourceId:'project',sourceRevision:revision('1')};
  await client.gpuPreview.create({source,size:{width:1280,height:720},alphaMode:'straight',api:'vulkan',consumerId:'a'.repeat(32)});
  await client.gpuPreview.create({source,size:{width:1280,height:720},alphaMode:'opaque',api:'cpu',consumerId:'a'.repeat(32)});
  await client.gpuPreview.list();
  await client.gpuPreview.read('stream');
  await client.gpuPreview.replaceInput('stream',{expectedStreamRevision:'1',source:{...source,sourceRevision:revision('2')}});
  await client.gpuPreview.resize('stream',{expectedStreamRevision:'2',size:{width:2560,height:1440}});
  await client.gpuPreview.close('stream','3');
  const calls=fake.calls.filter(call=>call.path.includes('/gpu-preview-streams'));
  expect(calls.map(call=>[call.method,call.path])).toEqual([['POST','/api/v1/gpu-preview-streams'],['POST','/api/v1/gpu-preview-streams'],['GET','/api/v1/gpu-preview-streams'],['GET','/api/v1/gpu-preview-streams/stream'],['PUT','/api/v1/gpu-preview-streams/stream/source'],['PUT','/api/v1/gpu-preview-streams/stream/size'],['DELETE','/api/v1/gpu-preview-streams/stream']]);
  for(const call of calls){
    expect(call.headers.get('authorization')).toMatch(/^Bearer /);
    if(call.method!=='GET')expect(call.body).toHaveProperty('metadata');
    expect(JSON.stringify(call.body)).not.toMatch(/brokerEndpoint|handshake|consumerSecret|plane|fdIndex/);
  }
});
