# Windows 消息通知器 | Windows Message Notifier

一个基于 **WPF (C#)** 的轻量级 Windows 桌面消息通知工具，用于显示系统或应用的自定义弹窗通知。

A lightweight Windows desktop message notifier built with **WPF (C#)**, designed to display custom popup notifications for system or application messages.

---

## 功能特性 | Features

| 中文 | English |
|------|---------|
| 🖥 **WPF 原生界面**，现代 XAML 布局，包含主窗口和资源管理 | 🖥 **WPF-based UI** with modern XAML layout, main window, and resource management |
| 🔔 **Toast 通知支持**，通过 `toast.cs` 处理 Windows 原生弹窗 | 🔔 **Toast notification support** via `toast.cs` for native Windows popups |
| 🧩 **值转换器**，内置 `NullToVisibilityConverter.cs` 简化数据绑定 | 🧩 **Value converter** (`NullToVisibilityConverter.cs`) for cleaner data binding in XAML |
| ⚙️ **标准项目结构**，基于 `.csproj` 配置，易于扩展 | ⚙️ **Standard project structure** based on `.csproj`, easy to extend |

---

## 部分截图 | Some screenshots
<img width="306" height="298" alt="QQ20260721-141755" src="https://github.com/user-attachments/assets/e0fa82b3-46a0-4a32-8161-189f7424db8f" />
<img width="678" height="168" alt="QQ20260721-141646" src="https://github.com/user-attachments/assets/ec782889-d8f8-4296-8b90-a03e4025276f" />


---

## 技术栈 | Tech Stack

- **语言 | Language**: C# 100%
- **框架 | Framework**: .NET / WPF
- **平台 | Platform**: Windows

---

## 注意事项 | Notes

- 项目处于早期开发阶段（截至 2026年7月21日 的初始提交）。
- 建议在 **Windows 10 / 11** 上运行以获得最佳 Toast 通知支持。

The project is in early development (initial commits as of July 21, 2026).  
**Windows 10/11** is recommended for optimal toast notification support.
