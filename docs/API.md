# 管理接口

路由前缀：`/api/plugins/router-tasks/`。使用宿主管理员会话，写请求由宿主校验 CSRF。

页面通过 `window.Router2API.request(method, route, body)` 调用。

## 账号接口

| 方法 | 路由 | 功能 |
| --- | --- | --- |
| GET | state | 账号任务、连接元数据、模板摘要、运行状态 |
| GET | network/proxies | 可选代理来源及节点目录（不含代理认证） |
| GET | accounts/detail?id=ID | 账号配置、站点、完整 API Key、通知凭据与修订号 |
| POST | accounts/save | 统一保存账号连接、请求参数、通知和日志设置 |
| POST | accounts/preview | 展开完整请求内容 |
| POST | accounts/query | 使用当前站点地址和 API Key 查询模型与额度 |
| POST | accounts/delete | 删除账号及其任务、日志和通知配置 |
| GET | accounts/export?id=ID | 导出包含完整凭据和模板的账号 JSON |
| GET | tasks/detail?id=ID&laneOffset=0 | 账号任务的快照、日志、每日统计、通知与分页线程状态 |
| POST | tasks/action | 单账号或批量控制任务 |
| POST | keys/refresh | 查询账号模型和额度 |
| POST | notifications/resume | 恢复指定账号的暂停通知 |

`state` 与 `tasks/detail` 响应中的 `serverNow` 是生成响应时的 UTC 时间参考。浏览器用它与本机时间的差值校准页面显示，并统一按 `Asia/Shanghai` 时区格式化；运行记录、事件时间和任务调度保持原始时间。

### 代理选择

`site.route` 为 `direct` 或 `pool`。`site.subscriptionIds=[]` 允许所有启用订阅及手动节点，`["manual"]` 仅允许手动节点，其他数组值限定到相应启用来源。`site.proxyId` 为固定节点 ID；`null`、空字符串或省略表示自动选择。账号保存是完整配置写入，请在编辑时保留详情返回的代理字段。

`GET network/proxies` 返回 `subscriptions:[{id,name,enabled}]` 与 `nodes:[{id,subscriptionId,address,state,available}]`。目录中的不可用节点仅供显示现有选择；实际发送再次核验来源、状态及冷却窗口。固定节点不在范围内或没有可用节点时返回 503、`code=proxy_unavailable`，不会切换范围外节点或直连。代理服务缺失同样明确报错，不依赖旧版回退。

模型/额度查询、任务运行快照与源码获取共用选择规则。一次模型/额度查询的多个 GET 共用同一节点和客户端。`KeyInfo`、`runtime`、`runtime.lanes[]` 的 `lastRoute`、`lastProxy` 保存实际出站；尚未取得节点时为 `null`。直连时 `lastRoute=direct`、`lastProxy=null`。日志记录安全节点摘要，完整代理凭据始终留在宿主。

### 保存账号

```json
{
  "config": {
    "id": "account-1",
    "name": "日常账号",
    "model": "MODEL",
    "mode": "queue_then_keepalive",
    "templateId": "responses",
    "settings": {
      "telegramEnabled": true,
      "barkEnabled": false,
      "maxEventsPerTask": 200
    }
  },
  "site": {
    "name": "日常账号",
    "baseUrl": "https://TARGET",
    "route": "direct",
    "queueHttpStatuses": [500]
  },
  "token": "TOKEN",
  "secrets": {
    "telegramToken": "TOKEN",
    "telegramChatId": "CHAT_ID",
    "credentialVersion": 0
  }
}
```

编辑时传入 `expectedRevision`、`credentialVersion`、`site.revision` 和 `secrets.credentialVersion`，使用账号详情返回的修订号。

`config.settings` 保存此账号的通知渠道、重试参数和日志保留限制。`secrets` 保存此账号的通知凭据，空字段清除对应值。

保存时自动关联内部站点和 Key，账号名称同时用作 Key 别名。

改变 `config.mode` 会建立新的运行状态和运行编号，清空旧快照、接受状态及本次运行计数，保留历史事件、每日统计和通知回执。同模式编辑保留原运行及预算。兼容的 `tasks/save`、`config/import` 入口遵循相同规则。

### 通知恢复

```json
{"id":"account-1"}
```

只恢复指定账号的暂停记录。

## 任务控制

```json
{"ids":["account-1"],"action":"resume"}
```

`action` 支持 `start`、`pause`、`resume`、`cancel`、`retry`、`requeue`、`restart`。

`retry` 保留预算，`requeue` 开始新排队轮次，`restart` 创建新运行记录。批量请求逐项返回执行结果。

已完成任务、已接受后中断的仅排队请求须显式 `restart`。预算耗尽后 `resume` 不返还次数；可提高当前阶段预算或显式 `restart`。页面批量继续会跳过需要重新开始的账号并列出原因。

## 请求模板

| 方法 | 路由 | 功能 |
| --- | --- | --- |
| GET | templates/detail?id=ID | 读取完整模板 |
| POST | templates/save | 保存完整模板 |

```json
{
  "id":"responses-custom",
  "name":"自定义 Responses",
  "protocol":"responses",
  "path":"responses",
  "headers":{"Authorization":"Bearer {{TOKEN}}"},
  "body":"{\"model\":\"{{MODEL}}\",\"input\":\"{{PROMPT}}\",\"stream\":true}"
}
```

`body` 返回为 JSON 文本，写入时接受文本或对象，保留大整数和小数精度。

页面按协议选择接口：OpenAI Responses 使用 `/v1/responses`，Anthropic Messages 使用 `/v1/messages`；不需要填写自定义接口。

## 任务字段

- `mode`：`queue`、`keepalive`、`queue_then_keepalive`。
- `autoRequeue`：自动重新排队开关。
- `useSourcePrompt`：默认 `false`，使用 `prompt` 固定提示词；`true` 时成功获取源码后仅使用 Linux 源码分析提示词，不拼接固定提示词。获取失败时使用已有源码缓存；无缓存则回退到 `prompt` 固定提示词。
- `sourcePromptCacheMinutes`：Linux 源码成功缓存时长，单位为分钟（支持小数），范围 `0`–`2000000`，默认 `360`（6 小时）；旧配置缺少字段时使用默认值。`0` 表示每次请求尝试刷新，正数到期后由下一次请求刷新。片段缓存共享，刷新条件按各账号配置判断；失败重试单独退避 10 分钟，不限制成功后的缓存过期刷新。账号复制、导入、导出和运行快照保留该字段。
- `claude1MContext`：Claude 1M 上下文开关，默认 `true`；仅影响 Messages 请求。关闭时移除 1M 上下文标记。
- `queueHttpStatuses`：状态码数组；`null` 使用站点规则。
- `queue`、`keepalive`：排队与保活阶段参数。
- `keepalive.idleEvictionSeconds`：距最近一次正式接受事件达到此时长后，在失败结果上开始观察，默认 300 秒。仅适用于已确认的会话。
- `keepalive.dropConfirmSeconds`：从实际开始观察起持续失败的确认时长，默认 120 秒；0 表示不额外等待。在确认前收到正式接受事件，清除观察且不产生掉线/恢复通知。
- `requestOverride`：账号专用请求模板。
- `settings`：账号独立通知与日志设置。
- `notificationEvents`：`entered`、`dropped`、`recovered`、`stopped`、`paused`、`exhausted`。
- `runtime.status`：`running`、`recovering`、`paused`、`completed`、`failed`、`exhausted`、`cancelled`。
- `runtime.phase`：`queue` 或 `keepalive`。
- `runtime.sessionConfirmed`：本次运行已确认接受事件。
- `runtime.dropPendingSince`：掉线观察开始时间；未观察、已停止或已成功恢复时为 `null`。
- `runtime.outageNotified`：本次故障已确认的逻辑标记，不表示外部渠道已投递成功；只有此标记对应的故障恢复才产生 `recovered`。继续/手动重排保留已确认故障，显式重新开始重置。
- `runtime.outageSequence`：已确认故障编号，不因短暂失败递增；掉线及恢复通知按运行编号、故障编号和渠道去重。
- `runtime.totalAttempts`：累计请求尝试次数。

`entered` 仅本次运行首次接受时产生；短暂失败与恢复、普通排队、人工暂停/取消只记录事件。确认掉线且关闭自动重排时，`dropped` 与 `paused` 合并通知（优先 `dropped`）。关闭网络/超时重试导致的停止使用 `paused`，不可重试错误使用 `stopped`，阶段全部次数预算耗尽使用 `exhausted`。HTTP 408/425/429/5xx 和非永久的协议失败按阶段参数重试。

## 配置接口

`sites/save`、`sites/delete`、`keys/save`、`keys/delete`、`keys/reconcile`、`tasks/save`、`tasks/delete`、`requests/preview`、`settings/save`、`config/export`、`config/import` 提供分项配置读写。

`config/export` 导出业务配置；`accounts/export` 导出包含凭据的完整账号配置。

## 状态码

400 参数错误；404 资源不存在；409 修订冲突或操作不适用；503 插件停止或存储不可用。

宿主管理接口请求体上限为 1 MiB。
