# 光伏·空调项目：BMS 独立实时页面与本地客户端互通开发交接

版本：接口契约 v1，2026-10-03。目标仓库：`https://github.com/Mike666wq/photovoltaic-air-conditioner`；生产域名：`https://pv-ac.bbben.xyz`。

本文可直接交给云端开发机器的 Codex。请先阅读该机器最新仓库的 AGENTS.md，并检查实际代码。以下项目结构来自 Windows 机器已有研究副本，**没有拉取最新远端，不能当成当前生产事实**。若最新结构不同，保留本协议和隔离原则，按实际代码调整文件位置。不要擅自改客户端路由、大小写、单位或 JSON 包装。

## 1. 用户目标与范围

新增 `/bms/realtime` 独立页面。用户打开页面，选择设备与 Pack，点击“连接本地数据源”；云端创建观看租约，本地正在运行且已启用云连接的程序通过心跳得到租约，上传实时采样，网页展示。停止观看或全部观看租约过期后，本地停止上传快照，仍发轻量心跳。

本地仍负责串口、实验采集间隔、全面 SQLite 记录和 Excel/CSV 导出。网页连接不能启动远端 EXE、打开串口、改变记录间隔或控制充放电。网页不能直接 fetch 实验室电脑的 localhost。设备不在线时明确提示“本地程序未在线/等待本地程序”，不能假造数据或显示连接成功。

本轮只做实时展示和有界短趋势，原理图 `/`、分析大屏 `/analysis`、文件导入、仿真、回放、场景保存保持既有语义。原 store/simulation、store/analysis、injector、playbackSession 都不能承接新实时状态。后续对齐原项目的逻辑另行开发；本轮没有报告、分析建议、历史导出或远程实验控制需求。

## 2. 仓库实际落点（先重新核对）

研究副本：React 18、TypeScript、Vite、React Router、Zustand、ECharts；`apps/web/src/App.tsx` 当前注册 `/` 和 `/analysis`；`apps/web/scripts/serve-prod.mjs` 用 Node 原生 http 提供静态 SPA、`/health` 和 `/scenarios/` CRUD，并非纯静态网站。

建议新增：

```text
apps/web/src/pages/BmsRealtimePage.tsx
apps/web/src/store/bmsRealtime.ts
apps/web/src/services/bmsRealtimeApi.ts
apps/web/src/services/bmsRealtimeTypes.ts
apps/web/src/components/bmsRealtime/*
apps/web/src/pages/bms-realtime.css
apps/web/scripts/realtime/contract.mjs
apps/web/scripts/realtime/state.mjs
apps/web/scripts/realtime/auth.mjs
apps/web/scripts/realtime/routes.mjs
```

组件可复用现有 EChart/MetricCard 的通用部分，但不能导入其仿真数据依赖。页面懒加载，增加导航入口，首次打开原理图不能启动 SSE、观看租约或 BMS 图表循环。

生产服务在 SPA fallback **之前**处理 `/api/realtime/*`，未知 API 返回 JSON 404，不能返回 index.html。为 Vite 开发环境增加代理到同一 Node API 服务，或中间件插件挂载同一 `routes.mjs`；两边不能各写一套协议。Dockerfile 当前只复制 serve-prod.mjs，新增模块后必须一起 COPY；保持非 root 运行和 `/health`。

研究副本没有已证实的用户登录系统，不能凭空假定已有认证。先检查最新仓库：若已有登录及设备授权，复用；否则本模块提供最小独立观看登录（服务端配置的观看凭据→HttpOnly 会话 Cookie），设备注册/令牌由服务端配置或管理员工具完成。不要把设备写令牌交给浏览器，不要公开匿名写接口。

## 3. 两端通信的明确边界

本地客户端现有生产实现：HTTPS + JSON + Bearer；仅两个写接口，下面名称必须兼容。客户端服务地址填写域名根 `https://pv-ac.bbben.xyz`，请求最终落到根路径 `/api/realtime/...`，不要填写带 `/api` 的地址导致双重路径。禁止 HTTP、自动重定向和关闭证书验证；服务器应直接返回 JSON，不重定向到登录页或附加斜杠路径。

单次整体请求上限 5 秒，客户端允许的响应体最多 64 KiB。服务端设备接口限正文 64 KiB（流式计数，含 chunked），错误响应应小且不含令牌/采样全文。UTF-8 JSON，`Content-Type: application/json`，API 和 SSE 禁止浏览器/代理缓存。

设备 token 与 deviceId 在服务端一对一绑定。设备编号稳定且不依赖 COM；客户端 DPAPI 令牌通常不能跨电脑直接解密，迁移后重新输入令牌。只允许写自身设备、已注册地址/Pack。别名不作为身份或授权依据。浏览器凭独立用户会话只能读授权设备和管理自己的观看租约。

## 4. 必须兼容的设备接口

### 4.1 POST `/api/realtime/heartbeat`

请求头 `Authorization: Bearer <device-token>`；正文：

```json
{"deviceId":"lab-bms-01","alias":"实验室 BMS"}
```

无观看者，HTTP 200：

```json
{"subscriptionId":"","leaseSeconds":0,"requestedPacks":[]}
```

有观看者，HTTP 200：

```json
{"subscriptionId":"device-lease-opaque-id","leaseSeconds":45,"requestedPacks":[1,2]}
```

三个字段全部必需；leaseSeconds 为整数 0–3600，Pack 为 1–16，数组最多 16 项去重；有效租约必须有非空 subscriptionId。服务端统一选 **45 秒**设备租约，剩余有效时间受有效观看租约约束。正常客户端心跳 15 秒；当前优化代码对于较短租约会提前续期，不依赖固定 15 秒假设。

这里 subscriptionId 是设备级聚合上传租约，不是任意一个浏览器 viewerId。多观看者的 Pack 取并集；同一聚合租约续期保持 ID，全部失效后换新 ID。服务端单调时钟管理租约，客户端收到响应后开始本地租约计时。最后一位观看者退出时服务端立即撤销授权，客户端在下一心跳或租约到期停止上传。UI 可注明开始上传通常等待下一次心跳，约 15 秒内加网络耗时。

心跳只证明客户端云端模块在线，**不证明串口已连接或采集正在进行**，现有 heartbeat 正文没有这两个字段。网页数据状态必须依据收到的快照及其年龄判断；若需要精确采集状态，应另行协商向后兼容扩展，不能把“设备在线”写成“正在采集”。

### 4.2 POST `/api/realtime/snapshots`

相同 Bearer 头；完整请求示例（数值为接口夹具，不代表实测）：

```json
{
  "subscriptionId":"device-lease-opaque-id",
  "snapshot":{
    "schemaVersion":1,
    "deviceId":"lab-bms-01",
    "connectionSessionId":"session-opaque-id",
    "sequence":12,
    "acquisitionRound":8,
    "periodSeconds":2,
    "capturedUtc":"2026-10-02T08:00:00.123Z",
    "source":"serial",
    "address":1,
    "pack":1,
    "voltageCentivolts":5331,
    "currentCentiamps":-102,
    "socPercent":88,
    "sohPercent":100,
    "remainingCentiAh":8878,
    "totalCentiAh":10000,
    "cycles":92,
    "humidityPercent":0,
    "cellsMillivolts":[3331,3332,3332,3332,3332,3332,3332,3332,3332,3332,3332,3332,3332,3332,3332,3332],
    "temperaturesCelsius":[30,31,31,31,30,30],
    "alarmObservationAvailable":false,
    "alarmObservation":null
  }
}
```

成功和幂等重复均 HTTP 200 `{"accepted":true}`。只有正文完整验证且快照已接受到缓存，才确认；不以“开始处理”冒充完成。设备无效/过期租约返回 409；非法字段 400；未知 schemaVersion 422；未认证 401；设备不匹配/未授权 Pack/禁止模拟来源 403；正文超限 413；限流 429；临时服务故障 503。错误采用 `{"error":{"code":"LEASE_EXPIRED","message":"观看租约已失效"}}`，服务端不得把详细内部异常回给客户端。现有客户端不会消费 Retry-After，不要依赖它保证退避。

验证关键点：snapshot.deviceId 与认证设备一致，Pack 在聚合租约和设备注册集合内；sequence 为正安全整数，session 非空；source 仅 serial/simulation；日期严格有效 UTC ISO 字符串；数值不得 NaN/Infinity/字符串冒充数字；有符号电流必须保留负数。数组上限电芯 **48**、温度 **32**，来自当前本地解析边界；不要误设电芯最多 16。原始协议可能上送越界百分比，不要截断或补零；可接受协议整数并显示异常标记，合理值域校验和展示要分清。缺字段不能默认正常/0。

`periodSeconds` 当前是可选整数/null，单次读取可缺失；有效范围 1–86400，缺失时按未知处理，兼容旧客户端。其他示例字段由客户端发送；告警未观测时必须 false/null。已观测告警结构：

```json
{"observedUtc":"2026-10-02T07:59:58.000Z","acquisitionRound":7,"pack":1,"payloadHex":"00AB"}
```

payloadHex 仅为示意；真实字段是原始 44 payload 的十六进制编码，不是告警位定义。校验偶数十六进制、Pack 对齐、有界长度；不能将其当作“无告警”。本轮网页可以显示“告警原始观测/尚未解码”，后续按准确设备协议加解码，不准猜状态位。没有该观测时显示未知，过期时显示历史告警观测时刻。

## 5. 数据口径与去重

| 字段 | 存储/传输 | 页面转换 |
|---|---|---|
| voltageCentivolts | 整数 0.01 V | 除以 100，单位 V |
| currentCentiamps | 有符号整数 0.01 A | 除以 100，单位 A，保留正负 |
| remainingCentiAh / totalCentiAh | 整数 0.01 Ah | 除以 100，单位 Ah |
| socPercent / sohPercent / humidityPercent | 原协议整数百分比 | 百分比；湿度可能未接传感器，0 不自动解释为真实零湿度 |
| cellsMillivolts | 按电芯编号顺序的整数 mV | mV；若显示 V 再除 1000 |
| temperaturesCelsius | 按测点顺序的整数 ℃ | 直接 ℃，不要再减 40 |
| capturedUtc | 客户端接收到采样的绝对时间 | 页面按明确时区显示，当前不是 BMS 内部时钟 |
| alarmObservation.observedUtc | 44 告警自己的观测时间 | 独立显示，不能用采样时间代替 |
| sequence / acquisitionRound | 会话内上传序号 / 本地采集轮次 | 去重/关联用途，允许跳号 |

客户端合并中间值，只保留最新待发值，序号跳跃正常；断网不补传完整历史。幂等键 `deviceId + connectionSessionId + sequence`，重复确认成功、不重复添加趋势。不要无限存储所有去重键：每会话有界水位/有限窗口即可。**不能只用全设备最大 sequence 丢弃不同 Pack 的乱序数据**；同会话维护各 source/address/pack 的最新序号。会话 ID 为不透明值，不能按字典序判断新旧；服务器接到新会话时可标记旧会话已退役，并在有限时间内拒绝其迟到快照，但不能每次旧会话重试都倒回当前会话。跨会话历史无需补传。

`capturedUtc` 不能用于决定设备是否在线，系统时钟可能校正；服务端新增 receivedAt（服务端接收时刻）用于展示链路延迟，TTL 用单调时钟，不把 receivedAt 替换成采样时间。若设备时间明显偏差，保留原值并提示“采集机时间可能有偏差”。内部 UTC 用于交互，页面不增加 UTC 展示列。

当前 v1 **没有采集机时区字段**。首版页面明确显示“中国标准时间（Asia/Shanghai）”，将 UTC 按该时区格式化，和当前实验机时间对齐；不能声称已自动识别任意采集机时区。未来增加 captureTimeZone/capturedOffsetMinutes 需两端另行对齐。格式化使用时区转换，不能硬加 8 小时后再次本地转换。电流方向只标正/负，不能直接沿用原仿真 batteryConvention 的正负口径；需要实验设备确认后才可写充电/放电标签。

## 6. 网页读接口（由本轮云端新增）

所有浏览器接口同源 `/api/realtime`，用授权会话 Cookie；写操作校验 Origin 与 CSRF，不能让其他网站创建/续期观看或清理数据。首次打开页面只请求授权设备列表，默认不创建观看者。

| 方法/路径 | 请求 | 响应/行为 |
|---|---|---|
| GET `/devices` | 无 | `{devices:[{deviceId,alias,allowedPacks,online,lastHeartbeatAt}]}`；只返回授权设备，绝不返回 deviceToken |
| POST `/viewers` | `{deviceId,packs:[1]}` | 201 `{viewerId,expiresAt,renewAfterSeconds:15}`；网页观看 TTL 45 秒 |
| PUT `/viewers/:viewerId` | `{packs:[1,2]}` | 200 同上，续期并可改 Pack，必须本人所有；过期返回 410，页面重建 |
| DELETE `/viewers/:viewerId` | 无 | 204，幂等释放，仅自己的观看者 |
| GET `/devices/:deviceId/latest` | 可选 `packs=1,2` | `{deviceId,online,lastHeartbeatAt,packs:[{snapshot,receivedAt,stale}]}`，无样本 packs=[]；授权必需 |
| GET `/devices/:deviceId/trend` | `pack=1&metric=voltage&limit=600` | `{points:[{capturedUtc,value,receivedAt,sequence,connectionSessionId}]}`，只给有界缓存和授权 Pack |
| GET `/events?viewerId=...` | 同源 EventSource | SSE，鉴权和 viewer 所有权；不得在 URL 中放 token |
| DELETE `/devices/:deviceId/cache` | CSRF 校验 | 204，清理该设备最新值/趋势/去重缓存，不删除注册凭据；只授权管理员 |

SSE 格式 `event: snapshot\ndata: {"snapshot":{...},"receivedAt":"..."}\n\n`；另有 device-status、cache-cleared 事件；每 15 秒注释心跳 `: keepalive\n\n`。开始 SSE 时发授权 Pack 当前缓存值，随后发新值。缓存值须有 stale 状态；不能冒充刚采样。缓存清理后客户端已确认样本不会保证立刻重发，网页明确等待下一次采样。

事件 id 可用会话+序号；首版断线恢复走 latest/bootstrap，**不保证全量事件回放**，Last-Event-ID 不能承诺未实现的补传。事件只能携带该观看者获准的数据；慢 SSE 连接有界写缓冲，超过上限关闭连接并提示重连，不能每个页面持续堆积所有数据。

页面用定时续期，不依赖 requestAnimationFrame（隐藏页会节流）。浏览器隐藏时暂停续期/关闭 SSE，恢复可重新点击或明示自动恢复；任一种必须与页面状态一致。卸载时尝试释放租约，真正保证靠 TTL，不能依赖 beforeunload 一定成功。EventSource 自动重连不得永久维持已过期 viewer。

## 7. 有限缓存、部署和资源

首版每 source/address/Pack 保留最新快照一个；趋势只总压/电流/SOC，可由一个有界快照 ring 派生。每 Pack **最多 600 点且最长 10 分钟**，两者同时限制。最新缓存也 10 分钟过期（低频实验可能没有当前值，应提示间隔和等待新样本）。不写 Excel/CSV、大历史库或无限请求日志。

全部观看者离开后清除短趋势，设备注册保留；无租约快照不缓存。限制设备数量、每设备 Pack、观看者、每用户 SSE 连接和请求速率；预算初始设备 20、每设备 16 Pack、单用户 4 条 SSE，参数可配置。10 分钟清理任务与每次写入都执行淘汰；租约自身 45 秒过期，退役会话/去重信息也有界。

初始用单 Node 进程内存缓存即可，**前提是实际部署单副本且发布期不并存两个独立状态实例**。研究副本 Kubernetes 部署必须检查 replicas、滚动更新策略及 Ingress。单副本内存模式可选 Recreate（发布短时断开，页面重连、客户端重新领租约）；如果需要多个副本或无停机滚动，则租约、最新值、去重、发布订阅必须共享（例如 Redis），单靠浏览器粘性会话不足以把设备 POST 和所有观看者路由到同一实例。重启丢失短缓存属允许行为，注册凭据要由 Secret/持久配置保存。

Ingress 为 SSE 设置合理 read timeout、禁缓冲；HTTP 响应 `Content-Type: text/event-stream`、`Cache-Control: no-cache, no-store`、`X-Accel-Buffering: no`。外部 HTTPS 在现有 Ingress 终止，Node 内部可 HTTP；证书需客户端机器信任。连接设备心跳/快照路径不得做浏览器登录重定向。公网接口不能使用测试专用 mock/open loopback 授权。

## 8. 独立页面布局和状态

顶部设备选择、Pack、连接/断开观看；区分网站连接、采集端心跳在线、观看租约、最后样本时间/数据过期。指标卡：总压、电流、SOC、SOH、剩余容量、总容量、循环；主要趋势：总压、电流/SOC分开单位，不画混合单位共轴。电芯/温度为辅助区域可折叠，按编号显示，未确认的温度测点不冒称环境/MOS温度。

状态至少覆盖：未连接观看、建立观看、设备不在线、设备在线等待采样、实时展示、样本过期、观看过期、网络重连、无权限、模拟数据（显著标注）。数字 0 是有效值，缺失用“—”，缓存/旧数据不能显示绿灯实时。只有采样到达才进入实时展示。

页面宽屏充分利用空间，小屏纵向滚动；趋势轴不重叠，保留时间和单位，用户缩放/拖动时不要强制跳回最新，提供返回最新。只缓冲有限点数，不使用每帧刷新。建立页面 store 清理 timer/SSE/订阅，不污染其他页面。

## 9. 实施顺序与验收

1. 核对最新仓库、认证和部署；实现共享 API 模块及其 contract tests。
2. 实现设备认证、聚合观看租约、两个现有客户端写接口；用本文 fixture 验证单位和结构。
3. 实现浏览器接口/SSE、短缓存和清理，确保同源开发/生产一致。
4. 实现独立懒加载页面及状态；用测试夹具与受控服务浏览器验证。
5. Node 集成测试用动态 loopback 端口，覆盖无观看仅心跳、观看启动、双观看者/Pack并集、续期、主动断开/TTL、重复/乱序/重连、缓存清理、401/403/409/413/422/429、非JSON、SSE授权/慢消费者/断线恢复、API未知路径不能返回HTML。
6. 前端测试覆盖 5331→53.31 V、-102→-1.02 A、8878→88.78 Ah、负温度、未知告警、时间转换、真实/模拟标识、零与缺失、停止观看清理；不得依赖仓库忽略的真实 data/ 文件。
7. 运行仓库类型检查、单元测试、生产构建，并实际用 Node serve-prod.mjs 访问新路由/API；检查 Docker COPY、新页面刷新 SPA 和 Ingress SSE。
8. 与 Windows 本地客户端在隔离测试配置下联调，再真实实验机联合验收。提交测试结果、接口变更清单、需要的 Secret/环境变量、部署条件和回滚方式。用户确认后才部署生产。

完成标准不是“做出页面截图”，而是 Windows 客户端→服务端接收→授权浏览器正确显示；停止观看后上传暂停而本地记录继续；原有仿真和文件回放测试通过。

## 10. 当前 Windows 分工与交接

Windows 这边继续改进客户端会话失效、发送队列来源过滤、采集间隔相关新鲜度、真实上传时间、多 Pack 公平性，并做隔离回归。**客户端现有 wire schema v1 不变**，新元信息字段不会由云端单方面强制要求。HTTPS 证书与正式网站互通仍须联调，本机 HTTP 测试不等于证书/公网通过。

云端 Codex 请先按这份协议开发，不改 Windows 项目，也不要执行部署/改生产凭据。若发现最新仓库差异或协议必须变更，先给出差异和兼容方案，由两边对齐后实施。开发结束回传：后端路由/版本、响应示例、登录/令牌发放方式、测试服务 HTTPS 根地址、缓存/部署模式、测试结果。本地需填的是服务根地址和设备写令牌，浏览器使用独立观看凭据。

## 11. 环境配置与离线夹具交付

建议云端配置（名称可按最新项目约定调整，但需在交付中说明）：

- `BMS_REALTIME_ENABLED=1`：模块启用。关闭时所有模块接口返回明确 JSON 503，原项目正常运行。
- `BMS_DEVICES_FILE`：Secret 挂载的设备注册文件；每项包含 deviceId、alias、allowedPacks、deviceTokenHash、allowSimulation、实验设备显示时区（首版 Asia/Shanghai）。设备 token 至少 32 随机字节，服务端仅保存散列并使用恒定时间比较；浏览器不可见。
- `BMS_VIEWER_CREDENTIALS_FILE`：无现有登录时独立模块的观看用户配置，含用户名、密码哈希和可读设备集合；密码使用 Node crypto.scrypt（随机 salt），不用明文/单次 SHA256。提供登录 POST `/api/realtime/auth/login`、登出 POST `/api/realtime/auth/logout`、当前身份 GET `/api/realtime/auth/session`；登录成功设置 Secure/HttpOnly/SameSite Cookie，CSRF nonce 单独返回，登录失败不泄露用户是否存在；限次尝试。若复用已有认证，则这些路由由已有登录机制替代，仍需明确设备授权。
- `BMS_REALTIME_CACHE_TTL_SECONDS=600`、`BMS_REALTIME_MAX_POINTS=600`、`BMS_REALTIME_VIEWER_TTL_SECONDS=45`：有界缓存和租约参数。
- `BMS_REALTIME_MAX_DEVICES=20`、`BMS_REALTIME_MAX_VIEWERS_PER_USER=4`：入口配额。测试可缩短 TTL，不可在生产使用匿名鉴权或跳过 TLS。

设备元信息初始化例（不可含真实 token，tokenHash 由工具生成）：

```json
{"devices":[{"deviceId":"lab-bms-01","alias":"实验室 BMS","allowedPacks":[1],"deviceTokenHash":"GENERATED_BY_ADMIN_TOOL","allowSimulation":false,"displayTimeZone":"Asia/Shanghai"}]}
```

注册不是客户端 token 随便带上就自动开通；由管理员读取本地界面设备编号，再生成和绑定 token。本地保存稳定 deviceId，迁移沿用该编号时不能在两台设备同时主动上传；服务器同设备并发会话冲突要明确显示/拒绝，不能让数据无提示来回跳。

需要新增后端测试运行命令，例如 `node --test apps/web/scripts/realtime/*.test.mjs`，并加入 CI verify；现有 Vitest 命令未必会扫描 scripts/ 下 Node 测试。生产镜像的 API 环境需从 Secret 注入，不进入镜像、源码、浏览器 bundle 或计划书。前端不得要求用户输入设备写 token。

fixture 文中的示例应由测试编码成实际 JSON 文件；另外包括缺 periodSeconds、无告警、旧告警、模拟标记、过期、错误 schema、48 电芯/32 温度边界。云端开发可在 Windows 客户端尚未连接前独立执行这些接口/页面测试，但 mock 只能用于测试配置，生产不能以 mock 数据冒充实机。

联调回传清单：实际 HTTPS 根地址、注册 deviceId、设备 token 的安全交付方式（不要贴进公开日志）、观看登录方式、响应样例、测试结果、缓存/副本配置、证书和 Ingress 状态。未得到 Windows 的最终回归确认前，页面开发可以推进，生产联合验收仍不可宣称完成。

## 12. Windows 本轮完成状态（2026-10-03）
当前最新源码目录 BmsSerialDemo，用户优化后的文件版本 1.2.2.0。本轮已修复上述客户端边界，235项整体回归通过，未信任TLS证书拒绝测试通过。没有进行正式网站HTTPS成功联调，没有升级现有迁移包。服务端可按本文v1契约独立开发，联调时需回传地址、注册及观看授权方式。证据及限制见 CLOUD_LOCAL_VERIFICATION.md。
