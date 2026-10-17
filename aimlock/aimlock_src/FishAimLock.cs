using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace aimlock
{
    [BepInPlugin("me.yexiao07.plugin.fishaimlock", "FishAimLock", "5.1.0")]
    public class FishAimLock : BaseUnityPlugin
    {
        internal static FishAimLock Instance;
        internal static ManualLogSource Log;

        // ---------------- config ----------------
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<KeyCode> _toggleKey;
        private static ConfigEntry<KeyCode> _diagKey;
        private static ConfigEntry<KeyCode> _releaseKey;
        private static ConfigEntry<bool> _requireAds;
        private static ConfigEntry<bool> _requireAimKey;
        private static ConfigEntry<KeyCode> _aimKey;

        private static ConfigEntry<float> _circleRadius;
        private static ConfigEntry<float> _maxDistance;
        private static ConfigEntry<float> _minDistance;
        private static ConfigEntry<float> _bossBonus;
        private static ConfigEntry<float> _scanInterval;
        private static ConfigEntry<bool> _requireLineOfSight;
        private static ConfigEntry<float> _missGrace;        // 丢失目标宽限：连续丢失超过该秒数才解除锁定
        private static ConfigEntry<float> _releaseFactor;    // 已锁定时允许偏离识别圆的倍率（滞回）

        // 瞄点参数（新）：全部以生物本地空间的“爆头半空间”为准
        private static ConfigEntry<float> _headDepth;        // 在 [HeadPos, 模型前端] 之间取点的比例
        private static ConfigEntry<float> _headHeightBias;   // 在 [模型中线, 模型顶部] 之间取点的比例
        private static ConfigEntry<bool> _snapToCollider;    // 把瞄点吸附到实际可命中的碰撞体表面
        private static ConfigEntry<bool> _preferRenderer;    // 用渲染器包围盒(视觉模型)而非碰撞体计算瞄点
        private static ConfigEntry<bool> _worldUpOffset;     // 垂直偏移沿世界上方(而非生物本地上下)
        private static ConfigEntry<float> _fallbackUp;

        private static ConfigEntry<float> _maxLeadDist;
        private static ConfigEntry<float> _maxRotSpeed;
        private static ConfigEntry<float> _trackSharpness;
        private static ConfigEntry<float> _aimSmooth;
        private static ConfigEntry<float> _leadTime;
        private static ConfigEntry<float> _maxTargetSpeed;

        private static ConfigEntry<bool> _lockBirds;
        private static ConfigEntry<bool> _lockFish;
        private static ConfigEntry<bool> _lockBoss;
        private static ConfigEntry<bool> _lockOthers;

        // 方式A：让子弹自动命中头部（强制爆头）
        private static ConfigEntry<bool> _forceHeadshot;
        private static ConfigEntry<float> _forceHeadshotDepth;

        // 生物专属瞄点（不同生物各自的瞄点偏移）
        private static ConfigEntry<bool> _overrideEnabled;
        private static ConfigEntry<string> _overrideTable;
        private static ConfigEntry<float> _nudgeStep;
        private static ConfigEntry<KeyCode> _nudgeUpKey;
        private static ConfigEntry<KeyCode> _nudgeDownKey;
        private static ConfigEntry<KeyCode> _nudgeForwardKey;
        private static ConfigEntry<KeyCode> _nudgeBackKey;
        private static ConfigEntry<KeyCode> _nudgeResetKey;

        private static ConfigEntry<bool> _verbose;
        private static ConfigEntry<bool> _dumpHeadZone;

        // ---------------- reflection ----------------
        private static FieldInfo _rotField;
        private static FieldInfo _moveRotField;
        private static FieldInfo _shakeMultiField;

        // ---------------- runtime state ----------------
        private static Creature _target;
        private static float _nextEvalTime;
        private static float _lastLogTime;
        private static float _lastNoTargetLog;
        private static float _missSeconds;      // 当前目标连续丢失的累计时间
        private static bool _playerReady;
        private static bool _wasAds;
        private static Item _lastHeldItem;

        private static Vector3 _aimSmoothVec;
        private static bool _aimValid;
        private static Vector3 _targetVel;
        private static Vector3 _lastTargetPos;
        private static float _lastTargetTime;
        private static bool _targetVelInit;
        private static Creature _lastTarget;
        private static float _lastTargetDropTime;

        // diagnostics counters
        private static int _stTotal;
        private static int _stAlive;
        private static int _stDist;
        private static int _stOnScreen;
        private static int _stRange;

        // 生物专属瞄点偏移（键=生物名，值=(本地Y偏移, 本地Z偏移)）
        private static readonly Dictionary<string, Vector2> _headOffsets = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);

        // 缓存（避免每帧 GetComponentsInChildren 分配）
        private static readonly Dictionary<int, Collider[]> _colCache = new Dictionary<int, Collider[]>();
        private static readonly Dictionary<int, Renderer[]> _rendCache = new Dictionary<int, Renderer[]>();
        private static readonly List<Creature> _candidates = new List<Creature>();

        // 供 LocalHit 补丁做诊断
        internal static string LastHitInfo = "";

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            _enabled = Config.Bind("基础设置", "锁头功能启用", true, "锁头功能是否启用（可用热键切换）");
            _toggleKey = Config.Bind("基础设置", "开关热键", (KeyCode)258, "按下该键切换锁头功能开启/关闭");
            _diagKey = Config.Bind("基础设置", "诊断热键", (KeyCode)259, "按下该键在日志中打印当前识别到的目标（排障用）");
            _releaseKey = Config.Bind("基础设置", "解除锁定热键", (KeyCode)260, "按下该键立即解除当前锁定并重新识别（默认小键盘4；若与其他mod冲突可在此改键）");
            _requireAds = Config.Bind("基础设置", "仅在开镜时锁定", true, "开启后只有长按右键开镜(ADS)时才把准心锁到目标；未开镜时只做识别，不接管相机");
            _requireAimKey = Config.Bind("基础设置", "需要按住触发键", false, "开启后必须按住下面这个键才锁定");
            _aimKey = Config.Bind("基础设置", "锁头触发热键", (KeyCode)325, "需要按住触发键开启时使用的按键");

            _circleRadius = Config.Bind("识别参数", "屏幕中心识别半径", 1f, "以屏幕中心为圆心的圆形识别半径（单位=半个屏幕高度）。只识别该圆内的生物，防止过度扫描。1.0≈屏幕中央大部分；0.5=只认很靠近准星的；1.5≈几乎全屏");
            _maxDistance = Config.Bind("识别参数", "最大识别距离", 120f, "超过该距离(米)的生物不识别（默认120，避免锁到天上极远的海鸥）");
            _minDistance = Config.Bind("识别参数", "最小识别距离", 0.3f, "小于该距离(米)的生物不识别");
            _bossBonus = Config.Bind("识别参数", "BOSS优先量", 12f, "BOSS 在识别时的优先加成（单位与识别半径相同）。0=完全按离准星最近");
            _scanInterval = Config.Bind("识别参数", "识别间隔", 0.08f, "每隔多少秒重新识别一次（秒）。识别始终在跑，此项控制开销");
            _requireLineOfSight = Config.Bind("识别参数", "需要视线无遮挡", false, "开启后目标头部被地形/船挡住时不识别（更真实但可能锁不到水下的鱼）");
            _missGrace = Config.Bind("识别参数", "丢失目标宽限(秒)", 0.5f, "“已经锁定”的目标短暂跑出识别圆/被遮挡时，连续丢失超过该秒数才解除锁定。飞行BOSS(如信天翁)建议 0.4~0.8，可显著减少反复丢失");
            _releaseFactor = Config.Bind("识别参数", "解除锁定半径倍率", 1.6f, "已锁定的目标允许偏离识别圆到该倍率仍不丢（滞回）。1=与识别半径相同；飞行BOSS建议 1.5~2.0");

            _headDepth = Config.Bind("瞄点参数", "头部命中深度比例", 0.5f, "瞄点 = 在“爆头分界面(HeadPos)”与“模型最前端”之间按该比例取点。游戏爆头判定 = 命中点本地Z > HeadPos，所以只要 >0 就一定算爆头；0.5 取头部中段最稳，过大容易打到最前端尖部");
            _headHeightBias = Config.Bind("瞄点参数", "头部高度偏置", 0.35f, "瞄点在模型竖直范围上的位置：0=模型中线，1=模型顶部。设置在能命中头部模型的高度即可（鱼头通常偏低，鸟/蟹可调高）");
            _snapToCollider = Config.Bind("瞄点参数", "瞄点吸附到碰撞体", true, "把计算出的瞄点吸附到生物实际碰撞体表面，保证枪口指向“打得到的地方”。强烈建议开启");
            _preferRenderer = Config.Bind("瞄点参数", "瞄点用视觉模型包围盒", true, "用“可见模型(渲染器)”的本地包围盒计算瞄点——方向正确、不会因生物转身/俯冲而漂移（信天翁这类飞行BOSS必须靠它）。取不到模型时自动回退到碰撞体");
            _worldUpOffset = Config.Bind("瞄点参数", "垂直偏移用世界上方", true, "上移/下移的偏移沿“世界上方”施加，而不是生物自己的上下轴——飞行/俯冲的生物更稳定、更好调；前后偏移仍沿生物头朝向");
            _fallbackUp = Config.Bind("瞄点参数", "无法取模型时的上移", 0.35f, "取不到碰撞体/网格时的兜底：在质心基础上按模型半高×该比例上移");

            _leadTime = Config.Bind("跟踪参数", "提前量(秒)", 0f, "速度预测提前量(秒)。默认0=不做预判，纯硬锁当前头部。远处高速海鸥打不到时再调到 0.05~0.12");
            _maxTargetSpeed = Config.Bind("跟踪参数", "速度估计上限(米/秒)", 40f, "目标速度估计的钳制上限，防止切换目标瞬间的速度尖峰");
            _maxRotSpeed = Config.Bind("跟踪参数", "最大转速度", 4000f, "相机朝目标转动的速度上限（度/秒）。仅压制“刚锁上/瞬移”的一帧跳变");
            _trackSharpness = Config.Bind("跟踪参数", "跟踪平滑", 0f, "0=硬锁（准心完全贴合，最跟手）；>0 轻度平滑，越大越柔但略滞后");
            _aimSmooth = Config.Bind("跟踪参数", "瞄准点平滑", 0f, "0=不平滑（最跟手）；若准心抖动可调到 0.15~0.3");
            _maxLeadDist = Config.Bind("跟踪参数", "提前位移上限(米)", 1f, "提前量造成的偏移最大不超过这么多米，防止速度尖峰把准心推飞");

            _lockBirds = Config.Bind("目标筛选", "锁定鸟类", true, "海鸥、信天翁等天上的鸟");
            _lockFish = Config.Bind("目标筛选", "锁定鱼类", true, "水里的鱼");
            _lockBoss = Config.Bind("目标筛选", "锁定BOSS", true, "带 BossType 的大型生物（识别时优先）");
            _lockOthers = Config.Bind("目标筛选", "锁定其他生物", true, "螃蟹等不属于鸟/鱼的生物");

            _forceHeadshot = Config.Bind("爆头(方式A)", "强制爆头", true, "【推荐开启】开启后：只要子弹/箭命中生物，无论打中哪个部位，都按爆头结算伤害（×1.5）。对所有远程武器生效（手枪/步枪/狙击/弓/鱼叉等），且【不受开镜状态限制】——任何时候开枪都算爆头。原理：把命中点沿生物本地+Z推到爆头分界面之后；服务端不校验命中点，100%生效");
            _forceHeadshotDepth = Config.Bind("爆头(方式A)", "强制爆头深度", 0.1f, "强制爆头时，命中点在爆头分界面之后推进的比例(相对爆头区厚度)，0.1 已足够");

            _overrideEnabled = Config.Bind("生物专属瞄点", "启用专属瞄点", true, "开启后，可为不同生物单独记录瞄点偏移（用本组的热键在游戏里对着目标实时微调，自动保存到“专属瞄点表”）");
            _overrideTable = Config.Bind("生物专属瞄点", "专属瞄点表", "", "自动维护，一般不用手改。格式： 名称=y,z; 名称2=y,z （本地坐标偏移，单位米）。名称=生物名(不含(Clone))");
            _nudgeStep = Config.Bind("生物专属瞄点", "调整步长(米)", 0.15f, "每次按调整热键移动瞄点的距离（米）。数值越大调得越快");
            _nudgeUpKey = Config.Bind("生物专属瞄点", "瞄点上移热键", (KeyCode)264, "把当前锁定目标的瞄点向上移（小键盘8）");
            _nudgeDownKey = Config.Bind("生物专属瞄点", "瞄点下移热键", (KeyCode)261, "把当前锁定目标的瞄点向下移（小键盘5）");
            _nudgeForwardKey = Config.Bind("生物专属瞄点", "瞄点前移热键", (KeyCode)265, "把当前锁定目标的瞄点沿本地+Z(头部方向)前移（小键盘9）");
            _nudgeBackKey = Config.Bind("生物专属瞄点", "瞄点后移热键", (KeyCode)263, "把当前锁定目标的瞄点沿本地+Z向后移（小键盘7）");
            _nudgeResetKey = Config.Bind("生物专属瞄点", "瞄点重置热键", (KeyCode)262, "清除当前生物的专属瞄点偏移，恢复自动计算（小键盘6）");

            _verbose = Config.Bind("调试", "详细日志", false, "开启后每 0.5 秒输出一次锁定状态");
            _dumpHeadZone = Config.Bind("调试", "诊断输出头部信息", true, "诊断热键被按下时，是否额外输出每个生物的 HeadPos 与本地包围盒（用于核对不同生物的头部建模位置）");

            ParseHeadOffsets();

            _rotField = AccessTools.Field(typeof(PlayerCamera), "_rot");
            _moveRotField = AccessTools.Field(typeof(PlayerCamera), "_moveRot");
            _shakeMultiField = AccessTools.Field(typeof(PlayerCamera), "_screenShakeRotMulti");

            bool ok = _rotField != null;
            if (!ok) Log.LogError("[aimlock] 反射失败: PlayerCamera._rot");

            try
            {
                new Harmony("me.yexiao07.plugin.fishaimlock").PatchAll();
                Log.LogInfo("[aimlock] FishAimLock v5.1 加载成功，Harmony Patch 已应用");
            }
            catch (Exception ex)
            {
                Log.LogError("[aimlock] Harmony Patch 失败: " + ex);
                ok = false;
            }

            Log.LogInfo("[aimlock] 初始化: " + (ok ? "OK" : "有问题")
                + " | 筛选 鸟=" + _lockBirds.Value + " 鱼=" + _lockFish.Value + " BOSS=" + _lockBoss.Value + " 其他=" + _lockOthers.Value
                + " | 识别圆半径=" + _circleRadius.Value + " 最大距=" + _maxDistance.Value + "m 提前量=" + _leadTime.Value + "s"
                + " | 强制爆头=" + _forceHeadshot.Value);
            LogKeyHelp();
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey.Value))
            {
                _enabled.Value = !_enabled.Value;
                Log.LogInfo("[aimlock] 锁头功能 -> " + (_enabled.Value ? "开启" : "关闭"));
            }
            if (Input.GetKeyDown(_diagKey.Value)) DumpDiagnostics();
            if (Input.GetKeyDown(_releaseKey.Value) && _target != null)
            {
                Log.LogInfo("[aimlock] 手动解除锁定: " + TargetName(_target));
                DropTarget();
            }

            // 生物专属瞄点微调（自动记录并保存）
            if (_overrideEnabled.Value && _target != null)
            {
                float s = Mathf.Max(0.01f, _nudgeStep.Value);
                if (Input.GetKeyDown(_nudgeUpKey.Value)) NudgeOffset(_target, +s, 0f, "上移");
                if (Input.GetKeyDown(_nudgeDownKey.Value)) NudgeOffset(_target, -s, 0f, "下移");
                if (Input.GetKeyDown(_nudgeForwardKey.Value)) NudgeOffset(_target, 0f, +s, "前移");
                if (Input.GetKeyDown(_nudgeBackKey.Value)) NudgeOffset(_target, 0f, -s, "后移");
                if (Input.GetKeyDown(_nudgeResetKey.Value)) ResetOffset(_target);
            }
        }

        // ==================================================================
        //  相机接管
        // ==================================================================
        internal static void Tick(PlayerCamera cam)
        {
            if (!_enabled.Value || cam == null || _rotField == null) return;

            Player localPlayer = Player.LocalPlayer;
            if (localPlayer == null)
            {
                if (_playerReady) { _playerReady = false; LogInfo("本地玩家消失（已离开存档）"); }
                return;
            }
            if (!_playerReady) { _playerReady = true; LogInfo("本地玩家已就绪，识别逻辑开始接管"); }
            if (localPlayer.BlockInputs) return;

            PlayerCamera pc = null;
            try { pc = localPlayer.Camera; } catch { pc = null; }
            if (pc != null && cam != pc) return;

            TrackHeldItem(localPlayer);

            bool ads = IsAimingDownSights(localPlayer);
            if (ads != _wasAds)
            {
                _wasAds = ads;
                LogInfo("开镜状态 -> " + (ads ? "已开镜(ADS)" : "未开镜"));
                if (ads) _nextEvalTime = 0f;
            }

            if (Time.time >= _nextEvalTime)
            {
                _nextEvalTime = Time.time + Mathf.Max(0.02f, _scanInterval.Value);
                Scan(cam);
            }

            if ((_requireAds.Value && !ads) || (_requireAimKey.Value && !Input.GetKey(_aimKey.Value))) return;

            if (_target == null)
            {
                if (Time.time - _lastNoTargetLog > 2f)
                {
                    _lastNoTargetLog = Time.time;
                    LogInfo($"开镜中但无可锁目标 | 候选{_stTotal} 存活{_stAlive} 距离内{_stDist} 屏幕内{_stOnScreen} 圆内{_stRange} | 半径{_circleRadius.Value} 有效{_minDistance.Value:F1}~{_maxDistance.Value:F0}m");
                }
                return;
            }

            Vector3 rot = (Vector3)_rotField.GetValue(cam);
            Vector3 camPos = cam.CamTransform.position;
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);

            Vector3 head = ComputeHeadPoint(_target);

            float now = Time.time;
            if (_targetVelInit)
            {
                float d = now - _lastTargetTime;
                if (d > 0.004f)
                {
                    Vector3 v = (head - _lastTargetPos) / d;
                    if (v.magnitude > _maxTargetSpeed.Value) v = v.normalized * _maxTargetSpeed.Value;
                    _targetVel = Vector3.Lerp(_targetVel, v, 1f - Mathf.Exp(-10f * d));
                }
            }
            _lastTargetPos = head; _lastTargetTime = now; _targetVelInit = true;

            Vector3 aim = head;
            if (_leadTime.Value > 0.001f)
                aim += Vector3.ClampMagnitude(_targetVel * _leadTime.Value, Mathf.Max(0f, _maxLeadDist.Value));

            if (_aimSmooth.Value > 0.001f && _aimValid)
                _aimSmoothVec = Vector3.Lerp(_aimSmoothVec, aim, 1f - Mathf.Exp(-_aimSmooth.Value * dt));
            else
                _aimSmoothVec = aim;
            _aimValid = true;

            Vector3 aimPt = _aimSmoothVec;

            float moveRotX = 0f, shakeX = 0f, shakeY = 0f, shakeMulti = 0f;
            try
            {
                Vector2 shake = cam.ShakePos; shakeX = shake.x; shakeY = shake.y;
                if (_moveRotField != null) moveRotX = ((Vector3)_moveRotField.GetValue(cam)).x;
                if (_shakeMultiField != null) shakeMulti = (float)_shakeMultiField.GetValue(cam);
            }
            catch { }

            Quaternion look = Quaternion.LookRotation(aimPt - camPos, Vector3.up);
            Vector3 euler = look.eulerAngles;
            float tx = euler.x - moveRotX + shakeY * shakeMulti;
            float ty = euler.y - shakeX * shakeMulti;

            float dx = Mathf.DeltaAngle(rot.x, tx);
            float dy = Mathf.DeltaAngle(rot.y, ty);

            if (_trackSharpness.Value > 0.001f)
            {
                float k = 1f - Mathf.Exp(-_trackSharpness.Value * dt);
                dx *= k; dy *= k;
            }
            float maxStep = _maxRotSpeed.Value * dt;
            float mag = Mathf.Sqrt(dx * dx + dy * dy);
            if (mag > maxStep && mag > 0.0001f)
            {
                float s = maxStep / mag; dx *= s; dy *= s; mag = maxStep;
            }
            rot.x += dx; rot.y += dy; rot.x = Mathf.Clamp(rot.x, -90f, 90f);
            _rotField.SetValue(cam, rot);

            if (_verbose.Value && Time.time - _lastLogTime > 0.5f)
            {
                _lastLogTime = Time.time;
                LogInfo(string.Format("锁定 {0} [{1}] | 距离 {2:F1}m | 本帧修正 {3:F2}° | 提前量={4:F2}s 速度={5:F1}m/s | 瞄点本地Z={6:F2}",
                    TargetName(_target), KindOf(_target), (aimPt - camPos).magnitude, mag, _leadTime.Value, _targetVel.magnitude, LocalZAim(_target, aimPt)));
            }
        }

        // ==================================================================
        //  识别
        // ==================================================================
        private static void Scan(PlayerCamera cam)
        {
            _candidates.Clear();
            CollectCandidates(_candidates);
            _stTotal = _candidates.Count; _stAlive = 0; _stDist = 0; _stOnScreen = 0; _stRange = 0;

            Camera rc = GetRenderCamera(cam);
            if (rc == null) return;
            float halfW = rc.pixelWidth * 0.5f;
            float halfH = rc.pixelHeight * 0.5f;
            if (halfH < 1f) return;

            float radius = Mathf.Max(0.05f, _circleRadius.Value);
            float releaseFactor = Mathf.Max(1f, _releaseFactor.Value);
            Vector3 camPos = rc.transform.position;

            Creature best = null;
            float bestScore = float.MaxValue, bestDist = float.MaxValue, bestReal = float.MaxValue;

            for (int i = 0; i < _candidates.Count; i++)
            {
                Creature c = _candidates[i];
                if (c == null || !IsAliveCreature(c)) continue;
                _stAlive++;
                if (!Wants(c)) continue;

                Vector3 head = ComputeHeadPoint(c);
                float dist = Vector3.Distance(head, camPos);
                if (dist > _maxDistance.Value || dist < _minDistance.Value) continue;
                _stDist++;

                if (_requireLineOfSight.Value && IsHeadObstructed(camPos, head)) continue;

                Vector3 sp = rc.WorldToScreenPoint(head);
                if (sp.z <= 0f) continue;
                _stOnScreen++;

                float nx = (sp.x - halfW) / halfH;
                float ny = (sp.y - halfH) / halfH;
                float off = Mathf.Sqrt(nx * nx + ny * ny);

                // 已锁定的目标用“放大后的圆”做滞回，避免贴边时反复丢失
                float allowed = (c == _target) ? radius * releaseFactor : radius;
                if (off > allowed) continue;
                _stRange++;

                float score = off * 100f;
                if (_lockBoss.Value && IsBoss(c)) score -= _bossBonus.Value;
                if (c == _target) score -= 25f;   // 粘滞：优先保持当前目标
                if (_lastTarget != null && c == _lastTarget && Time.time - _lastTargetDropTime < 1.5f) score -= 30f;

                if (score < bestScore)
                {
                    bestScore = score; best = c; bestDist = off; bestReal = dist;
                }
            }

            if (best != null)
            {
                _missSeconds = 0f;
                if (best != _target)
                {
                    _target = best; _aimValid = false; _targetVelInit = false;
                    LogInfo(string.Format("识别到目标: {0} [{1}] | 离准星 {2:F2} 距离 {3:F1}m HeadPos={4:F2}{5}",
                        TargetName(best), KindOf(best), bestDist, bestReal, SafeHeadPos(best), IsBoss(best) ? " (BOSS优先)" : ""));
                }
                _lastTarget = best;
            }
            else if (_target != null)
            {
                // 宽限：连续丢失累计超过 _missGrace 秒才真正解除锁定
                _missSeconds += Mathf.Max(0.02f, _scanInterval.Value);
                if (_missSeconds >= Mathf.Max(0f, _missGrace.Value))
                {
                    LogInfo("目标离开识别圆/失效: " + TargetName(_target));
                    DropTarget();
                }
            }
        }

        private static Camera GetRenderCamera(PlayerCamera cam)
        {
            try { if (GameInfo.CurCamera != null) return GameInfo.CurCamera; } catch { }
            if (cam != null)
            {
                try
                {
                    if (cam.CamTransform != null)
                    {
                        Camera component = cam.CamTransform.GetComponent<Camera>();
                        if (component != null) return component;
                    }
                }
                catch { }
            }
            return Camera.main;
        }

        private static void CollectCandidates(List<Creature> list)
        {
            try
            {
                foreach (KeyValuePair<Transform, Item> kv in ItemManager.Items)
                {
                    Item it = kv.Value;
                    if (it == null) continue;
                    Creature cr = it.Creature;
                    if (cr != null && !list.Contains(cr)) list.Add(cr);
                }
            }
            catch (Exception ex) { LogWarning("ItemManager 扫描异常: " + ex.Message); }
        }

        private static bool Wants(Creature c)
        {
            if (c == null) return false;
            if (IsBoss(c)) return _lockBoss.Value;
            if (c is Bird) return _lockBirds.Value;
            if (c is Fish) return _lockFish.Value;
            return _lockOthers.Value;
        }

        // ==================================================================
        //  瞄点：以游戏自己的爆头判定 (本地Z > HeadPos) 为准
        // ==================================================================
        internal static Vector3 ComputeHeadPoint(Creature c)
        {
            if (c == null) return Vector3.zero;
            Transform t = c.transform;
            float headPos = SafeHeadPos(c);

            Bounds lb;
            bool hasBounds = TryGetLocalBounds(c, out lb);

            if (hasBounds && lb.size.z > 0.0001f)
            {
                float minZ = lb.min.z, maxZ = lb.max.z;
                float zAim;
                if (headPos <= minZ + 0.0001f && headPos <= 0.0001f)
                {
                    // HeadPos 未配置(默认0)或落在模型最后端：用“模型前段”代替
                    zAim = Mathf.Lerp(lb.center.z, maxZ, 0.6f);
                }
                else
                {
                    float start = Mathf.Clamp(headPos, minZ, maxZ);
                    zAim = Mathf.Lerp(start, maxZ, Mathf.Clamp01(_headDepth.Value));
                }
                zAim = Mathf.Clamp(zAim, minZ, Mathf.Max(minZ, maxZ - 0.0005f));

                float yAim = Mathf.Lerp(lb.center.y, lb.max.y, Mathf.Clamp01(_headHeightBias.Value));
                Vector3 aimLocal = new Vector3(lb.center.x, yAim, zAim);
                Vector3 world = t.TransformPoint(aimLocal);
                world = ApplyOffset(c, t, world);          // 生物专属偏移（可在游戏里用热键微调）
                if (_snapToCollider.Value) world = SnapToCollider(c, world);
                return world;
            }

            // 兜底：质心 + 上移
            Vector3 wCenter = WorldCenter(c);
            float up = UpOffsetFromBounds(c);
            Vector3 fw = wCenter + Vector3.up * up;
            Vector3 fl = t.InverseTransformPoint(fw);
            fl.z += Mathf.Max(0.05f, headPos);
            return ApplyOffset(c, t, t.TransformPoint(fl));
        }

        // 应用生物专属偏移。
        // 默认“垂直偏移用世界上方”：上/下沿世界 up，前/后沿生物本地 +Z（头朝向）——
        // 这样对会俯冲/侧倾的飞行生物(如信天翁)也能稳定地调，不会因姿态变化而漂。
        private static Vector3 ApplyOffset(Creature c, Transform t, Vector3 world)
        {
            Vector3 off = GetOffset(c);
            if (off == Vector3.zero) return world;
            if (_worldUpOffset == null || _worldUpOffset.Value)
            {
                Vector3 fwd = t.forward;
                if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
                return world + Vector3.up * off.y + fwd.normalized * off.z;
            }
            return t.TransformPoint(t.InverseTransformPoint(world) + off);
        }

        // ---------- 生物专属瞄点偏移 ----------
        private static Vector3 GetOffset(Creature c)
        {
            if (c == null || !_overrideEnabled.Value) return Vector3.zero;
            Vector2 v;
            if (_headOffsets.TryGetValue(TargetName(c), out v)) return new Vector3(0f, v.y, v.x);
            return Vector3.zero;
        }

        private static void NudgeOffset(Creature c, float dy, float dz, string label)
        {
            if (c == null) return;
            string key = TargetName(c);
            Vector2 cur;
            _headOffsets.TryGetValue(key, out cur);
            cur.y += dy; cur.x += dz;
            _headOffsets[key] = cur;
            SaveHeadOffsets();
            LogInfo(string.Format("专属瞄点[{0}] {1}: 偏移 Y={2:F2} Z={3:F2} (本地坐标，已保存)", key, label, cur.y, cur.x));
        }

        private static void ResetOffset(Creature c)
        {
            if (c == null) return;
            string key = TargetName(c);
            if (_headOffsets.Remove(key)) { SaveHeadOffsets(); LogInfo("专属瞄点[" + key + "] 已清除，恢复自动计算"); }
            else LogInfo("专属瞄点[" + key + "] 本来就没有偏移");
        }

        private static void ParseHeadOffsets()
        {
            _headOffsets.Clear();
            try
            {
                string s = _overrideTable != null ? _overrideTable.Value : null;
                if (string.IsNullOrEmpty(s)) return;
                foreach (string part in s.Split(';'))
                {
                    string e = part.Trim();
                    if (e.Length == 0) continue;
                    int eq = e.LastIndexOf('=');
                    if (eq <= 0) continue;
                    string name = e.Substring(0, eq).Trim();
                    string[] nums = e.Substring(eq + 1).Split(',');
                    if (name.Length == 0 || nums.Length < 2) continue;
                    float y, z;
                    if (float.TryParse(nums[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                        && float.TryParse(nums[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    {
                        _headOffsets[name] = new Vector2(z, y);   // 存储为 (Z, Y)
                    }
                }
                if (_headOffsets.Count > 0) Log.LogInfo("[aimlock] 已加载生物专属瞄点 " + _headOffsets.Count + " 条");
            }
            catch (Exception ex) { LogWarning("解析专属瞄点表失败: " + ex.Message); }
        }

        private static void SaveHeadOffsets()
        {
            try
            {
                List<string> parts = new List<string>();
                foreach (KeyValuePair<string, Vector2> kv in _headOffsets)
                {
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}={1},{2}",
                        kv.Key, kv.Value.y.ToString("0.###", CultureInfo.InvariantCulture), kv.Value.x.ToString("0.###", CultureInfo.InvariantCulture)));
                }
                string s = string.Join("; ", parts);
                _overrideTable.Value = s;
                Instance.Config.Save();
            }
            catch (Exception ex) { LogWarning("保存专属瞄点表失败: " + ex.Message); }
        }

        private static Vector3 WorldCenter(Creature c)
        {
            try { if (c.Rig != null) return c.Rig.worldCenterOfMass; } catch { }
            return c.transform.position;
        }

        private static float UpOffsetFromBounds(Creature c)
        {
            Bounds lb;
            if (TryGetLocalBounds(c, out lb) && lb.size.sqrMagnitude > 0.0001f)
                return lb.extents.y * _fallbackUp.Value;
            return 0.25f;
        }

        private static bool TryGetLocalBounds(Creature c, out Bounds localBounds)
        {
            localBounds = default(Bounds);
            Transform t = c.transform;
            bool any = false;
            bool preferRenderer = _preferRenderer == null || _preferRenderer.Value;   // 默认用可见模型

            if (!preferRenderer)
            {
                Collider[] cols = GetColliders(c);
                if (cols != null)
                {
                    for (int i = 0; i < cols.Length; i++)
                    {
                        Collider col = cols[i];
                        if (!IsUsableCollider(col)) continue;
                        EncapsulateColliderLocal(ref localBounds, ref any, t, col);
                    }
                }
            }
            if (!any)
            {
                Renderer[] rs = GetRenderers(c);
                if (rs != null)
                {
                    for (int i = 0; i < rs.Length; i++)
                    {
                        Renderer r = rs[i];
                        if (!IsUsableRenderer(r)) continue;
                        EncapsulateLocalBounds(ref localBounds, ref any, t.worldToLocalMatrix * r.localToWorldMatrix, r.localBounds);
                    }
                }
            }
            if (!any)
            {
                // 最后兜底：用碰撞体的世界AABB反算（旋转时会略偏大，但好过没有）
                Collider[] cols = GetColliders(c);
                if (cols != null)
                    for (int i = 0; i < cols.Length; i++)
                    {
                        Collider col = cols[i];
                        if (!IsUsableCollider(col)) continue;
                        EncapsulateWorldAabb(ref localBounds, ref any, t, col.bounds);
                    }
            }
            if (!any)
            {
                localBounds = new Bounds(Vector3.zero, Vector3.one * 0.5f);
                return false;
            }
            return true;
        }

        private static bool IsUsableCollider(Collider col)
        {
            if (col == null || !col.enabled || col.isTrigger) return false;
            try { return col.gameObject.activeInHierarchy; } catch { return true; }
        }

        // 只认真正的“模型网格”。排除粒子/拖尾/线条渲染器——它们的包围盒常常远大于生物本体，
        // 会把瞄点撑大、抬高（信天翁的“便便”粒子就是一个典型）。
        private static bool IsUsableRenderer(Renderer r)
        {
            if (r == null || !r.enabled) return false;
            return r is SkinnedMeshRenderer || r is MeshRenderer;
        }

        // 把 rt 空间的本地包围盒 8 角转换到 creature 本地空间后合并。
        // 关键：用“本地包围盒 + 变换矩阵”，而不是把世界AABB的角反算回来——
        // 后者在生物转身/俯冲时会把盒子放大且随姿态漂移（信天翁锁不住的主因）。
        private static void EncapsulateLocalBounds(ref Bounds local, ref bool any, Matrix4x4 toCreatureLocal, Bounds b)
        {
            Vector3 cc = b.center, e = b.extents;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 p = toCreatureLocal.MultiplyPoint3x4(cc + new Vector3(e.x * sx, e.y * sy, e.z * sz));
                        if (!any) { local = new Bounds(p, Vector3.zero); any = true; }
                        else local.Encapsulate(p);
                    }
        }

        private static void EncapsulateColliderLocal(ref Bounds local, ref bool any, Transform t, Collider col)
        {
            Bounds b;
            if (TryColliderLocalBounds(col, out b))
                EncapsulateLocalBounds(ref local, ref any, t.worldToLocalMatrix * col.transform.localToWorldMatrix, b);
            else
                EncapsulateWorldAabb(ref local, ref any, t, col.bounds);
        }

        // 取碰撞体在其自身 transform 空间的本地包围盒（避免世界AABB随旋转膨胀）
        private static bool TryColliderLocalBounds(Collider col, out Bounds b)
        {
            b = default(Bounds);
            BoxCollider bc = col as BoxCollider;
            if (bc != null) { b = new Bounds(bc.center, bc.size); return true; }
            SphereCollider sc = col as SphereCollider;
            if (sc != null) { b = new Bounds(sc.center, Vector3.one * (sc.radius * 2f)); return true; }
            CapsuleCollider cc = col as CapsuleCollider;
            if (cc != null)
            {
                float d = Mathf.Max(cc.height, cc.radius * 2f);
                float w = cc.radius * 2f;
                Vector3 size = (cc.direction == 0) ? new Vector3(d, w, w)
                             : (cc.direction == 1) ? new Vector3(w, d, w)
                             : new Vector3(w, w, d);
                b = new Bounds(cc.center, size);
                return true;
            }
            MeshCollider mc = col as MeshCollider;
            if (mc != null && mc.sharedMesh != null) { b = mc.sharedMesh.bounds; return true; }
            return false;
        }

        private static void EncapsulateWorldAabb(ref Bounds local, ref bool any, Transform t, Bounds wb)
        {
            Vector3 cc = wb.center, e = wb.extents;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 wc = cc + new Vector3(e.x * sx, e.y * sy, e.z * sz);
                        Vector3 lc = t.InverseTransformPoint(wc);
                        if (!any) { local = new Bounds(lc, Vector3.zero); any = true; }
                        else local.Encapsulate(lc);
                    }
        }

        private static Vector3 SnapToCollider(Creature c, Vector3 world)
        {
            Collider[] cols = GetColliders(c);
            if (cols == null || cols.Length == 0) return world;
            float best = float.MaxValue;
            Vector3 bestP = world;
            for (int i = 0; i < cols.Length; i++)
            {
                Collider col = cols[i];
                if (!IsUsableCollider(col)) continue;
                Vector3 p;
                try { p = col.ClosestPoint(world); } catch { continue; }
                float d = (p - world).sqrMagnitude;
                if (d < best) { best = d; bestP = p; }
            }
            return bestP;
        }

        private static Collider[] GetColliders(Creature c)
        {
            int id = c.GetInstanceID();
            Collider[] arr;
            if (_colCache.TryGetValue(id, out arr) && arr != null) return arr;
            try { arr = c.GetComponentsInChildren<Collider>(true); } catch { arr = new Collider[0]; }
            if (_colCache.Count > 512) _colCache.Clear();
            _colCache[id] = arr;
            return arr;
        }

        private static Renderer[] GetRenderers(Creature c)
        {
            int id = c.GetInstanceID();
            Renderer[] arr;
            if (_rendCache.TryGetValue(id, out arr) && arr != null) return arr;
            try { arr = c.GetComponentsInChildren<Renderer>(true); } catch { arr = new Renderer[0]; }
            if (_rendCache.Count > 512) _rendCache.Clear();
            _rendCache[id] = arr;
            return arr;
        }

        private static float SafeHeadPos(Creature c)
        {
            try { return c.HeadPos; } catch { return 0f; }
        }

        private static float LocalZAim(Creature c, Vector3 world)
        {
            try { return c.transform.InverseTransformPoint(world).z; } catch { return 0f; }
        }

        private static bool IsHeadObstructed(Vector3 camPos, Vector3 head)
        {
            try
            {
                return Physics.Linecast(camPos, head, (int)GameInfo.LevelLayer | (int)GameInfo.BoatLayer, QueryTriggerInteraction.Ignore);
            }
            catch { return false; }
        }

        // ==================================================================
        //  方式A：强制爆头（由 Patch_Creature_LocalHit 调用）
        // ==================================================================
        internal static void OnCreatureLocalHit(Creature c, ref Vector3 point, bool rangedHit)
        {
            if (c == null) return;
            try
            {
                Transform t = c.transform;
                float headPos = SafeHeadPos(c);
                Vector3 local = t.InverseTransformPoint(point);
                LastHitInfo = string.Format("{0} 命中点本地Z={1:F2} HeadPos={2:F2} {3}",
                    TargetName(c), local.z, headPos, local.z > headPos ? "爆头区" : "非爆头区");

                if (!_forceHeadshot.Value || !rangedHit) return;
                if (local.z > headPos) return;            // 本来就是爆头

                // 把命中点沿本地 +Z 推到爆头分界面之后 —— 与几何无关，必定通过爆头判定
                float thickness;
                Bounds lb;
                if (TryGetLocalBounds(c, out lb) && lb.size.z > 0.0001f && lb.max.z > headPos)
                    thickness = Mathf.Max(0.01f, (lb.max.z - headPos) * Mathf.Clamp01(_forceHeadshotDepth.Value));
                else
                    thickness = Mathf.Max(0.01f, Mathf.Abs(headPos) * 0.1f + 0.01f);

                local.z = headPos + thickness;
                point = t.TransformPoint(local);
            }
            catch (Exception ex) { LogWarning("强制爆头处理异常: " + ex.Message); }
        }

        // ==================================================================
        //  辅助
        // ==================================================================
        private static bool IsAimingDownSights(Player p)
        {
            try
            {
                Item held = p.Holding != null ? p.Holding.HeldItem : null;
                if (held == null) return false;
                Weapon w = held.Weapon;
                return w != null && w.IsAds;
            }
            catch { return false; }
        }

        private static bool IsAliveCreature(Creature c)
        {
            if (c == null) return false;
            try
            {
                if (!c.isActiveAndEnabled) return false;
                if (c.IsDeinitializing) return false;
                return !c.IsDead;
            }
            catch { return !c.IsDead; }
        }

        private static bool IsBoss(Creature c)
        {
            try { return (int)c.BossType > 0; } catch { return false; }
        }

        private static void DropTarget()
        {
            if (_target != null) { _lastTarget = _target; _lastTargetDropTime = Time.time; }
            _target = null; _aimValid = false; _targetVelInit = false; _nextEvalTime = 0f; _missSeconds = 0f;
        }

        private static void TrackHeldItem(Player p)
        {
            Item held = null;
            try { held = p.Holding != null ? p.Holding.HeldItem : null; } catch { held = null; }
            if (held != _lastHeldItem)
            {
                _lastHeldItem = held;
                LogInfo("手持 -> " + (held != null ? CleanName(held.name) : "空手") + " | 是武器=" + ((held != null && held.Weapon != null) ? "是" : "否"));
            }
        }

        private static string KindOf(Creature c)
        {
            if (c == null) return "?";
            string s = (c is Bird) ? "鸟" : ((c is Fish) ? "鱼" : "其他");
            if (IsBoss(c)) s = "BOSS/" + s;
            return s;
        }

        private static string TargetName(Creature c) => c != null ? CleanName(c.name) : "null";

        private static string CleanName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "?";
            if (n.EndsWith("(Clone)")) n = n.Substring(0, n.Length - 7);
            return n;
        }

        // ==================================================================
        //  终端热键说明
        // ==================================================================
        private static void LogKeyHelp()
        {
            try
            {
                Log.LogInfo("[aimlock] ================ FishAimLock 热键说明 ================");
                Log.LogInfo("[aimlock]   " + KeyName(_toggleKey) + "       开关锁头功能（开/关）");
                Log.LogInfo("[aimlock]   " + KeyName(_diagKey) + "       诊断：打印识别目标 + 每个生物的 HeadPos/包围盒/瞄点");
                Log.LogInfo("[aimlock]   " + KeyName(_releaseKey) + "       解除当前锁定并重新识别");
                Log.LogInfo("[aimlock]   " + KeyName(_nudgeUpKey) + "       当前目标瞄点上移（自动保存为该生物的专属瞄点）");
                Log.LogInfo("[aimlock]   " + KeyName(_nudgeDownKey) + "       当前目标瞄点下移");
                Log.LogInfo("[aimlock]   " + KeyName(_nudgeForwardKey) + "       当前目标瞄点沿头部方向前移");
                Log.LogInfo("[aimlock]   " + KeyName(_nudgeBackKey) + "       当前目标瞄点后移");
                Log.LogInfo("[aimlock]   " + KeyName(_nudgeResetKey) + "       清除当前生物的专属瞄点偏移");
                Log.LogInfo("[aimlock]   " + KeyName(_aimKey) + "       按住触发（当“需要按住触发键”开启时）");
                Log.LogInfo("[aimlock] 方式A 强制爆头=" + (_forceHeadshot != null && _forceHeadshot.Value) + "（对所有远程武器生效，且不受开镜限制）");
                Log.LogInfo("[aimlock] ====================================================");
            }
            catch (Exception ex) { LogWarning("打印热键说明失败: " + ex.Message); }
        }

        private static string KeyName(ConfigEntry<KeyCode> e)
        {
            if (e == null) return "?";
            KeyCode k = e.Value;
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
                case KeyCode.Mouse0: return "鼠标左键";
                case KeyCode.Mouse1: return "鼠标右键";
                case KeyCode.Mouse2: return "鼠标中键";
                default: return k.ToString();
            }
        }

        // ==================================================================
        //  诊断
        // ==================================================================
        private static void DumpDiagnostics()
        {
            Player p = Player.LocalPlayer;
            Log.LogInfo("========== [aimlock] 诊断 ==========");
            Log.LogInfo("开关=" + _enabled.Value + " 筛选: 鸟=" + _lockBirds.Value + " 鱼=" + _lockFish.Value + " BOSS=" + _lockBoss.Value + " 其他=" + _lockOthers.Value);
            Log.LogInfo("机制: 始终在【屏幕中心圆】内识别最近生物(WorldToScreenPoint)，开镜(ADS)时锁定其爆头区");
            Log.LogInfo("游戏爆头判定: rangedHit && transform.InverseTransformPoint(hitPoint).z > HeadPos  →  伤害 ×" + GameInfo.HeadShotDamageMulti);
            Log.LogInfo("瞄点: 本地Z = Lerp(clamp(HeadPos), 模型最前端, " + _headDepth.Value + ") | 高度偏置=" + _headHeightBias.Value + " 吸附碰撞体=" + _snapToCollider.Value
                + " | 用视觉模型包围盒=" + (_preferRenderer == null || _preferRenderer.Value) + " 垂直偏移用世界上方=" + (_worldUpOffset == null || _worldUpOffset.Value));
            Log.LogInfo("锁定稳定性: 丢失宽限=" + _missGrace.Value + "s 解除半径倍率=" + _releaseFactor.Value + " 粘滞=开");
            Log.LogInfo("方式A 强制爆头=" + _forceHeadshot.Value + "  (把命中点本地Z推到 HeadPos 之后；对全部远程武器生效，不受开镜限制)");
            Log.LogInfo("生物专属瞄点: 启用=" + _overrideEnabled.Value + " 条目=" + _headOffsets.Count + " 步长=" + _nudgeStep.Value + "m");

            if (p == null) { Log.LogWarning("Player.LocalPlayer 为空（尚未进入存档？）"); return; }

            Item held = p.Holding != null ? p.Holding.HeldItem : null;
            Log.LogInfo("手持=" + (held != null ? CleanName(held.name) : "无") + " | 是武器=" + ((held != null && held.Weapon != null) ? "有" : "无") + " | 开镜=" + IsAimingDownSights(p));
            Log.LogInfo("当前识别=" + (_target != null ? (TargetName(_target) + " [" + KindOf(_target) + "]") : "无"));

            PlayerCamera cam = null;
            try { cam = p.Camera; } catch { }
            Camera rc = GetRenderCamera(cam);
            if (rc == null) { Log.LogWarning("找不到渲染相机"); return; }
            float halfW = rc.pixelWidth * 0.5f;
            float halfH = rc.pixelHeight * 0.5f;
            float radius = Mathf.Max(0.05f, _circleRadius.Value);
            Vector3 camPos = rc.transform.position;

            _candidates.Clear();
            CollectCandidates(_candidates);
            int alive = 0;
            foreach (Creature c in _candidates) if (IsAliveCreature(c)) alive++;
            Log.LogInfo("候选=" + _candidates.Count + " 存活=" + alive);
            Log.LogInfo("识别圆: 屏幕中心半径 " + _circleRadius.Value + "，取离中心最近者（BOSS 优先 " + _bossBonus.Value + "）");
            Log.LogInfo("锁定稳定性: 丢失宽限 " + _missGrace.Value + "s，解除半径倍率 " + _releaseFactor.Value + "，粘滞优先当前目标");
            if (_dumpHeadZone.Value) Log.LogInfo(" 生物 | 类型 | 距离 | HeadPos | 瞄点本地坐标(是否爆头区) | 偏移 | 本地包围盒XYZ | 碰撞体/模型数 | 质心 | 屏幕偏离");

            int n = 0;
            for (int i = 0; i < _candidates.Count && n < 40; i++)
            {
                Creature c = _candidates[i];
                if (c == null) continue;
                n++;

                Vector3 head = ComputeHeadPoint(c);
                float dist = Vector3.Distance(camPos, head);
                Vector3 sp = rc.WorldToScreenPoint(head);
                string screen;
                bool lockable = false;
                if (sp.z <= 0f) screen = "相机后方";
                else
                {
                    float nx = (sp.x - halfW) / halfH;
                    float ny = (sp.y - halfH) / halfH;
                    float off = Mathf.Sqrt(nx * nx + ny * ny);
                    float allowed = (c == _target) ? radius * Mathf.Max(1f, _releaseFactor.Value) : radius;
                    lockable = off <= allowed && dist <= _maxDistance.Value && dist >= _minDistance.Value;
                    screen = "偏离 " + off.ToString("F2") + $"(x{nx:F2},y{ny:F2})" + (c == _target ? "(已锁定宽容)" : "");
                }

                string zone = "";
                if (_dumpHeadZone.Value)
                {
                    Bounds lb;
                    bool hasB = TryGetLocalBounds(c, out lb);
                    string bounds = hasB
                        ? $"X[{lb.min.x:F1},{lb.max.x:F1}] Y[{lb.min.y:F1},{lb.max.y:F1}] Z[{lb.min.z:F1},{lb.max.z:F1}]"
                        : "?";
                    Vector3 aimL = c.transform.InverseTransformPoint(head);
                    float hp = SafeHeadPos(c);
                    Vector3 off = GetOffset(c);
                    int nCol = 0, nRend = 0;
                    Collider[] cs = GetColliders(c); if (cs != null) for (int k = 0; k < cs.Length; k++) if (IsUsableCollider(cs[k])) nCol++;
                    Renderer[] rr = GetRenderers(c); if (rr != null) for (int k = 0; k < rr.Length; k++) if (IsUsableRenderer(rr[k])) nRend++;
                    Vector3 com = Vector3.zero; bool hasCom = false;
                    try { if (c.Rig != null) { com = c.transform.InverseTransformPoint(c.Rig.worldCenterOfMass); hasCom = true; } } catch { }
                    zone = $" HeadPos={hp:F2} 瞄点本地=({aimL.x:F2},{aimL.y:F2},{aimL.z:F2}){(aimL.z > hp ? " 爆头区" : " 非爆头区")}"
                        + $" 偏移Y={off.y:F2} Z={off.z:F2} | 本地包围盒 {bounds} | 碰撞体={nCol} 模型={nRend}"
                        + (hasCom ? $" 质心本地=({com.x:F1},{com.y:F1},{com.z:F1})" : "")
                        + (c == _target ? $" [当前锁定 丢失累计={_missSeconds:F2}s]" : "");
                }

                Log.LogInfo($"  - {TargetName(c)} [{KindOf(c)}] 存活={IsAliveCreature(c)} 距离={dist:F1}m{screen}{(lockable ? " [可锁]" : "")}{zone}");
            }
            Log.LogInfo("====================================");
            LogKeyHelp();
        }

        private static void LogInfo(string msg) { if (Log != null) Log.LogInfo("[aimlock] " + msg); }
        private static void LogWarning(string msg) { if (Log != null) Log.LogWarning("[aimlock] " + msg); }
    }
}
