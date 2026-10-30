const input = document.getElementById('code'), status = document.getElementById('status');
const start = document.getElementById('start'), stop = document.getElementById('stop');
const repair=document.getElementById('repair'), reload=document.getElementById('reload');
let busy=false, reading=false, revision=0;
const loaded=chrome.storage.local.get('connectionCode').then(value=>{if(!input.value)input.value=value.connectionCode||'';});
function setBusy(value){busy=value;for(const button of [start,stop,repair])button.disabled=value;input.disabled=value;}
function showState(result){
  if(result?.changing)status.textContent='正在处理连接…';
  else if(result?.active && result.reconnecting)status.textContent='正在恢复：'+result.title+'\n'+result.error+'\n也可点击“修复并重连”。';
  else if(result?.active)status.textContent='正在预览：'+result.title+'\n可切换页面，鼠标悬停时观看。';
  else status.textContent=result?.error||'等待连接';
}
async function refreshState(){
  if(busy||reading)return;
  reading=true;const before=revision;
  try {
    const result=await HoverCore.withTimeout(chrome.runtime.sendMessage({type:'GET_STATE'}),4500);
    if(!busy && before===revision)showState(result);
  }catch{if(!busy && before===revision)status.textContent='扩展暂未响应。可直接点击下方“重新载入扩展”，再打开面板连接。';}
  finally{reading=false;}
}
async function connect(type){
  if(busy)return;revision++;setBusy(true);
  status.textContent=type==='REPAIR_CAPTURE'?'正在清理旧连接并重新采集…':'正在连接…';
  try {
    await loaded;HoverCore.parseCode(input.value);
    await chrome.storage.local.set({connectionCode:input.value.trim()});
    const result=await HoverCore.withTimeout(chrome.runtime.sendMessage({type,code:input.value.trim()}),30000);
    if(!result?.ok)throw new Error(result?.error||'连接失败，请点击“修复并重连”。');
    status.textContent = '已连接：' + result.title + '\n现在可切换页面，鼠标悬停时观看。';
  } catch (error) { status.textContent = error.message; }
  finally {setBusy(false);}
}
start.addEventListener('click',()=>connect('START_CAPTURE'));
repair.addEventListener('click',()=>connect('REPAIR_CAPTURE'));
stop.addEventListener('click', async () => {
  if(busy)return;revision++;setBusy(true);status.textContent='正在停止连接…';
  try {
    const result=await HoverCore.withTimeout(chrome.runtime.sendMessage({type:'STOP_CAPTURE'}),8000);
    if(!result?.ok)throw new Error(result?.error||'停止失败，请重新载入扩展。');
    status.textContent='连接已停止，视频仍可在原网页播放。';
  }
  catch (error) { status.textContent = error.message; }
  finally{setBusy(false);}
});
reload.addEventListener('click',async()=>{
  revision++;reload.disabled=true;
  try {
    // A browser API call works even if the service worker is unresponsive.
    await loaded;await chrome.storage.local.set({connectionCode:input.value.trim()});
    chrome.runtime.reload();
  }catch(error){status.textContent=error.message;reload.disabled=false;}
});
void refreshState();setInterval(refreshState,2000);
