# Changelog

本项目所有显著变更均记录于此文件。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本管理遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

暂无内容。

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
