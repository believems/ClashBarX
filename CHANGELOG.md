# Changelog

本项目所有显著变更均记录于此文件。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本管理遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

**✨ 新增功能**

- **内网前置代理**（双平台，Issue #2）：集团内网仅允许经企业代理出外网时，节点连接与订阅拉取统一经企业代理拨出（mihomo dialer-proxy 三处注入：静态节点/provider 节点/provider 自身拉取——域名解析由代理端完成，绕开内网 DNS 黑洞）。设置页独立区块：开关/服务器/端口/Basic 认证，未启用置灰；开关切换热重载即时生效；注入失败降级不阻塞启动。

## v1.0.1

> 2026-11-28 · 双平台首个同 tag 版本：macOS 更新检查平台过滤 + Windows 试用反馈 14 项修复与布局精修，新增轻量安装包。

**🐞 修复问题**

- macOS 检查更新误报 Windows 版本：/releases/latest 不区分平台，改为按 DMG 资产过滤出 macOS 可用的最新稳定发版。
- Windows：首次点「启动内核」无反馈——缺内核时弹出引导（获取方式 + 放置路径 + 一键打开 core 目录）。
- Windows：导入订阅缺少首次引导提示（对齐 Mac 版提醒语义）。
- Windows：代理页在内核未运行时一片空白——现提示「启动内核后显示分组与订阅源」。
- Windows：升级内核时「更新 GEO 数据」按钮联动进入执行中——两操作状态独立。
- Windows：检查更新误报 macOS 版本——对齐双平台按资产过滤口径。
- Windows：带 secret 的配置全链路 401——本机控制器补读配置顶层密钥（含配置热切换后刷新凭据）。
- Windows：订阅源列表混入代理分组与零值日期——按「default 名 / Compatible 车型」过滤（对齐 Mac 口径）。
- Windows：真机三修——托盘图标资源释放异常、GEO 204 空体误报、内核升级 500 与「已是最新」归一。

**🚀 优化改进**

- Windows：固定窗体尺寸（布局稳定）；代理端口输入框按 65535 定宽；维护卡按钮等大；守护配置改为未启用时置灰（不折叠、高度恒定）。
- Windows：订阅区双栏布局（左导入+列表 / 右操作），各处说明文字收进 info 图标 ToolTip；日志过滤改 Windows 原生控件样式。
- Windows：新增不含 .NET 运行时的轻量安装包 setup-lite（约 5-10MB，需 .NET 8 Desktop Runtime）。

## win-v1.0.0

> 2026-11-26 · ClashBarX Windows 首个正式版（WPF + .NET 8，tag `win-v1.0.0`）：
> 以 macOS 版 v1.0.0 功能面为对齐基线逐模块移植（M1–M5），Windows 范式对应物
> （托盘 tooltip 闪现替代 banner、管理员运行替代 setuid、WLAN API 替代 CoreWLAN）。
> v1.1.0 起双平台同 tag 发版。

**✨ 新增功能**

- **TUN 模式整块**：代理页快捷开关 + system/gvisor/mixed/mips stack 选择（mips 需内核 ≥ 1.19.31）、PATCH 后运行态校验、mips 门槛警告与托盘 TUN 图标态；开启需管理员权限——经 UAC 提权重启应用（意图落盘自动恢复），拒绝如实回滚；启动时开关校验、stack 对齐与降级关闭（对应 macOS validate/verify/align 三连）。
- **远程机器管理**：机器增删改（name/host/port/secret/HTTPS）+ 独立 JSON 持久化 + 5s 周期连通探测（401/超时/拒绝归因）；主窗头部目标切换器（切前连通验证）；远程目标下订阅 / 守护 / 规则覆写 / SSID 策略与本机内核动作整体只读（「远程只读」提示条），系统代理指向远程内核端口。
- **SSID 策略自动切配置**：WLAN API 监听 Wi-Fi 变化（P/Invoke 隔离在 App 层），绑定管理（SSID → 配置，同绑定再点即解绑）；命中自动热重载目标配置（PUT /configs），配置缺失按指纹去重告警。
- **应用更新检查**：GitHub Releases latest API（启动自动查 + 设置页手动查），非 draft / 非 prerelease 且版本更新才提示；设置页「关于」区版本行打开发布页、有新版本时醒目链接。
- **代理 Provider 卡片**：可折叠订阅源区——更新全部（单个失败不阻断）、每行节点数 / 相对更新时间 / 到期天数（不足 15 天警示色）/ 流量配额条（≥75% 黄、≥90% 红）、单 provider 刷新与 healthcheck。
- **分组卡四视图**：按延迟排序（不可用节点过滤）、延迟历史信号条（5 槽右对齐、高度随延迟、颜色分好坏）、显示 hidden 分组（非 global 模式隐藏 GLOBAL）、一键测全部分组（并发）；分组行图标直载。
- **WebUI 入口**：已配置 external-ui 时打开 `{controller}/ui`；未配置打开 metacubexd setup 引导页（带 controller 连接参数）。
- **界面双语**：VM 动态文案 / 全部日志 / 托盘菜单 / XAML 静态文案统一接入中英字典，设置页切换即时生效。
- 代理 / 规则 / 连接 / 日志 / 网络 / 设置六 tab 全功能（M1–M4 落地）：分组卡切换与测速、规则列表与规则覆写、连接三维度统计、mihomo 日志管线、系统代理与绕过列表、自动代理守护、订阅管理、外观与核心设置。

**🚀 优化改进**

- 绿色便携版 zip（自包含解压即用，数据在 `%APPDATA%\ClashBarX`）与 Inno Setup 安装器双产物；CI（GitHub Actions）双 job 单测 + `win-v` tag 触发发版流水线。
- mihomo sidecar 启停（Job 兜底防孤儿进程）、API 状态轮询（前台 4s / 后台 12s）、WebSocket 四流重连去抖。

**🐞 修复问题**

- 已知限制：内核自升级在 Windows 有运行中文件锁限制（失败反馈含手动替换指引）；托盘通知以 tooltip 闪现替代 macOS banner（H.NotifyIcon 2.4.1 无气泡 API）。

## v1.0.0

> 2026-11-25 · 首个正式版：以上游 Sitoi/ClashBar 为基座，整合 mh567 / kingcanfish / Gongsc / flowke
> 四条社区发行线的全量精选与上游 #128 / #123 修复，并新增本仓库自研的「自动代理守护」。
> 品牌独立为 ClashBarX，版本号自 v1.0.0 起独立编号。

**✨ 新增功能**

- **自动代理守护**（本仓库新功能）：周期性经当前代理端口探测外网连通性，连续失败后自动恢复——先在策略组内测速并切换到组内最快可用节点，组内全挂时自动更新全部订阅再重测切换。「设置 → 自动代理守护」提供开关、探测周期（秒，30–86400）与「立即检查一次」。 CLI：`ClashBarX.app/Contents/MacOS/ClashBarX --proxy-guard` 触发运行中实例立即检查（无实例则启动后立即检查一次），方便自动化工具在大模型发现网络异常时自行触发。
- 节点 / 应用 / 域名三维度流量统计（上游 #123）：连接页可切换统计视图，配置重载后自动重挂进程匹配。
- 运行时数据目录迁移至 `~/.config/clashbar`（源自 flowke）：首次运行自动从 `~/Library/Application Support/clashbar` 平滑迁移；系统代理 Helper 支持备份恢复与 v5 协议版本协商。
- 运行时端口保持（源自 flowke）：端口草稿自动落盘，内核重载 / 订阅更新后核对并恢复配置端口。
- macOS 26+ 原生 Liquid Glass 菜单栏面板与侧边子菜单（源自 kingcanfish v0.4.x）。
- 代理组节点弹窗支持搜索过滤（源自 kingcanfish v0.4.9）。
- 连接按实时速度排序，排序依据与方向分离（源自 kingcanfish）。
- 「切换节点时断开经由旧节点的连接」开关（默认关闭，源自 kingcanfish）。
- 代理提供者「全部更新」按钮，单个失败不影响其余提供者（源自 kingcanfish）。
- 延迟信号条按实测结果决定高度与颜色（源自 Gongsc）。
- 「覆写」页：规则与规则集覆写，保存即生效；系统唤醒后自动恢复覆写（源自 Gongsc）。
- 系统代理 Helper 健康自检与注册失效自动修复（源自 Gongsc）。
- 策略组测速完成后立即刷新代理组，同步各策略组实际选中节点（源自 flowke，上游 #120）。
- 菜单栏面板固定在所点击的显示器（源自 flowke）。
- WebSocket 默认最大消息上限提升至 4 MB（源自 flowke）。
- 本地测试体系与 `make test`（源自 mh567）。

**🚀 优化改进**

- 品牌与发布线独立为 ClashBarX：应用内更新源、DMG 产物名与界面文案统一更名； Bundle ID 与特权助手标签保持不变，覆盖安装即原地升级。
- 订阅元数据模型融合双方语义：`displayName` / 用量信息 / 启用开关（flowke）与更新成功时间 / 失败退避重试（Gongsc）。
- 超高屏上面板保持菜单尺寸（上限 640 pt），超出部分内容内部滚动（kingcanfish 与 flowke 融合）。
- 发布版本号推导脚本化（`Scripts/resolve_version_bump.sh`，源自 mh567）。
- 界面按 Apple HIG 梳理配色：强调色仅用于交互状态，支持「增强对比度」（源自 kingcanfish）。
- 新增 [Yams](https://github.com/jpsim/Yams) 5.4+ 依赖（YAML 配置解析，规则合并引擎使用）。

**🐞 修复问题**

- 节点故障后延迟徽标永远冻结在最后一次成功值，手动测速也无法纠正（上游 #128）。
- 日志搜索仅覆盖最近 120 条（源自 mh567）。
- 端口自动保存丢失（源自 mh567）。
- 退出与启动阶段的主线程阻塞（源自 mh567）。
- 文档站生成文件污染工作区（源自 mh567）。
- fallback 组切换误报失败：超时延长至 10 秒，并以组的实际选中状态判定结果（源自 kingcanfish）。
- 一键测速区分新旧延迟，测不通的节点显示超时而非残留旧值（源自 kingcanfish）。
- 订阅节点单独测速改走 provider healthcheck 接口（源自 kingcanfish）。
- 本地按架构打包时误用其他架构内核（源自 kingcanfish）。
- 内核升级与订阅更新后运行时设置被重置（源自 Gongsc）。
- 远程配置更新时间与倒计时显示逻辑（源自 Gongsc）。
- 更新订阅后代理组加载缓慢（源自 Gongsc）。
- TUN 图标状态错位后无法自愈（源自 Gongsc）。
- 启动时弹出空白 Settings 窗口（源自 Gongsc）。

## 历史版本（整合前）

ClashBarX 的首个版本 v1.0.0 之前的变更来自上游与各社区发行线，
详细记录见各自仓库的 CHANGELOG：

上游 [Sitoi/ClashBar](https://github.com/Sitoi/ClashBar)（v0.3.x 及更早）、[mh567/ClashBar](https://github.com/mh567/ClashBar)（工程加固线）、[kingcanfish/ClashBar](https://github.com/kingcanfish/ClashBar)（v0.4.x，Liquid Glass UI 线）、[Gongsc/ClashBar](https://github.com/Gongsc/ClashBar)（v1.x，ClashBarPlus 功能线）、[flowke/ClashBar](https://github.com/flowke/ClashBar)（Dashboard 与数据目录线）。

[Unreleased]: https://github.com/believems/ClashBarX/compare/main...integration
[1.0.0]: https://github.com/believems/ClashBarX/releases/tag/v1.0.0
