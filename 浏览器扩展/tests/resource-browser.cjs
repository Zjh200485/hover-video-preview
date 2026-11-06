// Reuse the isolated Edge fixture, replacing its already-tested outage scenario
// with a decoded-video freeze/recovery regression.
const fs = require('node:fs'), path = require('node:path');
const {createRequire} = require('node:module');
const file = path.join(__dirname, 'browser-recovery.cjs');
let source = fs.readFileSync(file, 'utf8').replace("'edge-'+Date.now()", "'resource-edge-'+Date.now()");
source = source.replace('viewport:{width:960,height:760}', 'viewport:{width:3840,height:2160}');
const begin = source.indexOf(' const other=await browser.newPage()');
const end = source.indexOf(" await worker.evaluate(()=>chrome.storage.local.set", begin);
if (begin < 0 || end < 0) throw Error('Fixture structure changed');
source = source.slice(0, begin) + `
 const root=await browser.browser().newBrowserCDPSession();
 const targets=(await root.send('Target.getTargets')).targetInfos;
 const target=targets.find(t=>t.url==='chrome-extension://'+id+'/offscreen.html');
 assert.ok(target,'offscreen target not found');
 const attached=await root.send('Target.attachToTarget',{targetId:target.targetId,flatten:false});
 let serial=0;
 async function evaluate(expression){
  const callId=++serial;
  return new Promise((resolve,reject)=>{
   const timer=setTimeout(()=>{root.off('Target.receivedMessageFromTarget',listener);reject(Error('CDP timeout'));},4000);
   function listener(event){
    if(event.sessionId!==attached.sessionId)return;
    const msg=JSON.parse(event.message);if(msg.id!==callId)return;
    clearTimeout(timer);root.off('Target.receivedMessageFromTarget',listener);
    if(msg.error)reject(Error(JSON.stringify(msg.error)));else resolve(msg.result.result.value);
   }
   root.on('Target.receivedMessageFromTarget',listener);
   root.send('Target.sendMessageToTarget',{sessionId:attached.sessionId,message:JSON.stringify({id:callId,method:'Runtime.evaluate',params:{expression,returnByValue:true,awaitPromise:true}})}).catch(reject);
  });
 }

 const settings=await evaluate('session.stream.getVideoTracks()[0].getSettings()');
 assert.ok(settings.width<=1280 && settings.height<=720,JSON.stringify(settings));
 assert.ok(settings.frameRate<=15,JSON.stringify(settings));
 report.push('PASS 4K 页面实际采集尺寸/帧率上限：'+JSON.stringify({width:settings.width,height:settings.height,frameRate:settings.frameRate}));
 const samples=[];
 const {execFileSync}=require('node:child_process');
 async function sampleMemory(){
  const pids=(await root.send('SystemInfo.getProcessInfo')).processInfo.map(p=>p.id).filter(Number.isInteger);
  const total=execFileSync('powershell.exe',['-NoProfile','-Command','(Get-Process -Id '+pids.join(',')+' -ErrorAction SilentlyContinue | Measure-Object PrivateMemorySize64 -Sum).Sum'],{windowsHide:true,encoding:'utf8'}).trim();
  samples.push({seconds:Math.round((Date.now()-sampleStart)/1000),privateBytes:Number(total),frames});
 }
 const sampleStart=Date.now();await sampleMemory();
 for(let i=0;i<4;i++){await sleep(30000);await sampleMemory();console.log('资源观察 '+(i+1)*30+'/120 秒');}
 const state=await worker.evaluate(()=>getState());
 assert.equal(state.active,true);assert.equal(state.reconnecting,false);
 assert.ok(frames>500,'preview stopped producing frames');
 report.push('OBSERVE 独立 Edge 进程私有内存及帧数（短时观测，不等于长期无泄漏）：'+JSON.stringify(samples));
 // Make one native encoder never complete. No further work may be queued.
 await evaluate("globalThis.encodeCalls=0;canvas.toBlob=()=>{encodeCalls++};true");
 await until(async()=>!(await worker.evaluate(()=>getState())).active);
 await sleep(4000);
 assert.equal(await evaluate('encodeCalls'),1);
 assert.equal(await evaluate('session===null'),true);
 assert.equal(await evaluate('canvas.width*canvas.height'),1);
 assert.equal(await source.locator('#video').evaluate(v=>v.paused),false);
 report.push('PASS 图像转换挂起后只提交一次转换，停止采集并释放画布，原视频继续播放');
 // Exercise a fresh start and let the shared fixture verify the real Stop button.
 await worker.evaluate(async code=>{const tab=(await chrome.tabs.query({active:true,currentWindow:true}))[0];await startCapture(code,tab);},code);
 await until(async()=>(await worker.evaluate(()=>getState())).active);
 await root.detach();
` + source.slice(end);
Function('require', '__dirname', source)(createRequire(file), __dirname);

