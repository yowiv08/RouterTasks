import test from 'node:test';
import assert from 'node:assert/strict';
import { harness, until } from './harness.mjs';

function task(h,id,mode,values={}) {
  const config={...h.U.defaults(id),name:'账号-'+id,siteId:'site',keyId:'key',model:'MODEL',mode};
  return {config,configRevision:1,totalLanes:0,runtime:{status:'paused',phase:mode==='keepalive'?'keepalive':'queue',
    queueRound:1,totalAttempts:2,heartbeats:0,recoveries:0,lanes:[],queueLaneAttempts:{},keepaliveLaneAttempts:{},
    entered:false,responseState:'none',sessionConfirmed:false,...values}};
}
async function render(h,tasks) {
  h.data.tasks=tasks;h.click('refresh');
  await until(()=>!h.document.getElementById('refresh').disabled&&h.document.querySelectorAll('.task-card').length===tasks.length);
}
const actions=h=>h.calls.filter(call=>call.path==='tasks/action');
const card=(h,id)=>h.document.querySelector('.task-card[data-id="'+id+'"]');
const menu=(h,id,label)=>[...card(h,id).querySelectorAll('.menu-list button')].find(button=>button.textContent===label);

test('三种模式的普通暂停可点击继续，运行和恢复中可点击暂停',async t=>{
  const h=await harness();t.after(h.close);
  const tasks=Object.keys(h.U.modes).flatMap(mode=>['paused','running','recovering'].map(status=>task(h,mode+'-'+status,mode,{status})));
  tasks.push(task(h,'accepted-reading','queue',{status:'running',entered:true,responseState:'reading'}));
  await render(h,tasks);
  for(const entry of tasks){
    const run=card(h,entry.config.id).querySelector('.run'),paused=entry.runtime.status==='paused',before=actions(h).length;
    assert.equal(run.textContent,paused?'继续任务':'暂停任务');run.click();
    await until(()=>actions(h).length===before+1&&!run.disabled);
    assert.deepEqual(actions(h).at(-1).body,{ids:[entry.config.id],action:paused?'resume':'pause'});
    assert.equal(h.document.getElementById('confirmDialog').open,false);
  }
  assert.equal(menu(h,'accepted-reading','立即重试').disabled,true);assert.equal(menu(h,'accepted-reading','手动重排').disabled,true);
  assert.deepEqual(h.errors,[]);
});

test('已接受后中断的仅排队任务需要确认重新开始，取消不会发送请求',async t=>{
  const h=await harness();t.after(h.close);
  const entry=task(h,'interrupted','queue',{entered:true,responseState:'interrupted',status:'failed'});
  await render(h,[entry]);
  const run=card(h,entry.config.id).querySelector('.run');assert.equal(run.textContent,'重新开始');
  assert.match(run.title,/请求已被接受/);
  for(const label of ['立即重试','手动重排']){assert.equal(menu(h,entry.config.id,label).disabled,true);menu(h,entry.config.id,label).click();}
  assert.equal(actions(h).length,0);
  run.click();await until(()=>h.document.getElementById('confirmDialog').open);
  assert.equal(actions(h).length,0);h.click('confirmNo');await until(()=>!run.disabled);
  assert.equal(actions(h).length,0);run.click();await until(()=>h.document.getElementById('confirmDialog').open);
  h.click('confirmYes');await until(()=>actions(h).length===1);
  assert.deepEqual(actions(h)[0].body,{ids:['interrupted'],action:'restart'});assert.deepEqual(h.errors,[]);
});

test('已完成与实际耗尽预算的任务主操作均为显式重新开始',async t=>{
  const h=await harness();t.after(h.close);
  const completed=task(h,'completed','keepalive',{status:'completed'});
  const queue=task(h,'queue-limit','queue',{status:'cancelled',queueLaneAttempts:{1:3}});queue.config.queue.maxAttemptsPerLane=3;
  const keepalive=task(h,'keepalive-limit','keepalive',{status:'exhausted',keepaliveLaneAttempts:{1:2}});keepalive.config.keepalive.maxAttemptsPerLane=2;
  const legacy=task(h,'legacy-limit','queue_then_keepalive',{status:'paused',lanes:[{index:1,attempts:4}]});legacy.config.queue.maxAttemptsPerLane=4;
  const tasks=[completed,queue,keepalive,legacy];await render(h,tasks);
  for(const entry of tasks){
    const run=card(h,entry.config.id).querySelector('.run'),before=actions(h).length;assert.equal(run.textContent,'重新开始');
    assert.equal(menu(h,entry.config.id,'立即重试').disabled,true);
    run.click();await until(()=>h.document.getElementById('confirmDialog').open);assert.equal(actions(h).length,before);
    h.click('confirmYes');await until(()=>actions(h).length===before+1&&!run.disabled);
    assert.deepEqual(actions(h).at(-1).body,{ids:[entry.config.id],action:'restart'});
  }
});

test('预算判断使用当前阶段完整线程计数，新增线程和解除预算限制后可继续',async t=>{
  const h=await harness();t.after(h.close);
  const count=Object.fromEntries(Array.from({length:12},(_,i)=>[i+1,1]));
  const exhausted=task(h,'twelve-exhausted','queue',{status:'exhausted',queueLaneAttempts:count,
    lanes:Array.from({length:8},(_,i)=>({index:i+1,attempts:1}))});
  Object.assign(exhausted.config.queue,{concurrency:12,maxAttemptsPerLane:1});exhausted.totalLanes=12;
  const remaining=task(h,'twelfth-available','queue',{status:'exhausted',queueLaneAttempts:{...count,12:0},lanes:exhausted.runtime.lanes});
  Object.assign(remaining.config.queue,{concurrency:12,maxAttemptsPerLane:1});remaining.totalLanes=12;
  const expanded=task(h,'new-lane','keepalive',{status:'exhausted',keepaliveLaneAttempts:{1:5}});
  Object.assign(expanded.config.keepalive,{concurrency:2,maxAttemptsPerLane:5});
  const unlimited=task(h,'unlimited','keepalive',{status:'exhausted',keepaliveLaneAttempts:{1:5}});
  const requeue=task(h,'requeue-phase','keepalive',{status:'exhausted',phase:'queue',queueLaneAttempts:{1:3}});requeue.config.queue.maxAttemptsPerLane=3;
  const tasks=[exhausted,remaining,expanded,unlimited,requeue];await render(h,tasks);
  for(const entry of tasks){
    const expected=['twelve-exhausted','requeue-phase'].includes(entry.config.id)?'restart':'resume';
    const run=card(h,entry.config.id).querySelector('.run'),before=actions(h).length;run.click();
    if(expected==='restart'){await until(()=>h.document.getElementById('confirmDialog').open);h.click('confirmYes');}
    await until(()=>actions(h).length===before+1&&!run.disabled);
    assert.deepEqual(actions(h).at(-1).body,{ids:[entry.config.id],action:expected});
  }
});

test('耗尽后编辑同模式增加阶段预算，继续不会重置已用次数',async t=>{
  const h=await harness();t.after(h.close);
  for(const mode of ['queue','keepalive']){
    const queue=mode==='queue',entry=task(h,'increase-'+mode,mode,{status:'exhausted',totalAttempts:2,
      [queue?'queueLaneAttempts':'keepaliveLaneAttempts']:{1:2}});
    entry.config[queue?'queue':'keepalive'].maxAttemptsPerLane=2;await render(h,[entry]);
    assert.equal(card(h,entry.config.id).querySelector('.run').textContent,'重新开始');
    card(h,entry.config.id).querySelector('.task-edit').click();await until(()=>h.document.getElementById('taskDialog').open);
    const form=h.document.getElementById('taskForm');h.change(form.elements[queue?'q_maxAttemptsPerLane':'k_maxAttemptsPerLane'],'3');
    h.submit(form);await until(()=>!h.document.getElementById('taskDialog').open&&card(h,entry.config.id).querySelector('.run').textContent==='继续任务');
    assert.equal(h.data.tasks[0].runtime.totalAttempts,2);
    assert.deepEqual(h.data.tasks[0].runtime[queue?'queueLaneAttempts':'keepaliveLaneAttempts'],{1:2});
    const before=actions(h).length,run=card(h,entry.config.id).querySelector('.run');run.click();
    await until(()=>actions(h).length===before+1&&!run.disabled);
    assert.deepEqual(actions(h).at(-1).body,{ids:[entry.config.id],action:'resume'});
    assert.equal(h.document.getElementById('confirmDialog').open,false);
  }
  assert.equal(h.calls.filter(call=>call.path==='accounts/save').length,2);assert.deepEqual(h.errors,[]);
});

test('批量继续跳过必须重新开始的账号并显示原因，正常账号继续且逐项错误有账号名',async t=>{
  const h=await harness();t.after(h.close);
  const paused=task(h,'paused','keepalive'),blocked=task(h,'accepted','queue',{status:'paused',entered:true,responseState:'interrupted'});
  const completed=task(h,'completed','queue',{status:'completed'}),exhausted=task(h,'exhausted','queue',{status:'exhausted',queueLaneAttempts:{1:300}});
  const rejected=task(h,'rejected','keepalive');await render(h,[paused,blocked,completed,exhausted,rejected]);
  const original=h.window.Router2API.request;
  h.window.Router2API.request=async(method,path,body)=>{
    const result=await original(method,path,body);
    if(path==='tasks/action')result.results=result.results.map(item=>item.id==='rejected'?{...item,success:false,error:'配置已变更，请刷新'}:item);
    return result;
  };
  h.click('selectAll');const resume=h.document.querySelector('[data-bulk=resume]');resume.click();await until(()=>actions(h).length===1&&!resume.disabled);
  assert.deepEqual(actions(h)[0].body,{ids:['paused','rejected'],action:'resume'});
  const notice=h.document.getElementById('notice');assert.match(notice.textContent,/已完成 1 个账号/);
  assert.match(notice.textContent,/账号-accepted：请求已被接受.*请使用“重新开始”/);
  assert.match(notice.textContent,/账号-completed：任务已完成/);assert.match(notice.textContent,/账号-exhausted：当前阶段的次数预算已耗尽/);
  assert.match(notice.textContent,/账号-rejected：配置已变更，请刷新/);assert.equal(notice.className,'error');
  assert.equal(h.document.getElementById('confirmDialog').open,false);assert.equal(actions(h).some(call=>call.body.action==='restart'),false);
});

test('全部所选任务需要重新开始时不发送无效继续请求，菜单保留明确重排操作',async t=>{
  const h=await harness();t.after(h.close);
  const entry=task(h,'exhausted','keepalive',{status:'exhausted',keepaliveLaneAttempts:{1:1}});entry.config.keepalive.maxAttemptsPerLane=1;
  await render(h,[entry]);h.click('selectAll');const resume=h.document.querySelector('[data-bulk=resume]');resume.click();
  await until(()=>!resume.disabled);assert.equal(actions(h).length,0);assert.match(h.document.getElementById('notice').textContent,/请使用“重新开始”/);
  const requeue=menu(h,entry.config.id,'手动重排');assert.equal(requeue.disabled,false);requeue.click();
  await until(()=>actions(h).length===1);assert.deepEqual(actions(h)[0].body,{ids:['exhausted'],action:'requeue'});
  assert.equal(h.document.getElementById('confirmDialog').open,false);
});
