import test from 'node:test';
import assert from 'node:assert/strict';
import { harness, until } from './harness.mjs';

async function draft(h){
  h.click('newAccount');await until(()=>h.document.getElementById('taskDialog').open);
  const f=h.document.getElementById('taskForm').elements;
  h.change(f.model,'MODEL');f.token.value='TOKEN-ui-test';f.baseUrl.value='http://127.0.0.1';return f;
}
function runtime(values={}){return {status:'paused',phase:'queue',queueRound:0,totalAttempts:0,heartbeats:0,recoveries:0,lanes:[],sessionConfirmed:false,...values};}

test('导航只保留账号任务和请求模板，添加账号无需预先设置站点',async t=>{
  const h=await harness();t.after(h.close);
  assert.deepEqual([...h.document.querySelectorAll('nav button')].map(n=>n.dataset.page),['tasks','templates']);
  for(const id of ['page-sites','page-logs','page-settings','siteDialog','keyDialog'])assert.equal(h.document.getElementById(id),null);
  h.data.sites=[];h.data.keys=[];h.click('refresh');await until(()=>h.calls.filter(c=>c.path==='state').length>=2);
  const f=await draft(h);assert.equal(f.queueHttpStatuses.value,'500');f.name.value='独立账号';
  h.submit(h.document.getElementById('taskForm'));await until(()=>h.calls.some(c=>c.path==='accounts/save'));
  const input=h.calls.find(c=>c.path==='accounts/save').body;
  assert.equal(input.site.baseUrl,'http://127.0.0.1');assert.equal(input.token,'TOKEN-ui-test');
  assert.equal(h.calls.some(c=>['sites/save','keys/save','settings/save'].includes(c.path)),false);
  await until(()=>h.document.querySelector('.task-card'));assert.equal(h.document.querySelector('.task-logs').title,'日志统计');
  assert.deepEqual(h.errors,[]);
});

test('添加账号按模板选择固定接口，不显示自定义接口输入',async t=>{
  const h=await harness();t.after(h.close);const f=await draft(h);
  assert.equal(h.document.querySelector('[name=requestPath]'),null);
  assert.equal(h.document.getElementById('requestEndpoint').textContent,'/v1/responses');
  h.change(f.templateId,'messages');
  await until(()=>h.document.getElementById('requestEndpoint').textContent==='/v1/messages');
  h.submit(h.document.getElementById('taskForm'));
  await until(()=>h.calls.some(c=>c.path==='accounts/save'));
  assert.equal(h.calls.find(c=>c.path==='accounts/save').body.config.requestOverride.path,'messages');
});

test('Claude 1M 上下文默认开启，可按账号关闭并保留选择',async t=>{
  const h=await harness();t.after(h.close);const f=await draft(h);
  assert.equal(f.claude1MContext.checked,true);assert.equal(h.document.getElementById('claudeContextField').hidden,true);
  h.change(f.templateId,'messages');await until(()=>!h.document.getElementById('claudeContextField').hidden);
  assert.equal(f.claude1MContext.checked,true);f.claude1MContext.checked=false;
  h.change(f.templateId,'responses');await until(()=>h.document.getElementById('claudeContextField').hidden);
  h.change(f.templateId,'messages');await until(()=>!h.document.getElementById('claudeContextField').hidden);
  assert.equal(f.claude1MContext.checked,false);
  h.submit(h.document.getElementById('taskForm'));await until(()=>h.calls.some(c=>c.path==='accounts/save'));
  assert.equal(h.calls.find(c=>c.path==='accounts/save').body.config.claude1MContext,false);
  await until(()=>!h.document.getElementById('taskDialog').open);
  h.click('newAccount');await until(()=>h.document.getElementById('taskDialog').open);
  assert.equal(f.claude1MContext.checked,true);
});

test('分组参数页签保留草稿，非法参数定位到对应分组', async t=>{
  const h=await harness();t.after(h.close);const f=await draft(h),form=h.document.getElementById('taskForm');
  h.document.querySelector('[data-editor-target=queue]').click();f.q_concurrency.value='5';
  h.document.querySelector('[data-editor-target=keepalive]').click();f.k_concurrency.value='2';f.k_heartbeatSeconds.value='0';
  h.document.querySelector('[data-editor-target=basic]').click();h.submit(form);
  assert.equal(h.document.querySelector('[data-editor-pane=keepalive]').hidden,false);assert.equal(f.q_concurrency.value,'5');
  assert.equal(h.calls.filter(c=>c.path==='accounts/save').length,0);h.change(f.k_heartbeatSeconds,'120');h.submit(form);
  await until(()=>h.calls.some(c=>c.path==='accounts/save'));assert.equal(h.calls.find(c=>c.path==='accounts/save').body.config.queue.concurrency,5);
});

test('账号表单保存独立参数、原始 JSON 及状态码', async t=>{
  const h=await harness();t.after(h.close);const f=await draft(h),form=h.document.getElementById('taskForm');
  h.change(f.mode,'keepalive');assert.equal(f.q_readFullResponse.disabled,true);assert.equal(f.autoRequeue.disabled,false);
  h.change(f.prompt,'测试提示词');f.queueHttpStatuses.value='503,429';f.autoRequeue.checked=false;
  f.q_concurrency.value='11';f.k_concurrency.value='2';f.unlimitedAttempts.checked=true;f.unlimitedAttempts.dispatchEvent(new h.window.Event('change'));
  assert.equal(f.q_maxAttemptsPerLane.required,false);h.submit(form);await until(()=>h.calls.some(c=>c.path==='accounts/save'));
  const input=h.calls.find(c=>c.path==='accounts/save').body,config=input.config;
  assert.deepEqual(config.queueHttpStatuses,[503,429]);assert.deepEqual(input.site.queueHttpStatuses,[503,429]);
  assert.equal(config.queue.concurrency,11);assert.equal(config.keepalive.concurrency,2);
  assert.equal(config.queue.maxAttemptsPerLane,null);assert.equal(config.autoRequeue,false);assert.match(config.requestOverride.body,/9007199254740993123456789/);
  assert.match(config.requestOverride.body,/测试提示词/);assert.equal(config.mode,'keepalive');assert.deepEqual(h.errors,[]);
});

test('刷新保留草稿，预览显示完整 Key 且不保存或启动', async t=>{
  const h=await harness();t.after(h.close);const f=await draft(h);f.name.value='未保存草稿';f.queueHttpStatuses.value='[]';
  h.click('refresh');await until(()=>h.calls.filter(c=>c.path==='state').length>1);assert.equal(f.name.value,'未保存草稿');
  h.click('previewRequest');await until(()=>h.document.getElementById('previewDialog').open);
  const text=h.document.getElementById('previewContent').textContent;
  assert.match(text,/9007199254740993123456789/);assert.match(text,/Bearer TOKEN-ui-test/);assert.doesNotMatch(text,/REDACTED/);
  assert.equal(h.calls.filter(c=>['tasks/action','accounts/save'].includes(c.path)).length,0);
  assert.deepEqual(h.calls.find(c=>c.path==='accounts/preview').body.config.queueHttpStatuses,[]);
});

test('账号列表将名称按文本显示，批量入口固定', async t=>{
  const h=await harness();t.after(h.close);const config=h.U.defaults('task');Object.assign(config,{siteId:'site',keyId:'key',model:'MODEL',name:'<img src=x onerror=alert(1)>'});
  h.data.tasks.push({config,configRevision:1,runtime:runtime(),totalLanes:0});h.click('refresh');await until(()=>h.document.querySelector('.task-card'));
  assert.equal(h.document.querySelectorAll('.task-card img').length,0);assert.match(h.document.querySelector('.task-title').textContent,/<img/);
  h.click('selectAll');h.document.querySelector('[data-bulk="resume"]').click();await until(()=>h.calls.some(c=>c.path==='tasks/action'));
  assert.deepEqual(h.calls.find(c=>c.path==='tasks/action').body,{ids:['task'],action:'resume'});
});

test('单选立即启用批量按钮，全选半选与刷新同步，清空后置灰',async t=>{
  const h=await harness();t.after(h.close);
  for(const id of ['one','two'])h.data.tasks.push({config:{...h.U.defaults(id),siteId:'site',keyId:'key',model:'MODEL'},configRevision:1,runtime:runtime(),totalLanes:0});
  h.click('refresh');await until(()=>h.document.querySelectorAll('.task-card').length===2);
  const bulk=()=>[...h.document.querySelectorAll('[data-bulk]')];
  assert.ok(bulk().every(button=>button.disabled));
  h.document.querySelector('.task-head>input').click();
  assert.ok(bulk().every(button=>!button.disabled));
  assert.equal(h.document.getElementById('selectionCount').textContent,'已选 1 个');
  assert.equal(h.document.getElementById('selectAll').indeterminate,true);
  assert.equal(h.document.querySelectorAll('.task-card.is-selected').length,1);
  const before=h.calls.filter(c=>c.path==='state').length;h.click('refresh');await until(()=>h.calls.filter(c=>c.path==='state').length>before);
  assert.ok(bulk().every(button=>!button.disabled));
  h.click('selectAll');assert.equal(h.document.getElementById('selectionCount').textContent,'已选 2 个');
  assert.equal(h.document.getElementById('selectAll').indeterminate,false);
  h.click('selectAll');assert.ok(bulk().every(button=>button.disabled));
  assert.equal(h.document.querySelectorAll('.task-card.is-selected').length,0);
});

test('任务删除后的失效选择被清除，卡片操作顺序固定',async t=>{
  const h=await harness();t.after(h.close);
  h.data.tasks.push({config:{...h.U.defaults('one'),siteId:'site',keyId:'key',model:'MODEL'},configRevision:1,runtime:runtime(),totalLanes:0});
  h.click('refresh');await until(()=>h.document.querySelector('.task-card'));
  assert.deepEqual([...h.document.querySelector('.card-actions').children].map(node=>node.tagName==='DETAILS'?node.querySelector('summary').textContent:node.textContent),['编辑','继续任务','日志','更多']);
  h.click('selectAll');h.data.tasks=[];const before=h.calls.filter(c=>c.path==='state').length;
  h.click('refresh');await until(()=>h.calls.filter(c=>c.path==='state').length>before&&h.document.querySelectorAll('.task-card').length===0);
  assert.equal(h.document.getElementById('selectionCount').textContent,'已选 0 个');
  assert.ok([...h.document.querySelectorAll('[data-bulk]')].every(button=>button.disabled));
});

test('子线程摘要在账号卡片内限制高度并保留长错误换行',async t=>{
  const h=await harness();t.after(h.close);
  const config={...h.U.defaults('lanes'),siteId:'site',keyId:'key',model:'MODEL'};
  const longError='HTTP 500 · '+('x'.repeat(420));
  const lanes=Array.from({length:8},(_,index)=>({index:index+1,status:'waiting',attempts:2,lastError:longError}));
  h.data.tasks.push({config,configRevision:1,totalLanes:12,runtime:runtime({lanes})});
  h.click('refresh');await until(()=>h.document.querySelector('.task-card'));
  const details=h.document.querySelector('.lane-details');
  assert.ok(details);
  assert.equal(details.querySelectorAll('p').length,9);
  assert.match(details.textContent,/更多线程请在详情中查看/);
  assert.match(details.querySelector('p').textContent,/x{20}/);
  assert.equal(details.dataset.id,'lanes:lanes');
  assert.deepEqual(h.errors,[]);
});

test('重新开始通过页面确认框执行', async t=>{
  const h=await harness();t.after(h.close);const config=h.U.defaults('task');Object.assign(config,{siteId:'site',keyId:'key',model:'MODEL'});
  h.data.tasks.push({config,configRevision:1,runtime:runtime({queueRound:1,totalAttempts:2}),totalLanes:0});
  h.click('refresh');await until(()=>h.document.querySelector('.task-card'));
  [...h.document.querySelectorAll('.task-card button')].find(n=>n.textContent==='重新开始').click();await until(()=>h.document.getElementById('confirmDialog').open);
  assert.equal(h.calls.filter(c=>c.path==='tasks/action').length,0);h.click('confirmYes');await until(()=>h.calls.some(c=>c.path==='tasks/action'));
  assert.equal(h.calls.find(c=>c.path==='tasks/action').body.action,'restart');assert.deepEqual(h.errors,[]);
});

test('每个账号分别保存通知目标和日志参数，新账号通知默认关闭',async t=>{
  const h=await harness();t.after(h.close);h.data.settings.telegramEnabled=true;
  const f=await draft(h),form=h.document.getElementById('taskForm');
  assert.equal(f.telegramEnabled.checked,false);assert.equal(f.barkEnabled.checked,false);
  f.telegramEnabled.checked=true;f.telegramEnabled.dispatchEvent(new h.window.Event('change'));f.telegramToken.value='BOT-A';f.telegramChatId.value='CHAT-A';
  f.maxEventsPerTask.value='25';f.summaryBytes.value='1024';f.notificationMaxAttempts.value='3';
  h.submit(form);await until(()=>!h.document.getElementById('taskDialog').open&&!form.querySelector('button[type=submit]').disabled);
  const a=h.calls.find(c=>c.path==='accounts/save').body;
  assert.equal(a.config.settings.telegramEnabled,true);assert.equal(a.secrets.telegramToken,'BOT-A');assert.equal(a.secrets.telegramChatId,'CHAT-A');
  assert.equal(a.config.settings.maxEventsPerTask,25);assert.equal(a.config.settings.notificationMaxAttempts,3);
  const second=await draft(h);assert.equal(second.telegramEnabled.checked,false);assert.equal(second.telegramToken.value,'');
  second.barkEnabled.checked=true;second.barkEnabled.dispatchEvent(new h.window.Event('change'));second.barkKey.value='BARK-B';second.maxEventsPerTask.value='80';
  h.submit(form);await until(()=>h.calls.filter(c=>c.path==='accounts/save').length===2);
  const b=h.calls.filter(c=>c.path==='accounts/save')[1].body;
  assert.equal(b.config.settings.telegramEnabled,false);assert.equal(b.config.settings.barkEnabled,true);
  assert.equal(b.secrets.barkKey,'BARK-B');assert.equal(b.config.settings.maxEventsPerTask,80);assert.deepEqual(h.errors,[]);
});

test('编辑账号回显完整凭据，刷新不覆盖凭据版本和修改草稿',async t=>{
  const h=await harness();t.after(h.close);const f=await draft(h),form=h.document.getElementById('taskForm');
  f.telegramToken.value='BOT-A';f.telegramChatId.value='CHAT-A';h.submit(form);
  await until(()=>!h.document.getElementById('taskDialog').open&&h.document.querySelector('.task-card'));
  h.document.querySelector('.task-edit').click();
  await until(()=>h.document.getElementById('taskDialog').open);
  assert.equal(f.token.value,'TOKEN-ui-test');assert.equal(f.telegramToken.value,'BOT-A');assert.equal(f.telegramChatId.value,'CHAT-A');
  assert.equal(f.telegramToken.type,'text');assert.equal(f.barkKey.type,'text');
  const version=f.notificationCredentialVersion.value;f.telegramChatId.value='DRAFT-CHAT';
  h.data.accountSecrets[f.id.value].credentialVersion=99;h.click('refresh');
  await until(()=>h.calls.filter(c=>c.path==='state').length>=3);
  assert.equal(f.telegramChatId.value,'DRAFT-CHAT');assert.equal(f.notificationCredentialVersion.value,version);
});

test('通知开启时校验接收方，未填写时定位通知页签',async t=>{
  const h=await harness();t.after(h.close);const f=await draft(h);
  f.telegramEnabled.checked=true;f.telegramEnabled.dispatchEvent(new h.window.Event('change'));
  h.submit(h.document.getElementById('taskForm'));
  assert.equal(h.document.querySelector('[data-editor-pane=notify]').hidden,false);
  assert.equal(h.calls.filter(c=>c.path==='accounts/save').length,0);
});

test('日志统计位于账号卡片，仅展示和恢复当前账号的通知',async t=>{
  const h=await harness();t.after(h.close);
  for(const id of ['a','b']){const config=h.U.defaults(id);Object.assign(config,{siteId:'site',keyId:'key',model:'MODEL',name:'账号-'+id});
    h.data.tasks.push({config,configRevision:1,runtime:runtime({totalAttempts:2,entered:true}),totalLanes:0,events:[{at:new Date().toISOString(),kind:'request',message:'日志-'+id}]});
    h.data.stats.push({taskId:id,day:'2026-10-10',protocol:'responses',entered:id==='a'?1:99,recoveries:0,heartbeats:0});
    h.data.notifications.push({id:'notice-'+id,taskId:id,title:'通知-'+id,message:'消息-'+id,state:'paused',attempts:1,channel:'telegram'});}
  h.click('refresh');await until(()=>h.document.querySelector('.task-logs'));
  h.document.querySelector('.task-logs').click();await until(()=>h.document.getElementById('detailDialog').open);
  const content=h.document.getElementById('detailContent');assert.match(content.textContent,/日志-a/);assert.match(content.textContent,/通知-a/);assert.doesNotMatch(content.textContent,/日志-b|通知-b|99/);
  [...content.querySelectorAll('button')].find(n=>n.textContent==='恢复通知投递').click();await until(()=>h.document.getElementById('confirmDialog').open);h.click('confirmYes');
  await until(()=>h.calls.some(c=>c.path==='notifications/resume'));
  assert.deepEqual(h.calls.find(c=>c.path==='notifications/resume').body,{id:'a'});assert.equal(h.data.notifications[1].state,'paused');
});

test('模板编辑器使用紧凑布局，保存保持完整 JSON 数值',async t=>{
  const h=await harness();t.after(h.close);h.document.querySelector('[data-page=templates]').click();h.document.querySelector('#templateList button').click();
  await until(()=>h.document.getElementById('templateDialog').open);const form=h.document.getElementById('templateForm');
  assert.equal(h.document.getElementById('templateDialog').classList.contains('compact-dialog'),true);assert.ok(form.elements.json.rows<=12);
  assert.match(form.elements.json.value,/9007199254740993123456789/);h.submit(form);await until(()=>!h.document.getElementById('templateDialog').open);
  assert.match(h.calls.find(c=>c.path==='templates/save').body.body,/9007199254740993123456789/);
});

test('账号导出包含完整凭据并保持请求 Body 精度',async t=>{
  const h=await harness();t.after(h.close);const f=await draft(h),form=h.document.getElementById('taskForm');f.telegramToken.value='BOT-EXPORT';
  h.submit(form);await until(()=>!h.document.getElementById('taskDialog').open&&h.document.querySelector('.task-card'));
  [...h.document.querySelectorAll('.task-card button')].find(n=>n.textContent==='导出账号').click();await until(()=>h.document.getElementById('exportDialog').open);
  const text=h.document.getElementById('exportText').value;
  assert.match(text,/TOKEN-ui-test/);assert.match(text,/BOT-EXPORT/);assert.match(text,/9007199254740993123456789/);
  const imported=h.U.importAccount(text);assert.match(imported.config.requestOverride.body,/9007199254740993123456789/);
  assert.equal(imported.secrets.telegramToken,'BOT-EXPORT');assert.deepEqual(h.errors,[]);
});

test('添加账号时可直接查询模型和额度，无需先保存账号或填写模型',async t=>{
  const h=await harness();t.after(h.close);h.click('newAccount');await until(()=>h.document.getElementById('taskDialog').open);
  const f=h.document.getElementById('taskForm').elements;f.baseUrl.value='http://127.0.0.1';f.token.value='TOKEN-query';
  assert.equal(f.model.value,'');h.click('queryAccount');await until(()=>h.document.querySelectorAll('#modelOptions option').length===2);
  assert.match(h.document.getElementById('modelQueryStatus').textContent,/2 个模型.*9\.00/);
  const request=h.calls.find(c=>c.path==='accounts/query');assert.equal(request.body.token,'TOKEN-query');
  assert.equal(h.calls.some(c=>['accounts/save','tasks/action'].includes(c.path)),false);
});
