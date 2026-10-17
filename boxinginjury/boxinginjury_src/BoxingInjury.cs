using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace boxinginjury
{
    /// <summary>
    /// Boxinjurymod —— 拳击伤害改版。
    /// 游戏里空手左键攻击走 PlayerPunching.HitTarget(int side)：
    ///   int damage = ServerSettings.OneShotEnabled ? 99999 : _damage;
    /// 其中 _damage 是 [SerializeField] private int _damage。
    /// 本插件在 HitTarget 执行前把 _damage 设为指定值（默认 99999），
    /// 于是任何一次空手出拳都造成该伤害。对生物/玩家/船都生效。
    /// </summary>
    [BepInPlugin("me.yexiao07.plugin.boxinginjury", "BoxingInjury", "1.0.0")]
    public class BoxingInjury : BaseUnityPlugin
    {
        internal static BoxingInjury Instance;
        internal static ManualLogSource Log;

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _damage;
        private static ConfigEntry<KeyCode> _toggleKey;

        private static FieldInfo _damageField;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            _enabled = Config.Bind("基础设置", "拳击伤害改版启用", true, "开启后，空手攻击(拳头)的伤害固定为下面设定的数值");
            _damage = Config.Bind("基础设置", "拳头伤害", 99999, "空手左键攻击造成的基础伤害（改动即时生效，无需重启）");
            _toggleKey = Config.Bind("基础设置", "开关热键", (KeyCode)266, "按下该键切换拳击伤害改版开启/关闭（默认小键盘小数点）");

            _damageField = AccessTools.Field(typeof(PlayerPunching), "_damage");
            if (_damageField == null) Log.LogError("[boxing] 反射失败: PlayerPunching._damage");

            bool ok = _damageField != null;
            try
            {
                new Harmony("me.yexiao07.plugin.boxinginjury").PatchAll(typeof(Patch_PlayerPunching_HitTarget));
                Log.LogInfo("[boxing] BoxingInjury v1.0 加载成功，Harmony Patch 已应用");
            }
            catch (Exception ex)
            {
                Log.LogError("[boxing] Harmony Patch 失败: " + ex);
                ok = false;
            }

            Log.LogInfo("[boxing] 初始化: " + (ok ? "OK" : "有问题") + " | 启用=" + _enabled.Value + " 拳头伤害=" + _damage.Value);
            LogKeyHelp();
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey.Value))
            {
                _enabled.Value = !_enabled.Value;
                Log.LogInfo("[boxing] 拳击伤害改版 -> " + (_enabled.Value ? "开启" : "关闭"));
            }
        }

        // 由补丁调用：在 HitTarget 计算伤害前，把 _damage 改为配置值
        internal static void OnHitTarget(PlayerPunching inst)
        {
            if (!_enabled.Value || inst == null || _damageField == null) return;
            try { _damageField.SetValue(inst, Mathf.Max(0, _damage.Value)); }
            catch (Exception ex) { if (Log != null) Log.LogWarning("[boxing] 设置伤害失败: " + ex.Message); }
        }

        internal static void LogKeyHelp()
        {
            try
            {
                Log.LogInfo("[boxing] ================ BoxingInjury 热键说明 ================");
                Log.LogInfo("[boxing]   " + KeyName(_toggleKey.Value) + "       开关“拳击伤害改版”（开/关）");
                Log.LogInfo("[boxing]   拳头伤害 = " + _damage.Value + "（在配置里改，即时生效，无需重启）");
                Log.LogInfo("[boxing] ========================================================");
            }
            catch (Exception ex) { Log.LogWarning("[boxing] 打印热键说明失败: " + ex.Message); }
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
                case KeyCode.Mouse0: return "鼠标左键";
                case KeyCode.Mouse1: return "鼠标右键";
                case KeyCode.Mouse2: return "鼠标中键";
                default: return k.ToString();
            }
        }
    }

    [HarmonyPatch(typeof(PlayerPunching), "HitTarget")]
    internal static class Patch_PlayerPunching_HitTarget
    {
        private static void Prefix(PlayerPunching __instance)
        {
            try { BoxingInjury.OnHitTarget(__instance); }
            catch (Exception ex)
            {
                if (BoxingInjury.Log != null) BoxingInjury.Log.LogError("[boxing] HitTarget 补丁异常: " + ex);
            }
        }
    }
}
