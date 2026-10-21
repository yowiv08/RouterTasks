import test from 'node:test';
import assert from 'node:assert/strict';
import { harness, until, sampleConfig } from './harness.mjs';

const manual={id:'manual-a',subscriptionId:'manual',address:'http://127.0.0.1:8080',state:'Ready',available:true};
const subscribed={id:'sub-a',subscriptionId:'source-a',address:'socks5://127.0.0.1:1080',state:'Ready',available:true};
const catalog=()=>({subscriptions:[{id:'source-a',name:'订阅 A',enabled:true},{id:'source-off',name:'已停用订阅',enabled:false},{id:'manual',name:'手动节点',enabled:true}],nodes:[manual,subscribed,{...manual,id:'cooling',address:'http://127.0.0.1:8081',state:'Cooling',available:false}]});
const runtime=(values={})=>({status:'paused',phase:'queue',queueRound:1,totalAttempts:1,heartbeats:0,recoveries:0,lanes:[],...values});
async function draft(h){h.click('newAccount');await until(()=>h.document.getElementById('taskDialog').open);const f=h.document.getElementById('taskForm').elements;h.change(f.model,'MODEL');f.token.value='TOKEN-ui-only';f.baseUrl.value='http://127.0.0.1';return f;}
async function pool(h,f){h.change(f.route,'pool');await until(()=>h.document.getElementById('proxyCatalogStatus').textContent==='2 个可用节点');}

test('代理目录按需加载，来源和固定节点进入查询、预览和保存',async t=>{
  const h=await harness();t.after(h.close);h.data.proxyCatalog=catalog();h.data.queryResult={lastProxy:manual};const f=await draft(h);
  assert.equal(h.calls.filter(call=>call.path==='network/proxies').length,0);
  assert.equal(h.document.getElementById('proxyFields').hidden,true);await pool(h,f);
  assert.equal(f.proxySourceMode.value,'all');assert.equal(f.subscriptionIds.value,'');assert.equal(f.proxyId.value,'');
  assert.equal([...f.proxyId.options].find(option=>option.value==='cooling').disabled,true);
  h.change(f.proxySourceMode,'manual');assert.equal(f.subscriptionIds.value,'manual');
  assert.equal([...f.proxyId.options].some(option=>option.value==='sub-a'),false);h.change(f.proxyId,'manual-a');
  h.click('queryAccount');await until(()=>h.calls.some(call=>call.path==='accounts/query'));
  assert.deepEqual(h.calls.find(call=>call.path==='accounts/query').body.site.subscriptionIds,['manual']);
  assert.equal(h.calls.find(call=>call.path==='accounts/query').body.site.proxyId,'manual-a');
  await until(()=>h.document.getElementById('modelQueryStatus').textContent.includes(manual.address));
  h.click('previewRequest');await until(()=>h.document.getElementById('previewDialog').open);
  assert.equal(h.calls.find(call=>call.path==='accounts/preview').body.site.proxyId,'manual-a');h.document.querySelector('[data-close=previewDialog]').click();
  h.submit(h.document.getElementById('taskForm'));await until(()=>h.calls.some(call=>call.path==='accounts/save'));
  assert.deepEqual(h.calls.find(call=>call.path==='accounts/save').body.site.subscriptionIds,['manual']);
  assert.equal(h.calls.find(call=>call.path==='accounts/save').body.site.proxyId,'manual-a');assert.deepEqual(h.errors,[]);
});

test('指定来源支持复选，范围变化保留失效固定节点且不自动改选',async t=>{
  const h=await harness();t.after(h.close);h.data.proxyCatalog=catalog();const f=await draft(h);await pool(h,f);
  h.change(f.proxyId,'sub-a');h.change(f.proxySourceMode,'manual');assert.equal(f.proxyId.value,'sub-a');
  assert.match(f.proxyId.selectedOptions[0].textContent,/不在所选来源/);assert.match(h.document.getElementById('proxySelectionStatus').textContent,/运行时将报错/);
  h.change(f.proxySourceMode,'selected');assert.equal(h.document.getElementById('proxySubscriptions').hidden,false);
  h.document.querySelector('[data-proxy-source="source-a"]').click();h.document.querySelector('[data-proxy-source="manual"]').click();
  assert.equal(f.subscriptionIds.value,'source-a');assert.equal(f.proxyId.value,'sub-a');assert.doesNotMatch(f.proxyId.selectedOptions[0].textContent,/不在所选来源/);
  assert.equal(h.document.querySelector('[data-proxy-source="source-off"]').disabled,true);
  h.submit(h.document.getElementById('taskForm'));await until(()=>h.calls.some(call=>call.path==='accounts/save'));
  assert.deepEqual(h.calls.find(call=>call.path==='accounts/save').body.site.subscriptionIds,['source-a']);
  assert.equal(h.calls.find(call=>call.path==='accounts/save').body.site.proxyId,'sub-a');
});

test('空的指定来源阻止请求，自动选择显式清除固定节点',async t=>{
  const h=await harness();t.after(h.close);h.data.proxyCatalog=catalog();const f=await draft(h);await pool(h,f);
  h.change(f.proxyId,'manual-a');h.change(f.proxyId,'');h.change(f.proxySourceMode,'manual');h.change(f.proxySourceMode,'selected');
  h.document.querySelector('[data-proxy-source="manual"]').click();h.click('queryAccount');await until(()=>h.document.getElementById('notice').textContent.includes('请选择至少一个代理来源'));
  h.submit(h.document.getElementById('taskForm'));await until(()=>h.document.querySelector('#taskForm .form-error').textContent.includes('请选择至少一个代理来源'));
  assert.equal(h.calls.some(call=>['accounts/query','accounts/save'].includes(call.path)),false);
  h.change(f.proxySourceMode,'all');h.submit(h.document.getElementById('taskForm'));await until(()=>h.calls.some(call=>call.path==='accounts/save'));
  assert.equal(h.calls.find(call=>call.path==='accounts/save').body.site.proxyId,null);assert.deepEqual(h.calls.find(call=>call.path==='accounts/save').body.site.subscriptionIds,[]);
});

test('目录刷新失败或节点删除保留选择和编辑草稿',async t=>{
  const h=await harness();t.after(h.close);h.data.proxyCatalog=catalog();const f=await draft(h);await pool(h,f);f.name.value='尚未保存';h.change(f.proxyId,'manual-a');
  h.data.proxyCatalogError='目录暂时不可用';h.click('refreshProxies');await until(()=>h.document.getElementById('proxyCatalogStatus').textContent.includes('目录暂时不可用'));
  assert.equal(f.proxyId.value,'manual-a');assert.equal(f.name.value,'尚未保存');
  delete h.data.proxyCatalogError;h.data.proxyCatalog.nodes=[];h.click('refreshProxies');await until(()=>h.document.getElementById('proxyCatalogStatus').textContent==='0 个可用节点');
  assert.equal(f.proxyId.value,'manual-a');assert.match(f.proxyId.selectedOptions[0].textContent,/节点已失效/);
  h.submit(h.document.getElementById('taskForm'));await until(()=>h.calls.some(call=>call.path==='accounts/save'));
  assert.equal(h.calls.find(call=>call.path==='accounts/save').body.site.proxyId,'manual-a');assert.equal(h.calls.find(call=>call.path==='accounts/save').body.site.name,'尚未保存');
});

test('已删除订阅和固定节点在编辑与复制中保留，导出和导入不丢配置',async t=>{
  const h=await harness();t.after(h.close);h.data.proxyCatalog=catalog();Object.assign(h.data.sites[0],{route:'pool',subscriptionIds:['missing-source'],proxyId:'missing-node'});
  h.data.tasks.push({config:sampleConfig('task',{siteId:'site',keyId:'key',model:'MODEL'}),configRevision:1,runtime:runtime(),totalLanes:0});h.click('refresh');await until(()=>h.document.querySelector('.task-card'));
  h.document.querySelector('.task-edit').click();await until(()=>h.document.getElementById('taskDialog').open);const f=h.document.getElementById('taskForm').elements;
  await until(()=>h.document.getElementById('proxyCatalogStatus').textContent==='0 个可用节点');
  assert.equal(f.proxyId.value,'missing-node');assert.equal(f.subscriptionIds.value,'missing-source');assert.match(h.document.getElementById('proxySubscriptions').textContent,/missing-source（已失效）/);
  h.document.querySelector('#taskDialog [data-close=taskDialog]').click();h.document.querySelector('.task-more summary').click();
  [...h.document.querySelectorAll('.menu-list button')].find(button=>button.textContent==='复制账号').click();await until(()=>h.document.getElementById('taskDialog').open);
  assert.notEqual(f.id.value,'task');assert.equal(f.proxyId.value,'missing-node');assert.equal(f.subscriptionIds.value,'missing-source');
  h.document.querySelector('#taskDialog [data-close=taskDialog]').click();h.document.querySelector('.task-more summary').click();
  [...h.document.querySelectorAll('.menu-list button')].find(button=>button.textContent==='导出账号').click();await until(()=>h.document.getElementById('exportDialog').open);
  const imported=h.U.importAccount(h.document.getElementById('exportText').value);assert.equal(imported.site.proxyId,'missing-node');assert.deepEqual([...imported.site.subscriptionIds],['missing-source']);assert.deepEqual(h.errors,[]);
});

test('实际节点安全文本出现在卡片、线程和日志详情，直连有独立标识',async t=>{
  const h=await harness();t.after(h.close);const address='<img src=x onerror=alert(1)>',proxy={...manual,address};
  h.data.tasks.push({config:sampleConfig('task',{siteId:'site',keyId:'key',model:'MODEL'}),configRevision:1,runtime:runtime({lastRoute:'pool',lastProxy:proxy,lanes:[{index:1,status:'waiting',attempts:1,lastRoute:'pool',lastProxy:proxy},{index:2,status:'waiting',attempts:1,lastRoute:'direct',lastProxy:null}]}),totalLanes:2});
  h.click('refresh');await until(()=>h.document.querySelector('.task-card'));
  assert.match(h.document.querySelector('.task-network').textContent,/<img/);assert.match(h.document.querySelector('.lane-details').textContent,/直连/);assert.equal(h.document.querySelectorAll('.task-card img').length,0);
  h.document.querySelector('.task-logs').click();await until(()=>h.document.getElementById('detailDialog').open);
  assert.match(h.document.getElementById('detailContent').textContent,/<img/);assert.match(h.document.getElementById('detailContent').textContent,/直连/);assert.equal(h.document.querySelectorAll('#detailContent img').length,0);
});
