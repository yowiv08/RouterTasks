import { createServer } from 'node:http';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { pageHtml, fixtureData, controller, sampleConfig } from '../tests/ui/harness.mjs';

// 回环管理 API 与宿主 iframe 沙箱的浏览器检查。
const out = resolve(process.env.ROUTERTASKS_BROWSER_OUTPUT || 'artifacts/browser');
await mkdir(out,{recursive:true});
const data=await fixtureData(), api=controller(data), checks=[];
const manualProxy={id:'manual-browser',subscriptionId:'manual',address:'http://127.0.0.1:18080',state:'Ready',available:true};
const subscriptionProxy={id:'subscription-browser',subscriptionId:'browser-source',address:'socks5://127.0.0.1:11080',state:'Ready',available:true};
data.proxyCatalog={subscriptions:[{id:'manual',name:'手动节点',enabled:true},{id:'browser-source',name:'浏览器测试订阅',enabled:true}],nodes:[manualProxy,subscriptionProxy]};
const hostLag=5*60*1000, now=new Date(Date.now()-hostLag).toISOString();
const definitions=[
  ['排队入口 · Responses','queue_then_keepalive','running','queue',false,500],
  ['工作时段持续保活','keepalive','running','keepalive',true,200],
  ['自动重排关闭 · 等待确认','queue_then_keepalive','paused','keepalive',false,500],
  ['首次心跳 · 尚未确认','keepalive','running','keepalive',false,null],
  ['仅排队 · 已进入','queue','completed','queue',false,200]
];
for(const [i,def] of definitions.entries()) {
  const [name,mode,status,phase,confirmed,code]=def;
  const lanes=i===0?Array.from({length:8},(_,index)=>({index:index+1,status:'waiting',attempts:2,lastError:'HTTP 500 · '+('x'.repeat(420))})):[];
  data.tasks.push({config:sampleConfig('task'+i,{name,mode,siteId:'site',keyId:'key',model:i%2?'claude-sonnet-4-6':'gpt-5.4',autoRequeue:i!==2}),configRevision:1,
    runtime:{status,phase,queueRound:i===1?3:1,totalAttempts:i===1?54:13,heartbeats:i===1?41:0,recoveries:i===1?2:0,
      sessionConfirmed:confirmed,lastSuccessAt:i!==0?now:null,lastHttpStatus:code,matchedRule:code===500?'HTTP 500':code===200?'SSE accepted':null,
      pauseReason:i===2?'HTTP 500 命中排队规则；自动重新排队已关闭':null,nextAttemptAt:status==='running'?now:null,lanes},totalLanes:i===0?12:0});
}
data.tasks[1].runtime.lastSuccessAt=new Date(Date.parse(now)-30000).toISOString();
data.tasks[1].runtime.nextAttemptAt=new Date(Date.parse(now)+90000).toISOString();
Object.assign(data.tasks[1].runtime,{lastRoute:'pool',lastProxy:manualProxy});
Object.assign(data.tasks[0].runtime.lanes[0],{lastRoute:'pool',lastProxy:subscriptionProxy});
data.activeCount=3;
data.stats=[{taskId:'task1',day:now.slice(0,10),protocol:'responses',entered:3,recoveries:2,heartbeats:41}];
data.usageResults={task1:{queriedAt:now,lastRoute:'pool',lastProxy:manualProxy,items:Array.from({length:51},(_,index)=>({
  id:String(index),at:'2026-10-22T00:00:00Z',type:index===50?5:2,modelName:'usage-model-'+index,
  tokenName:'浏览器模拟令牌',quota:index===50?null:12500,promptTokens:120,completionTokens:30,useTime:4,isStream:true,
  group:'default',content:index===0?'长日志内容 '+('x'.repeat(420)):'usage-content-'+index,other:'{"cache_tokens":10}'
}))}};
const bridge=`<script>(()=>{let n=0;const pending=new Map();addEventListener('message',e=>{if(e.data?.type!=='router2api-response')return;const p=pending.get(e.data.id);if(!p)return;pending.delete(e.data.id);e.data.ok?p.resolve(e.data.body):p.reject(Error(e.data.error));});window.Router2API={request:(method,route,body)=>new Promise((resolve,reject)=>{const id=String(++n);pending.set(id,{resolve,reject});parent.postMessage({type:'router2api-request',id,method,route,body},'*');})};})();</script>`;
const html=(await pageHtml()).replace('<head>',()=>'<head>'+bridge);
const wrapper=`<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>html,body{margin:0}iframe{width:100%;height:100vh;border:0;display:block}</style></head><body>
<iframe id="plugin" sandbox="allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox" title="RouterTasks 管理页面"></iframe>
<script>const frame=document.getElementById('plugin');frame.srcdoc=${JSON.stringify(html).replaceAll('<','\\u003c')};addEventListener('message',async e=>{if(e.source!==frame.contentWindow||e.data?.type!=='router2api-request')return;try{const r=await fetch('/api/plugins/router-tasks/'+e.data.route,{method:e.data.method,headers:{'Content-Type':'application/json'},body:e.data.body==null?undefined:JSON.stringify(e.data.body)});const body=await r.json();e.source.postMessage({type:'router2api-response',id:e.data.id,ok:r.ok,body,error:body.error},'*');}catch(error){e.source.postMessage({type:'router2api-response',id:e.data.id,ok:false,error:error.message},'*');}});</script></body></html>`;
const server=createServer(async (req,res)=>{
  if(req.url==='/'){res.writeHead(200,{'Content-Type':'text/html; charset=utf-8'});res.end(wrapper);return;}
  if(!req.url.startsWith('/api/plugins/router-tasks/')){res.writeHead(404);res.end();return;}
  try {let body='';for await(const part of req){body+=part;if(body.length>1048576)throw Error('Too large');}
    const route=req.url.slice('/api/plugins/router-tasks/'.length);
    const value=await api.request(req.method,route,body?JSON.parse(body):undefined);
    if(req.method==='GET'&&['state','tasks/detail'].includes(route.split('?')[0]))value.serverNow=new Date(Date.now()-hostLag).toISOString();
    res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(value));
  }catch(error){res.writeHead(400,{'Content-Type':'application/json'});res.end(JSON.stringify({error:error.message}));}
});
await new Promise(r=>server.listen(0,'127.0.0.1',r));
const origin='http://127.0.0.1:'+server.address().port;
let browser;
try {
  const options={headless:true};
  if(process.env.BROWSER_EXECUTABLE)options.executablePath=process.env.BROWSER_EXECUTABLE;
  else if(process.platform==='win32')options.channel='chrome';
  browser=await chromium.launch(options);
  const context=await browser.newContext({viewport:{width:1440,height:1100},locale:'zh-CN',timezoneId:'America/Los_Angeles'});
  const errors=[],blocked=[];await context.route('**/*',route=>route.request().url().startsWith(origin)?route.continue():(blocked.push(route.request().url()),route.abort()));
  const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
  await page.goto(origin);const frame=page.frameLocator('#plugin');await frame.locator('#taskList .task-card').first().waitFor();
  assert.equal(await frame.locator('nav button').count(),2);
  assert.equal(await frame.locator('#taskList .task-card').count(),5);
  assert.match(await frame.locator('#taskList').innerText(),/等待心跳确认/);checks.push('账号任务与请求模板双导航，三模式状态展示');
  assert.match(await frame.locator('.task-card[data-id="task1"] .task-network').innerText(),/18080/);
  assert.match(await frame.locator('.task-card[data-id="task0"] .lane-details').textContent(),/11080/);
  checks.push('账号卡片与线程摘要显示实际代理节点');
  const displayedTimes=await frame.locator('.task-card[data-id="task1"] .task-info>div').allTextContents();
  const shanghaiTimestamp=text=>{
    const [year,month,day,hour,minute,second]=text.match(/\d+/g).map(Number);
    return Date.UTC(year,month-1,day,hour-8,minute,second);
  };
  const next=shanghaiTimestamp(displayedTimes[0]),last=shanghaiTimestamp(displayedTimes[1]);
  assert.ok(Math.abs(next-(Date.parse(now)+hostLag+90000))<2000,displayedTimes.join('；'));
  assert.ok(Math.abs(last-(Date.parse(now)+hostLag-30000))<2000,displayedTimes.join('；'));
  assert.equal(next-last,120000);
  checks.push('宿主慢 5 分钟、浏览器为洛杉矶时区时，卡片按本机时钟校准为上海时间且保留请求间隔');
  const laneDetails=frame.locator('#taskList .task-card').first().locator('.lane-details');
  await laneDetails.locator('summary').click();
  const laneLayout=await laneDetails.evaluate(node=>({height:node.getBoundingClientRect().height,scrollHeight:node.scrollHeight,width:node.clientWidth,scrollWidth:node.scrollWidth,overflow:getComputedStyle(node).overflow}));
  assert.equal(laneLayout.overflow,'auto');
  assert.ok(laneLayout.scrollHeight>laneLayout.height,JSON.stringify(laneLayout));
  assert.ok(laneLayout.scrollWidth<=laneLayout.width+1&&laneLayout.height<=132,JSON.stringify(laneLayout));
  checks.push('子线程摘要在账号卡片内滚动，长错误不撑破卡片');
  await page.screenshot({path:resolve(out,'desktop.png')});
  assert.equal(await frame.locator('[data-bulk]:disabled').count(),4);
  const selectedCard=frame.locator('#taskList .task-card').nth(2);
  await selectedCard.locator('.task-head input[type=checkbox]').check();
  assert.equal(await frame.locator('[data-bulk]:enabled').count(),4);
  assert.equal(await frame.locator('#selectAll').evaluate(n=>n.indeterminate),true);
  const bulkStyle=await frame.locator('[data-bulk=resume]').evaluate(n=>({opacity:getComputedStyle(n).opacity,color:getComputedStyle(n).color}));
  assert.equal(bulkStyle.opacity,'1');assert.notEqual(bulkStyle.color,'rgb(124, 128, 148)');
  assert.deepEqual(await selectedCard.locator('.card-actions').evaluate(n=>[...n.children].map(c=>c.tagName==='DETAILS'?c.querySelector('summary').textContent:c.textContent)),['编辑','继续任务','日志','更多']);
  await page.screenshot({path:resolve(out,'desktop-selected.png')});
  await selectedCard.locator('.task-edit').click();await frame.locator('#taskDialog').waitFor({state:'visible'});
  assert.equal(await frame.locator('#taskEditorTitle').innerText(),'编辑账号');
  await frame.locator('#taskDialog [data-close=taskDialog]').first().click();
  assert.equal(await frame.locator('#taskList .task-card').first().locator('.task-edit').isDisabled(),true);
  checks.push('勾选即时高亮批量按钮，卡片按编辑、运行操作、日志、更多排序');
  await frame.locator('#selectAll').check();assert.equal(await frame.locator('.task-head input:checked').count(),5);
  await frame.locator('#selectAll').uncheck();assert.equal(await frame.locator('[data-bulk]:disabled').count(),4);
  checks.push('全选、部分选择与清空选择同步按钮状态');
  await frame.locator('#newAccount').click();await frame.locator('#taskDialog').waitFor({state:'visible'});
  assert.equal(await frame.locator('#claudeContextField').isVisible(),false);
  await frame.locator('#taskForm [name=templateId]').selectOption('messages');
  await frame.locator('#claudeContextField').waitFor({state:'visible'});
  assert.equal(await frame.locator('#taskForm [name=claude1MContext]').isChecked(),true);
  await frame.locator('#taskForm [name=claude1MContext]').uncheck();
  await frame.locator('#taskForm [name=templateId]').selectOption('responses');
  await frame.locator('#claudeContextField').waitFor({state:'hidden'});
  assert.equal(await frame.locator('#taskForm [name=claude1MContext]').isChecked(),false);
  checks.push('Claude 1M 上下文默认开启，可关闭，切换协议保留选择');
  await frame.locator('#taskForm [name=baseUrl]').fill('http://127.0.0.1');
  await frame.locator('#taskForm [name=token]').fill('TOKEN-browser-account');
  await frame.locator('#queryAccount').click();await frame.locator('#modelOptions option').first().waitFor({state:'attached'});
  assert.match(await frame.locator('#modelQueryStatus').innerText(),/2 个模型/);checks.push('添加账号时直接查询模型与额度');
  await frame.locator('#taskForm [name=route]').selectOption('pool');
  await frame.locator('#proxyCatalogStatus').getByText('2 个可用节点',{exact:true}).waitFor();
  await frame.locator('#taskForm [name=proxySourceMode]').selectOption('manual');
  await frame.locator('#taskForm [name=proxyId]').selectOption('manual-browser');
  await frame.locator('#taskForm [name=proxySourceMode]').selectOption('selected');
  await frame.locator('#proxySubscriptions [data-proxy-source="browser-source"]').check();
  await frame.locator('#proxySubscriptions [data-proxy-source="manual"]').uncheck();
  assert.equal(await frame.locator('#taskForm [name=proxyId]').inputValue(),'manual-browser');
  assert.match(await frame.locator('#proxySelectionStatus').innerText(),/运行时将报错/);
  await frame.locator('#taskForm [name=proxyId]').selectOption('subscription-browser');
  assert.doesNotMatch(await frame.locator('#proxySelectionStatus').innerText(),/运行时将报错/);
  await frame.locator('#taskForm [name=proxySourceMode]').selectOption('manual');
  await frame.locator('#taskForm [name=proxyId]').selectOption('manual-browser');
  data.queryResult={lastProxy:manualProxy};
  const proxyQuery=page.waitForResponse(result=>result.url().endsWith('/accounts/query'));
  await frame.locator('#queryAccount').click();await proxyQuery;
  await frame.locator('#modelQueryStatus').getByText(/18080/).waitFor();
  const query=api.calls.filter(call=>call.path==='accounts/query').at(-1);
  assert.deepEqual(query.body.site.subscriptionIds,['manual']);assert.equal(query.body.site.proxyId,'manual-browser');
  await page.screenshot({path:resolve(out,'proxy-selection.png')});
  data.proxyCatalog.nodes=[];await frame.locator('#refreshProxies').click();
  await frame.locator('#proxyCatalogStatus').getByText('0 个可用节点',{exact:true}).waitFor();
  assert.equal(await frame.locator('#taskForm [name=proxyId]').inputValue(),'manual-browser');
  assert.match(await frame.locator('#taskForm [name=proxyId] option:checked').innerText(),/节点已失效/);
  data.proxyCatalog.nodes=[manualProxy,subscriptionProxy];await frame.locator('#refreshProxies').click();
  await frame.locator('#proxyCatalogStatus').getByText('1 个可用节点',{exact:true}).waitFor();
  checks.push('代理来源全选、仅手动、指定订阅与固定节点，越界及删除保持选择，草稿查询携带代理配置');
  await frame.locator('#taskForm [name=model]').fill('MODEL');
  await frame.locator('#taskForm [name=prompt]').fill('测试保活');await frame.locator('#taskForm [name=prompt]').press('Tab');
  await frame.locator('#taskForm [name=autoRequeue]').uncheck();
  await frame.locator('#taskForm [name=queueHttpStatuses]').fill('500,429');
  await frame.locator('#taskForm .editor-body').evaluate(n=>n.scrollTop=0);
  await page.screenshot({path:resolve(out,'editor.png')});
  await frame.locator('[data-editor-target=queue]').click();await page.screenshot({path:resolve(out,'queue-settings.png')});
  await frame.locator('[data-editor-target=keepalive]').click();await frame.locator('#taskForm [name=k_maxAttemptsPerLane]').fill('500');
  await frame.locator('[data-editor-target=notify]').click();
  await frame.locator('#taskForm [name=telegramEnabled]').check();
  await frame.locator('#taskForm [name=telegramToken]').fill('BOT-browser-account');
  await frame.locator('#taskForm [name=telegramChatId]').fill('CHAT-browser-account');
  await frame.locator('#taskForm .editor-body').evaluate(n=>n.scrollTop=0);
  await page.screenshot({path:resolve(out,'account-notifications.png')});
  await frame.locator('#previewRequest').click();await frame.locator('#previewDialog').waitFor({state:'visible'});
  const preview=await frame.locator('#previewContent').innerText();
  assert.match(preview,/9007199254740993123456789/);assert.match(preview,/Bearer TOKEN-browser-account/);
  checks.push('完整凭据与无损请求预览');
  await frame.locator('[data-close=previewDialog]').click();
  await frame.locator('#taskForm button[type=submit]').click();await frame.locator('#taskDialog').waitFor({state:'hidden'});
  const saved=api.calls.find(c=>c.path==='accounts/save');
  assert.equal(saved.body.config.autoRequeue,false);assert.deepEqual(saved.body.site.queueHttpStatuses,[500,429]);
  assert.equal(saved.body.config.claude1MContext,false);
  assert.deepEqual(saved.body.site.subscriptionIds,['manual']);assert.equal(saved.body.site.proxyId,'manual-browser');
  assert.equal(saved.body.secrets.telegramChatId,'CHAT-browser-account');assert.equal(saved.body.config.settings.telegramEnabled,true);
  assert.match(saved.body.config.requestOverride.body,/9007199254740993123456789/);checks.push('账号连接、阶段参数与独立通知统一保存');
  await frame.locator('#taskList .task-card').last().locator('.task-logs').click();await frame.locator('#detailDialog').waitFor({state:'visible'});
  assert.match(await frame.locator('#detailTitle').innerText(),/日志统计/);await frame.locator('[data-close=detailDialog]').click();checks.push('任务卡片日志统计入口');
  await frame.locator('#taskList .task-card').last().locator('.task-more summary').click();
  await frame.locator('#taskList .task-card').last().getByRole('button',{name:'重新开始',exact:true}).click();
  await frame.locator('#confirmDialog').waitFor({state:'visible'});await frame.locator('#confirmYes').click();checks.push('页面内确认操作');
  await frame.locator('#taskList .task-card').last().locator('.task-more summary').click();
  await frame.locator('#taskList .task-card').last().getByRole('button',{name:'导出账号',exact:true}).click();
  await frame.locator('#exportDialog').waitFor({state:'visible'});
  const exported=await frame.locator('#exportText').inputValue();assert.match(exported,/TOKEN-browser-account/);assert.match(exported,/BOT-browser-account/);
  assert.equal(JSON.parse(exported).account.site.proxyId,'manual-browser');
  await frame.locator('[data-close=exportDialog]').first().click();
  await frame.locator('#importFile').setInputFiles({name:'account.json',mimeType:'application/json',buffer:Buffer.from(exported)});
  await frame.locator('#taskDialog').waitFor({state:'visible'});
  assert.equal(await frame.locator('#taskForm [name=token]').inputValue(),'TOKEN-browser-account');
  assert.equal(await frame.locator('#taskForm [name=telegramChatId]').inputValue(),'CHAT-browser-account');
  assert.equal(await frame.locator('#taskForm [name=proxyId]').inputValue(),'manual-browser');
  assert.equal(await frame.locator('#taskForm [name=proxySourceMode]').inputValue(),'manual');
  assert.notEqual(await frame.locator('#taskForm [name=id]').inputValue(),saved.body.config.id);
  await frame.locator('#taskDialog [data-close=taskDialog]').first().click();checks.push('账号导出与导入保留独立凭据及 JSON 精度');
  await frame.locator('#newAccount').click();await frame.locator('#taskDialog').waitFor({state:'visible'});
  assert.equal(await frame.locator('#taskForm [name=telegramEnabled]').isChecked(),false);
  assert.equal(await frame.locator('#taskForm [name=telegramToken]').inputValue(),'');
  await frame.locator('#taskDialog [data-close=taskDialog]').first().click();checks.push('新账号通知默认关闭且不复用其他账号接收方');
  await frame.locator('[data-page=templates]').click();
  for(const viewport of [{width:1280,height:720},{width:1024,height:600},{width:390,height:844},{width:390,height:600}]){
    await page.setViewportSize(viewport);await frame.locator('#templateList button').first().click();
    await frame.locator('#templateDialog').waitFor({state:'visible'});
    const layout=await frame.locator('#templateDialog').evaluate(dialog=>{
      const text=dialog.querySelector('textarea'),button=dialog.querySelector('button[type=submit]'),r=button.getBoundingClientRect();
      const hit=document.elementFromPoint(r.left+r.width/2,r.top+r.height/2);
      return {height:innerHeight,top:r.top,bottom:r.bottom,buttonVisible:hit===button||button.contains(hit),
        outerScroll:dialog.scrollHeight-dialog.clientHeight,editorHeight:text.clientHeight,editorScroll:text.scrollHeight-text.clientHeight};
    });
    assert.ok(layout.top>=0&&layout.bottom<=layout.height&&layout.buttonVisible,JSON.stringify({viewport,layout}));
    assert.ok(layout.outerScroll<=2&&layout.editorHeight<=330&&layout.editorScroll>0,JSON.stringify({viewport,layout}));
    if(viewport.width===1280)await page.screenshot({path:resolve(out,'template-desktop.png')});
    if(viewport.width===390&&viewport.height===600)await page.screenshot({path:resolve(out,'template-mobile.png')});
    await frame.locator('#templateDialog [data-close=templateDialog]').first().click();
  }
  checks.push('720px 与 600px 视口中模板保存按钮始终可见，仅 JSON 内部滚动');
  await page.setViewportSize({width:390,height:844});await frame.locator('[data-page=tasks]').click();
  await page.screenshot({path:resolve(out,'mobile.png')});
  const sizes=await frame.locator('html').evaluate(n=>({scroll:n.scrollWidth,width:n.clientWidth}));
  assert.ok(sizes.scroll<=sizes.width,JSON.stringify(sizes));
  await frame.locator('#newAccount').click();await frame.locator('#taskDialog').waitFor({state:'visible'});
  await frame.locator('#taskForm [name=route]').selectOption('pool');
  await frame.locator('#proxyCatalogStatus').getByText('2 个可用节点',{exact:true}).waitFor();
  await frame.locator('#taskForm [name=proxySourceMode]').selectOption('selected');
  const proxySize=await frame.locator('#proxyFields').evaluate(node=>({scroll:node.scrollWidth,width:node.clientWidth}));
  assert.ok(proxySize.scroll<=proxySize.width+1,JSON.stringify(proxySize));
  await page.screenshot({path:resolve(out,'mobile-editor.png')});
  const footer=await frame.locator('#taskForm button[type=submit]').boundingBox();
  assert.ok(footer&&footer.y+footer.height<=844);checks.push('390px 移动端无横向溢出，账号保存按钮可见');
  await frame.locator('#taskDialog [data-close=taskDialog]').first().click();
  const laneLayouts=[];
  for(const viewport of [{width:1440,height:1100},{width:1024,height:720},{width:768,height:720},{width:560,height:844},{width:390,height:844}]){
    await page.setViewportSize(viewport);
    await laneDetails.evaluate(node=>{node.open=true;node.scrollTop=0;});
    await laneDetails.scrollIntoViewIfNeeded();
    const layout=await laneDetails.evaluate(node=>{
      const r=node.getBoundingClientRect(),card=node.closest('.task-card'),c=card.getBoundingClientRect();
      return {open:node.open,height:r.height,clientWidth:node.clientWidth,scrollWidth:node.scrollWidth,
        scrollHeight:node.scrollHeight,insideCard:r.left>=c.left&&r.right<=c.right,
        cardOverflow:card.scrollWidth-card.clientWidth,pageOverflow:document.documentElement.scrollWidth-document.documentElement.clientWidth,
        textOverflow:Math.max(...[...node.querySelectorAll('p')].map(p=>p.scrollWidth-p.clientWidth))};
    });
    laneLayouts.push({viewport,...layout});
    assert.ok(layout.open&&layout.insideCard&&layout.height<=132&&layout.scrollHeight>layout.height,JSON.stringify({viewport,layout}));
    assert.ok(layout.scrollWidth<=layout.clientWidth+1&&layout.cardOverflow<=1&&layout.pageOverflow<=1&&layout.textOverflow<=1,JSON.stringify({viewport,layout}));
    if(viewport.width===1440||viewport.width===390)await frame.locator('#taskList .task-card').first().screenshot({path:resolve(out,'lanes-open-'+viewport.width+'.png')});
  }
  checks.push('展开子线程在五组视口内无横向溢出，超长错误完整换行且内部滚动');
  await page.setViewportSize({width:1440,height:1100});
  const deleteCard=frame.locator('.task-card[data-id="'+saved.body.config.id+'"]');
  await deleteCard.locator('.task-head input[type=checkbox]').check();
  const deletesBefore=api.calls.filter(c=>c.path==='accounts/delete').length;
  await frame.locator('[data-bulk=delete]').click();await frame.locator('#confirmDialog').waitFor({state:'visible'});
  await frame.locator('#confirmNo').click();await frame.locator('#confirmDialog').waitFor({state:'hidden'});
  assert.equal(api.calls.filter(c=>c.path==='accounts/delete').length,deletesBefore);
  await frame.locator('[data-bulk=delete]').click();await frame.locator('#confirmDialog').waitFor({state:'visible'});
  await frame.locator('#confirmYes').click();await deleteCard.waitFor({state:'detached'});
  assert.deepEqual(api.calls.filter(c=>c.path==='accounts/delete').map(c=>c.body.id),[saved.body.config.id]);
  assert.equal(await frame.locator('#selectionCount').innerText(),'已选 0 个');
  checks.push('删除所选在浏览器中取消不删除、确认仅删除选中的模拟账号');
  const actionTask=data.tasks.find(task=>task.config.id==='task2'),actionCard=frame.locator('.task-card[data-id="task2"]');
  const taskActions=()=>api.calls.filter(call=>call.path==='tasks/action');
  async function clickTaskAction(action){
    const before=taskActions().length;
    if(action==='restart'){
      await actionCard.locator('.run').click();await frame.locator('#confirmDialog').waitFor({state:'visible'});
      assert.equal(taskActions().length,before);await frame.locator('#confirmNo').click();
      await frame.locator('#confirmDialog').waitFor({state:'hidden'});assert.equal(taskActions().length,before);
      await actionCard.locator('.run').click();await frame.locator('#confirmDialog').waitFor({state:'visible'});
    }
    const response=page.waitForResponse(result=>result.url()===origin+'/api/plugins/router-tasks/tasks/action'&&result.request().method()==='POST');
    await (action==='restart'?frame.locator('#confirmYes'):actionCard.locator('.run')).click();await response;
    assert.equal(taskActions().length,before+1);assert.deepEqual(taskActions().at(-1).body,{ids:['task2'],action});
  }
  for(const mode of ['queue','keepalive','queue_then_keepalive']){
    actionTask.config.mode=mode;
    Object.assign(actionTask.runtime,{status:'paused',phase:mode==='keepalive'?'keepalive':'queue',entered:false,responseState:'none',
      queueLaneAttempts:{},keepaliveLaneAttempts:{},lanes:[]});
    await page.reload();await actionCard.getByRole('button',{name:'继续任务',exact:true}).waitFor();await clickTaskAction('resume');
  }
  checks.push('三模式暂停后点击继续任务，管理桥实际收到 resume');
  actionTask.config.mode='queue';Object.assign(actionTask.runtime,{phase:'queue',entered:true,responseState:'interrupted'});
  await page.reload();await actionCard.getByRole('button',{name:'重新开始',exact:true}).waitFor();await clickTaskAction('restart');
  checks.push('已接受后中断的仅排队请求显示重新开始，取消不发请求，确认发送 restart');
  actionTask.config.mode='keepalive';actionTask.config.keepalive.maxAttemptsPerLane=2;
  Object.assign(actionTask.runtime,{status:'exhausted',phase:'keepalive',keepaliveLaneAttempts:{1:2},totalAttempts:2});
  await page.reload();await actionCard.getByRole('button',{name:'重新开始',exact:true}).waitFor();
  await actionCard.locator('.task-edit').click();await frame.locator('#taskDialog').waitFor({state:'visible'});
  await frame.locator('[data-editor-target=keepalive]').click();await frame.locator('#taskForm [name=k_maxAttemptsPerLane]').fill('3');
  await frame.locator('#taskForm button[type=submit]').click();await frame.locator('#taskDialog').waitFor({state:'hidden'});
  await actionCard.getByRole('button',{name:'继续任务',exact:true}).waitFor();await clickTaskAction('resume');
  assert.equal(data.tasks.find(task=>task.config.id==='task2').runtime.totalAttempts,2);
  checks.push('保活预算耗尽显示重新开始，编辑增加预算后点击继续发送 resume 并保留已用次数');
  const usageCalls=()=>api.calls.filter(call=>call.path==='accounts/usage-logs');
  const usageCard=frame.locator('.task-card[data-id="task1"]');
  await usageCard.locator('.task-more summary').click();await usageCard.getByRole('button',{name:'使用日志',exact:true}).click();
  await frame.locator('#usageDialog[aria-busy=false]').waitFor();
  assert.deepEqual(usageCalls().at(-1).body,{id:'task1'});
  assert.equal(await frame.locator('#usageContent tbody tr').count(),50);
  assert.match(await frame.locator('#usageStatus').innerText(),/代理.*18080/);
  assert.equal(await frame.locator('#usageContent tbody td').first().innerText(),'2026/10/22 08:00:00');
  await page.screenshot({path:resolve(out,'usage-desktop.png')});
  await frame.locator('#nextUsage').click();assert.equal(await frame.locator('#usageContent tbody tr').count(),1);
  await frame.locator('#usageSearch').fill('usage-model-0');assert.equal(await frame.locator('#usageContent tbody tr').count(),1);
  await frame.locator('#usageType').selectOption('5');assert.match(await frame.locator('#usageContent').innerText(),/没有匹配/);
  await frame.locator('#usageSearch').fill('');assert.equal(await frame.locator('#usageContent tbody tr').count(),1);
  assert.equal(usageCalls().length,1);checks.push('使用日志管理桥仅传账号 ID，最近记录分页、搜索和类型筛选在本地执行');
  await frame.locator('#usageType').selectOption('');
  const usageLayouts=[];
  for(const viewport of [{width:1440,height:1100},{width:390,height:844}]){
    await page.setViewportSize(viewport);
    const layout=await frame.locator('#usageDialog').evaluate(node=>({dialogOverflow:node.scrollWidth-node.clientWidth,
      pageOverflow:document.documentElement.scrollWidth-document.documentElement.clientWidth,
      width:node.getBoundingClientRect().width,tableWidth:node.querySelector('.usage-table').clientWidth}));
    assert.ok(layout.dialogOverflow<=1&&layout.pageOverflow<=1,JSON.stringify({viewport,layout}));
    await frame.locator('#usageContent details').first().evaluate(node=>node.open=true);
    await page.screenshot({path:resolve(out,'usage-'+viewport.width+'.png')});usageLayouts.push({viewport,...layout});
  }
  checks.push('使用日志桌面与 390px 移动端无横向溢出，长详情在表格内滚动');
  await page.setViewportSize({width:1440,height:1100});
  data.usageError='模拟站点不支持使用日志';
  await frame.locator('#refreshUsage').click();await frame.locator('#usageError').getByText('模拟站点不支持使用日志',{exact:true}).waitFor();
  data.usageError='';data.usageResults.task1.items=[];
  await frame.locator('#refreshUsage').click();await frame.locator('#usageContent').getByText('站点暂无使用日志',{exact:true}).waitFor();
  await frame.locator('#usageDialog [data-close=usageDialog]').click();
  await usageCard.locator('.task-logs').click();await frame.locator('#detailDialog').waitFor({state:'visible'});
  await frame.locator('#detailContent').getByRole('button',{name:'使用日志',exact:true}).click();
  await frame.locator('#usageDialog[aria-busy=false]').waitFor();assert.deepEqual(usageCalls().at(-1).body,{id:'task1'});
  await frame.locator('#usageDialog [data-close=usageDialog]').click();await frame.locator('#detailDialog [data-close=detailDialog]').click();
  checks.push('日志统计可以打开使用日志，查询错误可重试且空记录有独立提示');
  assert.deepEqual(errors,[]);assert.deepEqual(blocked,[]);
  assert.ok(api.calls.some(c=>c.path==='tasks/action'&&c.body.action==='restart'));
  checks.push('浏览器运行无错误');
  await writeFile(resolve(out,'browser-report.json'),JSON.stringify({at:new Date().toISOString(),browser:browser.version(),checks,laneLayouts,usageLayouts,errors,blocked,scope:'本机模拟管理桥；与宿主一致的 iframe sandbox，未连接真实上游'},null,2));
  console.log(JSON.stringify({passed:checks.length,out,checks},null,2));
} finally {await browser?.close();await new Promise(r=>server.close(r));}
