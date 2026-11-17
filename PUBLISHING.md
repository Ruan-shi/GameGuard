# GitHub 发布步骤（照做即可，约 15 分钟）

## 前置条件

- 一个 GitHub 账号（没有就在 github.com 注册）
- 先在本地跑通 `build.bat`，确认能编译出 `GameGuard.exe`

## 第 1 步：创建仓库

1. 打开 github.com → 右上角 `+` → **New repository**
2. Repository name：`GameGuard`
3. Description：`游戏时长守卫 - 轻量 Windows 托盘工具，帮你说到做到地控制游戏时间`
4. 选择 **Public**，**不要**勾选任何 Initialize 选项（README 我们本地已有）
5. 点击 **Create repository**

## 第 2 步：本地初始化并推送

在 `C:\GameGuard` 目录打开命令行，逐行执行（把 `<你的用户名>` 换掉）：

```bat
git init
git add README.md LICENSE .gitignore GameGuardApp.cs build.bat make-release.bat GameGuard.ps1 install.bat uninstall.bat pause.bat resume.bat GameGuard.vbs guard.ico docs
git commit -m "GameGuard v1.0.0 首个发布版本"
git branch -M main
git remote add origin https://github.com/<你的用户名>/GameGuard.git
git push -u origin main
```

## 第 3 步：创建 Release（发布可下载的 zip）

1. 本地先运行 `make-release.bat`，会生成 `dist\GameGuard-1.0.0.zip`
2. GitHub 仓库页右侧 → **Releases** → **Create a new release**
3. Tag version 填 `v1.0.0`，标题填 `GameGuard v1.0.0`
4. 描述里粘贴版本要点（首次发布写功能列表即可）
5. 把 `dist\GameGuard-1.0.0.zip` 拖进附件区
6. 点 **Publish release**

## 第 4 步：补一张控制面板截图（强烈建议）

README 引用了两张截图，需要放进 `docs/screenshots/`：

- `panel.png` —— 打开控制面板截图
- `guard.png` —— 60 分钟提醒弹窗截图（可运行 `GameGuard.ps1 -SelfTest` 生成）

截图后提交推送：

```bat
git add docs
git commit -m "docs: 添加界面截图"
git push
```

## 后续版本

改完代码 → 在 `GameGuardApp.cs` 顶部把版本号改掉（如 1.0.1）→ 提交推送 → `make-release.bat` → GitHub 上新建 Release（tag v1.0.1）→ 传新 zip。

## 可选加分项

- 给仓库加 Topics：`windows` `parental-control` `game-time` `csharp` `winforms`
- 开 Issue 区，让用户报问题
- 首次发布后把链接发给我，我帮你检查 README 渲染效果
