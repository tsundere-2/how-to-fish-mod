using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace flymod
{
    /// <summary>
    /// FlyMod —— 让主角“起飞 / 悬浮”，并可实时调节飞行速度。
    ///
    /// 原理：游戏玩家移动由 PlayerMovement.FixedUpdate() 驱动：它在最后一步
    ///   Move() 里把 _rig.linearVelocity 设成“输入方向×移动速度 + 重力”。
    /// 我们给 PlayerMovement.FixedUpdate 加一个 Postfix，在它算完之后接管
    /// 刚体的 linearVelocity：
    ///   - 水平：用相机朝向 × WASD（所以“朝哪看就往哪飞”，俯仰也生效）
    ///   - 垂直：按“上升/下降”键；不按 = 垂直速度为 0 → 原地悬浮
    ///   - 关闭重力（useGravity=false），避免持续下坠
    /// FixedUpdate 的回调在物理模拟之前执行，因此这里设置的 velocity 本帧就生效。
    ///
    /// 只对本地玩家生效（联机时不会让别的玩家一起飞）。
    /// </summary>
    [BepInPlugin("me.yexiao07.plugin.flymod", "FlyMod", "1.0.0")]
    public class FlyMod : BaseUnityPlugin
    {
        internal static FlyMod Instance;
        internal static ManualLogSource Log;

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<float> _speed;
        private static ConfigEntry<float> _speedStep;
        private static ConfigEntry<float> _minSpeed;
        private static ConfigEntry<float> _maxSpeed;
        private static ConfigEntry<float> _boostMulti;

        private static ConfigEntry<KeyCode> _toggleKey;
        private static ConfigEntry<KeyCode> _speedUpKey;
        private static ConfigEntry<KeyCode> _speedDownKey;
        private static ConfigEntry<KeyCode> _resetSpeedKey;
        private static ConfigEntry<KeyCode> _upKey;
        private static ConfigEntry<KeyCode> _downKey;
        private static ConfigEntry<KeyCode> _boostKey;

        private static FieldInfo _rigField;
        private static FieldInfo _playerField;

        private static bool _gravityTurnedOff;
        private static Rigidbody _gravityRig;
        private static float _defaultSpeed = 20f;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            _enabled = Config.Bind("基础设置", "飞行启用", false, "是否进入飞行/悬浮模式（进游戏后用热键切换）");
            _speed = Config.Bind("基础设置", "飞行速度(米/秒)", 20f, "飞行/悬浮速度，可用热键实时调整");
            _defaultSpeed = _speed.Value;
            _speedStep = Config.Bind("基础设置", "调速步长(米/秒)", 5f, "每次按加速/减速键调整的数值");
            _minSpeed = Config.Bind("基础设置", "最小速度(米/秒)", 1f, "速度调节下限");
            _maxSpeed = Config.Bind("基础设置", "最大速度(米/秒)", 200f, "速度调节上限");
            _boostMulti = Config.Bind("基础设置", "临时加速倍率", 2f, "按住临时加速键时的速度倍率");

            _toggleKey = Config.Bind("热键", "飞行开关热键", (KeyCode)256, "切换飞行/悬浮（默认小键盘 0）");
            _speedUpKey = Config.Bind("热键", "加速热键", (KeyCode)268, "增加飞行速度（默认小键盘 * ）");
            _speedDownKey = Config.Bind("热键", "减速热键", (KeyCode)267, "降低飞行速度（默认小键盘 / ）");
            _resetSpeedKey = Config.Bind("热键", "速度重置热键", (KeyCode)272, "速度恢复到初始值（默认小键盘 = ）");
            _upKey = Config.Bind("热键", "上升热键", KeyCode.Space, "飞行时上升（默认空格）");
            _downKey = Config.Bind("热键", "下降热键", KeyCode.LeftControl, "飞行时下降（默认左Ctrl）");
            _boostKey = Config.Bind("热键", "临时加速键", KeyCode.LeftShift, "按住时速度×加速倍率（默认左Shift）");

            _rigField = AccessTools.Field(typeof(PlayerMovement), "_rig");
            _playerField = AccessTools.Field(typeof(PlayerMovement), "_player");
            if (_rigField == null) Log.LogWarning("[flymod] 未找到 PlayerMovement._rig，将回退用 Player.Rigidbody");

            bool ok = true;
            try
            {
                new Harmony("me.yexiao07.plugin.flymod").PatchAll(typeof(Patch_PlayerMovement_FixedUpdate));
                Log.LogInfo("[flymod] FlyMod v1.0 加载成功，Harmony Patch 已应用");
            }
            catch (Exception ex)
            {
                Log.LogError("[flymod] Harmony Patch 失败: " + ex);
                ok = false;
            }

            Log.LogInfo("[flymod] 初始化: " + (ok ? "OK" : "有问题") + " | 飞行=" + _enabled.Value + " 速度=" + _speed.Value + " m/s");
            LogKeyHelp();
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey.Value)) ToggleFly();
            if (Input.GetKeyDown(_speedUpKey.Value)) ChangeSpeed(+Mathf.Abs(_speedStep.Value));
            if (Input.GetKeyDown(_speedDownKey.Value)) ChangeSpeed(-Mathf.Abs(_speedStep.Value));
            if (Input.GetKeyDown(_resetSpeedKey.Value))
            {
                _speed.Value = Mathf.Clamp(_defaultSpeed, _minSpeed.Value, _maxSpeed.Value);
                Log.LogInfo("[flymod] 速度已重置为 " + _speed.Value.ToString("0.#") + " m/s");
            }

            // 持续维持/恢复重力状态
            if (_enabled.Value) EnsureGravityOff();
            else EnsureGravityRestored();
        }

        private static void ToggleFly()
        {
            _enabled.Value = !_enabled.Value;
            if (_enabled.Value)
            {
                EnsureGravityOff();
                Log.LogInfo("[flymod] 飞行 -> 开启（速度 " + _speed.Value.ToString("0.#") + " m/s，空格上升 / 左Ctrl下降 / 左Shift加速）");
            }
            else
            {
                EnsureGravityRestored();
                ResetPlayerVelocity();
                Log.LogInfo("[flymod] 飞行 -> 关闭");
            }
        }

        private static void ChangeSpeed(float delta)
        {
            float v = Mathf.Clamp(_speed.Value + delta, _minSpeed.Value, _maxSpeed.Value);
            _speed.Value = v;
            Log.LogInfo("[flymod] 飞行速度 = " + v.ToString("0.#") + " m/s");
        }

        private static Rigidbody GetLocalRig()
        {
            try
            {
                Player p = Player.LocalPlayer;
                if (p == null) return null;
                Rigidbody r = p.Rigidbody;
                if (r != null) return r;
                if (p.Movement != null) return p.Movement.GetComponent<Rigidbody>();
            }
            catch { }
            return null;
        }

        private static void EnsureGravityOff()
        {
            Rigidbody r = GetLocalRig();
            if (r == null) return;
            if (!_gravityTurnedOff)
            {
                _gravityRig = r;
                _gravityTurnedOff = true;
            }
            if (r.useGravity) r.useGravity = false;
        }

        private static void EnsureGravityRestored()
        {
            if (!_gravityTurnedOff) return;
            Rigidbody r = _gravityRig != null ? _gravityRig : GetLocalRig();
            if (r != null && !r.useGravity) r.useGravity = true;
            _gravityTurnedOff = false;
            _gravityRig = null;
        }

        private static void ResetPlayerVelocity()
        {
            try
            {
                Player p = Player.LocalPlayer;
                if (p == null) return;
                Rigidbody r = p.Rigidbody;
                if (r != null && !r.isKinematic) r.linearVelocity = Vector3.zero;
                if (p.Movement != null) p.Movement.SetVel(Vector3.zero);   // 同时清掉内部 _curVel，避免关闭瞬间弹射
            }
            catch { }
        }

        // 由补丁在 PlayerMovement.FixedUpdate 之后调用
        internal static void OnFixedUpdate(PlayerMovement m)
        {
            if (!_enabled.Value || m == null) return;

            Player local = Player.LocalPlayer;
            try
            {
                if (_playerField != null)
                {
                    Player owner = _playerField.GetValue(m) as Player;
                    if (owner != null)
                    {
                        if (local != null && owner != local) return;   // 不是本地玩家 → 不管
                        if (local == null) local = owner;
                    }
                }
            }
            catch { }
            if (local == null) return;

            Rigidbody rig = null;
            try { if (_rigField != null) rig = _rigField.GetValue(m) as Rigidbody; } catch { }
            if (rig == null) rig = local.Rigidbody;
            if (rig == null || rig.isKinematic) return;

            // WASD（用游戏自己的移动输入，尊重改键与“输入封锁”）
            Vector2 mv = m.Input;
            if (mv.sqrMagnitude > 1f) mv = mv.normalized;

            bool up = Input.GetKey(_upKey.Value);
            bool down = Input.GetKey(_downKey.Value);
            if (up && down) { up = false; down = false; }
            float vy = (up ? 1f : 0f) - (down ? 1f : 0f);

            Transform cam = null;
            try { if (local.Camera != null) cam = local.Camera.CamTransform; } catch { }
            Vector3 fwd, right;
            if (cam != null) { fwd = cam.forward; right = cam.right; }
            else if (local.Transform != null) { fwd = local.Transform.forward; right = local.Transform.right; }
            else { fwd = Vector3.forward; right = Vector3.right; }

            Vector3 move = right * mv.x + fwd * mv.y + Vector3.up * vy;
            if (move.sqrMagnitude > 1f) move = move.normalized;   // 任意方向都保持恒定速度

            float spd = Mathf.Clamp(_speed.Value, _minSpeed.Value, _maxSpeed.Value);
            if (Input.GetKey(_boostKey.Value)) spd *= Mathf.Max(1f, _boostMulti.Value);

            rig.linearVelocity = move * spd;    // 接管速度：无输入时 = 0 → 原地悬浮
        }

        internal static void LogKeyHelp()
        {
            try
            {
                Log.LogInfo("[flymod] ================== FlyMod 热键说明 ==================");
                Log.LogInfo("[flymod]   " + KeyName(_toggleKey.Value) + "       切换“飞行/悬浮”开启关闭");
                Log.LogInfo("[flymod]   " + KeyName(_speedUpKey.Value) + "       加速 + " + _speedStep.Value.ToString("0.#") + " m/s");
                Log.LogInfo("[flymod]   " + KeyName(_speedDownKey.Value) + "       减速 - " + _speedStep.Value.ToString("0.#") + " m/s");
                Log.LogInfo("[flymod]   " + KeyName(_resetSpeedKey.Value) + "       速度重置为 " + _defaultSpeed.ToString("0.#") + " m/s");
                Log.LogInfo("[flymod]   " + KeyName(_upKey.Value) + "       上升" + (Equals(_upKey.Value, KeyCode.Space) ? "（按住空格）" : ""));
                Log.LogInfo("[flymod]   " + KeyName(_downKey.Value) + "       下降");
                Log.LogInfo("[flymod]   " + KeyName(_boostKey.Value) + "（按住） 临时加速 ×" + _boostMulti.Value.ToString("0.#"));
                Log.LogInfo("[flymod]   操作: 朝哪看按W就往哪飞；松开WASD=原地悬浮；不按上升/下降=高度不变");
                Log.LogInfo("[flymod]   当前: 飞行=" + _enabled.Value + " 速度=" + _speed.Value.ToString("0.#") + " m/s");
                Log.LogInfo("[flymod] =====================================================");
            }
            catch (Exception ex) { Log.LogWarning("[flymod] 打印热键说明失败: " + ex.Message); }
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
                case KeyCode.Space: return "空格";
                case KeyCode.LeftControl: return "左Ctrl";
                case KeyCode.LeftShift: return "左Shift";
                case KeyCode.Mouse0: return "鼠标左键";
                case KeyCode.Mouse1: return "鼠标右键";
                case KeyCode.Mouse2: return "鼠标中键";
                default: return k.ToString();
            }
        }
    }

    [HarmonyPatch(typeof(PlayerMovement), "FixedUpdate")]
    internal static class Patch_PlayerMovement_FixedUpdate
    {
        private static void Postfix(PlayerMovement __instance)
        {
            try { FlyMod.OnFixedUpdate(__instance); }
            catch (Exception ex)
            {
                if (FlyMod.Log != null) FlyMod.Log.LogError("[flymod] FixedUpdate 补丁异常: " + ex);
            }
        }
    }
}
