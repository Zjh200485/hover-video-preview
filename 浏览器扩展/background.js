importScripts('core.js');
let creating, changing=false;
const endedSessions=new Set();
const timeout=HoverCore.withTimeout;
async function offscreenContexts() {
  return timeout(chrome.runtime.getContexts({contextTypes:['OFFSCREEN_DOCUMENT'],documentUrls:[chrome.runtime.getURL('offscreen.html')]}),2000);
}
async function ensureOffscreen() {
  const contexts = await offscreenContexts();
  if (contexts.length) return;
  if (!creating) creating = chrome.offscreen.createDocument({url:'offscreen.html',reasons:['USER_MEDIA'],justification:'持续读取用户主动选择的视频标签页，并保留声音播放，供本机悬停预览。'}).finally(()=>{creating=null});
  await timeout(creating,5000);
}
async function resetCapture() {
  // Invalidate the old generation before waiting for its shutdown notification.
  await chrome.storage.session.set({captureState:{active:false}});
  if (!(await offscreenContexts()).length) return;
  try { await timeout(chrome.runtime.sendMessage({target:'offscreen',type:'STOP'}),2200); }
  catch { /* Closing the document below also recovers an unresponsive receiver. */ }
  await timeout(chrome.offscreen.closeDocument(),2500);
}
async function exclusively(task) {
  if (changing) throw new Error('正在处理上一次连接操作，请稍后再试。');
  changing=true;
  try { return await task(); } finally { changing=false; }
}
async function waitForRelease(tabId) {
  // Closing the offscreen document does not synchronously release Chromium's stream.
  const deadline=Date.now()+4000;
  while ((await timeout(chrome.tabCapture.getCapturedTabs(),1500)).some(tab=>tab.tabId===tabId && ['active','pending'].includes(tab.status))) {
    if(Date.now()>=deadline)throw new Error('旧采集流尚未释放，请稍后再点击“修复并重连”。');
    await new Promise(resolve=>setTimeout(resolve,150));
  }
}
async function getVideoCrop(tabId) {
  try {
    const results = await chrome.scripting.executeScript({target:{tabId},func:()=>{
      const videos = [...document.querySelectorAll('video')].map(v=>({v,r:v.getBoundingClientRect()}))
        .filter(o=>o.r.width>80 && o.r.height>50 && o.r.bottom>0 && o.r.right>0 && o.r.top<innerHeight && o.r.left<innerWidth)
        .sort((a,b)=>b.r.width*b.r.height-a.r.width*a.r.height);
      if (!videos.length) return null;
      const r=videos[0].r;
      return {left:Math.max(0,r.left/innerWidth),top:Math.max(0,r.top/innerHeight),right:Math.min(1,r.right/innerWidth),bottom:Math.min(1,r.bottom/innerHeight)};
    }});
    return results[0]?.result || null;
  } catch { return null; }
}
async function startCapture(code, originalTab=null) {
  return exclusively(async()=>{
  const connection = HoverCore.parseCode(code);
  let response;
  try { response = await fetch(connection.url+'/status',{headers:{'X-Hover-Key':connection.key},signal:AbortSignal.timeout(3000)}); }
  catch { throw new Error('无法连接桌面工具。请先启动工具，并复制本次启动的连接码。'); }
  if (response.status===403) throw new Error('连接码已失效，请从桌面工具重新复制。');
  if (!response.ok || (await response.json()).version!==3) throw new Error('桌面工具版本不匹配，请运行配套的新版工具。');
  const tab = originalTab || (await timeout(chrome.tabs.query({active:true,currentWindow:true}),2000))[0];
  if (!tab?.id || (!originalTab && !/^https?:\/\//i.test(tab.url||''))) throw new Error('请先切到正在播放视频的网页，再打开这个扩展。');
  const sessionId=crypto.randomUUID(),title=tab.title||'视频标签页';
  // The recovery request is the authority for capture intent, not GET_STATE.
  // Replacing a manual selection must never leave the previous tab scheduled.
  await chrome.storage.session.remove('captureRequest');
  await chrome.alarms.clear('hover-capture-health');
  try {
  await resetCapture();
  await waitForRelease(tab.id);
  await ensureOffscreen();
  await chrome.storage.session.set({captureRequest:{tabId:tab.id,title,code,sessionId}});
  const crop = await timeout(getVideoCrop(tab.id),2000).catch(()=>null);
  const streamId = await timeout(chrome.tabCapture.getMediaStreamId({targetTabId:tab.id}),5000);
  const result = await timeout(chrome.runtime.sendMessage({target:'offscreen',type:'START',streamId,connection,crop,title,tabId:tab.id,sessionId}),12000);
  if (!result?.ok) throw new Error(result?.error||'无法读取标签页画面。');
  const live=await timeout(chrome.runtime.sendMessage({target:'offscreen',type:'STATUS'}),1500);
  if (!live?.active || live.sessionId!==sessionId || endedSessions.has(sessionId)) throw new Error(live?.error||'视频采集已结束，请再次连接。');
  await chrome.storage.session.set({captureState:live});
  await chrome.storage.session.set({captureRequest:{tabId:tab.id,title,code,sessionId}});
  await chrome.alarms.create('hover-capture-health',{periodInMinutes:1});
  if(endedSessions.has(sessionId))throw new Error('视频采集已结束，请再次连接。');
  return {ok:true,title};
  } catch(error) {
    await chrome.storage.session.remove('captureRequest');
    await chrome.alarms.clear('hover-capture-health');
    await resetCapture().catch(()=>{});
    await chrome.storage.session.set({captureState:{active:false,error:error.message}});
    throw error;
  }
  });
}
async function recoverCapture(expectedSessionId) {
  if(changing)return;
  const request=(await chrome.storage.session.get('captureRequest')).captureRequest;
  if(!request || (expectedSessionId && request.sessionId!==expectedSessionId))return;
  const live=await getState();
  if(live.active && !live.sourceStalled)return;
  let tab;
  try { tab=await chrome.tabs.get(request.tabId); }
  catch {
    await chrome.storage.session.remove('captureRequest');
    await chrome.alarms.clear('hover-capture-health');
    return;
  }
  const currentRequest=(await chrome.storage.session.get('captureRequest')).captureRequest;
  if(!currentRequest || currentRequest.sessionId!==request.sessionId || changing)return;
  // Recover the original user-selected tab; never capture the currently active other tab.
  try { await startCapture(request.code,tab); }
  catch(error) {
    await chrome.storage.session.set({captureState:{active:false,error:'自动恢复暂未成功：'+error.message+' 可回到原视频页点击“修复并重连”，无需重新导入扩展。'}});
  }
}
async function getState() {
  const saved=(await chrome.storage.session.get('captureState')).captureState||{active:false};
  if (changing) return {...saved,changing:true};
  let live;
  try {
    live=(await offscreenContexts()).length
      ? await timeout(chrome.runtime.sendMessage({target:'offscreen',type:'STATUS'}),1500)
      : {active:false,error:saved.active?'后台采集已中断，请点击“修复并重连”。':saved.error||''};
    if (typeof live?.active!=='boolean') throw new Error('后台采集未响应，请点击“修复并重连”。');
  } catch(error) { live={active:false,error:error.message}; }
  const current=(await chrome.storage.session.get('captureState')).captureState;
  if (!changing && current?.sessionId===saved.sessionId) await chrome.storage.session.set({captureState:live});
  return changing ? {...(current||saved),changing:true} : live;
}
chrome.runtime.onMessage.addListener((message,sender,reply)=>{
  if (sender.id!==chrome.runtime.id || message.target==='offscreen') return;
  (async()=>{
    if (message.type==='START_CAPTURE' || message.type==='REPAIR_CAPTURE') return startCapture(message.code);
    if (message.type==='STOP_CAPTURE') {
      return exclusively(async()=>{
        await chrome.storage.session.remove('captureRequest');
        await chrome.alarms.clear('hover-capture-health');
        await resetCapture();return {ok:true};
      });
    }
    if (message.type==='RECOVER_CAPTURE') {
      if(sender.url!==chrome.runtime.getURL('offscreen.html'))return {ok:false};
      await recoverCapture(message.sessionId);return {ok:true};
    }
    if (message.type==='GET_STATE') return getState();
    if (message.type==='CAPTURE_ENDED') {
      if (sender.url!==chrome.runtime.getURL('offscreen.html')) return {ok:false};
      if(!message.sessionId)return {ok:true};
      endedSessions.add(message.sessionId);
      if(endedSessions.size>64)endedSessions.delete(endedSessions.values().next().value);
      // STOP may report back while START is awaiting its teardown. Do not queue
      // behind START; its final live check rejects a session that ended here.
      if(changing)return {ok:true};
      return exclusively(async()=>{
        const request=(await chrome.storage.session.get('captureRequest')).captureRequest;
        if(request?.sessionId===message.sessionId){
          await chrome.storage.session.remove('captureRequest');
          await chrome.alarms.clear('hover-capture-health');
          await chrome.storage.session.set({captureState:{active:false,error:message.error||''}});
        }
        return {ok:true};
      });
    }
    return {ok:false,error:'未知操作。'};
  })().then(reply).catch(error=>reply({ok:false,error:error.message}));
  return true;
});
chrome.commands.onCommand.addListener(command=>{
  if (command==='reload-extension') chrome.runtime.reload();
});
chrome.alarms.onAlarm.addListener(alarm=>{
  if(alarm.name==='hover-capture-health')void recoverCapture().catch(()=>{});
});
// Alarms may be cleared by a browser restart. Re-establish only for a session
// with an explicitly selected tab; session storage is cleared when Edge exits.
chrome.storage.session.get('captureRequest').then(({captureRequest})=>{
  if(captureRequest)return chrome.alarms.create('hover-capture-health',{periodInMinutes:1});
}).catch(()=>{});
