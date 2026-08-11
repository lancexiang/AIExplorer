# AIExplorer

现代、跟手的 Windows 文件管理器 —— 用 **C# / .NET 8 + WinUI 3** 打造，强调多标签、多窗格与高速搜索体验。

<p align="center">
  <a href="https://github.com/lancexiang/AIExplorer/stargazers"><img src="https://img.shields.io/github/stars/lancexiang/AIExplorer?style=flat-square&logo=github" alt="Stars"></a>
  <a href="https://github.com/lancexiang/AIExplorer/network/members"><img src="https://img.shields.io/github/forks/lancexiang/AIExplorer?style=flat-square&logo=github" alt="Forks"></a>
  <a href="https://github.com/lancexiang/AIExplorer/watchers"><img src="https://img.shields.io/github/watchers/lancexiang/AIExplorer?style=flat-square&logo=github" alt="Watchers"></a>
  <a href="https://github.com/lancexiang/AIExplorer/issues"><img src="https://img.shields.io/github/issues/lancexiang/AIExplorer?style=flat-square" alt="Issues"></a>
  <a href="https://github.com/lancexiang/AIExplorer/pulls"><img src="https://img.shields.io/github/issues-pr/lancexiang/AIExplorer?style=flat-square" alt="Pull Requests"></a>
  <a href="https://github.com/lancexiang/AIExplorer/releases"><img src="https://img.shields.io/github/v/release/lancexiang/AIExplorer?style=flat-square&include_prereleases&label=release" alt="Release"></a>
  <a href="https://github.com/lancexiang/AIExplorer/blob/main/LICENSE"><img src="https://img.shields.io/github/license/lancexiang/AIExplorer?style=flat-square" alt="License"></a>
  <img src="https://img.shields.io/badge/.NET-8-512BD4?style=flat-square&logo=dotnet" alt=".NET 8">
  <img src="https://img.shields.io/badge/WinUI-3-0078D4?style=flat-square&logo=windows" alt="WinUI 3">
  <img src="https://img.shields.io/badge/platform-Windows%20x64-0078D4?style=flat-square&logo=windows11" alt="Windows x64">
</p>

---

## 产品特性

### 导航与工作区
- **浏览器式多标签**：新建 / 关闭 / 拖拽排序，标签可撕出到独立窗口
- **主页仪表盘**：常用文件夹、系统目录、本地/网络盘（含容量条）、最近访问
- **侧栏目录树**：常用、盘符懒加载、最近访问，可折叠
- **双窗格 / 多窗格**：左右或上下分栏，各窗格独立标签组，会话可恢复
- **Chrome 式顶栏**：收藏栏 + 面包屑地址栏（后退 / 前进 / 上级 / 刷新）

### 文件列表体验
- **详情 / 图标 / 卡片** 三种视图，`ListView` 虚拟化
- 可排序列（名称 / 大小 / 类型 / 修改时间），列宽与显隐可持久化
- 目录内实时筛选；详情行内展开子文件夹
- 内联重命名；复制 / 剪切 / 粘贴（冲突：替换 / 跳过 / 保留两者）
- 拖放反馈（「拖动 / 复制 / 移动 / 打开」）；快捷操作条（剪切、复制、置顶、属性等）
- **寿命徽章**（今天 / 昨天 / 本周 / …）与 Material 风格文件类型图标
- 按需计算文件夹大小；清理空文件夹；状态栏统计

### 搜索
- 顶栏搜索 → 独立搜索结果标签
- **Everything** 扩展：全盘 / 当前文件夹；不可用时自动降级为本地文件名搜索
- 搜索历史与建议

### 收藏与元数据
- 收藏无限嵌套分组、自定义显示名、拖拽归组、「打开分组下全部」
- 文件级元数据：置顶、颜色标记、备注（保存在 `%LocalAppData%\AIExplorer\`）
- 主题 / 强调色 / Mica·Acrylic、性能开关、扩展开关等设置持久化

### Shell 与终端
- 系统右键菜单叠加（`IContextMenu` / Vanara）
- Shell 图标；目录变更监视
- 底部可停靠 **ConPTY 多会话终端**（按工作目录复用 / 新建）

### 性能
- 后台线程增量枚举目录（先目录后文件）
- 列表虚拟化 + 按需加载图标
- 可选性能模式（降低 Probe / 缩略图等重操作）

---

## 技术栈

| 层级 | 技术 |
| --- | --- |
| 语言 / 运行时 | C# · **.NET 8**（`global.json` 钉版本）· Windows **x64** |
| UI | **WinUI 3** / Windows App SDK **1.6** · unpackaged（非 MSIX） |
| 架构 | `AIExplorer.App` → `AIExplorer.Infrastructure` → `AIExplorer.Core` |
| MVVM / DI | CommunityToolkit.Mvvm · Microsoft.Extensions.DependencyInjection |
| Shell | Vanara.PInvoke.Shell32 / Vanara.Windows.Shell |
| 搜索 | Everything IPC（`Everything64.dll`）+ 本地文件名降级 |
| 终端 | ConPTY + WebView2 渲染 |
| 安装器 | `AIExplorer.Setup`（WinForms 向导） |
| 测试 | xUnit |

```
src/
  AIExplorer.Core/             领域模型、契约、扩展接口
  AIExplorer.Infrastructure/   Shell、收藏、设置、Everything、ExtensionHost
  AIExplorer.App/              WinUI 3 界面
tests/
  AIExplorer.Core.Tests/
tools/
  install.ps1 / package.ps1    本机安装与分发包脚本
```

---

## 环境要求

1. **Windows 10 19041+ / Windows 11**（x64）
2. 开发：**Visual Studio 2022**（含 WinUI / Windows App SDK 工作负载）
3. **.NET 8 SDK**（建议与 `global.json` 一致）
4. 运行：**Windows App Runtime 1.6+**（`install.ps1` 依赖本机已装；`package.ps1` 会把 Runtime 安装器打进分发包）
5. 可选：[Everything](https://www.voidtools.com/)（启用全盘极速搜索）

---

## 快速开始

### 用 Visual Studio 运行（推荐）

1. 打开 `AIExplorer.sln`
2. 平台选 **x64**，启动项目设为 `AIExplorer.App`
3. **F5** 运行

> 纯 `dotnet build` 在部分环境会因缺少 VS 的 WinUI/MSIX 组件失败；优先用 VS / VS MSBuild。

### 一键安装到本机

```powershell
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```

默认安装到 `%LocalAppData%\Programs\AIExplorer`。

### 打分发包（拷到其它电脑）

按[微软未打包应用部署指南](https://learn.microsoft.com/windows/apps/windows-app-sdk/deploy-unpackaged-apps)：私有 .NET 8 Desktop + Windows App Runtime + Setup 向导。

```powershell
powershell -ExecutionPolicy Bypass -File tools\package.ps1 -RepoRoot .
```

产物示例：

- `artifacts\AIExplorer-win-x64\AIExplorer-Setup.exe`
- `artifacts\AIExplorer-Setup-win-x64.zip`

目标机：解压整个文件夹 → 双击 `AIExplorer-Setup.exe`。

---

## 配置与数据位置

用户数据在：

```
%LocalAppData%\AIExplorer\
  favorites.json
  settings.json
  file-metadata.json
```

不会上传到云端；开源仓库也不包含这些本地数据。

---

## 路线图（摘要）

- [x] 多标签 / 双窗格 / 主页 / 面包屑 / 收藏
- [x] Everything 搜索扩展与本地降级
- [x] 文件元数据（置顶 / 颜色 / 备注）与快捷操作条
- [x] unpackaged 安装与 Setup 分发
- [ ] 更完整的插件市场与扩展 API
- [ ] 更多视图与批量操作增强

欢迎通过 [Issues](https://github.com/lancexiang/AIExplorer/issues) / [Discussions](https://github.com/lancexiang/AIExplorer/discussions) 反馈。

---

## Star History

[![Star History Chart](https://api.star-history.com/svg?repos=lancexiang/AIExplorer&type=Date)](https://star-history.com/#lancexiang/AIExplorer&Date)

---

## 贡献

1. Fork 本仓库
2. 创建特性分支：`git checkout -b feature/your-idea`
3. 提交改动并推送
4. 发起 Pull Request

提交前请确认：不要带入本机截图、个人计划文档（`docs/superpowers`、`.planning` 等）或密钥文件。

---

## License

本项目采用 [MIT License](LICENSE)。
