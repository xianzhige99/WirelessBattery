# 发布到 GitHub：从零操作手册

> 面向第一次发布仓库的人。每一步都写了"点哪里、看到什么、出问题怎么办"。
> 仓库地址：https://github.com/xianzhige99/WirelessBattery

---

## 0. 现在这台电脑上已经准备好的东西

| 项目 | 状态 |
|---|---|
| 本地 git 仓库 | 已有，分支 `main`，提交历史完整 |
| 提交者身份 | 已配置（`Xianzhige99` / `bestxzh@gmail.com`） |
| 待发布文件 | 见 `git ls-files`，共 50 个；无 `settings.ini`、无 `bin/`、无临时文件 |
| 敏感信息 | 已清理：删掉了指向别人电脑的旧快捷方式，仓库内无绝对路径/用户名 |
| 许可 | `LICENSE`（MIT） |
| CI | `.github/workflows/build.yml`（每次 push 自动编译 + 跑自检） |
| 发布包脚本 | `tools/pack.ps1` → `dist/WirelessBattery-1.4.0.0-win64.zip` |

你只需要做下面 5 步。

---

## 1. 在 GitHub 上创建空仓库

1. 浏览器打开 https://github.com/new
2. **Repository name** 填 `WirelessBattery`（必须和你要用的地址一致，大小写不敏感但建议一致）
3. **Description** 可以填：`Windows 外设电量托盘工具：MCHOSE / 西伯利亚 K02BS / 罗技 HID++`
4. 选 **Public**（想私有就 Private，但别人看不到）
5. **下面三个勾选项全部不要勾**（Add a README file / Add .gitignore / Choose a license）——
   你本地已经有这些文件，勾了会造成"两边都有内容"，第一次推送会冲突
6. 点 **Create repository**

创建完页面会显示一段命令，忽略它，直接做第 2 步。

> 如果你之前已经建过这个仓库，且里面是空的（只有 GitHub 自动生成的一行提示），也没问题；
> 只要**没有** README / .gitignore / LICENSE 文件即可。

---

## 2. 关联远程仓库（一次就够）

打开 **Git Bash**（开始菜单搜 "Git Bash"），粘贴：

```bash
cd "/c/Users/ZX/WorkBuddy/无线设备电量"
git remote add origin https://github.com/xianzhige99/WirelessBattery.git
git remote -v
```

最后一行应该看到两行 `origin  https://github.com/xianzhige99/WirelessBattery.git (fetch/push)`。

> 如果提示 `remote origin already exists`，说明已经加过了，跳过这句即可。

---

## 3. 推送代码

```bash
cd "/c/Users/ZX/WorkBuddy/无线设备电量"
git push -u origin main
```

**第一次推送会弹窗要授权**，按下面任一方式做：

- **方式 A（推荐，最省事）**：弹窗里选 `Sign in with your browser` →
  浏览器登录 GitHub → 点 `Authorize Git Credential Manager` → 回到命令行，推送继续。
  凭据会存进 Windows 凭据管理器，以后不用再登。
- **方式 B（用 Token）**：GitHub 头像 → Settings → Developer settings →
  Personal access tokens → Tokens (classic) → Generate new token →
  勾 `repo` → 生成后复制那串 `ghp_...`，
  在弹窗里 **Username** 填 `xianzhige99`、**Password** 粘贴 token（不是账号密码）。

**看到什么算成功**：最后几行类似

```
 * [new branch]      main -> main
branch 'main' set up to track 'origin/main'.
```

刷新仓库页面，应该能看到 50 个文件和 README。

### 推送失败怎么办

| 报错 | 原因 | 处理 |
|---|---|---|
| `Authentication failed` | 用了账号密码 | 改用上面的 Token 或浏览器登录 |
| `remote: Repository not found` | 仓库名拼错 / 没建 / 无权访问 | 核对 `git remote -v` 的地址 |
| `Updates were rejected` | 远程已有提交（建仓时勾了 README） | 见下方"远程已有内容" |
| `failed to push some refs` + `non-fast-forward` | 同上 | 同上 |
| 卡在 `Writing objects` 很久 | 网络 | 稍等；或 `git config --global http.postBuffer 524288000` 后重试 |

**远程已有内容**（建仓时勾了 README）二选一：

```bash
# 方案一：把远程那个初始提交合并进来（保留双方）
git pull --rebase origin main
git push -u origin main

# 方案二：强行用本地覆盖（远程只有一行自动生成的 README，可直接覆盖）
git push -u origin main --force
```

---

## 4. 打版本标签（让 Release 有版本号）

```bash
cd "/c/Users/ZX/WorkBuddy/无线设备电量"
git tag -a v1.4.0 -m "1.4.0：K02BS 连接判定修复、阶梯设置页重排、关机暂停计时"
git push origin v1.4.0
```

标签推上去后，仓库页面的 **Releases** 里会出现 `v1.4.0` 草稿入口。

---

## 5. 发布 Release（用户下载的地方）

1. 先生成发布包（在 Git Bash 里）：

```bash
cd "/c/Users/ZX/WorkBuddy/无线设备电量"
```
然后用 PowerShell 执行（不要用 Git Bash）：
```
tools\pack.ps1
```
产物在 `dist\WirelessBattery-1.4.0.0-win64.zip`（约 200 KB，含 exe、两个 bat、使用说明、示例配置、构建信息）。

2. 浏览器打开 https://github.com/xianzhige99/WirelessBattery/releases/new
3. **Choose a tag** → 选 `v1.4.0`（刚推的）
4. **Release title** 填 `v1.4.0`
5. **Describe this release** 可以整段复制 `CHANGELOG.md` 里 `## 1.4.0` 那一节
6. 把 `WirelessBattery-1.4.0.0-win64.zip` **拖进** "Attach binaries" 区域，等上传完成
7. 点 **Publish release**

> exe 不进仓库是故意的：仓库只放源码（干净、可 review），二进制走 Release 附件。
> 这样 `git clone` 只有 800 KB 左右。

---

## 6. 仓库页面的几个小设置（建议）

| 位置 | 设置 |
|---|---|
| 仓库首页右侧 **About** ⚙ | Description 填一句话；Website 留空；**Topics** 加：`windows` `tray-icon` `hid` `battery` `bluetooth` `dotnet-framework` `winforms` |
| **Settings → General → Default branch** | 确认是 `main` |
| **Actions** 标签页 | 第一次 push 会自动跑 `build` 工作流；如果提示要授权，点 `I understand my workflows, go ahead and enable them` |
| **Settings → Code security** | 可开启 Dependabot alerts（本项目无依赖，基本用不上） |

---

## 7. 日常改动怎么发（以后用）

```bash
cd "/c/Users/ZX/WorkBuddy/无线设备电量"
git status                 # 看改了哪些文件
git add -A                 # 全部加入暂存
git commit -m "fix: 一句话说明"
git push                   # 推到 main
```

改了界面/行为要顺带更新 `CHANGELOG.md` 和 `README.md`——这是本项目一直以来的规矩
（见 `docs/02-决策记录.md` 的 D12）。

---

## 8. 最容易踩的 5 个坑

1. **把 `settings.ini` 提交上去**：里面有你的设备名和电量历史。`.gitignore` 已经排除，
   但如果曾经 `git add -f` 过，需要 `git rm --cached bin/settings.ini`。
2. **把 `bin/` 提交上去**：`.gitignore` 已忽略整个目录。构建产物请走 Release。
3. **中文文件名在网页上显示成 `\344\275\277...`**：这是 `git ls-files` 的转义显示，网页上是正常的。
   想让命令行也正常：`git config --global core.quotepath false`。
4. **满屏 `LF will be replaced by CRLF` 警告**：Windows 换行提示，不影响使用。
   `.gitattributes` 已固定策略，忽略即可。
5. **改了文件但网页没更新**：先 `git push` 成功再刷新；GitHub 有几十秒缓存。

---

## 9. 回滚（万一推错了）

```bash
git log --oneline            # 找到要回的提交
git revert <提交号>           # 生成一个"反向提交"，安全，不破坏历史
git push
```

不要用 `git reset --hard` + `push --force` 去改已经公开的历史——除非你确定没有别人在用。

---

## 10. 检查清单（发布前逐条打勾）

- [ ] `git status` 干净（没有未提交改动）
- [ ] `build.ps1 -Clean -Strict` 输出 `Build OK` 且零警告
- [ ] `tests/run-tests.ps1` 输出 `ALL PASS`
- [ ] `git ls-files | grep -i settings.ini` 为空
- [ ] `git ls-files | grep "^bin/"` 为空
- [ ] 仓库页面能看到 `LICENSE`、`README.md`、`CHANGELOG.md`
- [ ] Release 里 zip 能下载、解压后双击 `解除锁定并启动.bat` 能跑起来
