const test=require('node:test'),assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {randomUUID}=require('node:crypto');
const folder=path.join(__dirname,'..'), core=require(path.join(folder,'core.js'));
const code='52637:'+'A'.repeat(32),clone=value=>value===undefined?undefined:JSON.parse(JSON.stringify(value));
const flush=()=>new Promise(resolve=>setImmediate(resolve));
function background(){
  const data={captureState:{active:false}}, flags={open:false,closed:0,created:0,reloaded:0,hangStop:false,hangStatus:false,hangStart:false,failStart:false,releasePending:0,releaseChecks:0,targetTabId:null,otherActive:false,tabMissing:false};
  let listener,commandListener,alarmListener,live={active:false};
  const chrome={
    runtime:{id:'test',getURL:file=>'chrome-extension://test/'+file,
      getContexts:async()=>flags.open?[{}]:[],reload:()=>flags.reloaded++,
      onMessage:{addListener:fn=>listener=fn},
      sendMessage:async message=>{
        if(message.type==='STOP'){
          if(flags.hangStop)return new Promise(()=>{});
          live={active:false};return {ok:true};
        }
        if(message.type==='START'){
          if(flags.hangStart)return new Promise(()=>{});
          if(flags.failStart)return {ok:false,error:'模拟采集启动失败'};
          live={active:true,sessionId:message.sessionId,title:message.title,tabId:message.tabId};return {ok:true};
        }
        if(message.type==='STATUS')return flags.hangStatus?new Promise(()=>{}):clone(live);
        throw Error('unexpected message');
      }},
    commands:{onCommand:{addListener:fn=>commandListener=fn}},
    alarms:{create:async()=>{},clear:async()=>{},onAlarm:{addListener:fn=>alarmListener=fn}},
    offscreen:{createDocument:async()=>{flags.open=true;flags.created++;},closeDocument:async()=>{flags.open=false;flags.closed++;live={active:false};}},
    storage:{session:{get:async key=>({[key]:clone(data[key])}),set:async value=>Object.assign(data,clone(value)),remove:async key=>{delete data[key];}}},
    tabs:{query:async()=>[{id:flags.otherActive?99:7,title:'测试视频',url:'https://example.test/video'}],get:async id=>{if(flags.tabMissing)throw Error('tab closed');return {id,title:'原视频',url:'https://example.test/video'};}},
    scripting:{executeScript:async()=>[{result:null}]},
    tabCapture:{getMediaStreamId:async({targetTabId})=>{flags.targetTabId=targetTabId;assert.ok(flags.releasePending<=0);return 'stream';},
      getCapturedTabs:async()=>{flags.releaseChecks++;return flags.releasePending-->0?[{tabId:7,status:'active'}]:[];}}
  };
  const sandbox=vm.createContext({chrome,crypto:{randomUUID},console,AbortSignal,setTimeout,Date,
    fetch:async()=>({ok:true,status:200,json:async()=>({version:3})}),
    importScripts:()=>{},HoverCore:{...core,withTimeout:(task,ms)=>core.withTimeout(task,Math.min(ms,35))}});
  vm.runInContext(fs.readFileSync(path.join(folder,'background.js'),'utf8'),sandbox);
  function message(type,extra={},sender={id:'test',url:'chrome-extension://test/popup.html'}){
    return new Promise(resolve=>listener({type,...extra},sender,resolve));
  }
  return {sandbox,data,flags,message,command:command=>commandListener(command),recover:()=>vm.runInContext('recoverCapture()',sandbox),alarm:()=>alarmListener({name:'hover-capture-health'})};
}
test('missing offscreen document clears saved active state; STOP does not create a new document',async()=>{
  const b=background();b.data.captureState={active:true,sessionId:'old'};
  assert.equal((await b.message('GET_STATE')).active,false);
  assert.equal((await b.message('STOP_CAPTURE')).ok,true);
  assert.equal(b.flags.created,0);
});
test('repair closes a hung STOP receiver and creates a fresh capture; old END cannot clear new state',async()=>{
  const b=background();b.flags.open=true;b.flags.hangStop=true;
  b.data.captureState={active:true,sessionId:'old'};
  assert.equal((await b.message('REPAIR_CAPTURE',{code})).ok,true);
  assert.equal(b.flags.closed,1);assert.equal(b.flags.created,1);
  const id=b.data.captureState.sessionId;
  await b.message('CAPTURE_ENDED',{sessionId:'old',error:'late error'},{id:'test',url:'chrome-extension://test/offscreen.html'});
  assert.equal(b.data.captureState.sessionId,id);assert.equal(b.data.captureState.active,true);
});
test('failed and timed-out START are cleaned up and a later repair can succeed',async()=>{
  for(const flag of ['failStart','hangStart']){
    const b=background();b.flags[flag]=true;
    assert.equal((await b.message('REPAIR_CAPTURE',{code})).ok,false);
    assert.equal(b.data.captureState.active,false);assert.equal(b.flags.open,false);
    b.flags[flag]=false;
    assert.equal((await b.message('REPAIR_CAPTURE',{code})).ok,true);
  }
});
test('unresponsive STATUS returns an actionable error instead of waiting forever',async()=>{
  const b=background();b.flags.open=true;b.flags.hangStatus=true;
  b.data.captureState={active:true,sessionId:'old'};
  const result=await b.message('GET_STATE');assert.equal(result.active,false);assert.match(result.error,/超时/);
});
test('concurrent captures are rejected and the recovery shortcut reloads directly',async()=>{
  const b=background();b.flags.hangStart=true;
  const first=b.message('START_CAPTURE',{code});await flush();
  const second=await b.message('REPAIR_CAPTURE',{code});assert.equal(second.ok,false);assert.match(second.error,/上一次/);
  await first;b.command('reload-extension');assert.equal(b.flags.reloaded,1);
});
function offscreen(){
  let listener,now=100000,mode='ok',tick,healthTick,cleared=0,trackStops=0,posted=0,audiosClosed=0;
  const ended=[],tracks=[];
  class Clock extends Date{static now(){return now;}}
  const video={readyState:4,videoWidth:640,videoHeight:360,currentTime:0,paused:false,play:async()=>{video.paused=false;},srcObject:null};
  const canvas={getContext:()=>({drawImage(){}}),toBlob:fn=>{if(mode!=='blob-hang')fn({bytes:'image'});}};
  const makeStream=()=>{
    const track={readyState:'live',stop(){this.readyState='ended';trackStops++;},addEventListener(){}};tracks.push(track);
    return {getTracks:()=>[track],getVideoTracks:()=>[track],getAudioTracks:()=>[{}]};
  };
  const sandbox=vm.createContext({console,Date:Clock,AbortController,AbortSignal,
    HoverCore:{...core,withTimeout:(task,ms)=>core.withTimeout(task,Math.min(ms,35))},
    document:{getElementById:id=>id==='video'?video:canvas},
    navigator:{mediaDevices:{getUserMedia:async()=>makeStream()}},
    AudioContext:class{createMediaStreamSource(){return {connect(){}};}get destination(){return {};}
      async resume(){} close(){audiosClosed++;return mode==='audio-hang'?new Promise(()=>{}):Promise.resolve();}},
    setTimeout,clearTimeout,setInterval:(fn,ms)=>{if(ms===67)tick=fn;else healthTick=fn;return ms;},clearInterval:()=>cleared++,
    fetch:async url=>{
      if(url.endsWith('/stop') || url.endsWith('/start'))return {ok:true};posted++;
      if(mode==='offline')throw new Error('network down');
      return {ok:mode!=='forbidden',status:mode==='forbidden'?403:200};
    },
    chrome:{runtime:{id:'test',onMessage:{addListener:fn=>listener=fn},sendMessage:async message=>{ended.push(message);return {ok:true};}}}
  });
  vm.runInContext(fs.readFileSync(path.join(folder,'offscreen.js'),'utf8'),sandbox);
  const message=(type,extra={})=>new Promise(resolve=>listener({target:'offscreen',type,...extra},{id:'test'},resolve));
  return {sandbox,message,video,set mode(value){mode=value;},advance:ms=>{now+=ms;if(!video.paused)video.currentTime+=ms/1000;},
    tick:async()=>{tick();await flush();},stats:()=>({cleared,trackStops,posted,audiosClosed,ended}),tracks,
    health:async()=>{healthTick();await flush();},
    start:()=>message('START',{streamId:'stream',connection:core.parseCode(code),title:'视频',tabId:7,sessionId:randomUUID()})};
}
test('temporary receiver failures retry on the same capture and automatically recover',async()=>{
  const o=offscreen();await o.start();await flush();assert.equal(o.stats().posted,1);
  o.mode='offline';await o.tick();assert.equal((await o.message('STATUS')).reconnecting,true);
  o.mode='ok';o.advance(501);await o.tick();
  assert.equal((await o.message('STATUS')).reconnecting,false);assert.equal(o.stats().trackStops,0);
  await o.message('STOP');assert.equal(o.stats().trackStops,1);
});
test('invalid pairing code ends capture immediately and retains the recovery message',async()=>{
  const o=offscreen();o.mode='forbidden';await o.start();await flush();await flush();
  assert.equal((await o.message('STATUS')).active,false);
  assert.match((await o.message('STATUS')).error,/连接码已失效/);assert.equal(o.stats().trackStops,1);
});
test('missing video frames request recovery without silently terminating capture',async()=>{
  const o=offscreen();o.video.readyState=0;await o.start();await flush();
  assert.equal((await o.message('STATUS')).reconnecting,true);
  o.advance(61000);await o.tick();await flush();
  assert.equal((await o.message('STATUS')).active,true);assert.equal((await o.message('STATUS')).sourceStalled,true);
  assert.equal(o.stats().trackStops,0);await o.message('STOP');
});
test('a stuck canvas encoder and stuck audio shutdown cannot block STOP or the next START',async()=>{
  const o=offscreen();o.mode='blob-hang';await o.start();
  await new Promise(resolve=>setTimeout(resolve,45));
  assert.equal((await o.message('STATUS')).reconnecting,true);
  o.mode='audio-hang';assert.equal((await o.message('STOP')).ok,true);assert.equal(o.stats().trackStops,1);
  o.mode='ok';assert.equal((await o.start()).ok,true);await o.message('STOP');
});
test('waits for delayed Chromium stream release before reacquiring capture',async()=>{
  const b=background();b.flags.open=true;b.flags.releasePending=2;
  assert.equal((await b.message('REPAIR_CAPTURE',{code})).ok,true);
  assert.ok(b.flags.releaseChecks>=3);assert.equal(b.flags.targetTabId,7);
});
test('lost offscreen document recovers the ORIGINAL tab while another tab is active',async()=>{
  const b=background();await b.message('START_CAPTURE',{code});
  const oldId=b.data.captureState.sessionId;
  b.flags.open=false;b.flags.otherActive=true;
  await b.recover();
  assert.equal(b.flags.targetTabId,7);assert.equal(b.data.captureState.active,true);
  assert.notEqual(b.data.captureState.sessionId,oldId);
});
test('manual STOP disables automatic recovery; closing the source tab also disables it',async()=>{
  const b=background();await b.message('START_CAPTURE',{code});
  await b.message('STOP_CAPTURE');const created=b.flags.created;
  await b.recover();assert.equal(b.flags.created,created);assert.equal(b.data.captureRequest,undefined);
  await b.message('START_CAPTURE',{code});b.flags.open=false;b.flags.tabMissing=true;
  await b.recover();assert.equal(b.data.captureRequest,undefined);
});
test('receiver outage longer than one minute still recovers on the original stream',async()=>{
  const o=offscreen();await o.start();await flush();
  o.mode='offline';await o.tick();o.advance(90000);await o.tick();
  assert.equal((await o.message('STATUS')).active,true);assert.equal(o.stats().trackStops,0);
  o.mode='ok';o.advance(3100);await o.tick();
  assert.equal((await o.message('STATUS')).reconnecting,false);assert.equal(o.stats().trackStops,0);
  await o.message('STOP');
});
test('stalled frame pump notifies recovery even when no popup is open',async()=>{
  const o=offscreen();o.video.readyState=0;await o.start();await flush();o.advance(16000);
  await o.health();assert.ok(o.stats().ended.some(message=>message.type==='RECOVER_CAPTURE'));
  await o.message('STOP');
});

test('GET_STATE during teardown cannot erase the stop intent or restart capture',async()=>{
  const b=background();await b.message('START_CAPTURE',{code});
  const id=b.data.captureRequest.sessionId,send=b.sandbox.chrome.runtime.sendMessage;
  b.sandbox.chrome.runtime.sendMessage=async m=>m.type==='STATUS'?{active:false}:send(m);
  await b.message('GET_STATE');
  await b.message('CAPTURE_ENDED',{sessionId:id},{id:'test',url:'chrome-extension://test/offscreen.html'});
  assert.equal(b.data.captureRequest,undefined);
  const created=b.flags.created;b.flags.open=false;await b.recover();assert.equal(b.flags.created,created);
});

test('failed manual switch cancels the previous tab recovery request',async()=>{
  const b=background();await b.message('START_CAPTURE',{code});
  b.flags.otherActive=true;b.flags.failStart=true;
  assert.equal((await b.message('START_CAPTURE',{code})).ok,false);
  assert.equal(b.data.captureRequest,undefined);
  const created=b.flags.created;b.flags.failStart=false;await b.recover();assert.equal(b.flags.created,created);
});

test('capture ending during START cannot be committed as a live session',async()=>{
  const b=background(),send=b.sandbox.chrome.runtime.sendMessage;
  b.sandbox.chrome.runtime.sendMessage=async m=>{
    const result=await send(m);
    if(m.type==='START')await b.message('CAPTURE_ENDED',{sessionId:m.sessionId},{id:'test',url:'chrome-extension://test/offscreen.html'});
    return result;
  };
  assert.equal((await b.message('START_CAPTURE',{code})).ok,false);
  assert.equal(b.data.captureRequest,undefined);assert.equal(b.flags.open,false);
});

test('unchanged decoded video time triggers recovery even while JPEG uploads succeed',async()=>{
  const o=offscreen();await o.start();await flush();
  o.advance(16000);o.video.currentTime=0;await o.tick();
  assert.equal((await o.message('STATUS')).sourceStalled,true);
  await o.health();assert.ok(o.stats().ended.some(m=>m.type==='RECOVER_CAPTURE'));
  await o.message('STOP');
});

test('paused internal playback resumes, and static pixels with advancing time stay healthy',async()=>{
  const o=offscreen();await o.start();await flush();
  o.video.paused=true;await o.health();assert.equal(o.video.paused,false);
  for(let i=0;i<20;i++){o.advance(1000);await o.tick();}
  assert.equal((await o.message('STATUS')).sourceStalled,false);
  assert.equal(o.stats().ended.filter(m=>m.type==='RECOVER_CAPTURE').length,0);
  await o.message('STOP');
});
