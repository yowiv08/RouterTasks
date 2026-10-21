(() => {
  'use strict';
  const U = globalThis.RouterTasksUI, $ = id => document.getElementById(id);
  const el = (tag, text, cls) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; if (cls) node.className = cls; return node; };
  const id = () => crypto.randomUUID?.() || Array.from(crypto.getRandomValues(new Uint8Array(16)), n => n.toString(16).padStart(2, '0')).join('');
  const fields = form => form.elements;
  const displayClock = U.displayClock(), date = value => displayClock.format(value);
  let state = null, page = 'tasks', refreshing = false, bulkPending = false, timer, taskPage = 0, taskDraft, taskTemplate, siteDraft, detailId, detailOffset = 0;
  const selected = new Set();
  const qFields = [ ['concurrency','并发线程',1], ['maxAttemptsPerLane','每线程次数预算',1], ['intervalSeconds','排队间隔（秒）',.001], ['intervalMaxSeconds','随机间隔上限（可空）',.001], ['firstEventTimeoutSeconds','首事件超时（秒）',.001], ['responseTimeoutSeconds','完整响应总超时（秒）',.001], ['idleTimeoutSeconds','完整响应空闲超时（秒）',.001], ['maxResponseBytes','响应字节上限',1] ];
  const kFields = [ ['concurrency','并发线程',1], ['maxAttemptsPerLane','本阶段每线程预算（空为不限）',1], ['heartbeatSeconds','心跳间隔（秒）',.001], ['heartbeatMaxSeconds','随机心跳上限（可空）',.001], ['failureRetrySeconds','失败重试间隔（秒）',.001], ['firstEventTimeoutSeconds','首事件超时（秒）',.001], ['idleEvictionSeconds','失联窗口（秒）',.001], ['dropConfirmSeconds','掉线确认窗口（秒）',0], ['maxResponseBytes','响应字节上限',1] ];
  for (const [prefix, definitions, container] of [['q_',qFields,$('queueFields')],['k_',kFields,$('keepaliveFields')]]) {
    for (const [name,title,min] of definitions) { const label=el('label',title),input=el('input'); input.type='number'; input.name=prefix+name; input.min=String(min); input.step=['concurrency','maxAttemptsPerLane','maxResponseBytes'].includes(name)?'1':'any'; input.required=!name.endsWith('MaxSeconds')&&!(prefix==='k_'&&name==='maxAttemptsPerLane'); label.append(input); container.append(label); }
  }
  for (const [value,title] of Object.entries(U.eventNames)) { const label=el('label',undefined,'check'),input=el('input'); input.type='checkbox';input.name='event_'+value;label.append(input,document.createTextNode(title));$('taskNotificationEvents').append(label); }
  async function ask(message) {
    const dialog=$('confirmDialog'); if(dialog.open)return false;
    $('confirmText').textContent=message; dialog.showModal();
    return new Promise(resolve=>{const finish=value=>{dialog.close();$('confirmYes').onclick=$('confirmNo').onclick=null;dialog.oncancel=null;resolve(value);};
      $('confirmYes').onclick=()=>finish(true);$('confirmNo').onclick=()=>finish(false);dialog.oncancel=event=>{event.preventDefault();finish(false);};});
  }
  function notice(text, error=false) { $('notice').hidden=!text; $('notice').textContent=text; $('notice').className=error?'error':''; }
  const api = async (method,path,body) => {
    if (!window.Router2API?.request) throw Error('宿主页面桥未就绪；请在 Router2API 插件页面打开');
    const requestedAt=Date.now();
    const result=await window.Router2API.request(method,path,body);
    const receivedAt=Date.now();
    if (result?.error && !result?.config) throw Error(result.error);
    if(method==='GET')displayClock.sync(result?.serverNow,requestedAt,receivedAt);
    return result;
  };
  const get = path => api('GET',path), post = (path,body) => api('POST',path,body);
  const button = (title, work, cls='secondary') => { const node=el('button',title,cls);node.type='button';node.addEventListener('click',()=>act(node,work));return node; };
  async function act(node,work) { node.disabled=true; try{await work();}catch(error){notice(error.message,true);}finally{node.disabled=false;} }
  function clearError(form){ form.querySelector('.form-error')?.replaceChildren(); }
  function bindForm(form, work) { form.addEventListener('submit',async event=>{event.preventDefault();clearError(form);const submit=form.querySelector('button[type=submit]');if(!form.checkValidity()){const invalid=form.querySelector(':invalid');const pane=invalid?.closest('[data-editor-pane]');if(pane)editorPage(pane.dataset.editorPane);form.reportValidity();return;}submit.disabled=true;try{await work();}catch(error){form.querySelector('.form-error')?.replaceChildren(document.createTextNode(error.message));notice(error.message,true);}finally{submit.disabled=false;}}); }
  function options(select, values, value) { select.replaceChildren(...values.map(([id,label])=>{const opt=el('option',label);opt.value=id;return opt;})); if(value!=null)select.value=value; }
  function siteName(id){return state?.sites.find(s=>s.id===id)?.name||'站点已删除';}
  function keyName(id){return state?.keys.find(k=>k.id===id)?.alias||'账号';}
  function pill(text,tone){return el('span',text,'pill '+tone);}
  async function refresh() {
    if(refreshing)return;refreshing=true;
    try { state=await get('state');$('connectionDot').className='live';$('connectionText').textContent=`${state.activeCount} 个后台任务`;$('heroActive').textContent=String(state.activeCount);render(); }
    catch(error){$('connectionDot').className='';$('connectionText').textContent='连接失败';notice(error.message,true);}
    finally{refreshing=false;clearTimeout(timer);timer=setTimeout(refresh,document.hidden?10000:2000);}
  }
  function showPage(next){page=next;document.querySelectorAll('.page').forEach(n=>n.hidden=n.id!==`page-${page}`);document.querySelectorAll('nav button').forEach(n=>n.classList.toggle('active',n.dataset.page===page));render();}
  function render(){if(!state)return;if(page==='tasks')renderTasks();if(page==='templates')renderTemplates();if(state.notificationWarning||state.notificationError)notice(state.notificationWarning||state.notificationError,true);if(Object.keys(state.engineErrors||{}).length)notice('后台任务已停止：'+Object.values(state.engineErrors).join('；'),true);}
  function icon(name,size=20){
    const paths={layers:['M12 3 3 8l9 5 9-5-9-5Z','m3 12 9 5 9-5','m3 16 9 5 9-5'],queue:['M4 6h12m-3-3 3 3-3 3M4 12h8M4 18h16'],pulse:['M3 12h4l3-8 4 16 3-8h4'],send:['m21 3-7 18-4-7-7-4L21 3Z','m10 14 6-6']};
    const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');for(const [name,value]of Object.entries({width:size,height:size,viewBox:'0 0 24 24',fill:'none',stroke:'currentColor','stroke-width':'1.6','stroke-linecap':'round','stroke-linejoin':'round','aria-hidden':'true'}))svg.setAttribute(name,String(value));
    for(const path of paths[name]||paths.layers){const node=document.createElementNS(svg.namespaceURI,'path');node.setAttribute('d',path);svg.append(node);}return svg;
  }
  function metric(title,value,type){const node=el('div',undefined,'metric'),content=el('div'),mark=el('div',undefined,'metric-icon');content.append(el('span',title),el('strong',String(value)));mark.append(icon(type));node.append(content,mark);return node;}
  function filteredTasks(){const query=$('taskSearch').value.toLowerCase(),mode=$('modeFilter').value;return state.tasks.filter(t=>(!mode||t.config.mode===mode)&&`${t.config.name} ${t.config.model}`.toLowerCase().includes(query));}
  function updateSelection(){
    if(!state)return;
    const existing=new Set(state.tasks.map(task=>task.config.id));for(const taskId of selected)if(!existing.has(taskId))selected.delete(taskId);
    const visible=filteredTasks().slice(taskPage*50,(taskPage+1)*50),count=visible.filter(task=>selected.has(task.config.id)).length;
    $('selectAll').checked=visible.length>0&&count===visible.length;$('selectAll').indeterminate=count>0&&count<visible.length;$('selectAll').disabled=bulkPending||!visible.length;
    $('selectionCount').textContent='已选 '+selected.size+' 个';$('bulkActions').setAttribute('aria-busy',String(bulkPending));
    for(const node of document.querySelectorAll('[data-bulk]'))node.disabled=bulkPending||!selected.size;
    for(const card of $('taskList').querySelectorAll('.task-card')){const chosen=selected.has(card.dataset.id),check=card.querySelector('.task-head>input[type=checkbox]');card.classList.toggle('is-selected',chosen);check.checked=chosen;check.disabled=bulkPending;}
  }
  async function bulkAction(action){
    if(bulkPending||!selected.size)return;const ids=[...selected];bulkPending=true;updateSelection();
    try{if(action==='delete')await deleteAccounts(ids);else await taskAction(ids,action);}catch(error){notice(error.message,true);}finally{bulkPending=false;updateSelection();}
  }
  async function deleteAccounts(ids){
    const targets=state.tasks.filter(task=>ids.includes(task.config.id));
    if(targets.some(task=>task.runtime.generation||['running','recovering'].includes(task.runtime.status)))throw Error('所选账号中有正在运行的任务，请先暂停后删除。');
    if(!await ask(`删除所选 ${ids.length} 个账号及其任务、日志、通知配置和不再使用的 Key？此操作不可恢复。`))return;
    const names=new Map(targets.map(task=>[task.config.id,task.config.name])),errors=[];let deleted=0;
    for(const id of ids){
      try{
        const result=await post('accounts/delete',{id});if(!result?.success)throw Error('删除未完成');
        deleted++;selected.delete(id);state.tasks=state.tasks.filter(task=>task.config.id!==id);
      }catch(error){errors.push((names.get(id)||id)+'：'+error.message);}
    }
    await refresh();
    notice(`已删除 ${deleted} 个账号`+(errors.length?`；${errors.length} 个删除失败：`+errors.join('；'):''),errors.length>0);
  }
  function renderTasks(){
    const tasks=filteredTasks();taskPage=Math.min(taskPage,Math.max(0,Math.ceil(tasks.length/50)-1));
    $('heroActive').textContent=String(state.activeCount);$('taskListNote').textContent='共 '+tasks.length+' 个账号';
    $('metrics').replaceChildren(metric('全部账号',state.tasks.length,'layers'),metric('正在排队',state.tasks.filter(t=>t.runtime.status==='running'&&t.runtime.phase==='queue').length,'queue'),metric('保活 / 恢复中',state.tasks.filter(t=>['running','recovering'].includes(t.runtime.status)&&t.runtime.phase==='keepalive').length,'pulse'),metric('累计请求',state.tasks.reduce((n,t)=>n+t.runtime.totalAttempts,0),'send'));
    const open=new Set([...$('taskList').querySelectorAll('details[open]')].map(n=>n.dataset.id));
    const visible=tasks.slice(taskPage*50,(taskPage+1)*50);$('taskList').replaceChildren();
    if(!visible.length){const empty=el('div',undefined,'empty');empty.append(icon('layers',32),el('strong',state.tasks.length?'没有匹配的账号':'还没有账号'),el('p',state.tasks.length?'试试其他名称或运行模式':'添加账号，配置排队、保活和通知。'),button('添加账号',()=>openTask(),'primary'));$('taskList').append(empty);}
    for(const task of visible){
      const c=task.config,r=task.runtime,card=el('article',undefined,'task-card'),head=el('div',undefined,'task-head');
      card.dataset.id=c.id;
      const protocol=state.templates.find(t=>t.id===c.templateId)?.protocol||'responses',identity=el('div',undefined,'task-identity task-title');identity.append(el('strong',c.name),el('small',state.sites.find(s=>s.id===c.siteId)?.baseUrl||siteName(c.siteId)));identity.title=c.name;
      const check=el('input');check.type='checkbox';check.checked=selected.has(c.id);check.setAttribute('aria-label','选择任务 '+c.name);check.addEventListener('change',()=>{check.checked?selected.add(c.id):selected.delete(c.id);updateSelection();});
      head.append(el('span',protocol==='messages'?'M':'R','protocol-mark'),identity,check);card.append(head);
      const top=el('div',undefined,'task-top'),s=U.status(task);top.append(pill(s.label,s.tone),el('span',U.modes[c.mode],'mode-label'));card.append(top,el('div',c.model,'task-model'));
      const key=state.keys.find(k=>k.id===c.keyId),box=el('div',undefined,'request-box'),summary=el('div',undefined,'request-summary');
      const total=el('div',undefined,'request-total'),sub=el('div',undefined,'request-sub');
      total.append(el('strong',String(r.totalAttempts)),el('span','次请求'));
      for(const text of ['排队 '+r.queueRound+' 轮','心跳 '+r.heartbeats+' 次','恢复 '+r.recoveries+' 次'])sub.append(el('span',text));
      summary.append(el('small','本次运行累计'),total,sub);
      const quota=el('div',undefined,'quota-summary'),remaining=el('div',undefined,'quota-total');
      remaining.append(el('strong',key?U.remaining(key):'—'));
      quota.append(el('small','剩余额度'),remaining,el('div','已用 '+(key?.usedUsd==null?'—':'$'+Number(key.usedUsd).toFixed(2)),'quota-sub'));
      const queryError=key?.usageError||key?.error;
      if(queryError){const warning=el('small','查询失败','quota-error');warning.title='查询失败，已有额度读数保留：'+queryError;warning.setAttribute('aria-label',warning.title);quota.append(warning);}
      box.append(summary,quota);card.append(box);
      const info=el('div',undefined,'task-info');for(const [label,value]of [['下次请求',date(r.nextAttemptAt)],['上次成功',date(r.lastSuccessAt)]]){const item=el('div');item.append(el('small',label),document.createTextNode(value));info.append(item);}card.append(info);
      const http=el('div',undefined,'task-http');http.append(el('strong',r.lastHttpStatus?'HTTP '+r.lastHttpStatus:'等待请求'),el('span',r.matchedRule==='SSE accepted'?'SSE 已接受':r.matchedRule||'无匹配规则'));card.append(http);
      card.append(el('div','自动重排 '+(c.autoRequeue?'已开启':'已关闭'),'mode-label'));if(r.pauseReason||r.lastError)card.append(el('p',r.pauseReason||r.lastError,'task-error'));
      if(task.totalLanes>1){const details=el('details',undefined,'lane-details');details.dataset.id=c.id+':lanes';details.open=open.has(details.dataset.id);details.append(el('summary','子线程 '+task.totalLanes+' 个'));for(const lane of r.lanes)details.append(el('p','#'+lane.index+' · '+lane.status+' · '+lane.attempts+' 次 · '+(lane.lastError||date(lane.nextAttemptAt)),'subtle'));if(task.totalLanes>8)details.append(el('p','更多线程请在详情中查看。','subtle'));card.append(details);}
      const available=U.taskActions(task),running=available.action==='pause',actions=el('div',undefined,'card-actions');
      const edit=button('编辑',()=>openTask(c.id),'secondary task-edit');edit.disabled=running;edit.title=running?'请先暂停任务':'编辑账号';
      const logs=button('日志',()=>openDetail(c.id),'secondary task-logs');logs.title='日志统计';
      const run=button(available.label,()=>taskAction([c.id],available.action),'run');if(available.action==='restart')run.title=available.reason+'；重新开始将重置运行计数';
      actions.append(edit,run,logs);
      const more=el('details',undefined,'task-more');more.dataset.id=c.id+':menu';more.open=open.has(more.dataset.id);more.append(el('summary','更多'));const menu=el('div',undefined,'menu-list');
      const add=(label,work,disabled=false,cls='')=>{const b=button(label,async()=>{more.open=false;await work();},cls);b.disabled=disabled;menu.append(b);};
      add('复制账号',()=>openTask(c.id,true));add('查询模型 / 额度',async()=>{await post('keys/refresh',{id:c.keyId});await refresh();const updated=state.keys.find(k=>k.id===c.keyId),error=updated?.error||updated?.usageError;notice(error||'模型 / 额度已更新',Boolean(error));});add('导出账号',()=>exportAccount(c.id));add('立即重试',()=>taskAction([c.id],'retry'),!available.retry);add('手动重排',()=>taskAction([c.id],'requeue'),!available.requeue);if(available.action!=='restart')add('重新开始',()=>taskAction([c.id],'restart'));add('取消任务',()=>taskAction([c.id],'cancel'),!running);add('删除账号',async()=>{if(await ask('删除此账号及其任务、日志和通知配置？')){await post('accounts/delete',{id:c.id});selected.delete(c.id);await refresh();}},running,'danger');more.append(menu);actions.append(more);card.append(actions);$('taskList').append(card);
    }
    $('taskPage').textContent=(taskPage+1)+' / '+Math.max(1,Math.ceil(tasks.length/50))+' · '+tasks.length+' 个账号';
    $('previousTasks').disabled=taskPage===0;$('nextTasks').disabled=(taskPage+1)*50>=tasks.length;
    updateSelection();
  }
  async function taskAction(ids,action){
    if(action==='restart'&&!await ask('重置运行计数并重新开始？'))return;
    const names=new Map(state.tasks.map(task=>[task.config.id,task.config.name])),errors=[];
    if(action==='resume')ids=ids.filter(id=>{
      const task=state.tasks.find(task=>task.config.id===id),available=task&&U.taskActions(task);
      if(available?.action!=='restart')return true;
      errors.push(names.get(id)+'：'+available.reason+'，请使用“重新开始”');return false;
    });
    let completed=0;
    if(ids.length){
      const result=await post('tasks/action',{ids,action});
      for(const item of result.results)if(item.success)completed++;else errors.push((names.get(item.id)||item.id)+'：'+item.error);
      await refresh();
    }
    notice(errors.length?(completed?'已完成 '+completed+' 个账号；':'')+errors.join('；'):'操作已完成',errors.length>0);
  }
  function renderTemplates(){ $('templateList').replaceChildren();for(const template of state.templates){const card=el('article',undefined,'site-card');card.append(el('h3',template.name),el('p',(template.protocol==='messages'?'Anthropic Messages':'OpenAI Responses')+' · /v1/'+template.protocol),button('编辑完整 JSON',()=>openTemplate(template.id)),button('复制模板',()=>openTemplate(template.id,true),'ghost'));$('templateList').append(card);} }
  function populateModels(){
    const key=state.keys.find(k=>k.id===fields($('taskForm')).keyId.value);
    $('modelOptions').replaceChildren(...(key?.models||[]).map(m=>{const node=el('option');node.value=m;return node;}));
  }
  function putTemplate(template){taskTemplate=U.copy(U.wireTemplate(template));const f=fields($('taskForm'));$('requestEndpoint').textContent='/v1/'+template.protocol;$('claudeContextField').hidden=template.protocol!=='messages';f.requestHeaders.value=JSON.stringify(template.headers,null,2);f.requestBody.value=typeof template.body==='string'?template.body:U.stringify(template.body);}
  async function openTask(taskId,duplicate=false,imported=null){
    const form=$('taskForm');form.reset();clearError(form);
    const detail=imported||(taskId?await get('accounts/detail?id='+encodeURIComponent(taskId)):null);
    taskDraft=detail?U.copy(detail.config):U.defaults(id());
    taskDraft.claude1MContext??=true;
    taskDraft.sourcePromptCacheMinutes??=360;
    if(duplicate||imported){taskDraft.id=id();taskDraft.keyId='';if(duplicate)taskDraft.name+=' · 副本';}
    siteDraft=U.copy(detail?.site||{id:id(),revision:0,name:'',baseUrl:'https://anyrouter.top',route:'direct',subscriptionIds:[],queueHttpStatuses:[500]});
    if(duplicate||imported){siteDraft.id=id();siteDraft.revision=0;}
    taskDraft.settings=U.copy(taskDraft.settings||U.settingsDefaults());
    const f=fields(form);
    f.id.value=taskDraft.id;f.expectedRevision.value=duplicate||imported?'':detail?.configRevision??'';
    f.siteId.value=siteDraft.id;f.keyId.value=taskDraft.keyId;f.siteRevision.value=siteDraft.revision;
    f.credentialVersion.value=duplicate||imported?'':detail?.credentialVersion??'';
    f.baseUrl.value=siteDraft.baseUrl;f.route.value=siteDraft.route;f.subscriptionIds.value=siteDraft.subscriptionIds.join(',');
    f.token.value=detail?.token||'';
    const templateOptions=state.templates.map(t=>[t.id,t.name]);
    if(!templateOptions.some(t=>t[0]===taskDraft.templateId))templateOptions.push([taskDraft.templateId,'账号专用模板']);
    options(f.templateId,templateOptions,taskDraft.templateId);
    for(const name of ['name','mode','model','prompt'])f[name].value=taskDraft[name];
    for(const name of ['autoRequeue','claude1MContext'])f[name].checked=taskDraft[name];
    f.promptSource.value=taskDraft.useSourcePrompt?'linux':'fixed';
    f.sourcePromptCacheMinutes.value=taskDraft.sourcePromptCacheMinutes;
    const codes=taskDraft.queueHttpStatuses??siteDraft.queueHttpStatuses;
    f.queueHttpStatuses.value=codes.length?codes.join(','):'[]';
    for(const [prefix,object]of [['q_',taskDraft.queue],['k_',taskDraft.keepalive]])for(const [key,value]of Object.entries(object))if(f[prefix+key]){if(typeof value==='boolean')f[prefix+key].checked=value;else f[prefix+key].value=value??'';}
    f.unlimitedAttempts.checked=taskDraft.queue.maxAttemptsPerLane===null;
    for(const event of Object.keys(U.eventNames))f['event_'+event].checked=taskDraft.notificationEvents.includes(event);
    for(const [name,value]of Object.entries(taskDraft.settings))if(f[name]){if(typeof value==='boolean')f[name].checked=value;else f[name].value=value;}
    for(const name of ['telegramToken','telegramChatId','barkKey'])f[name].value=detail?.secrets?.[name]||'';
    f.notificationCredentialVersion.value=duplicate||imported?0:detail?.secrets?.credentialVersion??0;
    populateModels();$('modelQueryStatus').textContent='';
    putTemplate(taskDraft.requestOverride||await get('templates/detail?id='+encodeURIComponent(f.templateId.value)));
    updateMode();updateNotifications();updateRoute();updatePromptSource();
    $('taskEditorTitle').textContent=taskId&&!duplicate?'编辑账号':'添加账号';
    $('requestEditor').open=true;editorPage('basic');$('taskDialog').showModal();
  }
  function updateRoute(){$('subscriptionField').hidden=fields($('taskForm')).route.value!=='pool';}
  function updatePromptSource(syncBody=false){
    const f=fields($('taskForm')),linux=f.promptSource.value==='linux';
    $('fixedPromptField').hidden=linux;f.prompt.disabled=linux;
    $('sourcePromptCacheField').hidden=!linux;f.sourcePromptCacheMinutes.disabled=!linux;
    $('sourcePromptHelp').hidden=!linux;
    if(syncBody||linux)try{f.requestBody.value=U.setKnownField({...taskTemplate,body:f.requestBody.value},'prompt',linux?'{{PROMPT}}':f.prompt.value).body;}
    catch(error){$('taskForm').querySelector('.form-error').textContent=error.message;}
  }
  function updateNotifications(){
    const f=fields($('taskForm'));
    f.telegramToken.required=f.telegramChatId.required=f.telegramEnabled.checked;
    f.barkKey.required=f.barkEnabled.checked;
  }
  function editorPage(next){document.querySelectorAll('[data-editor-pane]').forEach(node=>node.hidden=node.dataset.editorPane!==next);document.querySelectorAll('[data-editor-target]').forEach(node=>{const active=node.dataset.editorTarget===next;node.classList.toggle('active',active);node.setAttribute('aria-selected',String(active));});$('taskForm').querySelector('.editor-body').scrollTop=0;}
  for(const node of document.querySelectorAll('[data-editor-target]'))node.addEventListener('click',()=>editorPage(node.dataset.editorTarget));
  function updateMode(){
    const f=fields($('taskForm')),mode=f.mode.value;
    f.autoRequeue.disabled=mode==='queue';f.q_readFullResponse.disabled=mode!=='queue';
    if(mode!=='queue')f.q_readFullResponse.checked=false;
    f.q_maxAttemptsPerLane.disabled=f.unlimitedAttempts.checked;f.q_maxAttemptsPerLane.required=!f.unlimitedAttempts.checked;
    $('modeHelp').textContent=mode==='queue'?'进入成功后完成任务。':
      (mode==='keepalive'?'立即开始心跳。':'先排队，成功后持续保活。')+
      (f.autoRequeue.checked?'持续失败并确认掉线后自动重排，短暂波动只重试。':'确认掉线后暂停；短暂波动继续重试，尚未进入时等待首次成功。');
  }
  function readTask(){
    const f=fields($('taskForm')),config=U.copy(taskDraft);
    for(const name of ['id','name','mode','siteId','keyId','model','prompt','templateId'])config[name]=f[name].value;
    config.autoRequeue=f.autoRequeue.checked;config.useSourcePrompt=f.promptSource.value==='linux';
    const cacheMinutes=f.sourcePromptCacheMinutes.value.trim();
    config.sourcePromptCacheMinutes=cacheMinutes===''?NaN:Number(cacheMinutes);
    if(!Number.isFinite(config.sourcePromptCacheMinutes)||config.sourcePromptCacheMinutes<0||config.sourcePromptCacheMinutes>2000000){
      if(config.useSourcePrompt)throw Error('Linux 源码缓存时长必须是 0–2000000 分钟');
      config.sourcePromptCacheMinutes=360;
    }
    config.claude1MContext=f.claude1MContext.checked;
    config.queueHttpStatuses=f.queueHttpStatuses.value.trim()==='[]'?[]:U.parseCodes(f.queueHttpStatuses.value);
    for(const [prefix,definitions,object]of [['q_',qFields,config.queue],['k_',kFields,config.keepalive]])for(const [name]of definitions){const value=f[prefix+name].value;object[name]=value===''?null:Number(value);}
    config.queue.maxAttemptsPerLane=f.unlimitedAttempts.checked?null:Number(f.q_maxAttemptsPerLane.value);
    for(const [prefix,object]of [['q_',config.queue],['k_',config.keepalive]])for(const field of ['retryNetworkErrors','retryTimeouts','readFullResponse'])if(f[prefix+field])object[field]=f[prefix+field].checked;
    config.notificationEvents=Object.keys(U.eventNames).filter(e=>f['event_'+e].checked);
    config.settings=U.settingsDefaults();
    for(const [name,value]of Object.entries(config.settings))config.settings[name]=typeof value==='boolean'?f[name].checked:typeof value==='number'?Number(f[name].value):f[name].value;
    config.requestOverride={...taskTemplate,path:taskTemplate.protocol,headers:U.jsonObject(f.requestHeaders.value,'Headers'),body:f.requestBody.value};
    if(config.useSourcePrompt)config.requestOverride=U.setKnownField(config.requestOverride,'prompt','{{PROMPT}}');
    if(config.queue.intervalMaxSeconds!==null&&config.queue.intervalMaxSeconds<config.queue.intervalSeconds)throw Error('排队随机上限不能小于下限');
    if(config.keepalive.heartbeatMaxSeconds!==null&&config.keepalive.heartbeatMaxSeconds<config.keepalive.heartbeatSeconds)throw Error('心跳随机上限不能小于下限');
    if(config.settings.notificationMaxRetrySeconds<config.settings.notificationRetrySeconds)throw Error('通知最大退避不能小于初始间隔');
    if(U.jsonObject(config.requestOverride.body,'Body').stream!==true)throw Error('Body.stream 必须是 true');
    return config;
  }
  function readAccount(){
    const f=fields($('taskForm')),config=readTask();
    return {config,site:{...siteDraft,id:f.siteId.value,revision:Number(f.siteRevision.value),name:config.name,
      baseUrl:f.baseUrl.value.trim(),route:f.route.value,subscriptionIds:f.subscriptionIds.value.split(',').map(s=>s.trim()).filter(Boolean),
      queueHttpStatuses:config.queueHttpStatuses},token:f.token.value.trim(),
      credentialVersion:f.credentialVersion.value===''?null:Number(f.credentialVersion.value),
      expectedRevision:f.expectedRevision.value===''?null:Number(f.expectedRevision.value),
      secrets:{telegramToken:f.telegramToken.value.trim(),telegramChatId:f.telegramChatId.value.trim(),barkKey:f.barkKey.value.trim(),
        credentialVersion:Number(f.notificationCredentialVersion.value)}};
  }
  async function openTemplate(templateId,duplicate=false){const value=templateId?await get('templates/detail?id='+encodeURIComponent(templateId)):await get('templates/detail?id=responses');if(duplicate||!templateId){value.id=id();value.name+=' · 副本';}clearError($('templateForm'));fields($('templateForm')).json.value=U.displayTemplate(value);$('templateDialog').showModal();}
  async function openDetail(taskId,offset=0){
    detailId=taskId;detailOffset=offset;
    const task=await get('tasks/detail?id='+encodeURIComponent(taskId)+'&laneOffset='+offset);
    $('detailTitle').textContent=task.config.name+' · 日志统计';
    const content=$('detailContent');content.replaceChildren();const status=U.status(task),r=task.runtime;
    const controls=el('div',undefined,'actions');
    controls.append(pill(status.label,status.tone),button('刷新日志',()=>openDetail(taskId,detailOffset),'ghost'));
    const notifications=task.notifications||[];
    if(notifications.some(n=>n.state==='paused'))controls.append(button('恢复通知投递',async()=>{
      if(await ask('恢复此账号的通知投递？已送达但未确认的通知可能重复。')){await post('notifications/resume',{id:taskId});await openDetail(taskId,detailOffset);}
    }));
    content.append(controls);
    const metrics=el('div',undefined,'metrics detail-metrics');
    metrics.append(metric('累计请求',r.totalAttempts,'send'),metric('首次进入',r.entered?1:0,'queue'),
      metric('恢复次数',r.recoveries,'layers'),metric('成功心跳',r.heartbeats,'pulse'));content.append(metrics);
    if(r.pauseReason||r.lastError)content.append(el('p',r.pauseReason||r.lastError,'task-error'));
    if(task.stats?.length){
      content.append(el('h3','每日统计'));const wrap=el('div',undefined,'table-scroll'),table=el('table'),head=el('tr');
      for(const title of ['日期（UTC+8）','协议','首次进入','恢复','心跳'])head.append(el('th',title));
      const thead=el('thead');thead.append(head);table.append(thead);const body=el('tbody');
      for(const stat of [...task.stats].sort((a,b)=>b.day.localeCompare(a.day))){const row=el('tr');for(const value of [stat.day,stat.protocol,stat.entered,stat.recoveries,stat.heartbeats])row.append(el('td',String(value)));body.append(row);}
      table.append(body);wrap.append(table);content.append(wrap);
    }
    content.append(el('h3','请求日志'));const events=el('pre');
    events.textContent=(task.events||[]).map(e=>date(e.at)+' ['+e.kind+'] '+(e.lane?'#'+e.lane+' ':'')+e.message).join('\n')||'暂无日志';content.append(events);
    if(r.responseSummary)content.append(el('h3','响应摘要'),el('pre',r.responseSummary));
    if(r.lanes.length){
      content.append(el('h3','子线程'));
      for(const lane of r.lanes)content.append(el('p','#'+lane.index+' · '+lane.status+' · '+lane.attempts+' 次 · '+(lane.lastError||date(lane.nextAttemptAt)),'subtle'));
      if(task.totalLanes>100){const pager=el('div',undefined,'actions'),prev=button('上一页线程',()=>openDetail(taskId,Math.max(0,offset-100))),next=button('下一页线程',()=>openDetail(taskId,offset+100));
        prev.disabled=offset===0;next.disabled=offset+100>=task.totalLanes;pager.append(prev,next);content.append(pager);}
    }
    content.append(el('h3','通知记录'));
    const names={sent:'已送达',dead:'投递失败',paused:'已暂停',sending:'投递中',queued:'待投递',retrying:'重试中'};
    for(const n of notifications){const item=el('div',undefined,'notification');item.append(el('strong',n.title),pill(names[n.state]||n.state,n.state==='sent'?'ok':n.state==='dead'?'bad':'muted'),
      el('p',n.channel+' · '+n.attempts+' 次 · '+date(n.createdAt)),el('p',n.error||n.message));content.append(item);}
    if(!notifications.length)content.append(el('p','暂无通知','subtle'));
    const details=el('details');details.append(el('summary','账号配置 JSON'),el('pre',JSON.stringify(task.config,null,2)));content.append(details);
    if(!$('detailDialog').open)$('detailDialog').showModal();
  }
  async function exportAccount(taskId){
    const config=await get('accounts/export?id='+encodeURIComponent(taskId));
    $('exportText').value=JSON.stringify(config,null,2);$('exportDialog').showModal();
  }
  bindForm($('taskForm'),async()=>{await post('accounts/save',readAccount());$('taskDialog').close();notice('账号已保存');await refresh();});
  bindForm($('templateForm'),async()=>{await post('templates/save',U.wireTemplate(U.jsonObject(fields($('templateForm')).json.value,'模板')));$('templateDialog').close();await refresh();});
  $('previewRequest').addEventListener('click',()=>act($('previewRequest'),async()=>{
    const result=await post('accounts/preview',readAccount());
    $('previewContent').textContent=U.stringify({...result,body:typeof result.body==='string'?U.jsonObject(result.body,'Body'):result.body});
    $('previewDialog').showModal();
  }));
  $('queryAccount').addEventListener('click',()=>act($('queryAccount'),async()=>{
    const f=fields($('taskForm'));
    for(const field of [f.baseUrl,f.token])if(!field.checkValidity()){field.reportValidity();return;}
    const connection=()=>({site:{...siteDraft,name:f.name.value.trim()||'账号',baseUrl:f.baseUrl.value.trim(),route:f.route.value,
      subscriptionIds:f.subscriptionIds.value.split(',').map(s=>s.trim()).filter(Boolean)},token:f.token.value.trim()});
    const input=connection(),accountId=f.id.value;const result=await post('accounts/query',input);
    if(f.id.value!==accountId||JSON.stringify(connection())!==JSON.stringify(input))return;
    $('modelOptions').replaceChildren(...(result.models||[]).map(m=>{const option=el('option');option.value=m;return option;}));
    $('modelQueryStatus').textContent=(result.models?.length||0)+' 个模型 · 剩余额度 '+U.remaining(result)+(result.usageError?' · '+result.usageError:'');
  }));
  for(const node of document.querySelectorAll('[data-close]'))node.addEventListener('click',()=>$(node.dataset.close).close());
  for(const node of document.querySelectorAll('nav button'))node.addEventListener('click',()=>showPage(node.dataset.page));
  for(const node of document.querySelectorAll('[data-bulk]'))node.addEventListener('click',()=>bulkAction(node.dataset.bulk));
  $('selectAll').addEventListener('change',()=>{for(const task of filteredTasks().slice(taskPage*50,(taskPage+1)*50))$('selectAll').checked?selected.add(task.config.id):selected.delete(task.config.id);renderTasks();});
  $('taskSearch').addEventListener('input',()=>{taskPage=0;renderTasks();});$('modeFilter').addEventListener('change',()=>{taskPage=0;renderTasks();});
  $('previousTasks').addEventListener('click',()=>{taskPage--;renderTasks();});$('nextTasks').addEventListener('click',()=>{taskPage++;renderTasks();});
  for(const [name,work]of [['newAccount',()=>openTask()],['newTemplate',()=>openTemplate()],['refresh',refresh]])$(name).addEventListener('click',()=>act($(name),work));
  const taskFields=fields($('taskForm'));
  for(const name of ['mode','autoRequeue','unlimitedAttempts'])taskFields[name].addEventListener('change',updateMode);
  taskFields.route.addEventListener('change',updateRoute);
  taskFields.promptSource.addEventListener('change',()=>updatePromptSource(true));
  for(const name of ['telegramEnabled','barkEnabled'])taskFields[name].addEventListener('change',updateNotifications);
  taskFields.templateId.addEventListener('change',async()=>{try{putTemplate(await get('templates/detail?id='+encodeURIComponent(taskFields.templateId.value)));updatePromptSource();}catch(error){notice(error.message,true);}});
  for(const name of ['model','prompt'])taskFields[name].addEventListener('change',()=>{try{const updated=U.setKnownField({...taskTemplate,body:taskFields.requestBody.value},name,taskFields[name].value);taskFields.requestBody.value=updated.body;}catch(error){$('taskForm').querySelector('.form-error').textContent=error.message;}});
  taskFields.requestBody.addEventListener('change',()=>{try{
    const body=U.jsonObject(taskFields.requestBody.value,'Body');
    if(typeof body.model==='string'&&!body.model.includes('{{'))taskFields.model.value=body.model;
    const list=taskTemplate.protocol==='responses'?body.input:body.messages;
    const user=Array.isArray(list)?list.filter(x=>x?.role==='user').at(-1):null;
    const text=typeof list==='string'?list:typeof user?.content==='string'?user.content:user?.content?.find(x=>['input_text','text'].includes(x.type))?.text;
    if(taskFields.promptSource.value==='fixed'&&typeof text==='string'&&!text.includes('{{'))taskFields.prompt.value=text;
  }catch(error){$('taskForm').querySelector('.form-error').textContent=error.message;}});
  $('importAccount').addEventListener('click',()=>$('importFile').click());
  $('importFile').addEventListener('change',async()=>{try{
    const file=$('importFile').files[0];if(!file)return;if(file.size>1024*1024)throw Error('账号 JSON 大小上限为 1 MiB');
    const input=U.importAccount(await file.text());await openTask(null,false,input);
  }catch(error){notice(error.message,true);}finally{$('importFile').value='';}});
  $('selectExport').addEventListener('click',()=>{$('exportText').focus();$('exportText').select();});
  window.addEventListener('pagehide',()=>clearTimeout(timer));document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh();});
  refresh();
})();
