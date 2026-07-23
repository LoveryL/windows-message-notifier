# Windows 消息通知器 | Windows Message Notifier

<p align="center">
  <img src="https://img.shields.io/badge/Language-C%23%20100%25-blue" alt="C# 100%" />
  <img src="https://img.shields.io/badge/Framework-.NET%2010%20%2F%20WPF-5C2D91" alt=".NET 10 / WPF" />
  <img src="https://img.shields.io/badge/Platform-Windows%2010%2B-0078D4" alt="Windows 10+" />
  <img src="https://img.shields.io/github/v/release/LoveryL/windows-message-notifier?include_prereleases&label=latest" alt="Latest Release" />
  <img src="https://img.shields.io/github/license/LoveryL/windows-message-notifier" alt="License" />
</p>

---

一个基于 **WPF (C#)** 的轻量级 Windows 桌面消息通知助手，能够 **监听系统 Toast 通知** 并以自定义弹窗形式展示，同时提供消息汇总面板，帮助你集中管理和查看所有通知。

A lightweight Windows desktop message notifier built with **WPF (C#)**. It **listens to system Toast notifications** and displays them as custom popup toasts, while providing a message summary panel to help you centrally manage and review all notifications.

---

## 📸 预览截图(部分) | Screenshots

| 通知弹窗效果 | 消息汇总面板 |
|:---:|:---:|
| <img width="400" alt="Toast Popup" src="https://github.com/user-attachments/assets/e0fa82b3-46a0-4a32-8161-189f7424db8f" /> | <img width="600" alt="Message Summary" src="https://github.com/user-attachments/assets/ec782889-d8f8-4296-8b90-a03e4025276f" /> |

---

## ✨ 功能特性 | Features

| 特性 | 说明 (中文) | Description (EN) |
|:---|:---|:---|
| 🔔 **系统通知监听** | 基于 Windows Runtime 的 `UserNotificationListener` API，实时捕获系统级 Toast 通知 | Built on Windows Runtime `UserNotificationListener` API for real-time system-wide Toast notification capture |
| 🖥 **自定义弹窗通知** | 收到新通知时，在屏幕右上角弹出美观的自定义 Toast 窗口，支持滑入/滑出动画 | Beautiful custom Toast popup in the top-right corner with slide-in/out animations |
| 📋 **消息汇总面板** | 点击系统托盘图标可打开消息汇总窗口，集中查看所有捕获的通知 | Click the tray icon to open a summary window that lists all captured notifications |
| 🧲 **系统托盘常驻** | 最小化到系统托盘运行，不占用任务栏空间，支持右键菜单操作 | Runs in the system tray with context menu support (auto-start toggle, exit) |
| 🔄 **开机自启** | 支持通过注册表配置开机自启，一键开关 | One-click toggle for auto-start via Windows Registry |
| 🧹 **通知管理** | 支持按标题批量清除通知，也可逐条删除并同步清除系统通知中心中的记录 | Clear notifications by title or remove individual items with sync to the system notification center |
| 🚫 **智能过滤** | 自动过滤来自微信等特定应用的通知，避免重复捕获 | Automatically filters out notifications from specific apps like WeChat |

---

## 🏗 项目结构 | Project Structure

```
windows-message-notifier/
├── .github/
│   └── workflows/
│       └── dotnet.yml              # GitHub Actions 自动构建工作流
├── Resources/                      # 资源文件（图标等）
├── App.xaml / App.xaml.cs          # 应用程序入口，系统托盘初始化
├── MainWindow.xaml / .cs           # 通知弹窗窗口（Toast Popup）
├── MessageSummaryWindow.xaml / .cs # 消息汇总窗口
├── toast.cs                        # Toast 通知监听与解析核心逻辑
├── toastmessagestore.cs            # 未读消息存储与同步管理
├── NullToVisibilityConverter.cs    # XAML 值转换器
├── Notifier.csproj                 # 项目配置 (.NET 10 / WinExe)
└── README.md
```

---

## 🚀 快速开始 | Quick Start

### 前置要求 | Prerequisites

- **操作系统**: Windows 10 (Build 1809+) / Windows 11
- **运行环境**: [.NET 10.0 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) 或更高版本
- **开发环境** (仅开发时需要): Visual Studio 2022+ / VS Code + C# Dev Kit

### 运行方式一：下载 Release（推荐）

1. 前往 [Releases](https://github.com/LoveryL/windows-message-notifier/releases) 页面
2. 下载最新版本的 `release.zip`
3. 解压后运行 `Notifier.exe`

### 运行方式二：从源码构建(对于普通用户不推荐)

```bash
# 克隆仓库
git clone https://github.com/LoveryL/windows-message-notifier.git
cd windows-message-notifier

# 还原依赖并构建
dotnet restore
dotnet build --configuration Release

# 运行
dotnet run --configuration Release
```

### 首次使用配置

首次运行时，应用会请求 **通知访问权限**：

> **设置路径**：`设置 → 隐私和安全性 → 通知 → 允许此应用访问通知`

授权后，通知监听将自动开始工作。

---

## 📖 使用说明 | Usage

### 核心交互流程

```
系统发出 Toast 通知
       │
       ▼
  Notifier 捕获通知
       │
       ├── 解析标题 + 正文
       │
       ├── 弹出右下角自定义 Toast 窗口（5 秒后自动消失）
       │
       ├── 托盘图标切换为"提醒"状态
       │
       └── 点击托盘图标 → 打开消息汇总面板
```

### 托盘图标操作

| 操作 | 效果 |
|:---|:---|
| **左键单击** 托盘图标 | 打开 / 聚焦消息汇总窗口 |
| **右键** → 开机自启 | 开启 / 关闭开机自启动 |
| **右键** → 退出 | 关闭应用 |

### 消息汇总面板

- 按通知 **标题分组** 展示所有捕获的消息
- 点击 **"清除"** 按钮：按标题批量清除对应通知
- 关闭窗口后重新打开可刷新消息列表

---

## 🛠 技术栈 | Tech Stack

| 层级 | 技术 | 说明 |
|:---|:---|:---|
| **语言** | C# 13 (.NET 10) | 100% C# 编写 |
| **UI 框架** | WPF (Windows Presentation Foundation) | XAML 声明式 UI + 数据绑定 |
| **通知 API** | Windows Runtime `UserNotificationListener` | 系统级通知监听 (WinAppSDK) |
| **托盘集成** | `System.Windows.Forms.NotifyIcon` | 系统托盘图标与右键菜单 |

### 核心依赖

```xml
<TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
```

- 目标 Windows SDK: `10.0.22621.0` (Windows 11 SDK)
- 向下兼容 Windows 10 (Build 1809+)

---


## ⚠️ 注意事项 | Notes

- 🚧 **项目处于早期开发阶段**，功能与 API 可能随时调整
- 💻 需要 **Windows 10 (Build 1809+)** 或 **Windows 11** 以获得 Toast 通知支持
- 🔐 首次运行需要 **用户授权通知访问权限**
- 📌 当前已过滤微信通知
- 🐛 如遇到通知无法捕获，请检查：
  - 通知访问权限是否已授予
  - Windows 版本是否满足最低要求
  - 防病毒软件是否拦截了应用

---

## 🤝 贡献 | Contributing

欢迎提交 Issue 和 Pull Request！

1. Fork 本仓库
2. 创建你的特性分支 (`git checkout -b feature/AmazingFeature`)
3. 提交你的更改 (`git commit -m 'Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 打开一个 Pull Request

---

> ⭐ 如果这个项目对你有帮助，欢迎给它一个 Star！
