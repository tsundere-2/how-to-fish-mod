# 货币重置 Mod（moneychange）

按一次热键，把游戏里的 **美元 + 欧元** 直接设为指定数值。

- 插件名：`FishUnlimitedMoneyFinal`
- 产物：`maxmoney.dll`
- 插件 GUID：`me.yexiao.fish.finalmod`
- 版本：3.2.0

## 目录

```
moneychange/
├─ maxmoney.dll              ← 编译好的成品，直接用这个
├─ README.md
└─ moneychange_src/
   ├─ moneychange.csproj
   └─ Money.cs
```

## 用法

1. 下载本目录下的 **`maxmoney.dll`**，放进 `BepInEx\plugins\`
2. 进游戏，按 **小键盘 1** → 美元和欧元变成 **9999999**
3. 数值和按键都能在 `BepInEx\config\me.yexiao.fish.finalmod.cfg` 里改：

```ini
[基础设置]
货币数值 = 9999999

[热键设置]
一键补满货币按键 = Keypad1
```

## 编译

```powershell
# 先把 csproj 里的 <GameDir> 改成你的游戏目录
cd moneychange\moneychange_src
dotnet build -c Release
# 产物: bin\Release\maxmoney.dll
```

也可以用参数覆盖路径，不改文件：

```powershell
dotnet build -c Release -p:GameDir="E:\Steam\steamapps\common\How to Fish\How to Fish"
```

## 为什么不能只改 `MoneyManager.Money`

游戏的货币真正存在两个 FishNet 同步变量里：

```csharp
public readonly SyncVar<int> _money;
public readonly SyncVar<int> _euro;
```

`MoneyManager.Money` / `Euro` 只是 **static 属性**（购买判定 `CanAfford` 读的是它）。
`SyncVar<T>` 内部还有两个私有字段：

| 字段 | 作用 |
|---|---|
| `_initialValue` | 服务器重连 / 回滚时的基准值 |
| `_value` | 当前运行值 |

只改属性的话，下次同步或重连就会从 `_initialValue` 回滚回去。所以本 Mod 三处一起写：

1. `_initialValue` —— 防止回滚
2. `SyncVar.Value` —— 走官方 setter，会 `Dirty()` 同步给客户端
3. `Money` / `Euro` 属性 —— 同步 static 数值

## 为什么还要手动刷新 UI

游戏自己的回调是这样的：

```csharp
private void OnChangeMoney(int prev, int next, bool asServer)
{
    if (!asServer)          // ← 只有纯客户端才刷新
    {
        PlayerUI.SetMoney(next, Mathf.Abs(num), num > 0);
        Money = next;
    }
}
```

**单人 / 自建主机**模式下 `asServer == true`，这个分支会被直接跳过，
所以数字不会变。本 Mod 会额外调一次 `PlayerUI.SetMoney(...)` 补上。

> 也就是说：原版 3.0.0 / 3.1.0 的「游戏原生 UI 自动刷新」在单人模式下其实是不成立的。

## 全部走反射

源码没有直接 `using` 游戏类型，`MoneyManager`、`SyncVar<T>`、`PlayerUI` 都是运行时用
`AccessTools.TypeByName` / 反射找的，游戏小更新时不容易编译失败。

启动日志会打印解析结果，方便排查：

```
[money] 字段解析: Money=_money Euro=_euro 基准值=OK 当前值=OK Value属性=OK 属性setter=OK UI刷新=OK
```

哪一项是 `未找到`，就说明那个版本的游戏改了名字。

## 注意

- 和旧版 `maxmoney.dll`（3.0.0 / 3.1.0）**GUID 相同**，不能同时放，二选一。
- 只在单人 / 自建主机有效；纯客户端连别人的服务器时，数值由服务端决定，改了也会被同步回来。
