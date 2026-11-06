// Real Edge/media APIs, isolated profile and receiver; never use the live desktop port.
const fs=require('node:fs'),path=require('node:path'),http=require('node:http');
const assert=require('node:assert/strict'),crypto=require('node:crypto');
const {chromium}=require('C:/Users/lenovo/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const extension=path.resolve(__dirname,'..'),project=path.resolve(extension,'..');
const out=path.join(__dirname,'results','edge-'+Date.now());fs.mkdirSync(path.join(out,'tmp'),{recursive:true});
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const report=[];let browser,receiver,pages,offline=false,frames=0,lastFrame=0;
const pairingKey=crypto.randomBytes(24).toString('base64url'),code='52637:'+pairingKey;
async function until(fn,ms=15000){const deadline=Date.now()+ms;while(Date.now()<deadline){if(await fn())return;await sleep(150);}throw Error('测试状态等待超时');}
async function listen(server){await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});return server.address().port;}
async function main(){
 receiver=http.createServer((req,res)=>{
  if(req.headers.origin?.startsWith('chrome-extension://'))res.setHeader('Access-Control-Allow-Origin',req.headers.origin);
  res.setHeader('Access-Control-Allow-Methods','GET,POST,OPTIONS');res.setHeader('Access-Control-Allow-Headers','Content-Type,X-Hover-Key,X-Hover-Session,X-Hover-Sequence');
  res.setHeader('Access-Control-Allow-Private-Network','true');res.setHeader('Content-Type','application/json');
  if(req.method==='OPTIONS'){res.writeHead(204);return res.end();}
  if(req.headers['x-hover-key']!==pairingKey){res.writeHead(403);return res.end('{}');}
  if(req.url==='/status')return res.end(JSON.stringify({version:3,frames,recent:Date.now()-lastFrame<3000}));
  if(req.url==='/start')return res.end('{}');
  if(req.url==='/stop'){lastFrame=0;return res.end('{}');}
  if(req.url==='/frame'){
   const chunks=[];req.on('data',b=>chunks.push(b));req.on('end',()=>{
    if(offline){res.writeHead(503);return res.end('{}');}
    const image=Buffer.concat(chunks);
    if(image[0]!==255||image[1]!==216){res.writeHead(415);return res.end('{}');}
    frames++;lastFrame=Date.now();res.end('{}');
   });return;
  }
  res.writeHead(404);res.end('{}');
 });
 const receiverPort=await listen(receiver);
 pages=http.createServer((req,res)=>{
  res.setHeader('Content-Type','text/html;charset=utf-8');
  res.end(req.url==='/video'?fs.readFileSync(path.join(project,'验证结果','browser-test.html')):'<!doctype html><title>其他工作标签页</title><p>此页不应被采集</p>');
 });
 const pagePort=await listen(pages);
 browser=await chromium.launchPersistentContext(path.join(out,'profile'),{
  executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:false,
  ignoreDefaultArgs:['--disable-extensions'],args:['--headless=new','--disable-extensions-except='+extension,'--load-extension='+extension],
  env:{...process.env,TEMP:path.join(out,'tmp'),TMP:path.join(out,'tmp')},viewport:{width:960,height:760}});
 await until(()=>browser.serviceWorkers().length);
 const worker=browser.serviceWorkers()[0],id=worker.url().split('/')[2];
 // Production code still validates the pairing format. Only this test worker's
 // receiver URL changes, so the real desktop at :52637 remains untouched.
 await worker.evaluate(port=>{const parse=HoverCore.parseCode;HoverCore.parseCode=raw=>({...parse(raw),url:'http://127.0.0.1:'+port});},receiverPort);
 const source=await browser.newPage();await source.goto('http://127.0.0.1:'+pagePort+'/video');
 await source.locator('#start').click();await until(async()=>/播放中/.test(await source.locator('#status').textContent()));
 await source.bringToFront();const cdp=await browser.newCDPSession(source);
 await cdp.send('Input.dispatchKeyEvent',{type:'rawKeyDown',modifiers:9,windowsVirtualKeyCode:72,nativeVirtualKeyCode:72,key:'H',code:'KeyH',isSystemKey:true});
 await cdp.send('Input.dispatchKeyEvent',{type:'keyUp',modifiers:9,windowsVirtualKeyCode:72,nativeVirtualKeyCode:72,key:'H',code:'KeyH',isSystemKey:true});
 assert.equal((await worker.evaluate(code=>startCapture(code),code)).ok,true);
 await until(()=>frames>4);const original=await worker.evaluate(()=>getState());
 report.push('PASS Edge '+(await browser.browser().version())+' 真实音视频采集');
 const other=await browser.newPage();await other.goto('http://127.0.0.1:'+pagePort+'/other');await other.bringToFront();
 const beforeLoss=frames;
 await worker.evaluate(async()=>{await chrome.offscreen.closeDocument();await chrome.alarms.create('hover-capture-health',{when:Date.now()+300});});
 await until(()=>frames>beforeLoss+4);
 const restored=await worker.evaluate(()=>getState());assert.equal(restored.active,true);assert.equal(restored.tabId,original.tabId);
 assert.notEqual(restored.sessionId,original.sessionId);
 const [active]=await worker.evaluate(()=>chrome.tabs.query({active:true,currentWindow:true}));assert.notEqual(active.id,original.tabId);
 report.push('PASS 未打开面板、未切回视频、未重新授权或重载：采集页丢失后由后台闹钟恢复原视频页');
 // The old implementation stopped its live stream after one minute of failure.
 offline=true;const outageId=restored.sessionId;
 console.log('正在实测 70 秒接收器故障；预期保留原始采集流…');
 await sleep(35000);console.log('已完成 35/70 秒故障等待。');await sleep(35000);
 const during=await worker.evaluate(()=>getState());assert.equal(during.active,true);assert.equal(during.sessionId,outageId);assert.equal(during.reconnecting,true);
 offline=false;const beforeResume=frames;await until(()=>frames>beforeResume+4);
 const resumed=await worker.evaluate(()=>getState());assert.equal(resumed.sessionId,outageId);assert.equal(resumed.reconnecting,false);
 report.push('PASS 接收器故障 70 秒后自动恢复同一采集流，全程无需重连或重载');
 await worker.evaluate(()=>chrome.storage.local.set({connectionCode:'52637:'+'A'.repeat(32)}));
 const popup=await browser.newPage();await popup.goto('chrome-extension://'+id+'/popup.html');
 assert.equal(await popup.evaluate(()=>chrome.runtime.getManifest().version),JSON.parse(fs.readFileSync(path.join(extension,'manifest.json'),'utf8')).version);
 await popup.locator('#status').filter({hasText:'正在预览'}).waitFor();
 await popup.locator('body').screenshot({path:path.join(out,'popup.png')});
 await popup.locator('#stop').click();await until(async()=>!(await worker.evaluate(()=>chrome.storage.session.get('captureRequest'))).captureRequest);
 await until(()=>Date.now()-lastFrame>3000);const stoppedFrames=frames;
 await worker.evaluate(()=>chrome.alarms.create('hover-capture-health',{when:Date.now()+300}));await sleep(1500);
 assert.equal(frames,stoppedFrames);assert.equal(await source.locator('#video').evaluate(v=>v.paused),false);
 report.push('PASS 实际面板停止后不会被后台自动重启，原网页继续播放');
 report.push('NOTE 测试使用独立 Edge 配置、本机生成视频及临时 HTTP 接收器；未验证用户当前网站或桌面悬停窗口。');
}
main().catch(error=>{report.push('FAIL '+error.stack);process.exitCode=1;}).finally(async()=>{
 if(browser)await browser.close().catch(()=>{});
 if(pages)pages.close();if(receiver)receiver.close();
 fs.writeFileSync(path.join(out,'report.txt'),report.join('\n'),'utf8');console.log(report.join('\n'));console.log('报告目录：'+out);
});

