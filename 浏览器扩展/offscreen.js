const video = document.getElementById('video'), canvas = document.getElementById('canvas');
const context = canvas.getContext('2d',{alpha:false}), timeout=HoverCore.withTimeout;
let session=null, changing=Promise.resolve(), lastError='';
function sampleVideoProgress(current){
  const time=video.currentTime;
  if(Number.isFinite(time) && time!==current.videoTime){current.videoTime=time;current.lastVideoAt=Date.now();}
}
function captureState() {
  const current=session;
  if (!current) return {active:false,error:lastError};
  const active=current.stream.getVideoTracks().some(track=>track.readyState==='live');
  sampleVideoProgress(current);
  const sourceStalled=Date.now()-current.lastVideoAt>15000 || Date.now()-(current.lastEncodedAt||current.startedAt)>15000;
  const stalled=Date.now()-(current.lastFrameAt||current.startedAt)>8000 || sourceStalled;
  return {active,sessionId:current.sessionId,title:current.title,tabId:current.tabId,
    reconnecting:current.failures>0 || stalled,lastFrameAt:current.lastFrameAt,
    sourceStalled,
    error:current.failures?current.error:stalled?'暂未收到新画面，可以点击“修复并重连”。':''};
}
async function stopCapture(error='') {
  lastError=error;
  const current=session;session=null;
  if (!current) return;
  clearInterval(current.timer);current.abort?.abort();
  clearInterval(current.healthTimer);
  current.stream.getTracks().forEach(track=>track.stop());video.srcObject=null;
  if (current.audio) await timeout(current.audio.close(),700).catch(()=>{});
  if (current.frameTask) await timeout(current.frameTask,2800).catch(()=>{});
  await fetch(current.connection.url+'/stop',{method:'POST',headers:{'X-Hover-Key':current.connection.key,'X-Hover-Session':current.sessionId},signal:AbortSignal.timeout(700)}).catch(()=>{});
  await timeout(chrome.runtime.sendMessage({type:'CAPTURE_ENDED',sessionId:current.sessionId,error}),700).catch(()=>{});
}
async function sendFrame(current) {
  if (session!==current || current.busy || Date.now()<current.nextAttemptAt) return;
  current.busy=true;
  let fatalError='';
  try {
    if (video.readyState<2 || !video.videoWidth) throw new Error('暂未收到视频画面，正在等待播放器恢复。');
    sampleVideoProgress(current);
    const crop=HoverCore.normalizedCrop(current.crop,video.videoWidth,video.videoHeight);
    const scale=Math.min(1,1280/crop.width,1280/crop.height);
    const width=Math.max(2,Math.round(crop.width*scale)),height=Math.max(2,Math.round(crop.height*scale));
    if(canvas.width!==width || canvas.height!==height){canvas.width=width;canvas.height=height;}
    context.drawImage(video,crop.x,crop.y,crop.width,crop.height,0,0,width,height);
    const blob=await timeout(new Promise(resolve=>canvas.toBlob(resolve,'image/jpeg',.78)),2500,'画面转换超时，请点击“修复并重连”。');
    if(session!==current) return;
    if(!blob) throw new Error('暂时无法读取视频画面。');
    current.lastEncodedAt=Date.now();
    const controller=new AbortController();current.abort=controller;
    const timer=setTimeout(()=>controller.abort(),2500);
    try {
      const response=await fetch(current.connection.url+'/frame',{method:'POST',headers:{'Content-Type':'image/jpeg','X-Hover-Key':current.connection.key,'X-Hover-Session':current.sessionId,'X-Hover-Sequence':String(++current.sequence)},body:blob,signal:controller.signal});
      if(response.status===403){
        fatalError='连接码已失效。请从桌面工具重新复制连接码，再点击“修复并重连”。';
        throw new Error(fatalError);
      }
      if(response.status===409){fatalError='当前采集会话已停止或被替换，请重新连接视频。';throw new Error(fatalError);}
      if(!response.ok)throw new Error('桌面工具暂时无法接收画面，正在重试。');
      current.failures=0;current.error='';current.lastFrameAt=Date.now();current.failureSince=0;
    }finally{clearTimeout(timer);}
  }catch(error){
    if(session===current){
      current.failures++;current.error=error.message;
      current.failureSince ||= Date.now();
      current.nextAttemptAt=Date.now()+Math.min(3000,500*2**Math.min(current.failures-1,3));
      // Keep a live stream through receiver outages. A timeout is not a user
      // request to stop; the next successful upload resumes the preview.
    }
  }finally{current.busy=false;}
  // Do not await teardown from inside the frame promise it needs to drain.
  if(fatalError && session===current)void stopCapture(fatalError);
}
async function startCapture(message) {
  await stopCapture();
  const stream=await navigator.mediaDevices.getUserMedia({
    audio:{mandatory:{chromeMediaSource:'tab',chromeMediaSourceId:message.streamId}},
    video:{mandatory:{chromeMediaSource:'tab',chromeMediaSourceId:message.streamId,maxFrameRate:30}}
  });
  const current={stream,connection:message.connection,crop:message.crop,title:message.title,
    tabId:message.tabId,sessionId:message.sessionId,busy:false,failures:0,timer:null,audio:null,
    startedAt:Date.now(),lastFrameAt:0,lastEncodedAt:0,lastVideoAt:Date.now(),videoTime:NaN,sequence:0,nextAttemptAt:0,failureSince:0,error:'',nextRecoveryAt:0};session=current;
  try {
    const opened=await fetch(current.connection.url+'/start',{method:'POST',headers:{'X-Hover-Key':current.connection.key,'X-Hover-Session':current.sessionId},signal:AbortSignal.timeout(3000)});
    if(!opened.ok)throw new Error(opened.status===403?'连接码已失效，请从桌面工具重新复制。':'无法建立采集会话，请确认桌面工具已更新。');
    video.srcObject=stream;await video.play();
    // tabCapture redirects source audio: route it to the speakers exactly once.
    if(stream.getAudioTracks().length){current.audio=new AudioContext();current.audio.createMediaStreamSource(stream).connect(current.audio.destination);await current.audio.resume();}
    stream.getVideoTracks()[0].addEventListener('ended',()=>{if(session===current)void stopCapture('视频标签页已关闭或已停止提供画面。');},{once:true});
    const tick=()=>{if(!current.busy)current.frameTask=sendFrame(current);};
    current.timer=setInterval(tick,67);
    current.healthTimer=setInterval(()=>{
      if(session!==current)return;
      // This is our internal playback element. Resuming it does not unpause
      // the source website's player or override the browser's Stop Sharing.
      if(video.paused)void timeout(video.play(),1200).catch(()=>{});
      if(!captureState().sourceStalled || Date.now()<current.nextRecoveryAt)return;
      current.nextRecoveryAt=Date.now()+30000;
      void timeout(chrome.runtime.sendMessage({type:'RECOVER_CAPTURE',sessionId:current.sessionId}),15000).catch(()=>{});
    },5000);
    tick();return {ok:true};
  }catch(error){await stopCapture(error.message);throw error;}
}
chrome.runtime.onMessage.addListener((message,sender,reply)=>{
  if(sender.id!==chrome.runtime.id || message.target!=='offscreen')return;
  if(message.type==='STATUS'){reply(captureState());return;}
  if(!['START','STOP'].includes(message.type)){reply({ok:false,error:'未知采集操作。'});return;}
  const task=()=>message.type==='START'?startCapture(message):stopCapture().then(()=>({ok:true}));
  const operation=changing.then(task);changing=operation.catch(()=>{});
  operation.then(reply).catch(error=>reply({ok:false,error:error.message}));
  return true;
});
