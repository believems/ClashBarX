## v0.4.8

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v0.4.8-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square)

> 本次更新为代理提供者新增「全部更新」按钮，一次更新所有订阅，不用再逐个点刷新。

### 📝 更新日志 (Changelog)

**✨ 新功能 (Features)**

- ![New](https://img.shields.io/badge/New-10B981?style=flat-square) **代理提供者全部更新**：代理提供者标题栏新增「全部更新」按钮，依次更新所有提供者；更新中各行刷新按钮同步转圈，每个提供者更新完成后立即刷新其更新时间与节点数，单个失败不影响其余提供者。

**🚀 优化改进 (Improvements)**

- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **分流显示切换按钮**：列表 / 按策略分组的切换按钮只是切换显示方式，不再使用选中高亮，与旁边的刷新按钮样式一致。

---

## v0.4.7

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v0.4.7-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square)

> 本次更新按 Apple 人机界面指南重新梳理了界面配色：强调色只用在开关、选中项等交互状态上，模式切换、标签页和各类按钮改为中性色，整体不再一片蓝；同时提升了文字对比度，并支持系统的"增强对比度"设置。

### 📝 更新日志 (Changelog)

**🚀 优化改进 (Improvements)**

- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **配色统一**：规则 / 全局 / 直连改为中性分段控件，标签页、图标开关、筛选标签的选中态改为中性灰；强调色只保留给系统开关、选中节点等交互状态，并跟随系统强调色设置。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **颜色含义不再混用**：下载流量改用青色，与强调色区分；额度进度条默认中性灰，接近用完时变橙、变红；日志协议标签改为中性色，不再与警告、正常状态色混淆。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **按钮更协调**：设置页"系统维护"等次要按钮不再是玻璃面板上的亮灰块；面板行悬停改为浅灰，下拉菜单保持系统原生高亮。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **可读性与无障碍**：更新时间、时间戳、列标题等说明文字对比度提升；复制成功改为绿色对勾，不再白字绿底；开启系统"增强对比度"后面板灰底与边框自动加深；顶部状态圆点支持悬停提示和读屏，连接失败时显示感叹号图标。

---

## v0.4.6

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v0.4.6-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square)

> 本次更新新增「切换节点时断开旧连接」：网络不好换节点时，已经建立在旧节点上的连接会被断开并通过新节点重连，切换立刻生效，不用再手动关连接。

### 📝 更新日志 (Changelog)

**✨ 新功能 (Features)**

- ![New](https://img.shields.io/badge/New-10B981?style=flat-square) **切换节点时断开旧连接**：在「核心设置」中开启后（默认关闭），切换策略组节点时只断开仍走旧节点的连接，直连和其他策略组的连接不受影响；url-test / fallback 组以内核实际选中的节点为准，mihomo 内部的下载与升级连接不会被断开。

**🐞 修复问题 (Bug Fixes)**

- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **fallback 组切换误报失败**：在 fallback 组中选择失效节点时，mihomo 会先重新测速（最多约 5 秒），原先 2 秒超时导致切换被误报为失败；现在超时延长至 10 秒，超时后也会以组的实际选中状态判断结果。

---

## v0.4.5

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v0.4.5-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square)

> 本次更新优化一键测速：测速过程中旧延迟会变淡显示，测完恢复正常亮度，一眼就能区分本轮结果和上一轮结果；测不通的节点也不再残留旧延迟。

### 📝 更新日志 (Changelog)

**🚀 优化改进 (Improvements)**

- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **测速中状态**：一键测速按钮在测速期间转圈并防止重复触发；组测速时，组行和该组成员节点的延迟变淡并显示测速中状态，测完恢复。

**🐞 修复问题 (Bug Fixes)**

- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **测失败的节点残留旧延迟**：mihomo 组测速接口只返回测通的节点，测不通的节点原先会继续显示上次的延迟；现在统一显示为超时。

---

## v0.4.4

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v0.4.4-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square)

> 本次更新优化模式切换控件：规则 / 全局 / 直连改为三个等宽原生按钮，并统一选中与未选中状态的圆角轮廓，避免选中项显得像直角矩形。

### 📝 更新日志 (Changelog)

**🚀 优化改进 (Improvements)**

- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **模式切换原生按钮**：规则 / 全局 / 直连改用三个等宽的原生 Liquid Glass 按钮，选中项使用强调色，保留单次点击切换与防闪烁处理。

**🐞 修复问题 (Bug Fixes)**

- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **模式按钮圆角不一致**：选中态与未选中态原先由不同按钮样式绘制圆角，导致选中项看起来偏方；现在统一为相同的大号圆角矩形轮廓。

---

## v0.4.3

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v0.4.3-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square)

> 修复 v0.4.2 中模式切换控件缩成居中一小块的问题。

### 📝 更新日志 (Changelog)

**🐞 修复问题 (Bug Fixes)**

- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **模式切换没有铺满整行**：SwiftUI 分段控件会按文字宽度紧凑显示并忽略外部宽度，改用 `NSSegmentedControl` 并让各段平分整行宽度。

---

## v0.4.2

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v0.4.2-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square)

> 本次更新聚焦 **面板观感与节点列表**：macOS 26 及以上的主面板与侧边子菜单改由系统原生 Liquid Glass（`NSGlassEffectView`）承载，去掉了边缘黑框与圆角外的尖角阴影；底栏改为状态栏样式，模式切换换成系统分段控件。同时修复订阅节点无法单独测速的问题，节点子菜单按节点名定宽、打开时不再左右跳动。

### 📝 更新日志 (Changelog)

**🚀 优化改进 (Improvements)**

- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **原生 Liquid Glass**：主面板与侧边子菜单使用 AppKit 原生玻璃，阴影由玻璃自身绘制并留出透明边距，不再出现深色描边与被裁切的尖角。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **底栏重设计**：去掉底框，改为细分隔线加单行状态栏，左侧 mihomo 版本与内核更新，右侧应用版本，有新版本时显示蓝色提示。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **模式切换**：规则 / 全局 / 直连改用系统原生分段控件。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **节点子菜单**：宽度按组内最长节点名计算，节点名完整显示；超长名称悬停时横向滚动；单节点测速按钮移到延迟数字之后，悬停时不再遮挡延迟。

**🐞 修复问题 (Bug Fixes)**

- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **订阅节点单独测速失效**：mihomo 的 `/proxies` 不包含订阅提供者中的节点，改走提供者的 healthcheck 接口；测速失败显示为超时，不再把内核连接标记为异常。
- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **打开面板时底部闪白**：底栏不再使用会先按浅色外观绘制的窗口背景色。
- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **切换模式时文字闪烁**：切换期间不再禁用分段控件。
- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **节点子菜单左右跳动与右侧空白**：宽度在弹出前确定；滚动区域不再为常驻滚动条预留空白；子菜单贴齐主面板玻璃边缘。

---

## v0.4.1

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v0.4.1-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square)

> 本次更新聚焦 **连接页的排序体验**：新增按实时上传 / 下载速度排序，排序依据与升降序拆分为两个独立控件；鼠标停在列表上时暂停重排，避免目标行跳走。同时流量配色由蓝绿改为橙蓝、数字回归正文颜色，浅色模式下更易读；并修复连接页下拉菜单在空间不足时被截断的问题。

### 📝 更新日志 (Changelog)

**✨ 新增功能 (New Features)**

- ![Feature](https://img.shields.io/badge/Feature-10B981?style=flat-square) **按速度排序连接**：mihomo 只提供每条连接的累计字节数，ClashBar 以相邻两次快照的差值除以实际间隔得出实时速度，并做轻度平滑避免排名随瞬时波动来回交换；排序依据新增「下载速度 / 上传速度」，此时连接行的 ↑ ↓ 列显示实时速率（空闲为 `–`）。
- ![Feature](https://img.shields.io/badge/Feature-10B981?style=flat-square) **排序依据与方向分离**：排序依据（默认顺序 / 连接时间 / 下载速度 / 上传速度 / 下载流量 / 上传流量 / 总流量）与升降序按钮独立设置并记忆；旧版的组合排序设置会自动迁移。
- ![Feature](https://img.shields.io/badge/Feature-10B981?style=flat-square) **悬停暂停重排**：鼠标位于连接列表上时，已显示的行保持位置、数据照常刷新，新连接追加到末尾；移开后立即按当前排序重排。

**🚀 优化改进 (Improvements)**

- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **流量配色**：上传 / 下载由蓝绿改为橙 / 蓝，颜色只用于箭头与曲线，数字使用正文颜色，浅色模式下更易读，对色弱用户也更友好。

**🐞 修复问题 (Bug Fixes)**

- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **下拉菜单被截断**：下拉胶囊不再被压缩截断；连接页控制栏在空间不足时依次隐藏胶囊图标与计数徽标，分流页策略菜单对过长的策略组名限宽并显示省略号。

---

## v0.4.0

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v0.4.0-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square)

> 本次更新是一次 **菜单栏面板的界面与交互重设计**：按 macOS 原生设计规范重做了面板的视觉层级与控件反馈——字体回归 SF Pro，图标统一为单色、颜色只用来表达状态，所有可点击元素都有一致的「静止 / 悬停 / 按下 / 开启」反馈；在 macOS 26 及以上面板与侧边弹出菜单使用系统 Liquid Glass 材质。同时应用更新检查与 Homebrew 安装源切换到本仓库（`kingcanfish/ClashBar`、`kingcanfish/tap`）。

### 📝 更新日志 (Changelog)

**✨ 新增功能 (New Features)**

- ![Feature](https://img.shields.io/badge/Feature-10B981?style=flat-square) **Liquid Glass 面板**：macOS 26+ 上主面板与侧边弹出菜单使用系统 `glassEffect` 材质，低版本系统自动回退到原有毛玻璃材质。
- ![Feature](https://img.shields.io/badge/Feature-10B981?style=flat-square) **统一的按钮交互规范**：新增图标按钮、列表行、胶囊按钮、分段控件、胶囊下拉菜单等共享样式，静止时即有浅底以示可点，悬停加深、按下加深并轻微缩放，开关类按钮开启时显示强调色底。

**🚀 优化改进 (Improvements)**

- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **模式与标签页**：代理模式改为原生分段控件样式（选中项为浮起滑块），标签页选中项以强调色胶囊标示；切换标签页改为即时切换，避免半透明背景下新旧页面叠影。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **节点页分组布局**：流量概览、快捷设置、代理提供者、代理组分别置于圆角分组卡片；代理组行右侧以 › 提示可展开节点列表，测速按钮仅在悬停时出现。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **TUN 协议栈选择**：由需双击的四个标签改为胶囊下拉菜单，单击展开、选择后生效，避免误触重启 TUN。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **面板高度上限**：面板最高 640pt，头部与底栏固定、内容区滚动，不再在高屏幕上铺满整屏。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **输入框与下拉菜单**：过滤框、端口与代理例外输入框改为自绘样式（聚焦时强调色描边），连接 / 分流 / 日志页的下拉筛选统一为胶囊样式。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **更新源切换**：应用内版本检查与发布页链接指向 `kingcanfish/ClashBar`，Homebrew Cask 发布到 `kingcanfish/tap`。

**🐞 修复问题 (Bug Fixes)**

- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **深色模式输入框闪白**：修复原生圆角输入框在玻璃面板上切换标签页时先绘制为浅色再变为深色的问题。
- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **订阅到期误报**：订阅剩余天数仅在 15 天内到期时以橙色提示。

---

## v0.3.6

## v1.2.2

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v1.2.2-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square)

### 📝 更新日志 (Changelog)

**✨ 新增功能 (New Features)**

- ![Feature](https://img.shields.io/badge/Feature-10B981?style=flat-square) **搜索框一键清除**：分流、连接、日志页的搜索框新增清除按钮，方便快速清空筛选关键词。

**🚀 优化改进 (Improvements)**

- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **调整覆写与设置标签页顺序**：标签栏调整为「节点 / 分流 / 连接 / 日志 / 覆写 / 设置」，对应快捷键同步为 `⌥⌘5`（覆写）与 `⌥⌘6`（设置）。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **优化更新订阅后代理组加载速度**：配置重载与代理集合更新完成后立即主动刷新代理组；解耦 `/proxies` 与 `/providers/proxies` 请求，放宽长耗时接口超时并移入独立连接池，缩短异常退避并消除面板打开时的重复并发请求。

**🐞 修复问题 (Bug Fixes)**

- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **修复内核升级与订阅更新后运行时设置被重置**：在订阅重载、内核升级与 Geo 数据库更新前后自动快照并恢复代理模式、允许局域网、IPv6、TCP 并发、日志级别、端口及 TUN 设置。
- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **修复远程配置更新时间与倒计时显示**：远端内容未变时「上次」时间同步刷新；失败不再推迟整个自动更新周期（自动更新失败按 15 分钟退避重试）；补齐「更新全部远程配置」状态刷新、倒计时向上取整及整小时显示，并仅在面板打开时运行倒计时刷新定时器。

## v1.2.1

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v1.2.1-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square)

### 📝 更新日志 (Changelog)

**🐞 修复问题 (Bug Fixes)**

- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **唤醒后恢复规则覆写**。系统唤醒后等待本机内核就绪，再自动应用规则覆写。控制器暂时断连时会重试，重载后恢复原有运行设置。
- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **自动修复系统代理 Helper**。开启系统代理时会检查 Helper 是否响应。遇到注册失效时，应用会在获得管理员授权后清理旧注册并重新加载 Helper。
- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **修正项目主页链接**。点击左上角图标会打开 ClashBarPlus 项目主页。

## v1.2.0

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v1.2.0-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square) ![Upstream](https://img.shields.io/badge/Synced-ClashBar_v0.3.6-8B5CF6?style=flat-square)

> 同步上游 [Sitoi/ClashBar](https://github.com/Sitoi/ClashBar) **v0.3.5 / v0.3.6**：系统代理 Helper 改为管理员授权直装，更新 App 后不再失效；TUN 协议栈选择会被记住。

### 📝 更新日志 (Changelog)

**✨ 新增功能 (New Features)**

- ![Feature](https://img.shields.io/badge/Feature-10B981?style=flat-square) **系统代理 Helper 管理员授权安装**：首次开启系统代理时输入管理员密码，Helper 安装到 `/Library/PrivilegedHelperTools`，不再依赖登录项后台活动；之后更新 App 不会让它失效，只有 Helper 本身升级时才会再次要求授权。
- ![Feature](https://img.shields.io/badge/Feature-10B981?style=flat-square) **TUN 协议栈记忆**：记住在 Proxy 快捷区选择的 TUN 协议栈，重启 App 或内核后保持不变。

**🚀 优化改进 (Improvements)**

- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **状态栏图标离屏合成**：图标与速率合成为一张图像再交给状态栏，减少视图层级与重绘。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **Helper 状态与排障精简**：移除「后台活动」相关状态，文档同步新的首次授权与卸载 Helper 步骤。

**🐞 修复问题 (Bug Fixes)**

- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **从 DMG 安装后 Helper 带隔离属性导致系统代理不生效**：安装时清除隔离属性，安装失败也不再写入成功标记。

## v1.1.0

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v1.1.0-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square)

> 参考 OpenClash 的自定义规则，新增「覆写」页：在订阅规则前后插入自己的规则与规则集，订阅更新后依然生效，原配置文件不会被修改。

### 📝 更新日志 (Changelog)

**✨ 新增功能 (New Features)**

- ![Feature](https://img.shields.io/badge/Feature-10B981?style=flat-square) **规则覆写**：设置之后新增「覆写」页（`⌥⌘6`），可维护前置规则、追加规则与规则集三份文件。前置规则插在订阅 rules 最前面，追加规则插在 `MATCH` 之前，规则集与订阅同名时以覆写为准。
- ![Feature](https://img.shields.io/badge/Feature-10B981?style=flat-square) **面板内编辑、保存即生效**：点击前置规则 / 追加规则 / 规则集即可在面板内折叠展开编辑区，编辑区限高、规则多时上下滚动；关闭智能引号等自动替换，保存（`⌘S`）后自动重载，只改注释或内容未变时不重载。

**🚀 优化改进 (Improvements)**

- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **覆写失败不静默回退**：生成或 `mihomo -t` 校验失败时直接报错并在覆写页显示原因，不会悄悄以原配置启动。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **相对路径保持可用**：运行时配置写在 `state/runtime/`，但核心工作目录仍取原配置所在目录，订阅里 `./ruleset/...` 等相对路径不受影响。

## v1.0.0

![macOS](https://img.shields.io/badge/macOS-Supported-000000?style=flat-square&logo=apple) ![Version](https://img.shields.io/badge/Release-v1.0.0-10B981?style=flat-square) ![Core](https://img.shields.io/badge/Core-Mihomo-6366f1?style=flat-square) ![Upstream](https://img.shields.io/badge/Fork_of-ClashBar_v0.3.3-8B5CF6?style=flat-square)

> ClashBarPlus 的首个版本，从上游 [Sitoi/ClashBar](https://github.com/Sitoi/ClashBar) 的 **v0.3.3** 切出，完整继承其全部能力，并修复了三个上游同样存在的问题。Bundle ID 与数据目录与上游保持一致，覆盖安装即原地升级。v0.3.3 及更早的历史见[上游更新日志](https://github.com/Sitoi/ClashBar/blob/main/CHANGELOG.md)。

### 📝 更新日志 (Changelog)

**✨ 新增功能 (New Features)**

- ![Feature](https://img.shields.io/badge/Feature-10B981?style=flat-square) **独立版本线**：自 v1.0.0 起独立编号，发布流程忽略从上游同步来的 `v0.x` 标签。
- ![Feature](https://img.shields.io/badge/Feature-10B981?style=flat-square) **流量曲线展示时长**：可选 1 / 5 / 15 / 30 分钟，默认 5 分钟，横轴按真实时间排布。
- ![Feature](https://img.shields.io/badge/Feature-10B981?style=flat-square) **保持流量曲线**：新增开关，位于「流量曲线时长」下方，默认关闭；开启后「仅图标」模式下曲线也不再断档，代价是应用不再完全空闲。

**🚀 优化改进 (Improvements)**

- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **品牌更名为 ClashBarPlus**：应用名、DMG 产物、文档站与界面文案统一更名；Bundle ID、数据目录与特权助手标签保持不变，既有配置与授权全部保留。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **流量曲线跨面板开合保留**：不再一关面板就清空；采样仅存于内存、不做持久化，未采集的时段以曲线断开如实呈现。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **延迟信号条按实测结果显示**：柱高由每次延迟决定（越慢越高，与旁边的毫秒数同向），颜色细分为 4 档，历史由最近 4 次扩展为 5 次。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **菜单栏状态对账**：打开面板时与显示模型对账一次，避免漏掉的增量更新永久留在菜单栏上。
- ![Optimize](https://img.shields.io/badge/Optimize-3B82F6?style=flat-square) **发布流程移除 Homebrew 环节**：不再向上游 tap 推送 cask，仅通过 GitHub Releases 分发 DMG。

**🐞 修复问题 (Bug Fixes)**

- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **TUN 已开启但菜单栏图标不变**：内核重启会让 TUN 状态短暂往返，防抖窗口内的抖动会把过期图标画上去且此后无法自愈。
- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **品牌图标退化为 SF Symbol**：较新工具链把 `Assets.xcassets` 编译成 `Assets.car`，原先按相对路径查找散装 PNG 的方式全部落空。
- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **启动时弹出空白 Settings 窗口**：SwiftUI 的 `Settings` 场景只是菜单命令的宿主，现已在窗口出现的瞬间关闭。
- ![Fix](https://img.shields.io/badge/Fix-EF4444?style=flat-square) **界面残留 ClashBar 字样**：面板标题与两份本地化统一更名，此前系统代理提示会指向并不存在的 `ClashBar.app`。
