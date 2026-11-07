using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace unrivalled
{
    /// <summary>
    /// Unrivalled mod —— 主角“无双/无敌”：受到攻击血量不减少。
    ///
    /// 游戏里玩家扣血的唯一入口是 PlayerVitals.TakeDamage(amount, pos, force, ignoreInvulnerability)：
    ///   攻击   : Server.HitPlayer → RpcLogic___HitPlayer → PlayerVitals.TakeDamage
    ///   螃蟹爪 : CrabArms.cs → _target.Vitals.TakeDamage
    ///   中毒/着火/饥饿 : PlayerVitals.PoisonTick / FireTick / DamageFromFullness → TakeDamage
    /// 该方法内部把 _syncedHealth.Value 调低（这是全游戏唯一降低玩家血量的地方）。
    /// 因此只要在 TakeDamage 前缀里返回 false，就能拦截全部扣血，且不影响其它逻辑。
    /// 默认只保护本地玩家，联机时不会让别的玩家也变无敌。
    /// </summary>
    [BepInPlugin("me.yexiao07.plugin.unrivalled", "Unrivalled", "1.1.0")]
    public class Unrivalled : BaseUnityPlugin
    {
        internal static Unrivalled Instance;
        internal static ManualLogSource Log;

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<bool> _keepFull;
        private static ConfigEntry<KeyCode> _toggleKey;

        private static FieldInfo _playerField;
        private static float _lastHeal;

        // 联机模式：只有房主/单人时 PlayerVitals 才是服务端权威（IsServerInitialized）。
        // 加入他人房间时为纯客户端，血量由服务器结算后同步，本 Mod 拦不到。
        private static bool _roleLogged;
        private static bool _clientWarned;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            _enabled = Config.Bind("基础设置", "无双/无敌启用", true, "开启后，本地玩家受到的任何伤害都不会减少血量（攻击、BOSS、中毒、着火、饥饿全部免疫）");
            _keepFull = Config.Bind("基础设置", "保持满血", false, "开启后每 0.5 秒把血量补满（可选；只要“不受伤”不需要开这个）");
            _toggleKey = Config.Bind("基础设置", "开关热键", (KeyCode)270, "按下该键切换无双/无敌开启关闭（默认小键盘 + ）");

            _playerField = AccessTools.Field(typeof(PlayerVitals), "_player");
            if (_playerField == null) Log.LogWarning("[unrivalled] 未找到 PlayerVitals._player，将保护场景内所有玩家");

            bool ok = true;
            try
            {
                new Harmony("me.yexiao07.plugin.unrivalled").PatchAll(typeof(Patch_PlayerVitals_TakeDamage));
                Log.LogInfo("[unrivalled] Unrivalled v1.1 加载成功，Harmony Patch 已应用");
            }
            catch (Exception ex)
            {
                Log.LogError("[unrivalled] Harmony Patch 失败: " + ex);
                ok = false;
            }

            Log.LogInfo("[unrivalled] 初始化: " + (ok ? "OK" : "有问题") + " | 启用=" + _enabled.Value + " 保持满血=" + _keepFull.Value);
            LogKeyHelp();
        }

        private void Update()
        {
            LogNetworkRoleOnce();

            if (Input.GetKeyDown(_toggleKey.Value))
            {
                _enabled.Value = !_enabled.Value;
                Log.LogInfo("[unrivalled] 无双/无敌 -> " + (_enabled.Value ? "开启" : "关闭"));
            }

            if (_keepFull.Value && Time.time - _lastHeal > 0.5f)
            {
                _lastHeal = Time.time;
                try
                {
                    Player p = Player.LocalPlayer;
                    if (p != null && p.Vitals != null)
                    {
                        if (p.Vitals.IsServerInitialized) p.Vitals.Heal(100);
                        else WarnClientOnly();
                    }
                }
                catch { }
            }
        }

        // 进入存档后只打印一次：当前是主机/单人还是联机客户端。
        private static void LogNetworkRoleOnce()
        {
            if (_roleLogged) return;
            try
            {
                Player p = Player.LocalPlayer;
                if (p == null || p.Vitals == null) return;
                _roleLogged = true;
                bool server = p.Vitals.IsServerInitialized;
                Log.LogInfo("[unrivalled] 网络模式: " + (server
                    ? "主机/单人（服务端在本进程，无敌生效）"
                    : "联机客户端（血量由房主服务器结算，无敌无效）"));
            }
            catch { }
        }

        private static void WarnClientOnly()
        {
            if (_clientWarned) return;
            _clientWarned = true;
            Log.LogWarning("[unrivalled] 检测到联机客户端：PlayerVitals 由服务器权威同步，本 Mod 无法拦截真实伤害。"
                + "无敌/保持满血仅在「本机开房间 / 单人」有效。");
        }

        // 由补丁调用。返回 false = 跳过原方法（不扣血）。
        internal static bool OnTakeDamage(PlayerVitals vitals, int amount)
        {
            if (!_enabled.Value) return true;
            try
            {
                // 客户端本地 TakeDamage 本身就会因 IsServerInitialized=false 直接返回，
                // 真正扣血发生在房主进程，这里拦不到，直接提示并放行。
                if (vitals != null && !vitals.IsServerInitialized)
                {
                    WarnClientOnly();
                    return true;
                }

                if (_playerField != null && vitals != null)
                {
                    Player owner = _playerField.GetValue(vitals) as Player;
                    Player local = Player.LocalPlayer;
                    if (owner != null && local != null && owner != local) return true;   // 不是本地玩家 → 放行
                }
            }
            catch { }
            return false;   // 拦截：血量不减少
        }

        internal static void LogKeyHelp()
        {
            try
            {
                Log.LogInfo("[unrivalled] ================ Unrivalled 热键说明 ================");
                Log.LogInfo("[unrivalled]   " + KeyName(_toggleKey.Value) + "       开关“无双/无敌”（开/关）");
                Log.LogInfo("[unrivalled]   当前: 无敌=" + _enabled.Value + " 保持满血=" + _keepFull.Value);
                Log.LogInfo("[unrivalled] ======================================================");
            }
            catch (Exception ex) { Log.LogWarning("[unrivalled] 打印热键说明失败: " + ex.Message); }
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

    [HarmonyPatch(typeof(PlayerVitals), "TakeDamage")]
    internal static class Patch_PlayerVitals_TakeDamage
    {
        private static bool Prefix(PlayerVitals __instance, int amount)
        {
            try { return Unrivalled.OnTakeDamage(__instance, amount); }
            catch (Exception ex)
            {
                if (Unrivalled.Log != null) Unrivalled.Log.LogError("[unrivalled] TakeDamage 补丁异常: " + ex);
                return true;
            }
        }
    }
}
