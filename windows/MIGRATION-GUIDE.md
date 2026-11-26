# ClashBarX Windows 版迁移指南

> 定稿：2026-11-26。本文是 Windows 版的唯一开工规格（BOSS 已拍板技术选型，
> 见仓库根 `PLATFORM-ROADMAP.md`）。执行人：小T（Windows 机）；
> 协调席：小M（Gitee ⇄ GitHub 双向同步、代码评审、CI、发版）。

## 0. 角色与环境

- **你（小T）**：Windows 10/11 机器，开发主属。仓库经 Gitee 镜像获取（GitHub 内网不可达）；
- 工具链：.NET 8 SDK + Visual Studio 2022（或 VS Code + C# Dev Kit）+ Git；
- **小M**：你推 Gitee 后经 BOSS 转达，小M 拉取、评审、同步 GitHub、跑 CI。

## 1. 技术选型（已定档，不再议）

- **C# / .NET 8 + WPF**（系统原生渲染、声明式 XAML、托盘/服务/注册表原生方案齐全）；
- 分层（严格遵守）：
  - `windows/src/ClashBarX.Core`：业务层——mihomo API 客户端、订阅、配置事务、代理守护。
    **纯 .NET，禁止 P/Invoke / Win32 / WPF 依赖**（为将来 Linux 版预留复用）；
  - `windows/src/ClashBarX.App`：WPF UI 层——托盘、面板、系统集成（注册表/服务/开机自启）全部隔离于此；
- monorepo：代码放 `windows/` 目录，macOS Swift 线（`Sources/`）不受任何影响。

## 2. 仓库与协作流

```sh
git clone https://gitee.com/folowind/clashbarx.git    # 或 git@gitee.com:folowind/clashbarx.git
cd clashbarx && git switch -c feature/win-m1 origin/integration
```

- 开发分支 `feature/win-*`，完成后推 Gitee 并请 BOSS 通知小M 评审合入 `integration`；
- Swift 源码是**行为规格参考**（只读，勿改）：协议、算法、超时、重连语义都以它为准；
- 验证纪律：`dotnet test` 全绿才提交；提交信息中文，格式 `<emoji> <type>: <摘要>`。

## 3. mihomo 协议层规格（HTTP）

基底 URL：`http://{controller}`（如 `127.0.0.1:9090`），认证头 `Authorization: Bearer {secret}`。
成功判定 2xx；GET 请求失败自动重试至多 3 次，其余方法不重试。
下表为全部 26 个端点（源：`Sources/ClashBar/Services/MihomoAPIService.swift`）：

| 端点 | Method | Path | Query | Body | 超时 |
|---|---|---|---|---|---|
| version | GET | `/version` | — | — | 2s |
| traffic | GET | `/traffic` | — | — | WS |
| memory | GET | `/memory` | — | — | WS |
| logs | GET | `/logs` | `level={info\|warning\|error}` | — | WS |
| getConfigs | GET | `/configs` | — | — | 5s |
| putConfigs | PUT | `/configs` | `force=true`（可选） | `{"path":...}` 或 `{"payload":...}` | 30s |
| patchConfigs | PATCH | `/configs` | — | `{任意配置字段}` | 5s |
| groupDelay | GET | `/group/{name}/delay` | `url`、`timeout`(ms) | — | `max(5, timeout/1000+2)s` |
| proxies | GET | `/proxies` | — | — | 5s |
| proxyDelay | GET | `/proxies/{name}/delay` | `url`、`timeout`(ms) | — | 同上 |
| switchProxy | PUT | `/proxies/{name}` | — | `{"name": target}` | **10s**（死节点会被内核同步复测至多 5s 才应答） |
| proxyProviders | GET | `/providers/proxies` | — | — | 5s |
| proxyProvider | GET | `/providers/proxies/{name}` | — | — | 5s |
| updateProxyProvider | PUT | `/providers/proxies/{name}` | — | — | 30s |
| proxyProviderHealthcheck | GET | `/providers/proxies/{name}/healthcheck` | `url`、`timeout` | — | **180s** |
| proxyProviderProxyHealthcheck | GET | `/providers/proxies/{provider}/{proxy}/healthcheck` | 同上 | — | 同上 |
| rules | GET | `/rules` | — | — | 2s |
| ruleProviders | GET | `/providers/rules` | — | — | 5s |
| updateRuleProvider | PUT | `/providers/rules/{name}` | — | — | 30s |
| connections | GET | `/connections` | `interval={秒}` | — | WS |
| closeAllConnections | DELETE | `/connections` | — | — | 2s |
| closeConnection | DELETE | `/connections/{id}` | — | — | 2s |
| flushFakeIPCache | POST | `/cache/fakeip/flush` | — | `{"path":"","payload":""}` | 2s |
| flushDNSCache | POST | `/cache/dns/flush` | — | 同上 | 2s |
| upgradeCore | POST | `/upgrade` | — | 同上 | 60s |
| upgradeGeo | POST | `/upgrade/geo` | — | 同上 | 60s |

路径参数需 URL 转义。模型字段命名遵循 mihomo 原始 JSON（camelCase），
参考 Swift 模型 `Sources/ClashBar/Models/Runtime.swift` / `Connection.swift`。

## 4. WebSocket 流规格

四条流（traffic / memory / connections / logs）共用基底 URL，ws 或 http 均可（升级握手）。
**单条消息上限 4 MB**。重连去抖算法（源：`StreamCoordinator.swift`）：

```
延迟 = min(2^min(attempt, 3), 8) 秒 × random(0.85, 1.15)
attempt 收到有效 payload 时重置为 0；连续失败计数封顶 8
payload 为空串 / "null" / "{}" 时视为噪声丢弃（不重置计数、不广播）
```

各流 payload：
- `/traffic`：`{"up":int,"down":int}`（字节/秒），约每秒一条；
- `/memory`：`{"inuse":int}`；
- `/connections`：`{"downloadTotal":int,"uploadTotal":int,"connections":[…],"memory":int}`，按 `interval` 轮询；
- `/logs`：`{"type":"info|warning|error","payload":"…"}`。

## 5. 系统集成对照（macOS → Windows）

| 功能 | macOS 实现 | Windows 实现 |
|---|---|---|
| 系统代理 | networksetup + PrivilegedHelper + XPC 特权链 | **注册表** `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings`：`ProxyEnable=1`、`ProxyServer=127.0.0.1:{mixedPort}`、`ProxyOverride=<绕过列表>`；改后调 `InternetSetOption(INTERNET_OPTION_SETTINGS_CHANGED)` 广播。**用户级，无需管理员** |
| 代理守护（防篡改） | SystemProxyMonitor | `RegistryWatcher` 监听上述键，被外部修改后按开关回写 |
| TUN | utun + Helper | wintun.dll + 系统服务模式（SCM，管理员安装一次；参考 Clash Verge Rev 的服务方案） |
| 开机自启 | SMAppService | `HKCU\…\Run` 键（或计划任务） |
| 内核进程管理 | Process + XPC | `System.Diagnostics.Process`；MVP 用户态启动即可，TUN 阶段再上服务 |
| 数据目录 | `~/.config/clashbar` | `%APPDATA%\ClashBarX`（配置/订阅/状态；内核与 Geo 资源放其 `core/` 子目录） |
| mihomo 内核 | 官方 darwin 产物 | **官方 Windows 产物**（releases `mihomo-windows-amd64-vX.Y.Z.zip`），no-core 策略：首启引导用户放置内核 |

## 6. MVP 里程碑（4-6 周）

| 里程碑 | 内容 | 验收 |
|---|---|---|
| **M1 骨架** | 解决方案 + Core/App 分层 + 托盘常驻 + 内核 sidecar 启停 + §3 全部端点客户端 + WS 四流 | Core 层单测（mock HttpClient）覆盖 26 端点与重连算法 |
| **M2 代理可用** | 系统代理开关 + 订阅导入/管理 + 代理组/节点列表 + 节点切换 + 延迟测速 | 真机：系统代理开关节点切流量走通 |
| **M3 守护闭环** | 连接页 + 日志页 + **自动代理守护**（算法见 `AppViewModel+ProxyGuard.swift`：HEAD 204 经混合端口探测 → 连续 3 次失败 → 组内测速切换 → 全挂则更新全部订阅自愈）+ CLI `--proxy-guard` 触发 | 断网模拟验证自愈 |
| **M4 收尾** | 规则页 + 覆写页 + 设置页（对应 v1.0.0 六 tab 结构）+ 安装器（MSIX/Inno）+ 发版流水线（小M 建 GitHub Actions windows job） | 端到端 + v1.1.0 双平台同 tag 发版 |

## 7. 行为规格参考文件（Swift，只读）

| 关注点 | 文件 |
|---|---|
| 协议层全规格 | `Sources/ClashBar/Services/MihomoAPIService.swift` |
| WS 重连去抖 | `Sources/ClashBar/Services/StreamCoordinator.swift` |
| 守护算法 | `Sources/ClashBar/ViewModels/AppViewModel+ProxyGuard.swift` |
| 数据模型 | `Sources/ClashBar/Models/Runtime.swift` 等 `Models/` 全目录 |
| 配置/订阅管理 | `Sources/ClashBar/Services/ConfigService.swift` |
| 系统代理链路 | `Sources/ClashBar/ViewModels/AppViewModel+SystemProxy.swift` |
| 面板信息架构（六 tab） | `Sources/ClashBar/Views/MenuBar/`（v1.0.0 已定型，照此组织） |

## 8. 纪律红线

1. Core 层零 Win32 依赖（CI 会跑 Linux 单测验证可移植性）；
2. `dotnet test` 全绿才可提交；合并前小M 评审；
3. GPL-3.0：参考 Clash Verge Rev（同为 GPL）的 wintun/服务方案合法，但**代码自写**；
4. 勿动 `Sources/`（Swift 线）、`Scripts/`、`.github/workflows/release-dmg*.yml`；
5. 版本号跟主仓库统一线（v1.1.0 起双平台同 tag）。
