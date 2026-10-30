// Reuse the isolated Edge fixture, replacing its already-tested outage scenario
// with a decoded-video freeze/recovery regression.
const fs = require('node:fs'), path = require('node:path');
const {createRequire} = require('node:module');
const file = path.join(__dirname, 'browser-recovery.cjs');
let source = fs.readFileSync(file, 'utf8').replace("'edge-'+Date.now()", "'audit-edge-'+Date.now()");
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
 const startTime=await evaluate('video.pause(); video.currentTime');
 const beforeFreeze=frames;
 await sleep(17000);
 const endTime=await evaluate('video.currentTime');
 const state=await worker.evaluate(()=>getState());
 assert.ok(endTime>startTime+5,'internal playback did not automatically resume');
 assert.ok(frames>beforeFreeze+10);
 assert.equal(state.sourceStalled,false);assert.equal(state.reconnecting,false);
 report.push('PASS 真实 Edge：强制暂停内部 video 后自动恢复播放，17 秒后时钟正常推进，无需重新载入扩展。');
 await evaluate('video.play().then(()=>true)');
 await root.detach();
` + source.slice(end);
Function('require', '__dirname', source)(createRequire(file), __dirname);
