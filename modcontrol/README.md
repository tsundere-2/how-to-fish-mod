# ModControlCenter — 游戏内控制面板（modcontrol）

按 **F1** 呼出一个「液态玻璃」风格的悬浮窗，把几个 Mod 的开关和数值全部放进游戏里调，
改完自动写回 `BepInEx\config\*.cfg`，不用退出游戏去改文件。

- 插件名：`ModControlCenter`
- 产物：`modcontrol.dll`
- 插件 GUID：`me.yexiao07.plugin.modcontrol`
- 版本：1.0.0

## 目录

```
modcontrol/
├─ modcontrol.dll            ← 编译好的成品，直接用这个
├─ README.md
└─ modcontrol_src/
   ├─ modcontrol.csproj
   └─ ModControlCenter.cs
```

## 用法

1. 下载本目录下的 **`modcontrol.dll`**，放进 `BepInEx\plugins\`
2. 进游戏，按 **F1** 显示 / 隐藏面板
3. 点行标题展开，点右侧开关切换，拖滑条调数值 —— **立即生效**，0.8 秒后自动写回 cfg

| 操作 | 效果 |
|---|---|
| 拖动标题栏 | 移动窗口 |
| 点标题栏右侧小方块 | 折叠 / 展开整个列表 |
| 点某一行 | 展开 / 收起该 Mod 的详细选项 |
| 右侧开关 | 开 / 关 |
| 滑条 | 调数值 |

## 面板里能调什么

| 条目 | 对应插件 | 可调项 |
|---|---|---|
| 自动瞄准 | `fishaimlock` | 总开关 · 锁鱼 / 锁鸟 / 锁 Boss / 锁其他 · 强制爆头 |
| 飞行模式 | `flymod` | 总开关 · 飞行速度（5 ~ 60 m/s） |
| 拳击伤害改版 | `boxinginjury` | 总开关 · 拳头伤害（100 ~ 99999） |
| 无双 · 无敌 | `unrivalled` | 总开关 · 保持满血 |
| 金钱拉满 | `finalmod` | 插件本体开关（这个 Mod 没有 cfg） |

标题栏的绿点和 `N 已启用` 实时显示当前开启数量。

## 它是怎么找到这些开关的

不写死任何类型，全部读 BepInEx 的 `Chainloader.PluginInfos`：

1. 先按内置表匹配 5 个已知插件（GUID → 标题 / 图标 / 配置节 / 键名）。
2. 再**兜底扫描**：其它任何插件里，凡是 `bool` 且键名含 `启用` 的配置，都会生成一行；
   键名含 `锁` / `爆头` / `满血` 的布尔项自动成为子开关。

所以以后加了新 Mod，只要它的配置符合这个命名习惯，面板里就会自己冒出来，不用改代码。

## 双向同步

- **面板 → 配置**：改动先落到 `ConfigEntry.BoxedValue`，再延迟 0.8 秒 `ConfigFile.Save()` 写盘
  （连续拖动滑条只会保存最后一次，不会疯狂写文件）。
- **配置 → 面板**：每 0.5 秒从 `ConfigEntry` 读一次，所以你在别处改了值（比如热键调速），
  面板上的数字也会跟着变。

## 实现细节

- 界面全部**代码生成**：圆角/圆形贴图用 SDF 现场烘焙，字体取系统的「微软雅黑」，
  不依赖任何图片或 prefab 资源，拷一个 DLL 过去就能用。
- 动画基于 `unscaledDeltaTime`，游戏暂停 / 时间缩放时照样流畅。
- 场景里没有 `EventSystem` 时会自己建一个，并 `DontDestroyOnLoad`。
- 面板 Canvas `sortingOrder = 30000`，盖在游戏 UI 上面。

## 配置

`BepInEx\config\me.yexiao07.plugin.modcontrol.cfg`：

```ini
[界面]
## 按下该键显示或隐藏 Mod 控制悬浮窗
显示/隐藏热键 = F1

## 进入游戏后是否自动显示悬浮窗
启动时显示 = true
```

## 注意

- **F1** 默认被本 Mod 占用；如果和别的软件冲突，改上面的 `显示/隐藏热键` 即可。
- 面板只覆盖它认识的条目。FishAimLock 的瞄点微调、识别参数、调试开关等**不在面板里**，
  仍然要直接改 `me.yexiao07.plugin.fishaimlock.cfg`。
- `金钱拉满` 的开关是直接 `enabled = true/false` 插件本体，**不会写盘**，重启游戏后恢复默认。
