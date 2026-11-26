# EasyTier with Extension

面向纯 Windows（Windows 10 1809+ / Windows 11 x64）的轻量级 **EasyTier** 图形客户端。基于 `C# (.NET 10 LTS)` + `WinUI 3` 构建，采用自包含 + ReadyToRun 绿色便携分发（解压即用），提供 OpenWrt LuCI 风格的简洁交互、多网络并行托管以及独立进程插件扩展体系。

## 核心特性

### 1. 网络概览
- **多网络实例并行**：支持配置并同时运行多个独立的 EasyTier 网络实例，每个实例按 `network_id` 隔离本地 RPC 端口与 TUN 虚拟网卡。
- **单行实例卡片**：每个网络实例展示为紧凑的单行卡片（配置名称、网络名称、独立连接开关与删除按钮）。
- **合并 Peer 节点列表**：在同一页面集中展示在线 Peer 节点（包含高亮置顶的本机 `LOCAL` 节点与虚拟 IP；无虚拟 IP 的服务器自动隐藏），支持按所属网络筛选和手动刷新。

### 2. 网络配置
- **普通设置**：依次为配置名称、Peer 地址（多个用逗号分隔，留空即独立建网）、网络名称、网络密钥、节点名、地址（`DHCP 自动获取` / `静态 IPv4`）、协议（`Default` / `KCP` / `QUIC`）、联机兼容（默认开启：为子网代理和 KCP 代理开启用户网络栈，并绑定物理网卡）。
- **客户端全局设置**：
  - 提供**开机自启动**开关，与系统托盘右键菜单双向同步。通过 Windows 任务计划程序（`EasyTierWithExtension_AutoStart`，`HighestAvailable` 权限）实现免 UAC 静默启动（`--autostart`）并驻留托盘，后台自动连接已开启“随软件自动启动”的网络。
  - 内置 **GitHub 在线更新**（`0511150/Easytier_Extension`）：启动时后台检查最新 GitHub Release，发现新版本后在标题栏与设置页提示，支持一键下载更新包（自动复用 GitHub 下载加速前缀并校验 `SHA256`）、覆盖替换并重启客户端。
- **高级设置**：直接编辑该网络的 EasyTier config（TOML），与普通设置双向同步，保存时由核心校验；可在 [配置助手](https://easytier.cn/assistant/) 中调整配置后粘贴。旧版的子网代理、监听地址、加密、MTU 等设置会自动迁移到 config 中。
- **TUN 网卡名称**：固定默认为 `EasyTier_With_Extension`；多个网络同时运行时，后启动的自动追加 `_2`、`_3`。
- **UDP 广播转发**：默认开启（`enable_udp_broadcast_relay`），将本机 UDP 广播转发给其他节点，帮助局域网游戏发现房间；可在高级设置中改为 `false`。
- **核心运行日志**：页面最底部实时展示当前选中网络实例的 `easytier-core.exe` 输出日志。

### 3. 系统托盘与进程守护
- 应用清单声明 `requireAdministrator`，启动即具备创建 WinTUN 虚拟网卡及高权限远程控制所需的管理员权限。
- 点击窗口关闭按钮默认最小化到系统托盘，右键托盘菜单支持打开主窗口、切换开机自启动与彻底退出。
- 通过 Windows **Job Object** 托管所有 `easytier-core.exe` 实例及插件子进程，主程序退出或异常终止时自动清理子进程，避免残留。

### 4. 插件中心与独立进程扩展体系
- **插件中心与多源订阅**：
  - 支持 **官方商店** 与 **第三方商店** 双标签浏览、关键词搜索与分类筛选，内置 **来源管理** 弹窗可增删改查、启停多个第三方厂商插件源，并支持配置可选的 GitHub 下载加速前缀。
  - **主程序与插件完全分离**：GitHub Releases 仅分发主程序便携包（`EasyTierWithExtension-win-x64-v*.zip`），不预装也不附带任何插件包；官方插件由插件源仓库的 GitHub Actions 自动构建并托管于 GitHub Pages 静态目录，**仅限通过客户端内置「插件中心」（软件中心）下载安装**（同时保留客户端内「本地导入 .zip」供开发调试），安装时强制校验 `SHA256`：
    - 官方插件源订阅地址：`https://0511150.github.io/Easytier_Extension_Registry/registry.json`
    - 官方插件源开源模板仓库：https://github.com/0511150/Easytier_Extension_Registry
  - 第三方厂商可使用开源模板仓库 `Easytier_Extension_Registry` 一键搭建自己的纯 GitHub 插件源。
- **独立进程与服务发现**：插件以独立 `.exe` 进程运行，通过命名管道（JSON-RPC 2.0）与宿主通信，借助宿主组网服务发现中心（默认 `UDP 39527`）在各已连接虚拟网上自动发现同插件在线节点。

### 5. 官方插件
官方插件均不内置于主程序，在客户端「插件中心 → 官方商店」中按需安装，详细功能见各插件说明。

| 插件 | 简介 | 说明 |
| --- | --- | --- |
| 屏幕共享（`screen-share`） | 基于虚拟局域网的低延迟多人屏幕共享，支持声音同步与远程协助 | [查看](plugins/ScreenShare/README.md) |
| 对端测速（`peer-speedtest`） | 测试到组网内其他节点的延迟、抖动与上下行带宽，支持多线程与测速记录 | [查看](plugins/PeerSpeedTest/README.md) |
| *更多官方插件* | *敬请期待* | [官方插件源](https://github.com/0511150/Easytier_Extension_Registry) |

<!-- 新增官方插件：在「更多官方插件」上方追加一行，链接指向 plugins/<插件目录>/README.md -->

---

## 目录结构与数据持久化

### 绿色发布目录（`dist/`）
```text
dist/
├── EasyTierClient.exe              # WinUI 3 客户端主程序（不含预装插件）
└── core/
    ├── easytier-core.exe           # EasyTier v2.6.4 核心
    ├── easytier-cli.exe            # EasyTier RPC 查询工具
    └── wintun.dll                  # WinTUN 虚拟网卡驱动
```

### 用户配置与持久化目录（`%AppData%\Easytier_With_Extension\`）
```text
%AppData%\Easytier_With_Extension\
├── config/
│   ├── client.json                 # 客户端全局设置与多网络配置
│   ├── networks/                   # 运行中网络的核心配置（停止后自动删除）
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

脚本将依次执行单元测试与核心集成测试、发布主程序与复制核心二进制到 `dist/`（不预装插件或离线市场目录），并将官方插件（`ScreenShare`、`PeerSpeedTest`）构建到临时目录执行冒烟测试在内的全量验证。

---

## 扩展开发

- 插件开发文档：[插件开发规范 (插件开发规范.md)](插件开发规范.md)
