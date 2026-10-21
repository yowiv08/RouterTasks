# 宿主集成验收

使用隔离的 Router2API 宿主源码和插件 ZIP，监听随机回环端口。

- 管理会话和 CSRF 校验。
- 插件页面、参数错误、任务状态和 JSON 数值精度。
- 排队、保活、取消请求和重载后的暂停状态。
- 公开代理目录不返回节点认证和订阅 URL，冷却节点及禁用订阅不可用。
- 模型、额度与后台探针使用实际固定代理；限定订阅、仅手动节点均经回环 HTTP 代理验证。
- 固定节点缺失、超出来源范围、冷却或订阅禁用时返回 503，不请求其他节点或直连。
- 连续模型与额度查询复用宿主物理连接池；热更新和禁用取消代理 SSE 并释放连接。
- 首次进入后 Telegram 与 Bark 的后台投递、成功回执及重载后不重复发送。
- 热更新后旧程序集回收，同时保留新版本可用性。
- 禁用和删除后的程序集回收；三个加载上下文分别校验。
- SQLite 账号服务的 Key、通知凭据、版本冲突和重载持久化。

入口为项目根目录的 `test.ps1 -Browser -HostIntegration`。`-RouterHostProject` 接受隔离宿主工程的绝对路径，`-PluginZip` 接受已打包的插件；未提供 ZIP 时先构建当前源码。运行目录必须不存在，每次验收自动生成独立目录。

## 序列化与回收

管理结果在插件内转换为 `JsonElement`，宿主缓存不持有插件 DTO。每次插件加载使用独立的 `DefaultJsonTypeInfoResolver`，不同版本不共享类型缓存。

.NET JSON 反射访问器使用一秒滑动缓存，并在下一次元数据访问时清理过期项。回收检查等待缓存到期，解析一个宿主测试类型，再执行有界 GC 检查；不通过反射清空缓存，不取消回收断言。

运行时缓存实现：[ReflectionEmitCachingMemberAccessor](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Metadata/ReflectionEmitCachingMemberAccessor.cs) / [Cache](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Metadata/ReflectionEmitCachingMemberAccessor.Cache.cs)。

通知验收使用虚构凭据和本机模拟接口。固定的测试 Telegram Bot 路径会映射到回环 HTTP，Bark 使用回环服务地址，其他外部 HTTP 均被拒绝。

代理验收使用官方宿主的 `ProxyTransportFactory` / `ProxyHttpClientFactory`，通过公开 `IProxyStore` 和 `IProxySubscriptionService` 提供模拟目录。两个本机 HTTP 代理只接受虚构 `upstream.invalid` 的测试路径，并转发到本机回环上游，不访问外网，也不修改宿主源码。连接数量来自代理观察到的实际连接 ID。

`host-report.json` 记录请求数、取消数、代理请求与连接数、通知投递渠道、各阶段 GC 次数和回收的上下文数量。`storage-report.json` 记录 SQLite 持久化结果。任何断言失败返回非零退出码。
