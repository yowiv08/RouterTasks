(function (root) {
  'use strict';
  const copy = value => JSON.parse(JSON.stringify(value));
  function displayClock() {
    let offset = 0;
    const formatter = new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai', year: 'numeric', month: 'numeric', day: 'numeric',
      hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' });
    return Object.freeze({
      sync(serverNow, requestedAt, receivedAt) {
        const server = typeof serverNow === 'string' ? Date.parse(serverNow) : NaN;
        if (!Number.isFinite(server) || !Number.isFinite(requestedAt) || !Number.isFinite(receivedAt) || receivedAt < requestedAt) return;
        // 用请求往返的中点估算时差，让宿主记录映射到查看页面的电脑时钟。
        offset = requestedAt + (receivedAt - requestedAt) / 2 - server;
      },
      format(value) {
        const timestamp = typeof value === 'string' ? Date.parse(value) : NaN;
        const adjusted = new Date(timestamp + offset);
        return Number.isFinite(adjusted.getTime()) ? formatter.format(adjusted) : '—';
      }
    });
  }
  // 模板数值保持原始 JSON token；不将 Int64、高精度小数或指数先转成 Number。
  class RawNumber { constructor(source) { this.source = source; } }
  function parseJson(text) {
    let marker = '__router_tasks_number__'; while (text.includes(marker)) marker += '_';
    const values = [];
    const encoded = text.replace(/"(?:\\[\s\S]|[^"\\])*"|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/g, token => {
      if (token.startsWith('"')) return token;
      return JSON.stringify(marker + (values.push(token) - 1));
    });
    return JSON.parse(encoded, (_, value) => typeof value === 'string' && value.startsWith(marker)
      ? new RawNumber(values[Number(value.slice(marker.length))]) : value);
  }
  function stringify(value, space = 2) {
    let marker = '__router_tasks_raw__'; const source = JSON.stringify(value);
    while (source?.includes(marker)) marker += '_';
    const values = [];
    return JSON.stringify(value, (_, item) => item instanceof RawNumber ? marker + (values.push(item.source) - 1) : item, space)
      .replace(new RegExp('"' + marker + '(\\d+)"', 'g'), (_, index) => values[Number(index)]);
  }
  function wireTemplate(template) {
    return { ...template, path: template.protocol, body: typeof template.body === 'string' ? template.body : stringify(template.body) };
  }
  function displayTemplate(template) {
    const { path, ...editable } = template;
    return stringify({ ...editable, body: typeof template.body === 'string' ? jsonObject(template.body, 'Body') : template.body });
  }
  function importConfig(text) {
    const config = parseJson(text);
    if (!Array.isArray(config.templates) || !Array.isArray(config.tasks)) throw Error('导入缺少模板或任务数组');
    config.templates = config.templates.map(wireTemplate);
    for (const task of config.tasks) if (task.requestOverride) task.requestOverride = wireTemplate(task.requestOverride);
    // 仅配置的有界计数/秒数进入 Number，正文保持为 JSON 文本。
    return JSON.parse(stringify(config));
  }
  function importAccount(text) {
    const input=parseJson(text);
    if(Number(stringify(input.schemaVersion))!==1||!input.account?.config||!input.account?.site)throw Error('账号 JSON 格式无效');
    const account=input.account;
    if(account.config.requestOverride)account.config.requestOverride=wireTemplate(account.config.requestOverride);
    return JSON.parse(stringify(account));
  }
  function settingsDefaults() {
    return {maxEventsPerTask:200,summaryBytes:8192,historyDays:30,telegramEnabled:false,barkEnabled:false,
      barkServer:'https://api.day.app',notificationMaxAttempts:10,notificationRetrySeconds:5,notificationMaxRetrySeconds:3600,maxNotificationRecords:1000};
  }
  const modes = { queue: '仅排队', keepalive: '持续保活', queue_then_keepalive: '排队后持续保活' };
  const eventNames = { entered: '首次进入', dropped: '确认掉线', recovered: '确认掉线后恢复', stopped: '错误停止', paused: '自动暂停', exhausted: '次数预算耗尽' };
  function parseCodes(text, inherit = false) {
    if (!text.trim()) return inherit ? null : [];
    const values = text.trim().split(/[\s,，]+/).map(value => {
      if (!/^\d{3}$/.test(value)) throw Error('HTTP 状态码使用逗号分隔的三位整数，如 500,429');
      const code = Number(value); if (code < 100 || code > 599) throw Error('HTTP 状态码须为 100–599'); return code;
    });
    return [...new Set(values)];
  }
  function defaults(id) {
    return { id, name: '新账号', mode: 'queue_then_keepalive', autoRequeue: true, siteId: '', keyId: '', model: '', claude1MContext: true,
      prompt: 'Reply only OK. Do not call tools.', templateId: 'responses', requestOverride: null, queueHttpStatuses: null,
      queue: { concurrency: 1, maxAttemptsPerLane: 300, intervalSeconds: 10, intervalMaxSeconds: null, firstEventTimeoutSeconds: 60,
        retryNetworkErrors: true, retryTimeouts: true, readFullResponse: false, responseTimeoutSeconds: 600, idleTimeoutSeconds: 60, maxResponseBytes: 2097152 },
      keepalive: { concurrency: 1, maxAttemptsPerLane: null, heartbeatSeconds: 120, heartbeatMaxSeconds: null, failureRetrySeconds: 15, firstEventTimeoutSeconds: 60,
        idleEvictionSeconds: 300, dropConfirmSeconds: 120, retryNetworkErrors: true, retryTimeouts: true, maxResponseBytes: 2097152 },
      useSourcePrompt: false, sourcePromptCacheMinutes: 360, settings: settingsDefaults(), notificationEvents: ['entered', 'dropped', 'stopped', 'paused', 'exhausted'] };
  }
  function status(task) {
    const r = task.runtime;
    if (r.status !== 'running' && r.status !== 'recovering') return { label: ({ paused: '已暂停', completed: '已完成', failed: '错误停止', exhausted: '预算耗尽', cancelled: '已取消' })[r.status] || r.status,
      tone: r.status === 'completed' ? 'ok' : ['failed', 'exhausted'].includes(r.status) ? 'bad' : 'muted' };
    if (r.status === 'recovering') return { label: r.outageNotified ? '掉线恢复中' : '掉线观察中', tone: 'warn' };
    return r.phase === 'queue' ? { label: '排队中', tone: 'warn' } : { label: r.sessionConfirmed ? '保活中' : '等待心跳确认', tone: r.sessionConfirmed ? 'ok' : 'warn' };
  }
  function taskActions(task) {
    const c = task.config, r = task.runtime, queue = r.phase === 'queue', stage = queue ? c.queue : c.keepalive;
    // 与引擎一致，按当前阶段和线程编号保留次数；列表只展示前 8 个线程，预算使用完整计数表。
    const saved = queue ? r.queueLaneAttempts : r.keepaliveLaneAttempts;
    const attempts = saved && Object.keys(saved).length ? saved : Object.fromEntries((r.lanes || []).map(lane => [lane.index, lane.attempts]));
    const exhausted = stage.maxAttemptsPerLane != null && Array.from({ length: stage.concurrency }, (_, i) => i + 1)
      .every(index => (attempts[index] || 0) >= stage.maxAttemptsPerLane);
    const accepted = c.mode === 'queue' && r.entered && (r.responseState || 'none') !== 'none';
    const reason = r.status === 'completed' ? '任务已完成' : accepted ? '请求已被接受，无法继续重放' : exhausted ? '当前阶段的次数预算已耗尽' : null;
    const action = ['running', 'recovering'].includes(r.status) ? 'pause' : reason ? 'restart' : 'resume';
    return { action, label: { pause: '暂停任务', restart: '重新开始', resume: '继续任务' }[action], reason,
      retry: !reason && r.responseState !== 'reading', requeue: r.status !== 'completed' && !accepted };
  }
  function remaining(key) {
    if (key.unlimited) return '无限制';
    if (key.limitUsd == null || key.usedUsd == null) return '—';
    return '$' + (Number(key.limitUsd) - Number(key.usedUsd)).toFixed(2);
  }
  function jsonObject(text, label) {
    let value; try { value = parseJson(text); } catch { throw Error(label + ' 不是合法 JSON'); }
    if (!value || Array.isArray(value) || value instanceof RawNumber || typeof value !== 'object') throw Error(label + ' 必须是 JSON 对象'); return value;
  }
  // 编辑已知字段只触及对应节点；完整工具、未知字段和大整数文本保持在原 JSON 编辑器中。
  function setKnownField(template, field, value) {
    const result = copy(wireTemplate(template));
    const body = jsonObject(typeof result.body === 'string' ? result.body : stringify(result.body), 'Body');
    if (field === 'model') body.model = value;
    if (field === 'prompt') {
      const list = result.protocol === 'responses' ? body.input : body.messages;
      if (result.protocol === 'responses' && typeof list === 'string') { body.input = value; result.body = stringify(body); return result; }
      const user = Array.isArray(list) ? list.filter(item => item?.role === 'user' && (result.protocol !== 'responses' || !item.type || item.type === 'message')).at(-1) : null;
      if (!user) throw Error('自定义正文没有可映射的 user 消息，请在 Body JSON 中编辑提示词');
      if (typeof user.content === 'string') user.content = value;
      else {
        const block = user.content?.find(item => item.type === 'input_text' || item.type === 'text');
        if (!block) throw Error('自定义正文没有文本块，请在 Body JSON 中编辑提示词'); block.text = value;
      }
    }
    result.body = stringify(body);
    return result;
  }
  root.RouterTasksUI = Object.freeze({ copy, displayClock, modes, eventNames, parseCodes, defaults, status, taskActions, remaining, jsonObject, setKnownField,
    parseJson, stringify, wireTemplate, displayTemplate, importConfig, importAccount, settingsDefaults });
})(globalThis);
