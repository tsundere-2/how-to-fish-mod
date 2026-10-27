# How to Fish — 本地 Mod 使用说明

《How to Fish》的 BepInEx 插件合集。仓库里同时提供**已编译好的 DLL**（直接丢进游戏就能用）和**完整源码**（想自己改数值/加功能时用）。

- 游戏：How to Fish（Unity 6000.4.4f1，**Mono** 运行时，64 位）
- 加载器：**BepInEx 5.4.23.5 x64**
- 补丁框架：Harmony 2（BepInEx 5 自带）
- 插件之间互相独立，可以只装其中几个

> ⚠️ 仅用于**单人 / 本地主机**模式。联机时改数值可能被服务端覆盖或影响他人，请自行判断。

---

## 快速开始

1. 装 **BepInEx 5.4.23.5 x64**（见第 1 节），先启动一次游戏再退出。
2. 把 `aimlock.dll` / `boxinginjury.dll` / `flymod.dll` / `unrivalled.dll` / `modcontrol.dll`
   （以及可选的 `maxmoney.dll`）全部丢进 `How to Fish\BepInEx\plugins\`。
3. 重启游戏 → 看 `BepInEx\LogOutput.log` 里出现 `Loading [FlyMod 1.0.0]` 之类的行就成功了。
4. 进游戏后左上角出现 **Mod 控制中心** 的小方框，点它（或按 **F1**）展开，所有开关和数值都能直接调。

> 想改数值或加功能 → 看第 3 节从源码编译；只想要效果 → 第 2 节就够了，**不需要装 .NET SDK**。

---

## 目录

1. [前置：安装 BepInEx](#1-前置安装-bepinex)
2. [方式一：直接用现成 DLL（推荐，不用编译）](#2-方式一直接用现成-dll推荐不用编译)
3. [方式二：从源码编译](#3-方式二从源码编译)
4. [Mod 清单与热键](#4-mod-清单与热键)
5. [配置文件怎么改](#5-配置文件怎么改)
6. [验证是否加载成功](#6-验证是否加载成功)
7. [常见问题](#7-常见问题)
8. [卸载](#8-卸载)
9. [仓库结构](#9-仓库结构)

---

## 1. 前置：安装 BepInEx

如果游戏目录下**已经**有下面两个东西，说明 BepInEx 装好了，可以跳到第 2 步：

```
How to Fish\
├─ winhttp.dll
└─ BepInEx\
   ├─ core\BepInEx.dll
   └─ plugins\
```

没有的话：

1. 去 [BepInEx Releases](https://github.com/BepInEx/BepInEx/releases) 下载 **5.4.23.5** 的 **x64** 包
   （文件名类似 `BepInEx_win_x64_5.4.23.5.zip`）。
   **不要**下 6.x 的 BleedingEdge，也**不要**下 IL2CPP 版——本游戏是 Mono。

   直接下载链接：
   `https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip`

2. 找到游戏目录：
   Steam 库 → 右键 **How to Fish** → **管理** → **浏览本地文件**
   （就是有 `How to Fish.exe` 的那一层）。

3. 把压缩包里的 `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`BepInEx\` 全部解压进游戏目录，和 `How to Fish.exe` 放在同一层。
   > 这几样必须齐全，少一个都会导致注入失败。

4. **先启动一次游戏**，等进到主菜单后退出。
   这一步会生成 `BepInEx\config\` 和 `BepInEx\LogOutput.log`，说明加载器正常工作了。

---

## 2. 方式一：直接用现成 DLL（推荐，不用编译）

仓库里每个 Mod 目录下都有一个编译好的 `.dll`：

| 文件 | 放到哪里 |
|---|---|
| `aimlock/aimlock.dll` | `BepInEx\plugins\` |
| `boxinginjury/boxinginjury.dll` | `BepInEx\plugins\` |
| `flymod/flymod.dll` | `BepInEx\plugins\` |
| `unrivalled/unrivalled.dll` | `BepInEx\plugins\` |
| `modcontrol/modcontrol.dll` | `BepInEx\plugins\` |
| `moneychange/maxmoney.dll` | `BepInEx\plugins\` |

操作步骤：

1. 在 GitHub 上打开对应目录 → 点 `xxx.dll` → 右上角 **Download raw file**（或 **Raw** → 右键另存为）。
2. 把下载到的 `.dll` 复制到 **`BepInEx\plugins\`** 里。
   > 直接平铺在 `plugins\` 下就行，不用建子文件夹（建了也能识别，BepInEx 会递归扫描）。
3. 有几个就放几个，**互不依赖**，只放 `flymod.dll` 也完全没问题。
4. **完全退出游戏再重开**（BepInEx 只在启动时扫描插件，游戏内无法热加载）。

最终大概长这样：

```
How to Fish\BepInEx\plugins\
├─ aimlock.dll
├─ boxinginjury.dll
├─ flymod.dll
├─ modcontrol.dll
├─ unrivalled.dll
└─ maxmoney.dll        ← 金钱 Mod（见下方说明）
```

### 关于 `moneychange\`（金钱 Mod）

`moneychange\` 和另外几个 Mod 结构完全一致：**编译好的 DLL + 源码**都在里面。

```
moneychange\
├─ maxmoney.dll            ← 编译好的成品，直接用这个
├─ README.md               ← 这个 Mod 的详细说明
└─ moneychange_src\
   ├─ moneychange.csproj
   └─ Money.cs
```

**用法**：把 `moneychange\maxmoney.dll` 复制到 `BepInEx\plugins\`，
进游戏按 **小键盘 1** → 美元和欧元设为 **9999999**。
数值和按键都能在 `BepInEx\config\me.yexiao.fish.finalmod.cfg` 里改。

**自己编译**：

```powershell
cd moneychange\moneychange_src
dotnet build -c Release
# → bin\Release\maxmoney.dll
```

> ⚠️ 如果你手上已有旧版 `maxmoney.dll`（3.0.0 / 3.1.0），它和仓库里这个是**同一个 GUID**
> （`me.yexiao.fish.finalmod`），**不能同时放**，删掉旧的再用新的。

> 📌 3.2.0 相比旧版修了一个实际问题：旧版依赖游戏的 `OnChangeMoney` 刷新 UI，
> 但那个回调在**单人/主机模式**下 `asServer == true` 会被直接跳过，导致数字不变。
> 新版会额外调一次 `PlayerUI.SetMoney` 补上。细节见 [`moneychange/README.md`](moneychange/README.md)。

---

## 3. 方式二：从源码编译

### 3.1 装工具

- 安装 [.NET SDK](https://dotnet.microsoft.com/download)（8 或 9 都行，只要支持 `netstandard2.1`）。
- 命令行验证：

```powershell
dotnet --version
```

### 3.2 ⚠️ 必改：`GameDir`

每个 `*.csproj` 里都硬编码了我本机的游戏路径：

```xml
<GameDir>D:\SteamLibrary\steamapps\common\How to Fish\How to Fish</GameDir>
```

**你必须把它改成自己的游戏目录**，否则编译时会找不到 `Assembly-CSharp.dll`。

改法（两种都行）：

**方法 A**：直接编辑 `csproj`，把 `<GameDir>` 那一行换成你的路径。

**方法 B**：编译时用参数覆盖，不用改文件：

```powershell
dotnet build -c Release -p:GameDir="E:\Steam\steamapps\common\How to Fish\How to Fish"
```

> 路径里有空格是正常的，PowerShell 里用双引号包住即可。

### 3.3 编译

```powershell
cd aimlock\aimlock_src
dotnet build -c Release
```

产物在 `bin\Release\aimlock.dll`，复制到 `BepInEx\plugins\` 即可。

其余 Mod 同理：

```powershell
cd boxinginjury\boxinginjury_src ; dotnet build -c Release
cd unrivalled\unrivalled_src    ; dotnet build -c Release
cd flymod\flymod_src            ; dotnet build -c Release
cd modcontrol\modcontrol_src    ; dotnet build -c Release   # 产物 modcontrol.dll
cd moneychange\moneychange_src  ; dotnet build -c Release   # 产物 maxmoney.dll
```

> `modcontrol` 的 csproj 带 `CopyToPlugins` 目标：编译成功后会自动把 `modcontrol.dll`
> 复制到 `$(GameDir)\BepInEx\plugins\`，不用手动再拷一次。

### 3.4 `flymod` 的工程文件

`flymod/flymod_src/` 里已经放好 `flymod.csproj`（和另外几个 Mod 的配置完全一致），
**只要把 `GameDir` 改成你的游戏路径就能直接编译**：

```powershell
cd flymod\flymod_src
dotnet build -c Release
```

如果编译时报 `<GameDir>` 相关的找不到文件错误，说明路径没改，见 3.2。

### 3.5 `moneychange` 编译

`moneychange\moneychange_src\` 里已经放好 `moneychange.csproj` 和源码，直接编译即可
（和 3.4 完全同一个模板）：

```powershell
cd moneychange\moneychange_src
dotnet build -c Release
```

产物 `bin\Release\maxmoney.dll` 丢进 `plugins\`。
详细说明（含原理和单人模式的 UI 刷新问题）见 [`moneychange/README.md`](moneychange/README.md)。

> 源码里用的 `Object.FindAnyObjectByType<T>()` 需要较新的 Unity，本作是 6000.4，没问题。
> 想改数值不用改代码：编译后直接改 `BepInEx\config\me.yexiao.fish.finalmod.cfg` 里的 `货币数值`。

---

## 4. Mod 清单与热键

**所有按键都可以在配置文件里改**（见第 5 节）。下表是默认值。

### 4.1 FlyMod — 飞行 / 悬浮（`flymod.dll`）

让角色起飞、悬停，速度可实时调节。

| 按键 | 功能 |
|---|---|
| **小键盘 0** | 开 / 关 飞行·悬浮 |
| **小键盘 \*** | 加速 +5 m/s |
| **小键盘 /** | 减速 −5 m/s |
| **小键盘 =** | 速度重置为 20 m/s |
| **空格**（按住） | 上升 |
| **左 Ctrl**（按住） | 下降 |
| **左 Shift**（按住） | 临时加速 ×2 |

操作要点：

- **朝哪看，按 W 就往哪飞**（水平方向用相机朝向，抬头也能往上飞）。
- **松开 WASD = 原地悬停**，不会掉下去（飞行时会关闭重力）。
- 不按上升/下降 = 高度不变。
- 默认速度 20 m/s，范围 1 ~ 200 m/s。

> 空格/左Ctrl/左Shift 原本是跳跃/蹲下/冲刺。飞行时它们被用作上升/下降/加速，
> 觉得冲突就在配置里改成小键盘的键（比如 `上升热键 = Keypad8`）。

### 4.2 FishAimLock — 瞄准锁定 / 强制爆头（`aimlock.dll`）

| 按键 | 功能 |
|---|---|
| **小键盘 2** | 开 / 关 锁头功能 |
| **小键盘 3** | 诊断：把识别到的生物、HeadPos、包围盒、瞄点打印到日志 |
| **小键盘 4** | 解除当前锁定，重新识别 |
| **小键盘 8 / 5** | 当前目标瞄点上移 / 下移（自动保存为该生物专属瞄点） |
| **小键盘 9 / 7** | 当前目标瞄点前移 / 后移 |
| **小键盘 6** | 清除当前生物的专属瞄点偏移 |
| **鼠标中键** | 可选：只有把 `需要按住触发键` 改成 `true` 后才生效（默认关） |

- 默认 **仅在开镜（ADS）时锁定**（`仅在开镜时锁定 = true`）。
- **强制爆头**默认开启：任何远程武器命中生物都按爆头结算（×1.5），且**不受开镜限制**。
- 打飞行 BOSS（信天翁）锁不稳时，先按 **小键盘 3** 看诊断，再用 8/5/9/7 微调。

### 4.3 BoxingInjury — 拳击伤害改版（`boxinginjury.dll`）

| 按键 | 功能 |
|---|---|
| **小键盘 小数点** | 开 / 关 拳击伤害改版 |

空手出拳固定造成 **99999** 伤害（可在配置里改 `拳头伤害`）。

### 4.4 Unrivalled — 无双 / 无敌（`unrivalled.dll`）

| 按键 | 功能 |
|---|---|
| **小键盘 +** | 开 / 关 无双·无敌 |

开启后本地玩家**受到任何伤害都不掉血**（攻击、BOSS、中毒、着火、饥饿全部免疫）。
另有可选开关 `保持满血`（默认关）。

### 4.5 FishUnlimitedMoneyFinal — 金钱（`maxmoney.dll`）

DLL 就在 [`moneychange/maxmoney.dll`](moneychange/maxmoney.dll)，直接下载放进 `plugins\` 即可。

| 按键 | 功能 |
|---|---|
| **小键盘 1** | 金钱和欧元直接设为 **9999999** |

> 数值和按键都可以在 `BepInEx\config\me.yexiao.fish.finalmod.cfg` 里改（`货币数值` / `一键补满货币按键`）。
> 和旧版 `maxmoney.dll`（3.0.0 / 3.1.0）是同一个 GUID，**不能同时放**，删掉旧的再用新的。

### 4.6 ModControlCenter — 游戏内控制面板（`modcontrol.dll`）

进游戏后左上角就是一个**小方框**（状态点 · 已启用数量 · 展开按钮），点它或按 **F1** 会以这个小方框的
左上角为原点展开「液态玻璃」风格的完整面板，所有 Mod 的开关和数值都能在游戏里直接调，
改完自动写回对应的 `.cfg`，不用退出游戏改文件。

| 按键 / 操作 | 功能 |
|---|---|
| **F1** | 小方框 ⇄ 完整面板 |
| **点小方框右侧按钮** | 展开 / 收起面板 |
| **拖动面板** | 移动窗口（自动夹在屏幕内） |
| **滚轮 / 列表内拖动** | 上下翻动 Mod 列表 |
| **点行标题** | 展开该 Mod 的详细选项（窗口高度跟着变） |
| **点右侧开关** | 实时开 / 关（轨道颜色即时切换） |
| **拖动滑条** | 调数值（飞行速度、拳头伤害） |
| **↑ ↓ / ← → / Enter** | 键盘逐行选中 · 收起/展开 · 开关 |

面板里能控制：

| 条目 | 可调项 |
|---|---|
| 自动瞄准 | 总开关 · 锁鱼 / 锁鸟 / 锁 Boss / 锁其他 · 强制爆头 |
| 飞行模式 | 总开关 · 飞行速度（5 ~ 60 m/s） |
| 拳击伤害改版 | 总开关 · 拳头伤害（100 ~ 99999） |
| 无双 · 无敌 | 总开关 · 保持满血 |
| 金钱拉满 | 插件本体开关（本 Mod 没有配置文件） |

- 面板是**自动发现**的：任何插件里带 `启用` 的布尔配置都会生成一行，所以以后加了新 Mod 也会出现。
- 标题栏左上角的圆点 + `N 已启用` 显示当前开启数量；收起成小方框时只显示这个数字。
- 界面完全由代码生成（SDF 烘焙圆角/渐变/图标贴图 + 系统字体），不依赖任何额外资源文件。
- 玻璃质感是「深色底 + 薄白高光」双层叠加，压住背后水面杂色，白字对比清楚。
- 行列表是可滚动区域：多个条目同时展开也不会把窗口撑爆，滚轮上下翻；窗口高度随内容自动变化（封顶 620）。
- 关闭面板后热键仍然有效，随时按 **F1** 再叫出来。

> 面板本身的三个设置也在配置里：`BepInEx\config\me.yexiao07.plugin.modcontrol.cfg`
> → `[界面] 显示/隐藏热键`（默认 `F1`）、`打开时解锁鼠标`（默认 `true`）、`启动时显示悬浮框`（默认 `true`）。

实现原理（自动发现、双向同步、贴图烘焙）见 [`modcontrol/README.md`](modcontrol/README.md)。

### 4.7 按键冲突检查表

默认配置下不冲突，全部按键占用情况：

| 按键 | 归属 |
|---|---|
| F1 | ModControlCenter 面板开关 |
| 小键盘 0 | FlyMod 开关 |
| 小键盘 1 | 金钱 |
| 小键盘 2 / 3 / 4 / 5 / 6 / 7 / 8 / 9 | FishAimLock |
| 小键盘 \* / / / = | FlyMod 调速 |
| 小键盘 + | Unrivalled |
| 小键盘 小数点 | BoxingInjury |
| 空格 / 左Ctrl / 左Shift | FlyMod 上升/下降/加速（与游戏原按键重叠） |

如果某个键没反应，多半是被别的 Mod 或游戏本体占了，去对应 `.cfg` 里换个键即可。

---

## 5. 配置文件怎么改

第一次成功加载某个 Mod 后，会自动在下面生成配置：

```
BepInEx\config\
├─ me.yexiao07.plugin.fishaimlock.cfg
├─ me.yexiao07.plugin.boxinginjury.cfg
├─ me.yexiao07.plugin.flymod.cfg
├─ me.yexiao07.plugin.unrivalled.cfg
├─ me.yexiao07.plugin.modcontrol.cfg
└─ me.yexiao.fish.finalmod.cfg        （金钱 Mod，自己编译的才有）
```

（每个文件在对应 Mod **第一次成功加载后**才会生成。）

> ModControlCenter 的开关改动会**自动写回**这些 cfg；但面板只覆盖它认识的条目，
> 想看全部参数（例如 FishAimLock 的瞄点微调、识别参数）还是直接改文件更全。

改之前建议先**备份**一份 cfg；改坏了删掉它，下次启动会重新生成默认值。

- 用记事本 / VS Code 打开，改 `键 = 值` 后面的部分。
- **改完必须重启游戏**（配置只在启动时读取）。
- 键位用 Unity 的 `KeyCode` 名称，例如：

| 想设成 | 写 |
|---|---|
| 小键盘 0~9 | `Keypad0` ~ `Keypad9` |
| 小键盘 + − * / | `KeypadPlus` `KeypadMinus` `KeypadMultiply` `KeypadDivide` |
| 小键盘小数点 | `KeypadPeriod` |
| 字母 | `F` `G` `H` |
| 功能键 | `F1` ~ `F12` |
| 鼠标 | `Mouse0` `Mouse1` `Mouse2` |

常用可调项举例：

```ini
# FlyMod：把飞行速度上限拉高，并把上升键改到小键盘8（避免和跳跃冲突）
最大速度(米/秒) = 200
上升热键 = Keypad8
下降热键 = Keypad5

# BoxingInjury：改拳头伤害
拳头伤害 = 99999

# FishAimLock：强制爆头（默认已开）
强制爆头 = true
```

---

## 6. 验证是否加载成功

1. 启动游戏，进到主菜单。
2. 打开 **`BepInEx\LogOutput.log`**（用 VS Code / Notepad++ 看，别用记事本，中文可能乱码）。
3. 搜索 `Loading [`，应该能看到：

```
[Info   :   BepInEx] 5 plugins to load
[Info   :   BepInEx] Loading [BoxingInjury 1.0.0]
[Info   :   BepInEx] Loading [FishAimLock 5.1.0]
[Info   :   BepInEx] Loading [FlyMod 1.0.0]
[Info   :   BepInEx] Loading [Unrivalled 1.0.0]
```

（`plugins to load` 的数字随你放进去的 DLL 数量变化，不用完全一样。）

4. 每个插件加载后都会打印一段 **热键说明**（搜 `热键说明`），把当前按键和数值列出来：

```
[flymod] ================== FlyMod 热键说明 ==================
[flymod]   小键盘0       切换“飞行/悬浮”开启关闭
[flymod]   当前: 飞行=False 速度=20 m/s
```

看到这些就说明成功了，直接进游戏按热键即可。

---

## 7. 常见问题

**Q：进游戏完全没反应，日志里连 `Loading [` 都没有。**
A：BepInEx 没装好。检查 `winhttp.dll` 是否和 `How to Fish.exe` 在同一层；确认下的是 **x64 / Mono / 5.4.x** 版本，不是 6.x 也不是 IL2CPP 版。

**Q：日志里出现 `Loading [xxx]` 但紧跟着报错，功能不生效。**
A：多半是游戏版本更新导致字段/方法改名。日志里会有 `Harmony Patch 失败` 或 `NullReferenceException`，
把 `LogOutput.log` 里报错那几行发出来，需要按新版本重新编译。

**Q：按键没反应。**
A：① 确认 NumLock 开着（小键盘键需要）；② 该键可能被别的 Mod 占用，见 4.6；③ 在对应 `.cfg` 里换个键并重启游戏。

**Q：FlyMod 打开了但人物不飞。**
A：① 必须先按一次 **小键盘 0** 开启（默认是关的）；② 开启后按 **WASD** 才移动，不按就是原地悬停；③ 在开船/驾驶状态时玩家移动会被游戏接管，飞行不生效——先下船。

**Q：飞行时一开一关人物会弹一下。**
A：这是正常的。关闭飞行时会把刚体速度清零并恢复重力，避免带着飞行速度砸出去。

**Q：飞行会掉进水里被淹死吗？**
A：水下窒息伤害仍然生效（本 Mod 不免疫）。想彻底安全就同时开 Unrivalled（小键盘 +）。

**Q：改完配置没变化。**
A：配置只在启动时读一次，**必须完全退出游戏再开**。

**Q：联机能用吗？**
A：这些 Mod 基本都只作用于**本地玩家**（无敌、飞行、拳击伤害都做了本地判断）。但金钱/伤害类在联机时可能被服务端校验覆盖，或被房主视为作弊，**建议只在单人/自建房里用**。

**Q：游戏更新后 Mod 全失效了。**
A：游戏更新通常会重写 `Assembly-CSharp.dll`，字段名/方法签名一变，Harmony 补丁就会失效。需要按新版本重新编译（第 3 步）。

**Q：`dotnet build` 报找不到 `Assembly-CSharp.dll`。**
A：`csproj` 里的 `<GameDir>` 还是我的路径，改成你自己的（见 3.2）。

---

## 8. 卸载

- **只关掉某个功能**：在对应 `.cfg` 里把 `启用` 改成 `false`（比如 `飞行启用 = false`），或游戏里按热键关闭。
- **彻底移除某个 Mod**：删掉 `BepInEx\plugins\` 下对应的 `.dll`，重启游戏。
- **全部移除**：删掉 `BepInEx\plugins\` 里的所有 `.dll`；想彻底干净就把 `winhttp.dll`、`doorstop_config.ini`、`BepInEx\` 一起删掉。
- 配置文件（`BepInEx\config\`）留着不影响，删了会在下次启动时重新生成默认值。

---

## 9. 仓库结构

```
how-to-fish-mod/
├─ aimlock/
│  ├─ aimlock.dll                    ← 编译好的插件
│  └─ aimlock_src/
│     ├─ aimlock.csproj
│     ├─ FishAimLock.cs              主逻辑：瞄点计算、识别、锁定、专属瞄点
│     ├─ Patch_Creature_LocalHit.cs  强制爆头（所有远程武器共用）
│     └─ Patch_PlayerCamera_SetCamPosRot.cs  相机钩子
├─ boxinginjury/
│  ├─ boxinginjury.dll
│  └─ boxinginjury_src/
│     ├─ boxinginjury.csproj
│     └─ BoxingInjury.cs
├─ flymod/
│  ├─ flymod.dll
│  └─ flymod_src/
│     ├─ flymod.csproj
│     └─ FlyMod.cs
├─ unrivalled/
│  ├─ unrivalled.dll
│  └─ unrivalled_src/
│     ├─ unrivalled.csproj
│     └─ Unrivalled.cs
├─ modcontrol/
│  ├─ modcontrol.dll                 ← 游戏内控制面板
│  ├─ README.md
│  └─ modcontrol_src/
│     ├─ modcontrol.csproj
│     └─ ModControlCenter.cs         悬浮窗：小方框/展开、滚动列表、自动发现 ConfigEntry
└─ moneychange/
   ├─ maxmoney.dll                    ← 编译好的插件
   ├─ README.md                       ← 这个 Mod 的原理与说明
   └─ moneychange_src/
      ├─ moneychange.csproj
      └─ Money.cs                     ← 货币重置（产物 maxmoney.dll）
```

---

## 免责声明

本项目仅供**单人离线 / 自建房间**娱乐使用，请勿用于公开联机对局或商业用途。
使用第三方 Mod 可能违反游戏用户协议，由此产生的一切后果由使用者自行承担。
