# EasyTier with Extension

面向纯 Windows（Windows 10 1809+ / Windows 11 x64）的轻量级 **EasyTier** 图形客户端。基于 `C# (.NET 10 LTS)` + `WinUI 3` 构建，采用自包含 + ReadyToRun 绿色便携分发（解压即用），提供 OpenWrt LuCI 风格的简洁交互、多网络并行托管以及独立进程插件扩展体系。

## 核心特性

### 1. 网络概览
- **多网络实例并行**：支持配置并同时运行多个独立的 EasyTier 网络实例，每个实例按 `network_id` 隔离本地 RPC 端口与 TUN 虚拟网卡。
- **单行实例卡片**：每个网络实例展示为紧凑的单行卡片（配置名称、网络名称、独立连接开关与删除按钮）。
- **合并 Peer 节点列表**：在同一页面集中展示在线 Peer 节点（包含高亮置顶的本机 `LOCAL` 节点与虚拟 IP），支持按所属网络筛选和手动刷新。

### 2. 快速组网与高级配置
- **客户端全局设置**：提供**开机自启动**开关，与系统托盘右键菜单双向同步。通过 Windows 任务计划程序（`EasyTierWithExtension_AutoStart`，`HighestAvailable` 权限）实现免 UAC 静默启动（`--autostart`）并驻留托盘，后台自动连接已开启“随软件自动启动”的网络。
- **LuCI 快速组网表单**：支持 `公共服务器发现`、`手动指定 Peer 节点`、`仅本地监听` 三种连接方式；虚拟 IP 默认采用 `DHCP 自动获取`，静态 IPv4 默认值及占位提示为 `10.10.10.10/24`（对应 `10.10.10.0 - 10.10.10.255` 网段）。
- **折叠式高级设置**：收纳子网代理 CIDR、本地监听地址、传输加密开关与算法（`aes-gcm` / `aes-256-gcm` / `chacha20` / `xor`）、TUN 网卡名称、MTU、延迟优先路由、KCP/QUIC TCP 代理以及无 TUN 模式。
- **核心运行日志**：页面最底部实时展示当前选中网络实例的 `easytier-core.exe` 输出日志。

### 3. 系统托盘与进程守护
- 应用清单声明 `requireAdministrator`，启动即具备创建 WinTUN 虚拟网卡及高权限远程控制所需的管理员权限。
- 点击窗口关闭按钮默认最小化到系统托盘，右键托盘菜单支持打开主窗口、切换开机自启动与彻底退出。
- 通过 Windows **Job Object** 托管所有 `easytier-core.exe` 实例及插件子进程，主程序退出或异常终止时自动清理子进程，避免残留。

### 4. 插件中心与内置屏幕共享插件
- **插件中心与多源订阅**：
  - 支持 **官方商店** 与 **第三方商店** 双标签浏览、关键词搜索与分类筛选，内置 **来源管理** 弹窗可增删改查、启停多个第三方厂商插件源，并支持配置可选的 GitHub Release 下载加速前缀。
  - 官方插件源采用纯 GitHub 架构（GitHub Pages 托管 `registry.json` + GitHub Releases 直链分发 `.zip` 插件包），同时支持本地离线 `market/registry.json` 兜底与 `.zip` 离线导入，安装时强制校验 `SHA256`：
    - 官方插件源订阅地址：`https://0511150.github.io/Easytier_Extension_Registry/registry.json`
    - 官方插件源开源模板仓库：https://github.com/0511150/Easytier_Extension_Registry
  - 第三方厂商可使用开源模板仓库 `Easytier_Extension_Registry` 一键搭建自己的纯 GitHub 插件源。
- **独立进程与服务发现**：插件以独立 `.exe` 进程运行，通过命名管道（JSON-RPC 2.0）与宿主通信，借助宿主组网服务发现中心（默认 `UDP 39527`）在各已连接虚拟网上自动发现同插件在线节点。
- **内置屏幕共享插件（`screen-share`）**：
  - 统一入口菜单 `屏幕共享 - 桌面共享`，支持在节点设置视图与内嵌 D3D11 画面视图（`viewport`）之间切换。
  - 支持 `全屏桌面`（`WGC` / `DXGI`）与 `指定窗口 (Window Capture)`（基于 WGC 捕获，支持窗口列表刷新及窗口关闭自动停播）两种捕获模式；全屏模式内置 `[16:9]`（`3840x2160`、`2560x1440`、`1920x1080` 等）与 `[16:10]`（`3840x2400`、`2880x1800`、`2560x1600`、`1920x1200` 等）常见显示器/笔记本分辨率预设，按比例缩放不拉伸。
  - 支持 Media Foundation H.264 硬件编码与解码、WASAPI Loopback + Opus 系统音频同步传输，以及单路编码多人同时观看扇出（`TCP 39600` 控制信道 / `UDP 39601` 音视频分片流）。
  - **opt-in 远程键鼠控制**：共享端需显式开启 **开启被控** 后，观看端才可申请并持有单人互斥远控权，支持按 `Ctrl + Alt + Esc` 随时脱离控制焦点。

---

## 目录结构与数据持久化

### 绿色发布目录（`dist/`）
```text
dist/
├── EasyTierClient.exe              # WinUI 3 客户端主程序
├── core/
│   ├── easytier-core.exe           # EasyTier v2.6.4 核心
│   ├── easytier-cli.exe            # EasyTier RPC 查询工具
│   └── wintun.dll                  # WinTUN 虚拟网卡驱动
├── market/
│   ├── registry.json               # 默认本地插件市场索引
│   └── screen-share-1.0.0.zip      # 内置屏幕共享插件包
└── plugins/
    └── screen-share/
        ├── manifest.json
        └── ScreenShare.exe
```

### 用户配置与持久化目录（`%AppData%\Easytier_With_Extension\`）
```text
%AppData%\Easytier_With_Extension\
├── config/
│   ├── client.json                 # 客户端全局设置与多网络配置
│   └── plugins_state.json          # 插件启用/禁用状态
├── plugins/                        # 通过插件市场或本地导入安装的插件
└── plugins_data/
    └── <plugin_id>/                # 各插件持久化数据（通过 --data-dir 传入）
```

---

## 构建与打包

项目已配置 `.NET 10 LTS` 打包脚本（支持标准 `nuget.org` 在线还原，也兼容本地离线 NuGet 缓存），发布前请先确保没有运行中的 `EasyTierClient.exe` 占用 `dist/` 目录，并将 EasyTier 核心二进制置于 `dist/core/` 或通过 `EASYTIER_CORE_SRC` 环境变量指定：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\build-dist.ps1
```

脚本将依次执行单元测试与核心集成测试、发布主程序与 `ScreenShare` 插件、生成本地市场包与 `registry.json`（含 `SHA256`），并在打包完成后运行包含已发布 `ScreenShare.exe` 冒烟测试在内的全量测试。

---

## 扩展开发

如需为客户端开发第三方插件，请阅读 [插件开发规范.md](插件开发规范.md)。
