# 平台路线图（选型决策记录）

> 2026-11-25 由 BOSS 拍板，小M 整理。本文件只记录「选了什么、为什么、何时开工」；
> 具体移植规格书与脚手架待开工时再产出。

## 结论速览

| 平台 | 选型 | 状态 |
|---|---|---|
| macOS | SwiftUI + AppKit（现状） | 已上线，v1.0.0 整合发行版 |
| Windows | **C# / .NET 8 + WPF** | 选型已定，待 macOS 发版后开工 |
| Linux | **暂不拍板**（GTK4 vs Qt 二选一） | 立项时按用户桌面分布再定 |

## Windows 版选型依据

### 架构分层（已定档）

```
ClashBarX.App (WPF)      ← 托盘/面板/Fluent+Mica 观感；Win32 集成隔离于此层
ClashBarX.Core (纯 .NET) ← mihomo API 客户端/订阅/配置事务/自动代理守护
                           零 Win32 依赖、可 AOT——为 Linux 版预留复用
mihomo sidecar + wintun  ← 官方 Windows 产物（amd64/386），零开发
```

### 为什么是 WPF（排除项一览）

- **排除网页壳系**（Tauri/Electron）：BOSS 明确要原生，不要 WebView 套壳；
- **排除 WinUI 3**：无内置托盘支持、工具链坑多（MSIX/热重载），为「潮」付稳定性税不值；
- **排除 C++/Qt**：原生观感与 WPF 同档，但 C++ 开发效率与 AI 辅助效率显著更低；
- **排除 Rust/Slint、Compose、Flutter**：均为自绘渲染，非系统控件；观感精致度或语料生态有硬伤；
- **WPF 胜出**：系统原生渲染 + 声明式 XAML（SwiftUI 的 Windows 对等物）+ 20 年生态，
  托盘/开机自启/服务/注册表全部原生成熟方案。

### 已实证的关键事实

- mihomo 官方发布 Windows 产物（v1.19.34 起 amd64/386/compatible/go120-125 全系）；
- Windows 系统代理为用户级注册表设置（HKCU `...\Internet Settings`），**不需要特权助手**，
  比 macOS 的 PrivilegedHelper + XPC 链路简单一个量级；
- TUN 需 wintun 驱动 + 系统服务（管理员安装一次），Clash Verge Rev 已验证同路线；
- 系统代理「守护」（防篡改回写）用 WinINET API + 注册表监听实现。

### 仓库与协作（开工时执行）

- **monorepo**：`windows/` 目录放 C# 解决方案，不新建仓库；
  上游同步（upstream-sync）永不触碰该目录，零合并冲突；
- CI 路径过滤：`windows/**` 变更走 windows runner，Swift 路径变更走 macOS runner；
- 版本线统一（ClashBarX 1.x 全平台同 tag），CHANGELOG 单线；
- **双 repo 中转**：小T（Windows，内网无 GitHub）本地开发，产出经 `git bundle`/
  format-patch 传小M（协调席）落库并推 GitHub；CI 结果与评审意见反向中转。

## Linux 版预留（不拍板）

- Linux 无单一「原生权威」：GNOME=GTK4+libadwaita（托盘被官方砍，要 AppIndicator）、
  KDE=Qt（托盘全桌面兼容）；阵营选择依赖目标用户桌面分布，立项时再定；
- 无论走哪条，`ClashBarX.Core`（C#）业务层零返工复用；UI 层分家写；
- Linux 无统一系统代理机制（gsettings/KDE/环境变量/TUN），业务模型需重设计——
  届时「自动代理守护」权重上升。

## 开工条件

1. macOS v1.0.0 发版完成（CI 全绿 + DMG 双架构发布）；
2. BOSS 下达开工指令后：小M 出移植规格书（协议层 99 接口清单从
   `MihomoAPIService.Endpoint` 导出）+ `windows/` 脚手架 + GitHub issue 工单，
   小T 主导开发，小M 协调席（中转/评审/CI/发版）。
