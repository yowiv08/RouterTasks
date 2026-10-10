import test from 'node:test';
import assert from 'node:assert/strict';
import { harness, until } from './harness.mjs';

async function withTasks(count) {
  const h=await harness();
  for(let i=0;i<count;i++)h.data.tasks.push({config:h.U.defaults('task-'+i),configRevision:1,totalLanes:0,
    runtime:{status:'paused',phase:'queue',queueRound:0,totalAttempts:0,heartbeats:0,recoveries:0,lanes:[],sessionConfirmed:false}});
  h.click('refresh');await until(()=>h.document.querySelectorAll('.task-card').length===Math.min(50,count));
  return h;
}

test('跨页选择和刷新保留账号选择，过滤与删除后全选状态准确',async t=>{
  const h=await withTasks(51);t.after(h.close);
  const all=h.document.getElementById('selectAll'),count=h.document.getElementById('selectionCount');
  h.click('selectAll');h.click('nextTasks');
  assert.equal(all.checked,false);assert.equal(count.textContent,'已选 50 个');
  h.document.querySelector('.task-head input[type=checkbox]').click();
  h.click('refresh');await until(()=>!h.document.getElementById('refresh').disabled);
  assert.equal(all.checked,true);assert.equal(count.textContent,'已选 51 个');
  const search=h.document.getElementById('taskSearch');search.value='不匹配的账号';search.dispatchEvent(new h.window.Event('input'));
  assert.equal(all.checked,false);assert.equal(all.disabled,true);assert.equal(count.textContent,'已选 51 个');
  h.data.tasks=[];h.click('refresh');await until(()=>count.textContent==='已选 0 个');
  assert.ok([...h.document.querySelectorAll('[data-bulk]')].every(n=>n.disabled));
});

test('批量请求在途时禁止重复提交和修改选择，失败后可重试',async t=>{
  const h=await withTasks(1);t.after(h.close);h.click('selectAll');
  const original=h.window.Router2API.request;let rejectAction,actions=0;
  h.window.Router2API.request=(method,path,body)=>path==='tasks/action'
    ?(actions++,new Promise((resolve,reject)=>rejectAction=reject)):original(method,path,body);
  const resume=h.document.querySelector('[data-bulk=resume]');resume.click();resume.click();
  assert.equal(actions,1);assert.ok([...h.document.querySelectorAll('[data-bulk]')].every(n=>n.disabled));
  assert.equal(h.document.getElementById('selectAll').disabled,true);
  assert.equal(h.document.querySelector('.task-head input[type=checkbox]').disabled,true);
  rejectAction(Error('测试请求失败'));await until(()=>!resume.disabled);
  assert.match(h.document.getElementById('notice').textContent,/测试请求失败/);
  h.click('selectAll');assert.ok([...h.document.querySelectorAll('[data-bulk]')].every(n=>n.disabled));
  assert.deepEqual(h.errors,[]);
});

test('删除所选位于取消所选后，未选禁用，取消确认不删除账号',async t=>{
  const h=await withTasks(2);t.after(h.close);
  const buttons=[...h.document.querySelectorAll('[data-bulk]')];
  assert.deepEqual(buttons.map(n=>n.textContent),['继续所选','暂停所选','取消所选','删除所选']);
  const remove=h.document.querySelector('[data-bulk=delete]');assert.equal(remove.disabled,true);
  h.click('selectAll');assert.equal(remove.disabled,false);remove.click();
  await until(()=>h.document.getElementById('confirmDialog').open);
  assert.match(h.document.getElementById('confirmText').textContent,/2 个账号/);
  assert.match(h.document.getElementById('confirmText').textContent,/不可恢复/);
  assert.equal(h.calls.some(c=>c.path==='accounts/delete'),false);
  h.click('confirmNo');await until(()=>!remove.disabled);
  assert.equal(h.calls.some(c=>c.path==='accounts/delete'),false);assert.equal(h.data.tasks.length,2);
  assert.equal(h.document.getElementById('selectionCount').textContent,'已选 2 个');
});

test('批量删除仅处理确认的所选账号并刷新列表和选择状态',async t=>{
  const h=await withTasks(3);t.after(h.close);
  for(const card of [...h.document.querySelectorAll('.task-card')].slice(0,2))card.querySelector('.task-head input').click();
  const remove=h.document.querySelector('[data-bulk=delete]');assert.ok(remove);remove.click();
  await until(()=>h.document.getElementById('confirmDialog').open);h.click('confirmYes');
  await until(()=>h.document.querySelectorAll('.task-card').length===1&&h.document.getElementById('bulkActions').getAttribute('aria-busy')==='false');
  assert.deepEqual(h.calls.filter(c=>c.path==='accounts/delete').map(c=>c.body.id),['task-0','task-1']);
  assert.equal(h.calls.some(c=>c.path==='tasks/action'),false);
  assert.equal(h.data.tasks[0].config.id,'task-2');assert.equal(h.document.getElementById('selectionCount').textContent,'已选 0 个');
  assert.equal(remove.disabled,true);assert.match(h.document.getElementById('notice').textContent,/已删除 2 个账号/);
  assert.deepEqual(h.errors,[]);
});

test('所选包含运行中任务时禁止批量删除，不自动暂停任务',async t=>{
  const h=await withTasks(2);t.after(h.close);h.data.tasks[1].runtime.status='running';
  h.click('refresh');await until(()=>!h.document.getElementById('refresh').disabled);h.click('selectAll');
  const remove=h.document.querySelector('[data-bulk=delete]');assert.ok(remove);remove.click();
  await until(()=>!remove.disabled);
  assert.match(h.document.getElementById('notice').textContent,/请先暂停/);
  assert.equal(h.document.getElementById('confirmDialog').open,false);
  assert.equal(h.calls.some(c=>['accounts/delete','tasks/action'].includes(c.path)),false);
  assert.equal(h.data.tasks.length,2);
});

test('批量删除逐项处理，失败账号保持选中并展示错误',async t=>{
  const h=await withTasks(3);t.after(h.close);
  const original=h.window.Router2API.request;
  h.window.Router2API.request=(method,path,body)=>path==='accounts/delete'&&body.id==='task-1'
    ?Promise.reject(Error('请等待通知投递结束')):original(method,path,body);
  h.click('selectAll');const remove=h.document.querySelector('[data-bulk=delete]');assert.ok(remove);remove.click();
  await until(()=>h.document.getElementById('confirmDialog').open);h.click('confirmYes');
  await until(()=>h.document.querySelectorAll('.task-card').length===1&&!remove.disabled);
  assert.equal(h.data.tasks[0].config.id,'task-1');assert.equal(h.document.getElementById('selectionCount').textContent,'已选 1 个');
  assert.equal(h.document.querySelector('.task-head input').checked,true);
  assert.match(h.document.getElementById('notice').textContent,/已删除 2 个账号.*1 个删除失败/);
  assert.match(h.document.getElementById('notice').textContent,/请等待通知投递结束/);
  assert.deepEqual(h.calls.filter(c=>c.path==='accounts/delete').map(c=>c.body.id),['task-0','task-2']);
});

test('批量删除在确认与执行期间锁定选择并按顺序发送请求',async t=>{
  const h=await withTasks(2);t.after(h.close);h.click('selectAll');
  const original=h.window.Router2API.request;let release;const deletes=[];
  h.window.Router2API.request=async(method,path,body)=>{
    if(path==='accounts/delete'){deletes.push(body.id);if(body.id==='task-0')await new Promise(resolve=>release=resolve);}
    return original(method,path,body);
  };
  const remove=h.document.querySelector('[data-bulk=delete]');assert.ok(remove);remove.click();remove.click();
  await until(()=>h.document.getElementById('confirmDialog').open);
  assert.ok([...h.document.querySelectorAll('[data-bulk]')].every(n=>n.disabled));
  assert.equal(h.document.getElementById('selectAll').disabled,true);
  h.click('confirmYes');await until(()=>release);remove.click();
  assert.deepEqual(deletes,['task-0']);assert.equal(h.document.querySelector('.task-head input').disabled,true);
  release();await until(()=>h.document.querySelectorAll('.task-card').length===0&&h.document.getElementById('bulkActions').getAttribute('aria-busy')==='false');
  assert.deepEqual(deletes,['task-0','task-1']);assert.deepEqual(h.errors,[]);
});
