# 🖥 LocalRemoteDesktop

局域网远程桌面 — 对等直连、极速流畅。

## ✨ 特性

- **零角色切换** — 启动即后台运行，每台电脑既是"被控端"也是"主控端"。右键托盘一键连接对方，双方可互相控制。
- **Tile 化增量传输** — 屏幕切为 128×128 方块，只传变化区域，大幅降低带宽和延迟，接近 RDP 体验。
- **文件传输** — 拖拽文件/文件夹到远程窗口即可发送，支持 Ctrl+F 选择文件。
- **剪贴板同步** — 双向自动同步文本剪贴板。
- **全屏 / 窗口化** — F11 一键切换，Esc 退出远程窗口。
- **帧率统计栏** — 实时显示 FPS、带宽、延迟、分辨率；F12 固定显示。
- **开机自启** — 托盘菜单一键开关，写入注册表 `HKCU\...\Run`。
- **连接历史** — 最近 20 条记录，托盘菜单直接点击连接。
- **动态图标** — 蓝色渐变 "LR" 图标，托盘清晰可见。
- **纯绿色** — 所有数据（连接记录）保存在 exe 同级目录，不留 C 盘痕迹。

## 🔧 技术栈

| 层 | 技术 |
|---|---|
| UI | WPF (.NET Framework 4.6.2) |
| 屏幕捕获 | GDI+ `CopyFromScreen`，Tile 化脏检测 |
| 图像编码 | GDI+ JPEG，128×128 分块 |
| 网络 | TCP（`TcpClient` / `TcpListener`），二进制协议帧 |
| 输入模拟 | `SendInput`（鼠标移动 / 按键 / 滚轮） |

## 📦 协议帧格式

```
[类型 1 byte] [长度 4 byte LE] [数据体]
```

| 类型 | 值 | 方向 | 说明 |
|---|---|---|---|
| ScreenInfo | 0x20 | S→C | 屏幕分辨率（初始化画布） |
| TileImage | 0x21 | S→C | 方块画面 `[2b X][2b Y][2b W][2b H][JPEG]` |
| TileEnd | 0x22 | S→C | 一帧方块发送完毕 |
| MouseMove | 0x02 | C→S | 归一化坐标 (0.0–1.0) |
| KeyDown/Up | 0x08/09 | C→S | 虚拟键码 |
| ClipboardText | 0x15 | 双向 | UTF8 文本 |
| FileRequest/Data/End | 0x10+ | C→S | 文件传输 |
| Heartbeat | 0xFF | 双向 | 延迟测量 |

## 🚀 使用方式

### 启动
双击 `LocalRemoteDesktop.exe`，自动启动后台监听（端口默认 8932），系统托盘出现蓝色 "LR" 图标。

### 连接远程电脑
- **右键托盘** → "🔗 连接远程电脑..." → 输入对方 IP
- 或直接点击**最近连接**列表中的记录
- **左键单击托盘图标**也可快速连接

### 快捷键（远程窗口内）
| 键 | 功能 |
|---|---|
| F11 | 全屏 / 窗口化切换 |
| Esc | 退出远程窗口 |
| Ctrl+F | 发送文件 |
| F12 | 固定统计栏 |
| 拖拽文件 | 发送文件/文件夹 |

### 命令行
```
LocalRemoteDesktop.exe --autostart   # 开机自启模式（静默启动，不弹气泡）
```

## 🛠 编译

```bash
# 需要 Visual Studio 2022 或 MSBuild
msbuild LocalRemoteDesktop.sln /p:Configuration=Release
```

或直接在 Visual Studio 中打开 `LocalRemoteDesktop.sln` → 生成。

## 📄 许可

MIT
