# NoHeartburn —— 作业润色提醒

托盘常驻小工具：当你**双击**或**拖拽**一个文件名里带你学号/姓名的 doc/docx/pdf 时，
通过 Win11 系统通知提醒你这份作业是否已经润色过，防止忘了润色就交作业。

## 功能

- **双击 / 拖拽检测**：全局鼠标钩子 + Shell COM / UI Automation 读取资源管理器（及桌面）中当前操作的文件
- **关键词匹配**：文件名含学号 / 姓名 / 自定义别名时触发提醒
- **润色记录**：本地 `polished.json` 按文件内容 SHA-256 记录，改文件名也能认出来
- **Win11 系统通知**：未润色 → 带「标记为已润色」按钮的 toast（协议激活回调）；已润色 → 静默通知
- **防打扰**：同一文件冷却时间（默认 15 分钟）、单一样式（toast / 程序弹窗二选一）、暂停开关
- **低占用**：单文件 exe，托盘常驻，内存 ~20 MB，空闲 CPU 为 0

## 快速开始

1. 克隆本仓库（或从 Releases 下载 exe）
2. `copy config.example.json config.json`，填入你的学号 / 姓名
3. 运行 `build.bat`（仅在从源码构建时需要；Releases 的 exe 可直接用）
4. 双击 exe，托盘出现图标即运行中

## 配置（config.json）

| 字段 | 说明 |
|---|---|
| `StudentId` / `StudentName` | 触发提醒的关键词（文件名包含即触发），必填 |
| `Aliases` | 额外关键词，如英文名、常用文件名片段 |
| `NotificationStyle` | `toast`（Win11 系统通知，默认）或 `popup`（程序弹窗），二选一 |
| `DoubleClickDetect` / `DragDetect` | 分别开关双击 / 拖拽检测 |
| `RemindWhenPolished` | 已润色的文件是否仍弹通知 |
| `CooldownMinutes` | 同一文件两次提醒的最小间隔 |
| `DebugLog` | 写 `log.txt` 排查日志 |

配置改动即时生效，无需重启。

## 实现说明

- **双击检测**：低级鼠标钩子收不到系统合成的 `WM_LBUTTONDBLCLK`，程序在钩子内自行判断
  （同位置、系统双击时限内连续两次按下）
- **通知按钮回调**：toast 的 `<action>` 用 `activationType="protocol"` +
  自定义 URI 协议（`noheartburn://`）回传，无需 COM 激活器（LocalServer32 /
  INotificationActivationCallback）；协议在 HKCU 注册，无需管理员权限
- **通知来源**：启动时在开始菜单创建带 AppUserModelID 的快捷方式
  （内存构建 ShellLink → QI IPropertyStore 写 AUMID → IPersistFile.Save 一次性落盘）

## 从源码构建

```
build.bat
```

使用系统自带 .NET Framework 4.x 的 `csc.exe`，无需安装 Visual Studio / SDK；
任何 Windows 10/11 机器可直接构建（脚本内引用路径均为系统标准路径）。

## 已知限制

- 往浏览器网页上传框拖拽时，程序通过「拖拽起点 + 放下动作」推断文件，
  不能保证 100% 命中（Windows 没有全局 API 直接知道你往浏览器拖了哪个文件）
- 通知按钮点击第一次可能触发系统的自定义协议确认框，勾选「始终允许」后不再出现



## License

见 LICENSE。
