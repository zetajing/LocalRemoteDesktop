# 🖥 LocalRemoteDesktop v2 — 低延迟远程桌面

基于 **DXGI Desktop Duplication** 的远程桌面工具，运行在 Windows 系统托盘中。支持实时屏幕传输、鼠标键盘控制、剪贴板同步、文件传输。

## ✨ 特性

- **DXGI 屏幕捕获** — 使用 Windows Desktop Duplication API，延迟 **~8ms**（vs 旧版 GDI ~38ms），与显示器 **VSync 同步**
- **全帧 JPEG 传输** — 移除旧的 Tile 瓦片系统，直接全帧编码，高分辨率下带宽效率更优
- **零角色切换** — 每台电脑既是"被控端"也是"主控端"，右键托盘一键连接
- **文件传输** — 拖拽文件/文件夹到远程窗口即可发送
- **剪贴板同步** — 双向自动同步文本
- **全屏 / 窗口化** — F11 一键切换，Esc 退出
- **实时统计栏** — 显示 FPS、带宽、延迟、分辨率；F12 固定显示
- **开机自启** — 托盘菜单一键开关
- **连接历史** — 最近 20 条记录

## 性能对比（vs 旧版 GDI 方案）

| 项目 | 旧版 (GDI + Tile) | 新版 (DXGI + 全帧) |
|------|------------------|-------------------|
| 截屏延迟 | ~38ms | **~8ms** |
| 变化检测 | `GetPixel()` 逐像素（慢） | GPU 全帧自动检测 |
| 纹理拷贝 | CPU 内存 | GPU 显存 → CPU |
| VSync 同步 | 无（Timer 轮询） | **原生支持** |
| 帧率 | 不稳定的 30-50fps | 稳定的 60fps |
| 4K 支持 | 受限 | **完全支持** |

## 架构

```
被控端 (Server)                      控制端 (Client)
    │                                      │
    ├─ DXGICapture (GPU 截屏)              │
    │   └─ OutputDuplication               │
    ├─ ScreenCapture (变化检测 + JPEG)      │
    ├─ RemoteServer (TCP 发送) ◄──────────►├─ RemoteClient (TCP 接收)
    ├─ InputSimulator (输入注入) ◄─────────┤  (鼠标/键盘事件)
    ├─ 剪贴板同步 ◄──────────────────────►  │
    └─ 文件接收 ◄──────────────────────────┘  (文件传输)
```

## 使用方式

- **被控端** — 启动程序，自动后台监听端口 8932
- **控制端** — 右键托盘图标 → 连接远程电脑 → 输入 IP 地址
- **左键单击托盘图标**快速连接

### 快捷键（远程窗口内）

| 键 | 功能 |
|----|------|
| F11 | 全屏/窗口化 |
| Escape | 断开连接 |
| Ctrl+F | 发送文件 |
| F12 | 固定统计栏 |
| 拖拽文件 | 发送文件/文件夹 |

## 🔧 技术栈

| 层 | 技术 |
|---|------|
| UI | WPF (.NET Framework 4.6.2) |
| 屏幕捕获 | **DXGI Desktop Duplication** (via SharpDX) |
| 图像编码 | GDI+ JPEG 全帧 |
| 网络 | TCP (TcpClient / TcpListener)，二进制协议 |
| 输入模拟 | Win32 `SendInput` |

## 📦 协议帧格式

```
[类型 1 byte] [长度 4 byte LE] [数据体]
```

| 类型 | 值 | 方向 | 说明 |
|------|----|------|------|
| ScreenInfo | 0x20 | S→C | 屏幕分辨率 |
| TileImage | 0x21 | S→C | **全帧 JPEG 数据**（不再分 Tile） |
| TileEnd | 0x22 | S→C | 帧结束标记 |
| MouseMove | 0x02 | C→S | 归一化坐标 (0.0–1.0) |
| MouseLeft/RightDown/Up | 0x03-06 | C→S | 鼠标按钮 |
| MouseWheel | 0x07 | C→S | 滚轮 |
| KeyDown/Up | 0x08/09 | C→S | 虚拟键码 |
| FileRequest/Accept/Reject/Data/End | 0x10-14 | 双向 | 文件传输 |
| ClipboardText | 0x15 | 双向 | UTF8 剪贴板文本 |
| Heartbeat | 0xFF | 双向 | 延迟测量 |

## 构建

```powershell
# 需要 Visual Studio 2022 或 dotnet CLI + .NET Framework 4.6.2 SDK
dotnet restore
dotnet build -c Release
```

或直接在 Visual Studio 中打开 `LocalRemoteDesktop.sln` → 生成。

### NuGet 依赖

- `SharpDX` / `SharpDX.Direct3D11` / `SharpDX.DXGI` — DXGI Desktop Duplication
- `System.ValueTuple` — 元组支持

## 最低要求

- **操作系统**: Windows 8.1 以上（DXGI Desktop Duplication 需要）
- **运行时**: .NET Framework 4.6.2
- **显卡**: 任何支持 DirectX 11.0 的 GPU

## 项目结构

```
LocalRemoteDesktop/
├── App.xaml(.cs)              # 程序入口 + 系统托盘
├── ClientWindow.xaml(.cs)     # 远程桌面查看窗口
├── ServerRunner.cs            # 被控端控制器
├── Capture/
│   ├── DXGICapture.cs         # DXGI 桌面复制（核心升级）
│   └── ScreenCapture.cs       # 变化检测 + JPEG 编码
├── Input/
│   └── InputSimulator.cs      # 鼠标键盘模拟
├── Network/
│   ├── RemoteClient.cs        # TCP 客户端
│   └── RemoteServer.cs        # TCP 服务端
├── Models/
│   ├── ProtocolFrame.cs       # 二进制协议
│   └── FrameType.cs           # 帧类型枚举
└── Utils/
    ├── VirtualDisplayManager.cs  # 虚拟显示器驱动
    ├── ConnectionHistory.cs      # 连接历史
    └── ArgumentParser.cs         # 命令行参数
```

## 与 v1 的差异

- **新增**：DXGI Desktop Duplication 捕获引擎
- **移除**：Tile 瓦片系统（128x128 分块 + 逐块 JPEG + `GetPixel` 逐像素检测）
- **移除**：旧 `LauncherWindow` 选择器
- **改进**：全帧压缩传输，vsync 同步
- **改进**：`ServerRunner` 精简了帧调度逻辑

## 许可证

MIT
