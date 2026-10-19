using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FishUnlimitedMoneyFinal
{
    /// <summary>
    /// 货币重置 Mod —— 按一次热键，把「美元 + 欧元」直接设为指定数值。
    ///
    /// 为什么不能只改 Money 属性：
    ///   MoneyManager.Money / Euro 是 static 属性，背后真正存数据的是两个 FishNet 同步变量：
    ///     public readonly SyncVar&lt;int&gt; _money;
    ///     public readonly SyncVar&lt;int&gt; _euro;
    ///   SyncVar&lt;T&gt; 内部有两个私有字段：
    ///     private T _initialValue;   // 服务器重连/回滚时的基准值
    ///     private T _value;          // 当前运行值
    ///   只改属性的话，下一次同步/重连就会从 _initialValue 回滚回去。
    ///
    /// 因此这里三处一起写：
    ///   1. _initialValue  —— 防止回滚
    ///   2. SyncVar.Value  —— 走官方 setter，会 Dirty() 同步给客户端，
    ///                        客户端收到后触发 OnChangeMoney(asServer:false) → UI 刷新
    ///   3. Money 属性      —— 同步 static 数值（购买判定读的是它）
    ///
    /// 全部走反射，不直接引用 MoneyManager 类型，游戏小更新也不容易编译失败。
    /// </summary>
    [BepInPlugin("me.yexiao.fish.finalmod", "FishUnlimitedMoneyFinal", "3.2.0")]
    public class FishUnlimitedMoneyFinal : BaseUnityPlugin
    {
        internal static FishUnlimitedMoneyFinal Instance;
        internal static ManualLogSource Log;

        private static ConfigEntry<int> _amount;
        private static ConfigEntry<KeyCode> _hotkey;

        private static Harmony _harmony;

        // ---- 反射缓存 ----
        private static FieldInfo _moneySyncVarField;        // MoneyManager._money
        private static FieldInfo _euroSyncVarField;         // MoneyManager._euro
        private static FieldInfo _syncVarInitialValueField; // SyncVar<int>._initialValue
        private static FieldInfo _syncVarCurrentValueField; // SyncVar<int>._value
        private static PropertyInfo _syncVarValueProp;      // SyncVar<int>.Value（公开 setter）
        private static MethodInfo _moneySetMethod;          // MoneyManager.Money 的 setter
        private static MethodInfo _euroSetMethod;           // MoneyManager.Euro  的 setter
        private static MethodInfo _uiSetMoneyMethod;        // PlayerUI.SetMoney（静态，刷新 UI）
        private static Type _currencyType;                  // Currency 枚举
        private static UnityEngine.Object _moneyMgrInstance;

        private static bool _fieldsReady;
        private static bool _instanceWarned;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            _amount = Config.Bind("基础设置", "货币数值", 9999999,
                "按热键后把美元和欧元都设为这个数值");
            _hotkey = Config.Bind("热键设置", "一键补满货币按键", (KeyCode)257,
                "按下该键把货币设为上面的数值（默认小键盘 1）");

            _harmony = new Harmony("me.yexiao.fish.finalmod");

            Type moneyMgrType = AccessTools.TypeByName("MoneyManager");
            if (moneyMgrType == null)
            {
                Log.LogError("[money] 找不到 MoneyManager 类型，Mod 终止（游戏版本可能已变更）");
                return;
            }

            _moneySetMethod = AccessTools.PropertySetter(moneyMgrType, "Money");
            _euroSetMethod = AccessTools.PropertySetter(moneyMgrType, "Euro");

            // UI 刷新：PlayerUI.SetMoney(int to, int diff, bool gainedMoney, Currency currency)
            // MoneyManager.OnChangeMoney 只在 asServer==false（纯客户端）时刷新 UI，
            // 单人/主机模式下 asServer==true，必须自己补一次，否则数字不变。
            try
            {
                Type playerUIType = AccessTools.TypeByName("PlayerUI");
                _currencyType = AccessTools.TypeByName("Currency");
                if (playerUIType != null && _currencyType != null)
                {
                    _uiSetMoneyMethod = playerUIType.GetMethod("SetMoney",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { typeof(int), typeof(int), typeof(bool), _currencyType }, null);
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[money] 准备 UI 刷新方法失败: " + ex.Message);
            }

            Log.LogInfo("[money] FishUnlimitedMoneyFinal v3.2 加载成功");
            LogKeyHelp();
        }

        private void Update()
        {
            // 主菜单阶段 MoneyManager 还没创建，等进游戏后再解析字段
            if (!_fieldsReady)
            {
                if (Time.timeSinceLevelLoad <= 1f) return;
                ResolveFields();
                if (!_fieldsReady) return;
            }

            if (_moneyMgrInstance == null)
            {
                // 每秒尝试一次（读档 / 重开一局后实例会变）
                if (Time.frameCount % 60 != 0) return;

                _moneyMgrInstance = FindMoneyManager();
                if (_moneyMgrInstance != null)
                {
                    Log.LogInfo("[money] 已获取 MoneyManager 实例");
                }
                else if (!_instanceWarned && Time.timeSinceLevelLoad > 10f)
                {
                    _instanceWarned = true;
                    Log.LogWarning("[money] 暂未找到 MoneyManager 实例，进游戏（读档）后会自动重试");
                }
                return;
            }

            if (Input.GetKeyDown(_hotkey.Value))
            {
                ApplyMoney(_amount.Value);
            }
        }

        /// <summary>解析所有反射成员。</summary>
        private static void ResolveFields()
        {
            Type moneyMgrType = AccessTools.TypeByName("MoneyManager");
            if (moneyMgrType == null) return;

            FieldInfo[] fields = moneyMgrType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo f in fields)
            {
                if (!f.FieldType.Name.StartsWith("SyncVar`1")) continue;
                if (f.Name == "_money") _moneySyncVarField = f;
                else if (f.Name == "_euro") _euroSyncVarField = f;
            }

            if (_moneySyncVarField != null)
            {
                Type svType = _moneySyncVarField.FieldType;
                _syncVarInitialValueField = svType.GetField("_initialValue", BindingFlags.Instance | BindingFlags.NonPublic);
                _syncVarCurrentValueField = svType.GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic);
                _syncVarValueProp = svType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
            }

            _fieldsReady = true;

            Log.LogInfo("[money] 字段解析: " +
                "Money=" + (_moneySyncVarField != null ? "_money" : "未找到") +
                " Euro=" + (_euroSyncVarField != null ? "_euro" : "未找到") +
                " 基准值=" + (_syncVarInitialValueField != null ? "OK" : "未找到") +
                " 当前值=" + (_syncVarCurrentValueField != null ? "OK" : "未找到") +
                " Value属性=" + (_syncVarValueProp != null ? "OK" : "未找到") +
                " 属性setter=" + ((_moneySetMethod != null && _euroSetMethod != null) ? "OK" : "未找到") +
                " UI刷新=" + (_uiSetMoneyMethod != null ? "OK" : "未找到"));

            if (_moneySyncVarField == null || _syncVarInitialValueField == null)
                Log.LogWarning("[money] 关键字段缺失，重置可能不完整（游戏更新导致？）");
        }

        private static UnityEngine.Object FindMoneyManager()
        {
            try
            {
                Type t = _moneySyncVarField != null
                    ? _moneySyncVarField.DeclaringType
                    : (_moneySetMethod != null ? _moneySetMethod.DeclaringType : null);
                if (t == null) return null;
                return UnityEngine.Object.FindAnyObjectByType(t);
            }
            catch (Exception ex)
            {
                if (Log != null) Log.LogWarning("[money] 查找实例失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>把美元和欧元同时设为 value。</summary>
        private static void ApplyMoney(int value)
        {
            if (_moneyMgrInstance == null) return;
            try
            {
                int before = ReadCurrent();

                ApplyOne(_moneySyncVarField, value);
                ApplyOne(_euroSyncVarField, value);

                // 同步 static 属性（购买判定读的是 Money/Euro）
                try { if (_moneySetMethod != null) _moneySetMethod.Invoke(_moneyMgrInstance, new object[] { value }); }
                catch (Exception ex) { Log.LogWarning("[money] 设置 Money 属性失败: " + ex.Message); }
                try { if (_euroSetMethod != null) _euroSetMethod.Invoke(_moneyMgrInstance, new object[] { value }); }
                catch (Exception ex) { Log.LogWarning("[money] 设置 Euro 属性失败: " + ex.Message); }

                // 主动刷新 UI（单人/主机模式下游戏自己不会刷）
                RefreshUI(value);

                Log.LogInfo("[money] 美元和欧元已设为 " + value + "（原值 " + before + "）");
            }
            catch (Exception ex)
            {
                Log.LogError("[money] 重置失败: " + ex);
            }
        }

        /// <summary>
        /// 写一个 SyncVar：先写 _initialValue（防回滚），
        /// 再走公开的 Value setter（触发 Dirty → 同步 → 客户端 OnChange → UI 刷新）。
        /// </summary>
        private static void ApplyOne(FieldInfo syncVarField, int value)
        {
            if (syncVarField == null) return;

            object syncVar = syncVarField.GetValue(_moneyMgrInstance);
            if (syncVar == null) return;

            // 1) 基准值，避免重连/回滚
            if (_syncVarInitialValueField != null)
                _syncVarInitialValueField.SetValue(syncVar, value);

            // 2) 走官方 setter：内部会 Dirty() 并触发 OnChange
            if (_syncVarValueProp != null && _syncVarValueProp.CanWrite)
            {
                try
                {
                    _syncVarValueProp.SetValue(syncVar, value, null);
                    return;
                }
                catch (Exception ex)
                {
                    if (Log != null) Log.LogWarning("[money] 写 SyncVar.Value 失败，回退直写字段: " + ex.Message);
                }
            }

            // 3) 兜底：直接写私有字段
            if (_syncVarCurrentValueField != null)
                _syncVarCurrentValueField.SetValue(syncVar, value);
        }

        /// <summary>
        /// 主动刷新金钱 UI。
        /// 单人/主机模式下 SyncVar 的 OnChange 回调 asServer==true，
        /// 游戏自己的 OnChangeMoney 会直接跳过，所以这里直接调 PlayerUI.SetMoney。
        /// </summary>
        private static void RefreshUI(int value)
        {
            if (_uiSetMoneyMethod == null || _currencyType == null) return;
            try
            {
                object dollar = Enum.Parse(_currencyType, "Dollar");
                object euro = Enum.Parse(_currencyType, "Euro");
                _uiSetMoneyMethod.Invoke(null, new object[] { value, 0, true, dollar });
                _uiSetMoneyMethod.Invoke(null, new object[] { value, 0, true, euro });
            }
            catch (Exception ex)
            {
                Log.LogWarning("[money] 刷新 UI 失败: " + ex.Message);
            }
        }

        /// <summary>读取当前美元数值（仅用于日志）。</summary>
        private static int ReadCurrent()
        {
            try
            {
                if (_moneySyncVarField == null || _moneyMgrInstance == null) return -1;
                object sv = _moneySyncVarField.GetValue(_moneyMgrInstance);
                if (sv == null) return -1;
                if (_syncVarValueProp != null) return (int)_syncVarValueProp.GetValue(sv, null);
                if (_syncVarCurrentValueField != null) return (int)_syncVarCurrentValueField.GetValue(sv);
            }
            catch { }
            return -1;
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        internal static void LogKeyHelp()
        {
            try
            {
                Log.LogInfo("[money] ================ 货币 Mod 热键说明 ================");
                Log.LogInfo("[money]   " + KeyName(_hotkey.Value) + "       把美元和欧元设为 " + _amount.Value);
                Log.LogInfo("[money]   数值/按键都可以在 cfg 里改（货币数值 / 一键补满货币按键）");
                Log.LogInfo("[money] ===================================================");
            }
            catch (Exception ex) { Log.LogWarning("[money] 打印热键说明失败: " + ex.Message); }
        }

        private static string KeyName(KeyCode k)
        {
            switch (k)
            {
                case (KeyCode)256: return "小键盘0";
                case (KeyCode)257: return "小键盘1";
                case (KeyCode)258: return "小键盘2";
                case (KeyCode)259: return "小键盘3";
                case (KeyCode)260: return "小键盘4";
                case (KeyCode)261: return "小键盘5";
                case (KeyCode)262: return "小键盘6";
                case (KeyCode)263: return "小键盘7";
                case (KeyCode)264: return "小键盘8";
                case (KeyCode)265: return "小键盘9";
                case (KeyCode)266: return "小键盘小数点";
                case (KeyCode)267: return "小键盘/";
                case (KeyCode)268: return "小键盘*";
                case (KeyCode)269: return "小键盘-";
                case (KeyCode)270: return "小键盘+";
                case (KeyCode)271: return "小键盘回车";
                case (KeyCode)272: return "小键盘=";
                case KeyCode.Mouse0: return "鼠标左键";
                case KeyCode.Mouse1: return "鼠标右键";
                case KeyCode.Mouse2: return "鼠标中键";
                default: return k.ToString();
            }
        }
    }
}
