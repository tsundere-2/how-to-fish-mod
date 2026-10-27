using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace modcontrol
{
    /// <summary>
    /// ModControlCenter —— 游戏内"液态玻璃"风格 Mod 控制悬浮窗 v1.5
    /// v1.5 全盘对齐网页版 mod-liquid-glass-hud:
    ///   1. 场景背景模糊(网页 backdrop-filter):低分辨率 RenderTexture 相机捕获 + RawImage 按面板屏幕区域局部显示,
    ///      GPU 双线性放大即天然模糊,得到真正"看穿场景"的液态玻璃;
    ///   2. 白磨砂玻璃配色:面板/行/键帽烘焙渐变+内高光,图标彩色半透明底;
    ///   3. 多层 box-shadow 烘焙:面板双层外柔影(大柔影+小紧影)、行底部柔影条、内描边;
    ///   4. iOS 弹簧开关(绿渐变轨道+发光+白渐变knob)、Chip 蓝白胶囊、kbd 键帽、
    ///      呼吸绿点(2.4s)、宽度折叠 210px、切换 jolt。
    /// </summary>
    [BepInPlugin("me.yexiao07.plugin.modcontrol", "ModControlCenter", "1.5.0")]
    public class ModControlCenter : BaseUnityPlugin
    {
        internal static ModControlCenter Instance;
        internal static ManualLogSource Log;

        // ---------------- config ----------------
        ConfigEntry<KeyCode> _toggleKey;
        ConfigEntry<bool> _unlockMouse;
        ConfigEntry<bool> _showBall;

        // ---------------- theme ----------------
        static readonly Color TextMain = new Color(1f, 1f, 1f, 1f);
        static readonly Color TextSub  = new Color(1f, 1f, 1f, 0.74f);
        // 深色玻璃底:压住场景杂色(钓线等),让白字有对比度
        static readonly Color GlassDark  = new Color(0.055f, 0.085f, 0.145f, 0.78f);
        static readonly Color GlassSheen = new Color(1f, 1f, 1f, 0.26f);
        static readonly Color GreenDot = new Color32(74, 222, 128, 255);
        static readonly Color DotHaloCol = new Color32(74, 222, 128, 110);

        static readonly Color ChipGreen  = new Color32(121, 232, 152, 255); // #79e898
        static readonly Color ChipBlue   = new Color32(130, 182, 255, 255); // #82b6ff
        static readonly Color ChipCyan   = new Color32(111, 214, 232, 255); // #6fd6e8
        static readonly Color ChipOrange = new Color32(255, 173, 98, 255);  // #ffad62
        static readonly Color ChipGold   = new Color32(255, 212, 105, 255); // #ffd469
        static readonly Color ChipWhite  = new Color(1f, 1f, 1f, 0.92f);

        // ---------------- sprites / font ----------------
        Sprite _spGlassPanel, _spGlassRow, _spGlassRowHover, _spGlassRowOff;
        Sprite _spChip, _spRect, _spPill, _spChipFillOn;
        Sprite _spCircle;
        Sprite _spTrackOn, _spTrackOff, _spKnob, _spLogo;
        Sprite _spSoftDot;
        readonly Dictionary<string, Sprite> _icons = new Dictionary<string, Sprite>();
        Font _font;

        // ---------------- ui refs ----------------
        GameObject _root;
        RectTransform _panel, _rowsRect, _rowsWrap;
        ScrollRect _scroll;
        CanvasGroup _panelCg, _rowsWrapCg;
        Text _countText, _headerTitle, _headerSub, _footText;
        RectTransform _headerTCol;
        Image _dot, _dotHalo, _minBar;
        RectTransform _minChev;
        Color _dotBase, _dotHaloBase;
        // 悬浮窗左上角在画布坐标系中的位置(锚点=左上, 枢轴=左上),展开/收起都以它为原点
        Vector2 _panelPos = new Vector2(36f, -36f);
        const float PanelW = 396f, CollapsedW = 210f, HeadH = 70f, RowHeadH = 62f, Gap = 10f;
        const float CollapsedH = 80f, MaxPanelH = 620f;
        readonly List<RowView> _rows = new List<RowView>();
        readonly List<NavItem> _nav = new List<NavItem>();
        int _navIdx = -1;
        readonly Dictionary<ConfigFile, Coroutine> _saveCos = new Dictionary<ConfigFile, Coroutine>();
        readonly Dictionary<RowView, Coroutine> _rowAnim = new Dictionary<RowView, Coroutine>();
        readonly Dictionary<RowView, Coroutine> _rowColorCos = new Dictionary<RowView, Coroutine>();
        readonly Dictionary<RowView, Coroutine> _joltCos = new Dictionary<RowView, Coroutine>();

        bool _built;
        bool _collapsed;
        bool _panelOpen;
        Coroutine _visCo, _colCo;
        float _t, _syncT;

        // ---------------- known mods ----------------
        struct KnownMod { public string guid, title, sub, sec, key, hk, glyph; public Color col; public bool behaviourToggle; }
        static readonly KnownMod[] Known =
        {
            new KnownMod{ guid="me.yexiao07.plugin.unrivalled", title="无双 · 无敌", sub="免疫全部伤害", hk="Num+", sec="基础设置", key="无双/无敌启用", col=ChipGreen, glyph="shield" },
            new KnownMod{ guid="me.yexiao07.plugin.fishaimlock", title="自动瞄准", sub="锁定视野内生物", hk="Num2", sec="基础设置", key="锁头功能启用", col=ChipBlue, glyph="aim" },
            new KnownMod{ guid="me.yexiao07.plugin.flymod", title="飞行模式", sub="朝准星方向飞行 · 可调速度", hk="", sec="基础设置", key="飞行启用", col=ChipCyan, glyph="fly" },
            new KnownMod{ guid="me.yexiao07.plugin.boxinginjury", title="拳击伤害改版", sub="空手高额伤害", hk="Num.", sec="基础设置", key="拳击伤害改版启用", col=ChipOrange, glyph="punch" },
            new KnownMod{ guid="me.yexiao.fish.finalmod", title="金钱拉满", sub="货币保持上限 · 开关插件本体", hk="", col=ChipGold, glyph="coin", behaviourToggle=true },
        };

        struct SubSpec { public string guid, sec, key, label, suffix; public bool slider, chip; public float min, max, step; }
        static readonly SubSpec[] Subs =
        {
            new SubSpec{ guid="me.yexiao07.plugin.fishaimlock", sec="目标筛选", key="锁定鱼类", label="锁鱼", chip=true },
            new SubSpec{ guid="me.yexiao07.plugin.fishaimlock", sec="目标筛选", key="锁定鸟类", label="锁鸟", chip=true },
            new SubSpec{ guid="me.yexiao07.plugin.fishaimlock", sec="目标筛选", key="锁定BOSS", label="锁 Boss", chip=true },
            new SubSpec{ guid="me.yexiao07.plugin.fishaimlock", sec="目标筛选", key="锁定其他生物", label="锁其他", chip=true },
            new SubSpec{ guid="me.yexiao07.plugin.fishaimlock", sec="爆头(方式A)", key="强制爆头", label="强制爆头" },
            new SubSpec{ guid="me.yexiao07.plugin.unrivalled", sec="基础设置", key="保持满血", label="保持满血" },
            new SubSpec{ guid="me.yexiao07.plugin.flymod", sec="基础设置", key="飞行速度(米/秒)", label="飞行速度", slider=true, min=5, max=60, step=1, suffix=" m/s" },
            new SubSpec{ guid="me.yexiao07.plugin.boxinginjury", sec="基础设置", key="拳头伤害", label="拳头伤害", slider=true, min=100, max=99999, step=100 },
        };

        // ============================================================
        void Awake()
        {
            Instance = this;
            Log = Logger;
            _toggleKey = Config.Bind("界面", "显示/隐藏热键", KeyCode.F1, "按下该键在左上角小方框与完整控制面板之间切换");
            _unlockMouse = Config.Bind("界面", "打开时解锁鼠标", true, "打开面板时强制切回系统硬光标并暂停游戏视角转动");
            _showBall = Config.Bind("界面", "启动时显示悬浮框", true, "启动后显示左上角的小方框;F1 可展开/收起完整面板");
            Log.LogInfo("[modctl] ModControlCenter v1.5.0 加载成功 (F1 打开/关闭)");
        }

        IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(1.2f);
            try { BuildUI(); _built = true; }
            catch (Exception ex) { Log.LogError("[modctl] UI 构建失败: " + ex); yield break; }
            DiscoverAndBuildRows();
            yield return new WaitForSecondsRealtime(0.25f);
            // 启动即为收起态:左上角一个小方框(摘要),点击方框或按 F1 展开为完整面板
            _collapsed = true;
            ApplyCollapseLayout();
            _panel.sizeDelta = new Vector2(CollapsedW, CollapsedH);
            SetPanelOpen(_showBall.Value, false);
            Log.LogInfo("[modctl] 悬浮窗就绪, 共 " + _rows.Count + " 个条目");
        }

        void Update()
        {
            // F1:小方框 ⇄ 完整面板(两者共用同一个左上角原点)
            if (_toggleKey != null && Input.GetKeyDown(_toggleKey.Value))
            {
                if (!_panelOpen) { _collapsed = true; ApplyCollapseLayout(); SetPanelOpen(true, true); }
                else ToggleCollapse();
            }
            if (!_built) return;
            _t += Time.unscaledDeltaTime;

            if (_panelOpen)
            {
                if (_unlockMouse.Value)
                {
                    if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                    if (!Cursor.visible) Cursor.visible = true;
                }
                if (!_collapsed)
                {
                    if (Input.GetKeyDown(KeyCode.DownArrow)) NavMove(1);
                    else if (Input.GetKeyDown(KeyCode.UpArrow)) NavMove(-1);
                    else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) NavActivate();
                    else if (Input.GetKeyDown(KeyCode.RightArrow)) NavExpand(true);
                    else if (Input.GetKeyDown(KeyCode.LeftArrow)) NavExpand(false);
                }

                UpdateHoverScale();
                UpdateDot();

                _syncT += Time.unscaledDeltaTime;
                if (_syncT >= 0.5f) { _syncT = 0f; SyncFromEntries(); }
            }
        }

        // 悬停时面板轻微放大(保留原有手感);跟随鼠标的白色光斑已移除。
        void UpdateHoverScale()
        {
            if (_panel == null) return;
            bool over = RectTransformUtility.RectangleContainsScreenPoint(_panel, Input.mousePosition, null);
            if (_visCo == null && _panelCg.alpha >= 0.999f)
            {
                float ns = Mathf.MoveTowards(_panel.localScale.x, over ? 1.006f : 1f, Time.unscaledDeltaTime * 3f);
                _panel.localScale = new Vector3(ns, ns, 1f);
            }
        }

        // 状态点呼吸:网页 2.4s 周期,opacity 1 → 0.45。
        void UpdateDot()
        {
            if (_dot == null) return;
            float breathe = 0.5f + 0.5f * Mathf.Sin(_t * 2.6f);
            float a = 0.45f + 0.55f * breathe;
            _dot.color = new Color(_dotBase.r, _dotBase.g, _dotBase.b, _dotBase.a * a);
            if (_dotHalo != null)
                _dotHalo.color = new Color(_dotHaloBase.r, _dotHaloBase.g, _dotHaloBase.b, _dotHaloBase.a * a);
        }

        // ============================================================
        // UI 构建(布局组完全控制子项尺寸)
        // ============================================================
        void BuildUI()
        {
            string[] fontNames = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Arial" };
            foreach (var fn in fontNames)
            {
                try { var f = Font.CreateDynamicFontFromOSFont(fn, 64); if (f != null) { _font = f; break; } }
                catch { }
            }
            if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // ---- 烘焙贴图(尺寸/圆角必须与显示尺寸匹配:
            //      Sliced 贴图的 border = radius + 4,必须 < 显示高度的一半,否则角块吃掉整块区域 → 巨型色块) ----
            // 没有真实的背景模糊,所以用更高的白磨砂不透明度来遮住 3D 场景,观感依然通透
            _spGlassPanel = BakeGlass(128, 128, 26, 0.60f, 0.48f, 0.55f, 0.16f, 0.14f); // border 30
            _spGlassRow      = BakeGlass(128, 128, 18, 0.40f, 0.26f, 0.48f, 0.14f, 0.10f); // border 22, 行高 62
            _spGlassRowHover = BakeGlass(128, 128, 18, 0.46f, 0.32f, 0.58f, 0.18f, 0.12f);
            _spGlassRowOff   = BakeGlass(128, 128, 18, 0.22f, 0.14f, 0.20f, 0.06f, 0.06f);
            _spChip     = BakeSprite(96, 96, 12, false, 0);      // 小圆角(标题/按钮)
            _spRect     = BakeSprite(96, 96, 5, false, 0);       // 长方形图标底: 38×30
            _spPill     = BakeSprite(96, 48, 24, false, 0);      // 胶囊/条: Simple 拉伸
            _spChipFillOn = BakeGradientPill(96, 48, new Color32(100, 170, 255, 255), new Color32(70, 120, 255, 255), 0.5f, 0.3f);
            _spCircle   = BakeCircleSprite(128, false, 0);
            _spTrackOn  = BakeGradientPill(96, 48, new Color32(79, 224, 109, 255), new Color32(35, 192, 82, 255), 1f, 1f);
            _spTrackOff = BakeGrayTrack(96, 48, new Color32(120, 120, 128, 255));
            _spKnob     = BakeKnob(96);
            _spLogo     = BakeLogo(96, 22);
            _spSoftDot  = BakeSheenSprite(64, 0.55f);

            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("MCC_EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                DontDestroyOnLoad(es);
            }

            _root = new GameObject("MCC_Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            DontDestroyOnLoad(_root);
            var cv = _root.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 30000;
            var scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 1f;
            var canvasRt = (RectTransform)_root.transform;

            // ---- 主面板:锚点/枢轴都固定在左上角,展开时只向右下生长(悬浮小框即左上角) ----
            //     高度固定,内容溢出交给 ScrollRect 滚动(不再用 ContentSizeFitter,避免手动尺寸被布局覆盖)
            _panel = NewRect(_root.transform, "Panel");
            _panel.anchorMin = _panel.anchorMax = new Vector2(0f, 1f);
            _panel.pivot = new Vector2(0f, 1f);
            _panel.sizeDelta = new Vector2(CollapsedW, CollapsedH);
            _panel.anchoredPosition = _panelPos;
            _panelCg = _panel.gameObject.AddComponent<CanvasGroup>();

            var pvg = _panel.gameObject.AddComponent<VerticalLayoutGroup>();
            pvg.padding = new RectOffset(12, 12, 0, 10);
            pvg.spacing = 6f;
            pvg.childAlignment = TextAnchor.UpperLeft;
            pvg.childControlWidth = true;
            pvg.childControlHeight = true;
            pvg.childForceExpandWidth = true;
            pvg.childForceExpandHeight = false;

            // 玻璃 = 深色底(压制场景杂色) + 白色薄高光层
            var bgBase = Img(_panel, "BGBase", _spGlassPanel, GlassDark, false, Image.Type.Sliced);
            bgBase.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            FullStretch(RT(bgBase), 0);
            var bgImg = Img(_panel, "BG", _spGlassPanel, GlassSheen, false, Image.Type.Sliced);
            bgImg.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            FullStretch(RT(bgImg), 0);

            // ---- 标题栏 ----
            var header = NewRect(_panel, "Header");
            var hle = header.gameObject.AddComponent<LayoutElement>();
            hle.preferredHeight = HeadH;
            hle.flexibleWidth = 1f;
            var hg = header.gameObject.AddComponent<HorizontalLayoutGroup>();
            hg.padding = new RectOffset(12, 12, 15, 13);
            hg.spacing = 10f;
            hg.childAlignment = TextAnchor.MiddleLeft;
            hg.childControlWidth = true;
            hg.childControlHeight = true;
            hg.childForceExpandWidth = false;
            hg.childForceExpandHeight = false;

            var hhit = Img(header, "Hit", null, new Color(0f, 0f, 0f, 0.01f), true);
            hhit.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            FullStretch(RT(hhit), 0);
            var drag = hhit.gameObject.AddComponent<DragMove>();
            drag.Init(_panel, canvasRt);

            var logo = NewRect(header, "Logo");
            LE(logo, 34, 34);
            var logoBgRt = NodeC(logo, "bgRt", 0, 0, 34, 34);
            Img(logoBgRt, "i", _spLogo, Color.white, false);
            var lg = NodeC(logo, "g", 0, 0, 17, 17);
            Img(lg, "i", GetIcon("fish"), new Color(0.62f, 0.78f, 1f, 0.98f), false);

            var tcol = NewRect(header, "TCol");
            _headerTCol = tcol;
            var tle = LE(tcol, 0, 40);
            tle.flexibleWidth = 1f;
            var tvg = tcol.gameObject.AddComponent<VerticalLayoutGroup>();
            tvg.spacing = 2f;
            tvg.childAlignment = TextAnchor.MiddleLeft;
            tvg.childControlWidth = true;
            tvg.childControlHeight = true;
            tvg.childForceExpandWidth = true;
            tvg.childForceExpandHeight = false;
            var t1 = NewRect(tcol, "T");
            LE(t1, 0, 21).flexibleWidth = 1f;
            _headerTitle = Txt(t1, "s", "Mod 控制中心", 15, TextMain);
            var t2 = NewRect(tcol, "S");
            LE(t2, 0, 15).flexibleWidth = 1f;
            _headerSub = Txt(t2, "s", "How to Fish · BepInEx", 11, TextSub);

            var dotRt = NewRect(header, "Dot");
            LE(dotRt, 8, 40);
            var haloRt = NodeC(dotRt, "halo", 0, 0, 22, 22);
            _dotHaloBase = DotHaloCol;
            _dotHalo = Img(haloRt, "i", _spSoftDot, _dotHaloBase, false);
            var dRt = NodeC(dotRt, "d", 0, 0, 8, 8);
            _dotBase = (Color)GreenDot;
            _dot = Img(dRt, "i", _spCircle, _dotBase, false);

            var ctRt = NewRect(header, "Count");
            LE(ctRt, 78, 20);
            _countText = Txt(ctRt, "s", "0 已启用", 12, new Color(1f, 1f, 1f, 0.85f));

            var minRt = NewRect(header, "MinBtn");
            LE(minRt, 26, 40);
            var minBgRt = NodeC(minRt, "bgRt", 0, 0, 26, 26);
            var minImg = Img(minBgRt, "i", _spCircle, new Color(1f, 1f, 1f, 0.14f), true);
            var minBar = NodeC(minBgRt, "bar", 0, 0, 12, 2.6f);
            _minBar = Img(minBar, "i", null, new Color(1f, 1f, 1f, 0.85f), false);
            // 小方框态:按钮显示"展开"双斜线,完整态:显示"收起"横线
            _minChev = NodeC(minBgRt, "chev", 0, 0, 12, 12);
            var chA = NodeC(_minChev, "a", -2.6f, 1.2f, 8f, 2.6f);
            Img(chA, "i", _spPill, new Color(1f, 1f, 1f, 0.85f), false);
            chA.localEulerAngles = new Vector3(0, 0, 45f);
            var chB = NodeC(_minChev, "b", 2.6f, 1.2f, 8f, 2.6f);
            Img(chB, "i", _spPill, new Color(1f, 1f, 1f, 0.85f), false);
            chB.localEulerAngles = new Vector3(0, 0, -45f);
            var btn = minImg.gameObject.AddComponent<Button>();
            btn.onClick.AddListener(ToggleCollapse);

            // ---- 行区域:ScrollRect 竖向滚动(内容超出面板高度时可用滚轮上下翻动) ----
            _rowsWrap = NewRect(_panel, "RowsWrap");
            var vpImg = Img(_rowsWrap, "Viewport", null, new Color(0f, 0f, 0f, 0f), true);
            vpImg.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            FullStretch(RT(vpImg), 0);
            _rowsWrap.gameObject.AddComponent<RectMask2D>();
            _rowsWrapCg = _rowsWrap.gameObject.AddComponent<CanvasGroup>();
            var vle = _rowsWrap.gameObject.AddComponent<LayoutElement>();
            vle.flexibleWidth = 1f;
            vle.flexibleHeight = 1f;
            vle.minHeight = 0f;

            _rowsRect = NewRect(_rowsWrap, "Rows");
            _rowsRect.anchorMin = new Vector2(0f, 1f);
            _rowsRect.anchorMax = new Vector2(1f, 1f);
            _rowsRect.pivot = new Vector2(0f, 1f);
            _rowsRect.offsetMin = new Vector2(0f, 0f);
            _rowsRect.offsetMax = new Vector2(0f, 0f);
            var rvg = _rowsRect.gameObject.AddComponent<VerticalLayoutGroup>();
            rvg.padding = new RectOffset(2, 2, 2, 8);
            rvg.spacing = Gap;
            rvg.childAlignment = TextAnchor.UpperLeft;
            rvg.childControlWidth = true;
            rvg.childControlHeight = true;
            rvg.childForceExpandWidth = true;
            rvg.childForceExpandHeight = false;
            _rowsRect.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll = _rowsWrap.gameObject.AddComponent<ScrollRect>();
            _scroll.content = _rowsRect;
            _scroll.viewport = _rowsWrap;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.inertia = true;
            _scroll.decelerationRate = 0.12f;
            _scroll.scrollSensitivity = 28f;

            // ---- 底部键位提示 ----
            var foot = NewRect(_panel, "Foot");
            LE(foot, 0, 16).flexibleWidth = 1f;
            _footText = Txt(foot, "s", "↑↓ 选择    Enter 开关    ←→ 展开/收起    F1 显示/隐藏", 10, TextSub, TextAnchor.MiddleCenter);
        }

        // ============================================================
        // 发现 mod 并生成条目
        // ============================================================
        void DiscoverAndBuildRows()
        {
            foreach (var k in Known)
            {
                var p = FindPlugin(k.guid);
                if (p == null) continue;

                ConfigEntryBase main = k.behaviourToggle ? null : FindMainEntry(p, k.sec, k.key);
                if (!k.behaviourToggle && main == null) continue;

                var row = AddRow(p, k.title, k.sub, k.hk, k.col, k.glyph, main, k.behaviourToggle);
                var chipList = new List<KeyValuePair<string, ConfigEntryBase>>();
                foreach (var s in Subs)
                {
                    if (s.guid != k.guid) continue;
                    var e = GetEntry(p, s.sec, s.key);
                    if (e == null) continue;
                    if (s.chip) { chipList.Add(new KeyValuePair<string, ConfigEntryBase>(s.label, e)); continue; }
                    if (s.slider) AddSubSlider(row, s.label, s.suffix, e, s.min, s.max, s.step);
                    else AddSubToggle(row, s.label, e);
                }
                if (chipList.Count > 0) AddChipGroup(row, chipList);
                FinishBody(row);
                _rows.Add(row);
            }

            foreach (var kv in Chainloader.PluginInfos)
            {
                if (kv.Key.Contains("modcontrol") || kv.Key.ToLower().Contains("unityexplorer")) continue;
                if (_rows.Any(r => r.owner != null && kv.Value.Instance == r.owner)) continue;
                var p = kv.Value.Instance as BaseUnityPlugin;
                if (p == null || p.Config == null) continue;
                var mains = p.Config.GetConfigEntries()
                    .Where(e => e.SettingType == typeof(bool) && e.Definition.Key.Contains("启用")).ToList();
                foreach (var m in mains)
                {
                    var row = AddRow(p, kv.Value.Metadata.Name, m.Definition.Section + " / " + m.Definition.Key, "", ChipWhite, "shield", m, false);
                    foreach (var e in p.Config.GetConfigEntries())
                    {
                        if (e == m) continue;
                        if (e.SettingType == typeof(bool) &&
                            (e.Definition.Key.Contains("锁") || e.Definition.Key.Contains("爆头") || e.Definition.Key.Contains("满血")))
                            AddSubToggle(row, e.Definition.Key, e);
                    }
                    FinishBody(row);
                    _rows.Add(row);
                }
            }

            if (_headerSub != null) _headerSub.text = "How to Fish · BepInEx · " + _rows.Count + " 个插件";
            RefreshAll();
        }

        BaseUnityPlugin FindPlugin(string guid)
        {
            PluginInfo pi;
            if (Chainloader.PluginInfos.TryGetValue(guid, out pi)) return pi.Instance as BaseUnityPlugin;
            return null;
        }

        ConfigEntryBase GetEntry(BaseUnityPlugin p, string sec, string key)
        {
            foreach (var e in p.Config.GetConfigEntries())
                if (e.Definition.Section == sec && e.Definition.Key == key) return e;
            return null;
        }

        ConfigEntryBase FindMainEntry(BaseUnityPlugin p, string sec, string key)
        {
            var e = GetEntry(p, sec, key);
            if (e != null) return e;
            return p.Config.GetConfigEntries().FirstOrDefault(x => x.SettingType == typeof(bool) && x.Definition.Key.Contains("启用"));
        }

        // ============================================================
        // 行
        // ============================================================
        RowView AddRow(BaseUnityPlugin owner, string title, string sub, string hotkey, Color iconCol, string glyph, ConfigEntryBase main, bool behaviourToggle)
        {
            var row = new RowView();
            row.owner = owner; row.main = main; row.behaviourToggle = behaviourToggle; row.iconCol = iconCol;

            row.rect = NewRect(_rowsRect, "Row");
            var rvg = row.rect.gameObject.AddComponent<VerticalLayoutGroup>();
            rvg.childControlWidth = true;
            rvg.childControlHeight = true;
            rvg.childForceExpandWidth = true;
            rvg.childForceExpandHeight = false;
            rvg.spacing = 0f;
            row.rect.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            row.bg = Img(row.rect, "BG", _spGlassRow, RowFX.ColOn, false, Image.Type.Sliced);
            row.bg.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            FullStretch(RT(row.bg), 0);

            // ---- 行头部 ----
            var head = NewRect(row.rect, "Head");
            LE(head, 0, RowHeadH).flexibleWidth = 1f;
            var hg = head.gameObject.AddComponent<HorizontalLayoutGroup>();
            hg.padding = new RectOffset(12, 12, 0, 0);
            hg.spacing = 11f;
            hg.childAlignment = TextAnchor.MiddleLeft;
            hg.childControlWidth = true;
            hg.childControlHeight = true;
            hg.childForceExpandWidth = false;
            hg.childForceExpandHeight = false;

            var hit = Img(head, "Hit", null, new Color(0f, 0f, 0f, 0.01f), true);
            hit.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            FullStretch(RT(hit), 0);
            var click = hit.gameObject.AddComponent<ClickForward>();
            row.hover = hit.gameObject.AddComponent<RowFX>();
            row.hover.Init(row.bg, _spGlassRow, _spGlassRowHover, _spGlassRowOff);
            click.onClick = () => { if (row.hasBody) ToggleRow(row); };

            var chip = NewRect(head, "Icon");
            LE(chip, 38, 40);
            // 图标底改为长方形(小圆角),彻底避免圆角边缘产生的黑色毛边
            var chipBgRt = NodeC(chip, "bgRt", 0, 0, 38, 30);
            row.chipBg = Img(chipBgRt, "i", _spRect, Tint(iconCol, 0.26f), false, Image.Type.Sliced);
            var glyphRt = NodeC(chip, "glyph", 0, 0, 19, 19);
            row.iconImg = Img(glyphRt, "i", GetIcon(glyph), iconCol, false);

            var tcol = NewRect(head, "TCol");
            var tle = LE(tcol, 0, 40);
            tle.flexibleWidth = 1f;
            var tvg = tcol.gameObject.AddComponent<VerticalLayoutGroup>();
            tvg.spacing = 2f;
            tvg.childAlignment = TextAnchor.MiddleLeft;
            tvg.childControlWidth = true;
            tvg.childControlHeight = true;
            tvg.childForceExpandWidth = true;
            tvg.childForceExpandHeight = false;
            var tt1 = NewRect(tcol, "T");
            LE(tt1, 0, 21).flexibleWidth = 1f;
            row.title = Txt(tt1, "s", title, 15, TextMain);
            var tt2 = NewRect(tcol, "S");
            LE(tt2, 0, 15).flexibleWidth = 1f;
            row.sub = Txt(tt2, "s", string.IsNullOrEmpty(hotkey) ? sub : sub + "   " + hotkey, 11, TextSub);

            var swRt = NewRect(head, "Switch");
            LE(swRt, 46, 28);
            row.sw = BuildSwitch(swRt, IsOn(row), 46, 28);
            row.sw.onChanged = v =>
            {
                PlayJolt(row);
                if (row.behaviourToggle) { if (row.owner != null) ((Behaviour)row.owner).enabled = v; }
                else row.main.BoxedValue = v;
                ApplyRowVisual(row, v, true);
                ScheduleSave(row.owner != null ? row.owner.Config : Config);
                UpdateCount();
            };

            row.chev = NewRect(head, "Chev");
            LE(row.chev, 14, 14);
            var c1 = NodeC(row.chev, "a", -2.2f, -1.2f, 8f, 2.6f);
            Img(c1, "i", _spPill, new Color(1f, 1f, 1f, 0.55f), false);
            c1.localEulerAngles = new Vector3(0, 0, 45);
            var c2 = NodeC(row.chev, "b", 2.2f, -1.2f, 8f, 2.6f);
            Img(c2, "i", _spPill, new Color(1f, 1f, 1f, 0.55f), false);
            c2.localEulerAngles = new Vector3(0, 0, -45);
            return row;
        }

        void InitBody(RowView row)
        {
            if (row.bodyWrap != null) return;
            row.bodyWrap = NewRect(row.rect, "BodyWrap");
            row.bodyLE = row.bodyWrap.gameObject.AddComponent<LayoutElement>();
            row.bodyLE.preferredHeight = 0f;
            row.bodyLE.flexibleWidth = 1f;
            Img(row.bodyWrap, "MaskImg", null, new Color(0, 0, 0, 0), false);
            row.bodyWrap.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            row.bodyCg = row.bodyWrap.gameObject.AddComponent<CanvasGroup>();
            row.bodyCg.alpha = 0f;
            row.bodyCg.blocksRaycasts = false;

            row.innerBody = NewRect(row.bodyWrap, "Inner");
            row.innerBody.anchorMin = new Vector2(0f, 1f);
            row.innerBody.anchorMax = new Vector2(1f, 1f);
            row.innerBody.pivot = new Vector2(0f, 1f);
            row.innerBody.offsetMin = new Vector2(0f, 0f);
            row.innerBody.offsetMax = new Vector2(0f, 0f);
            row.innerCg = row.innerBody.gameObject.AddComponent<CanvasGroup>();
            var ivg = row.innerBody.gameObject.AddComponent<VerticalLayoutGroup>();
            ivg.padding = new RectOffset(14, 12, 4, 12);
            ivg.spacing = 8f;
            ivg.childAlignment = TextAnchor.UpperLeft;
            ivg.childControlWidth = true;
            ivg.childControlHeight = true;
            ivg.childForceExpandWidth = true;
            ivg.childForceExpandHeight = false;
            row.innerBody.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            row.bodyWrap.gameObject.SetActive(false);
        }

        void FinishBody(RowView row)
        {
            if (row.innerBody == null) { row.hasBody = false; return; }
            // bodyWrap 平时是隐藏的,先临时激活才能量出真实高度
            bool was = row.bodyWrap.gameObject.activeSelf;
            row.bodyWrap.gameObject.SetActive(true);
            // 重建两轮:第一轮让 chip / 子项拿到真实尺寸,第二轮再量最终高度,避免量到中间态
            LayoutRebuilder.ForceRebuildLayoutImmediate(row.innerBody);
            LayoutRebuilder.ForceRebuildLayoutImmediate(row.innerBody);
            row.bodyH = LayoutUtility.GetPreferredHeight(row.innerBody);
            row.bodyWrap.gameObject.SetActive(was);
            row.hasBody = true;
        }

        void AddSubToggle(RowView row, string label, ConfigEntryBase e)
        {
            InitBody(row);
            var rt = NewRect(row.innerBody, "Sub");
            LE(rt, 0, 30).flexibleWidth = 1f;
            var hg = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            hg.spacing = 8f;
            hg.childAlignment = TextAnchor.MiddleLeft;
            hg.childControlWidth = true;
            hg.childControlHeight = true;
            hg.childForceExpandWidth = false;
            hg.childForceExpandHeight = false;

            var lt = NewRect(rt, "L");
            var lle = LE(lt, 0, 20);
            lle.flexibleWidth = 1f;
            Txt(lt, "s", label, 13, new Color(1f, 1f, 1f, 0.85f));

            var swRt = NewRect(rt, "Switch");
            LE(swRt, 40, 24);
            var sw = BuildSwitch(swRt, (bool)e.BoxedValue, 40, 24);
            sw.onChanged = v => { e.BoxedValue = v; ScheduleSave(e.ConfigFile); };
            row.subToggles.Add(new SubToggleItem { entry = e, sw = sw });
        }

        // ---- 锁目标 Chip 胶囊组(网页版 lg-chips) ----
        void AddChipGroup(RowView row, List<KeyValuePair<string, ConfigEntryBase>> chips)
        {
            InitBody(row);
            var rt = NewRect(row.innerBody, "Chips");
            var hg = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            hg.padding = new RectOffset(0, 0, 0, 0);
            hg.spacing = 7f;
            hg.childAlignment = TextAnchor.MiddleLeft;
            hg.childControlWidth = false;
            // 必须由布局组按 preferredHeight(27) 控制高度:否则量高度时 chip 还是默认 100 高,
            // 会把整行 bodyH 撑大一倍,展开后出现大片空白
            hg.childControlHeight = true;
            hg.childForceExpandWidth = false;
            hg.childForceExpandHeight = false;

            foreach (var c in chips)
            {
                var e = c.Value;
                var chipRt = NewRect(rt, "Chip");
                var chg = chipRt.gameObject.AddComponent<HorizontalLayoutGroup>();
                chg.padding = new RectOffset(13, 13, 3, 3);
                chg.spacing = 0f;
                chg.childAlignment = TextAnchor.MiddleCenter;
                chg.childControlWidth = true;
                chg.childControlHeight = true;
                chg.childForceExpandWidth = false;
                chg.childForceExpandHeight = false;
                var c2 = chipRt.gameObject.AddComponent<ContentSizeFitter>();
                c2.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                c2.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var le = chipRt.gameObject.AddComponent<LayoutElement>();
                le.minHeight = 27f;
                le.preferredHeight = 27f;

                var cv = chipRt.gameObject.AddComponent<ChipView>();
                cv.border = Img(chipRt, "Border", _spPill, ChipView.OffBorder, true);
                cv.border.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                FullStretch(RT(cv.border), 0f);
                cv.fill = Img(chipRt, "Fill", _spPill, ChipView.OffFill, false);
                cv.fill.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                FullStretch(RT(cv.fill), 1.2f);
                cv.fillOnSpr = _spChipFillOn;
                cv.fillOffSpr = _spPill;

                var lr = NewRect(chipRt, "L");
                var t = lr.gameObject.AddComponent<Text>();
                t.font = _font;
                t.text = c.Key;
                t.fontSize = 12;
                t.color = ChipView.OffText;
                t.alignment = TextAnchor.MiddleCenter;
                t.supportRichText = false;
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                t.raycastTarget = false;
                cv.label = t;

                cv.onChanged = v => { e.BoxedValue = v; ScheduleSave(e.ConfigFile); };
                cv.Set((bool)e.BoxedValue, false);
                row.chips.Add(new ChipItem { entry = e, view = cv });
            }
        }

        void AddSubSlider(RowView row, string label, string suffix, ConfigEntryBase e, float min, float max, float step)
        {
            InitBody(row);
            bool isInt = e.SettingType == typeof(int);

            var rt = NewRect(row.innerBody, "Sl");
            LE(rt, 0, 48).flexibleWidth = 1f;
            var vg = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            vg.spacing = 4f;
            vg.childAlignment = TextAnchor.UpperLeft;
            vg.childControlWidth = true;
            vg.childControlHeight = true;
            vg.childForceExpandWidth = true;
            vg.childForceExpandHeight = false;

            var top = NewRect(rt, "Top");
            LE(top, 0, 16).flexibleWidth = 1f;
            var thg = top.gameObject.AddComponent<HorizontalLayoutGroup>();
            thg.spacing = 8f;
            thg.childAlignment = TextAnchor.MiddleLeft;
            thg.childControlWidth = true;
            thg.childControlHeight = true;
            thg.childForceExpandWidth = false;
            thg.childForceExpandHeight = false;
            var lt = NewRect(top, "L");
            var lle = LE(lt, 0, 16);
            lle.flexibleWidth = 1f;
            Txt(lt, "s", label, 12, TextSub);
            var vt = NewRect(top, "V");
            LE(vt, 130, 16);
            var valueText = Txt(vt, "s", "", 12, TextMain, TextAnchor.MiddleRight);

            var sldRt = NewRect(rt, "Sld");
            LE(sldRt, 0, 22).flexibleWidth = 1f;
            var trackImg = Img(sldRt, "bg", _spPill, new Color(1f, 1f, 1f, 0.22f), true);
            FullStretch(RT(trackImg), 9);

            var fill = NewRect(sldRt, "Fill");
            fill.anchorMin = new Vector2(0f, 0.5f);
            fill.anchorMax = new Vector2(0f, 0.5f);
            fill.pivot = new Vector2(0f, 0.5f);
            fill.sizeDelta = new Vector2(0f, 6f);
            fill.anchoredPosition = Vector2.zero;
            Img(fill, "i", _spPill, new Color(1f, 1f, 1f, 0.88f), false);

            var handle = NewRect(sldRt, "Handle");
            handle.anchorMin = new Vector2(0f, 0.5f);
            handle.anchorMax = new Vector2(0f, 0.5f);
            handle.pivot = new Vector2(0.5f, 0.5f);
            handle.sizeDelta = new Vector2(20, 20);
            handle.anchoredPosition = Vector2.zero;
            var handleImg = Img(handle, "i", _spKnob, Color.white, false);

            var slider = sldRt.gameObject.AddComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImg;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = isInt || step >= 1f;

            float cur = Convert.ToSingle(e.BoxedValue);
            valueText.text = Fmt(isInt, suffix, cur);
            slider.SetValueWithoutNotify(cur);
            slider.onValueChanged.AddListener(v =>
            {
                e.BoxedValue = isInt ? (object)Mathf.RoundToInt(v) : (object)v;
                valueText.text = Fmt(isInt, suffix, v);
                ScheduleSave(e.ConfigFile);
            });
            row.subSliders.Add(new SubSliderItem { entry = e, slider = slider, valueText = valueText, isInt = isInt, suffix = suffix, step = step });
        }

        static string Fmt(bool isInt, string suffix, float v)
        {
            return (isInt ? Mathf.RoundToInt(v).ToString("N0") : v.ToString("0.#")) + suffix;
        }

        SwitchView BuildSwitch(RectTransform container, bool on, float w, float h)
        {
            var sv = container.gameObject.AddComponent<SwitchView>();
            var glowRt = NodeC(container, "glowRt", 0, 0, w + 8, h + 8);
            sv.glow = Img(glowRt, "i", _spSoftDot, new Color(0.24f, 0.86f, 0.43f, 0f), false);

            var trackRt = NodeC(container, "trackRt", 0, 0, w, h);
            sv.track = Img(trackRt, "i", on ? _spTrackOn : _spTrackOff, Color.white, true);
            sv.sprOn = _spTrackOn;
            sv.sprOff = _spTrackOff;
            // 圆钮行程 = (轨道宽 - 钮径)/2,钮刚好贴住轨道内边缘,不会溢出轨道
            float knobSize = h - 4f;
            float travel = (w - knobSize) * 0.5f;
            sv.xOff = -travel;
            sv.xOn = travel;

            sv.knob = NodeC(container, "knob", on ? travel : -travel, 0, knobSize, knobSize);
            var kImg = Img(sv.knob, "k", _spKnob, Color.white, false);
            FullStretch(RT(kImg), 0);
            sv.on = on;
            return sv;
        }

        // ============================================================
        // 状态
        // ============================================================
        bool IsOn(RowView r)
        {
            if (r.main != null) return (bool)r.main.BoxedValue;
            return r.behaviourToggle && r.owner != null && ((Behaviour)r.owner).enabled;
        }

        void RefreshAll()
        {
            foreach (var r in _rows)
            {
                bool on = IsOn(r);
                if (r.sw != null) r.sw.Set(on, false);
                ApplyRowVisual(r, on, false);
                foreach (var st in r.subToggles) st.sw.Set((bool)st.entry.BoxedValue, false);
                foreach (var c in r.chips) c.view.Set((bool)c.entry.BoxedValue, false);
                foreach (var sl in r.subSliders)
                {
                    float v = Convert.ToSingle(sl.entry.BoxedValue);
                    sl.slider.SetValueWithoutNotify(v);
                    sl.valueText.text = Fmt(sl.isInt, sl.suffix, v);
                }
            }
            UpdateCount();
        }

        void SyncFromEntries()
        {
            foreach (var r in _rows)
            {
                bool on = IsOn(r);
                if (r.sw != null && r.sw.on != on) { r.sw.Set(on, true); ApplyRowVisual(r, on, true); }
                foreach (var st in r.subToggles)
                {
                    bool b = (bool)st.entry.BoxedValue;
                    if (st.sw.on != b) st.sw.Set(b, true);
                }
                foreach (var c in r.chips)
                {
                    bool b = (bool)c.entry.BoxedValue;
                    if (c.view.on != b) c.view.Set(b, true);
                }
                foreach (var sl in r.subSliders)
                {
                    float v = Convert.ToSingle(sl.entry.BoxedValue);
                    if (Mathf.Abs(sl.slider.value - v) > 0.01f)
                    {
                        sl.slider.SetValueWithoutNotify(v);
                        sl.valueText.text = Fmt(sl.isInt, sl.suffix, v);
                    }
                }
            }
            UpdateCount();
        }

        void ApplyRowVisual(RowView r, bool on, bool animate)
        {
            Color i1 = on ? r.iconCol : new Color(0.78f, 0.81f, 0.88f, 0.5f);
            Color t1 = on ? TextMain : new Color(1f, 1f, 1f, 0.62f);
            Color c1 = on ? Tint(r.iconCol, 0.26f) : new Color(0.85f, 0.87f, 0.93f, 0.10f);
            if (r.innerCg != null)
            {
                r.innerCg.alpha = on ? 1f : 0.45f;
                r.innerCg.blocksRaycasts = on;
            }
            if (r.hover != null) r.hover.SetOn(on);
            if (!animate)
            {
                r.iconImg.color = i1;
                r.title.color = t1;
                if (r.chipBg != null) r.chipBg.color = c1;
            }
            else
            {
                // 单独存颜色协程,避免和行展开动画共用字典而互相中断
                Coroutine c;
                if (_rowColorCos.TryGetValue(r, out c) && c != null) StopCoroutine(c);
                _rowColorCos[r] = StartCoroutine(CoRowColor(r, i1, t1, c1));
            }
        }

        IEnumerator CoRowColor(RowView r, Color i1, Color t1, Color c1)
        {
            Color i0 = r.iconImg.color, t0 = r.title.color;
            Color c0 = r.chipBg != null ? r.chipBg.color : Color.clear;
            float t = 0;
            while (t < 1f)
            {
                t = Mathf.Min(1f, t + Time.unscaledDeltaTime / 0.25f);
                float e = EaseOutCubic(t);
                r.iconImg.color = Color.Lerp(i0, i1, e);
                r.title.color = Color.Lerp(t0, t1, e);
                if (r.chipBg != null) r.chipBg.color = Color.Lerp(c0, c1, e);
                yield return null;
            }
            r.iconImg.color = i1;
            r.title.color = t1;
            if (r.chipBg != null) r.chipBg.color = c1;
            _rowColorCos[r] = null;
        }

        void UpdateCount()
        {
            int n = 0;
            foreach (var r in _rows) if (IsOn(r)) n++;
            if (_countText != null) _countText.text = n + " 已启用";
            if (_dot != null)
            {
                _dotBase = (Color)GreenDot;
                _dot.color = _dotBase;
            }
        }

        public void ScheduleSave(ConfigFile f)
        {
            if (f == null) return;
            Coroutine c;
            if (_saveCos.TryGetValue(f, out c) && c != null) StopCoroutine(c);
            _saveCos[f] = StartCoroutine(CoSave(f));
        }

        IEnumerator CoSave(ConfigFile f)
        {
            yield return new WaitForSecondsRealtime(0.8f);
            _saveCos.Remove(f);
            try { f.Save(); }
            catch (Exception ex) { Log.LogWarning("[modctl] 配置保存失败: " + ex.Message); }
        }

        // ============================================================
        // 展开 / 折叠 / 面板显隐
        // ============================================================
        void PlayJolt(RowView r)
        {
            Coroutine c;
            if (_joltCos.TryGetValue(r, out c) && c != null) StopCoroutine(c);
            _joltCos[r] = StartCoroutine(CoJolt(r));
        }

        IEnumerator CoJolt(RowView r)
        {
            float t = 0f;
            while (t < 0.36f)
            {
                t = Mathf.Min(0.36f, t + Time.unscaledDeltaTime);
                float x = t / 0.36f;
                float s = x < 0.35f ? Mathf.Lerp(1f, 0.986f, x / 0.35f)
                    : x < 0.70f ? Mathf.Lerp(0.986f, 1.005f, (x - 0.35f) / 0.35f)
                    : Mathf.Lerp(1.005f, 1f, (x - 0.70f) / 0.30f);
                r.rect.localScale = new Vector3(s, s, 1f);
                yield return null;
            }
            r.rect.localScale = Vector3.one;
            _joltCos[r] = null;
        }

        internal void ToggleRow(RowView r)
        {
            if (!r.hasBody || r.bodyLE == null) return;
            r.open = !r.open;
            r.bodyCg.blocksRaycasts = r.open;
            // 立即按目标高度调整面板(不等动画结束),否则展开的内容只会被裁掉
            FitPanelHeight(r.open ? r.bodyH : -r.bodyH, true);
            Coroutine c;
            if (_rowAnim.TryGetValue(r, out c) && c != null) StopCoroutine(c);
            _rowAnim[r] = StartCoroutine(AnimRow(r));
            RebuildNav();
        }

        IEnumerator AnimRow(RowView r)
        {
            float from = r.bodyLE.preferredHeight, to = r.open ? r.bodyH : 0f;
            float a0 = r.bodyCg.alpha, a1 = r.open ? 1f : 0f;
            float e0 = r.chev.localEulerAngles.z, e1 = r.open ? 180f : 0f;
            float y0 = r.innerBody.anchoredPosition.y, y1 = r.open ? 0f : -8f;
            if (r.open) r.bodyWrap.gameObject.SetActive(true);
            float t = 0;
            while (t < 1f)
            {
                t = Mathf.Min(1f, t + Time.unscaledDeltaTime / 0.32f);
                float e = EaseOutCubic(t);
                r.bodyLE.preferredHeight = Mathf.Lerp(from, to, e);
                // 仅改 preferredHeight 不会自动标脏:必须手动标脏,否则行高/内容高度不会实时重算(表现为展开被覆盖)
                LayoutRebuilder.MarkLayoutForRebuild(r.rect);
                r.bodyCg.alpha = Mathf.Lerp(a0, a1, e);
                r.chev.localEulerAngles = new Vector3(0, 0, Mathf.LerpAngle(e0, e1, e));
                r.innerBody.anchoredPosition = new Vector2(0f, Mathf.Lerp(y0, y1, e));
                yield return null;
            }
            r.bodyLE.preferredHeight = to;
            LayoutRebuilder.MarkLayoutForRebuild(r.rect);
            r.bodyCg.alpha = a1;
            r.chev.localEulerAngles = new Vector3(0, 0, e1);
            r.innerBody.anchoredPosition = new Vector2(0f, y1);
            if (!r.open) r.bodyWrap.gameObject.SetActive(false);
            // 动画结束后按真实内容高度校正面板,并把展开的行滚入可视区
            FitPanelHeight(0f, true);
            if (r.open)
            {
                while (_colCo != null) yield return null;
                EnsureRowVisible(r);
            }
            _rowAnim[r] = null;
        }

        public void ToggleCollapse()
        {
            _collapsed = !_collapsed;
            ApplyCollapseLayout();
            if (_colCo != null) StopCoroutine(_colCo);
            _colCo = StartCoroutine(CoCollapse(true));
        }

        // 展开态目标高度:标题栏 + 行内容 + 内边距,封顶 MaxPanelH(超出改为滚动)。
        float CalcExpandedH(float delta)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_rowsRect);
            float content = LayoutUtility.GetPreferredHeight(_rowsRect) + delta;
            // 70(标题) + 16(底部提示) + 12(两处间距) + 10(内边距) = 108
            return Mathf.Clamp(content + 108f, 180f, MaxPanelH);
        }

        // 仅调整面板高度(宽度不变),用于展开/收起单个 mod 条目时让框跟着变大变小。
        void FitPanelHeight(float delta, bool animate)
        {
            if (!_built || !_panelOpen || _collapsed || _panel == null) return;
            float toH = CalcExpandedH(delta);
            Vector2 to = new Vector2(_panel.sizeDelta.x, toH);
            if (Mathf.Abs(_panel.sizeDelta.y - toH) < 0.5f) return;
            if (_colCo != null) StopCoroutine(_colCo);
            _colCo = StartCoroutine(CoFitHeight(to, animate));
        }

        IEnumerator CoFitHeight(Vector2 to, bool animate)
        {
            if (!animate)
            {
                _panel.sizeDelta = to;
                _colCo = null;
                yield break;
            }
            Vector2 from = _panel.sizeDelta;
            float t = 0;
            while (t < 1f)
            {
                t = Mathf.Min(1f, t + Time.unscaledDeltaTime / 0.28f);
                _panel.sizeDelta = Vector2.Lerp(from, to, EaseOutCubic(t));
                yield return null;
            }
            _panel.sizeDelta = to;
            _colCo = null;
        }

        // 内容超出可视区时,把刚展开的行滚进视口。
        void EnsureRowVisible(RowView r)
        {
            if (!_panelOpen || _collapsed) return;
            if (_scroll == null || _rowsWrap == null || r == null || r.rect == null) return;
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_rowsRect);
            if (_rowsRect.rect.height <= _rowsWrap.rect.height + 1f) return;

            var rc = new Vector3[4];
            var vc = new Vector3[4];
            r.rect.GetWorldCorners(rc);
            _rowsWrap.GetWorldCorners(vc);
            float rowTop = rc[1].y, rowBottom = rc[0].y;
            float vpTop = vc[1].y, vpBottom = vc[0].y;
            float viewH = vpTop - vpBottom;
            Vector2 p = _rowsRect.anchoredPosition;
            if (rowTop - rowBottom >= viewH)
            {
                // 行本身比视口还高:优先保证行头部可见
                if (rowTop > vpTop) p.y += rowTop - vpTop + 2f;
                else return;
            }
            else if (rowTop > vpTop) p.y += rowTop - vpTop + 2f;
            else if (rowBottom < vpBottom) p.y += rowBottom - vpBottom - 2f;
            else return;
            _rowsRect.anchoredPosition = p;
        }

        // 面板尺寸手动驱动(无 ContentSizeFitter):收起 210×80,展开 396×(内容高,封顶 MaxPanelH)。
        IEnumerator CoCollapse(bool animate)
        {
            float toW = _collapsed ? CollapsedW : PanelW;
            float toH = _collapsed ? CollapsedH : CalcExpandedH(0f);
            Vector2 from = _panel.sizeDelta;
            Vector2 to = new Vector2(toW, toH);

            if (!animate)
            {
                _panel.sizeDelta = to;
                _colCo = null;
                yield break;
            }

            float t = 0;
            while (t < 1f)
            {
                t = Mathf.Min(1f, t + Time.unscaledDeltaTime / 0.34f);
                float e = EaseOutCubic(t);
                _panel.sizeDelta = Vector2.Lerp(from, to, e);
                yield return null;
            }
            _panel.sizeDelta = to;
            _colCo = null;
        }

        // 收起态 = 左上角一个小方框:只留 logo、状态点、已启用计数、展开按钮。
        void ApplyCollapseLayout()
        {
            if (_headerTCol != null) _headerTCol.gameObject.SetActive(!_collapsed);
            if (_headerTitle != null) _headerTitle.gameObject.SetActive(!_collapsed);
            if (_headerSub != null) _headerSub.gameObject.SetActive(!_collapsed);
            if (_footText != null) _footText.gameObject.SetActive(!_collapsed);
            if (_minBar != null) _minBar.gameObject.SetActive(!_collapsed);
            if (_minChev != null) _minChev.gameObject.SetActive(_collapsed);
            if (_rowsWrap != null) _rowsWrap.gameObject.SetActive(!_collapsed);
            if (_collapsed) { _navIdx = -1; ApplyNavHighlight(); }
            else RebuildNav();
        }

        public void SetPanelOpen(bool open, bool animate)
        {
            if (!_built) return;
            if (_visCo != null) { StopCoroutine(_visCo); _visCo = null; }
            _panelOpen = open;
            if (!open) _collapsed = true;
            ApplyCollapseLayout();

            if (open)
            {
                _panel.gameObject.SetActive(true);
                if (_unlockMouse.Value) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
                if (!_collapsed) { if (_colCo != null) StopCoroutine(_colCo); _colCo = StartCoroutine(CoCollapse(false)); }
                RebuildNav();
            }

            if (!animate)
            {
                _panelCg.alpha = open ? 1f : 0f;
                _panelCg.blocksRaycasts = open;
                _panelCg.interactable = open;
                _panel.localScale = Vector3.one;
                if (!open) _panel.gameObject.SetActive(false);
                return;
            }
            _visCo = StartCoroutine(CoVis(_panelCg.alpha, open ? 1f : 0f, open));
        }

        IEnumerator CoVis(float a0, float a1, bool endOn)
        {
            _panelCg.blocksRaycasts = true;
            float t = 0;
            while (t < 1f)
            {
                t = Mathf.Min(1f, t + Time.unscaledDeltaTime / 0.3f);
                float e = EaseOutCubic(t);
                float a = Mathf.Lerp(a0, a1, e);
                _panelCg.alpha = a;
                float s = Mathf.Lerp(0.94f, 1f, a);
                _panel.localScale = new Vector3(s, s, 1f);
                yield return null;
            }
            _panelCg.alpha = a1;
            _panel.localScale = Vector3.one;
            _panelCg.blocksRaycasts = endOn;
            _panelCg.interactable = endOn;
            if (!endOn)
            {
                _panel.gameObject.SetActive(false);
                if (_unlockMouse.Value) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            }
            _visCo = null;
        }

        // ============================================================
        // 键盘导航
        // ============================================================
        void RebuildNav()
        {
            _nav.Clear();
            foreach (var r in _rows)
            {
                _nav.Add(new NavItem { row = r, kind = 0, sw = r.sw });
                if (r.open && r.bodyWrap != null)
                {
                    foreach (var st in r.subToggles) _nav.Add(new NavItem { row = r, kind = 1, sw = st.sw });
                    foreach (var sl in r.subSliders) _nav.Add(new NavItem { row = r, kind = 2, sl = sl });
                }
            }
            if (_navIdx >= _nav.Count) _navIdx = _nav.Count - 1;
            ApplyNavHighlight();
        }

        void NavMove(int d)
        {
            if (_nav.Count == 0) return;
            _navIdx = _navIdx < 0 ? 0 : (_navIdx + d + _nav.Count) % _nav.Count;
            ApplyNavHighlight();
        }

        void ApplyNavHighlight()
        {
            foreach (var r in _rows)
            {
                bool sel = _navIdx >= 0 && _navIdx < _nav.Count && _nav[_navIdx].row == r;
                if (r.hover != null) r.hover.SetSelected(sel);
            }
        }

        void NavActivate()
        {
            if (_navIdx < 0 || _navIdx >= _nav.Count) { NavMove(1); return; }
            var it = _nav[_navIdx];
            if (it.kind == 2)
            {
                var sl = it.sl;
                float v = Mathf.Clamp(sl.slider.value + sl.step, sl.slider.minValue, sl.slider.maxValue);
                sl.slider.value = v;
            }
            else if (it.sw != null)
            {
                it.sw.Set(!it.sw.on, true);
                if (it.sw.onChanged != null) it.sw.onChanged(it.sw.on);
            }
        }

        void NavExpand(bool expand)
        {
            if (_navIdx < 0 || _navIdx >= _nav.Count) return;
            var r = _nav[_navIdx].row;
            if (!r.hasBody) return;
            if ((expand && !r.open) || (!expand && r.open)) ToggleRow(r);
        }

        // ============================================================
        // 小工具
        // ============================================================
        static Color Tint(Color c, float a) { return new Color(c.r, c.g, c.b, a); }

        public static float EaseOutCubic(float x) { x = Mathf.Clamp01(x); return 1f - Mathf.Pow(1f - x, 3f); }

        static float SdfRoundBox(float px, float py, float w, float h, float r)
        {
            float qx = Mathf.Abs(px) - w * 0.5f + r, qy = Mathf.Abs(py) - h * 0.5f + r;
            float ax = Mathf.Max(qx, 0f), ay = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ax * ax + ay * ay) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        Sprite BakeSprite(int w, int h, float radius, bool ring, float ringW)
        {
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false);
            var px = new Color[w * h];
            float cx = w * 0.5f, cy = h * 0.5f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = SdfRoundBox(x + 0.5f - cx, y + 0.5f - cy, w, h, radius);
                    float a = ring ? Mathf.Clamp01(ringW * 0.5f + 0.5f - Mathf.Abs(d))
                                   : Mathf.Clamp01(0.5f - d);
                    px[y * w + x] = new Color(1f, 1f, 1f, a);
                }
            tex.SetPixels(px);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, false);
            float b = Mathf.Min(radius + 4f, Mathf.Min(w, h) * 0.5f - 1f);
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }

        // 白磨砂玻璃圆角块(alpha-only):上亮下暗渐变 + 内顶高光 + 内底微光 + 1px 内描边。
        Sprite BakeGlass(int w, int h, float radius, float aTop, float aBot, float topLine, float botLine, float border)
        {
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false);
            var px = new Color[w * h];
            float cx = w * 0.5f, cy = h * 0.5f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float px_ = x + 0.5f - cx, py = y + 0.5f - cy;
                    float d = SdfRoundBox(px_, py, w, h, radius);
                    float shape = Mathf.Clamp01(0.5f - d);
                    if (shape <= 0.003f) { px[y * w + x] = new Color(1f, 1f, 1f, 0f); continue; }
                    float v = Mathf.Clamp01((py + h * 0.5f) / h);
                    float a = Mathf.Lerp(aTop, aBot, v);
                    float topIn = py - (h * 0.5f - 2.2f);
                    a += topLine * Mathf.Clamp01(1f - Mathf.Abs(topIn)) * shape;
                    float botIn = py - (h * 0.5f - 1.6f);
                    a += botLine * Mathf.Clamp01(1f - Mathf.Abs(botIn)) * shape;
                    float edge = Mathf.Clamp01(1.2f - d);
                    a += border * edge * shape;
                    px[y * w + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
                }
            tex.SetPixels(px);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, false);
            float b = Mathf.Min(radius + 4f, Mathf.Min(w, h) * 0.5f - 1f);
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }

        // 渐变胶囊(color):用于开关轨道(CSS linear-gradient)。
        Sprite BakeGradientPill(int w, int h, Color top, Color bot, float aTop, float aBot)
        {
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false);
            var px = new Color[w * h];
            float cx = w * 0.5f, cy = h * 0.5f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = SdfRoundBox(x + 0.5f - cx, y + 0.5f - cy, w, h, h * 0.5f);
                    float shape = Mathf.Clamp01(0.5f - d);
                    float v = Mathf.Clamp01((y + 0.5f) / h);
                    Color c = Color.Lerp(top, bot, v);
                    c.a = Mathf.Lerp(aTop, aBot, v) * shape;
                    px[y * w + x] = c;
                }
            tex.SetPixels(px);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, false);
            float b = Mathf.Min(h * 0.5f + 4f, h * 0.5f - 1f);
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }

        // 灰色轨道(alpha + 上暗下亮内阴影),整体 tint 为灰。
        Sprite BakeGrayTrack(int w, int h, Color tint)
        {
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false);
            var px = new Color[w * h];
            float cx = w * 0.5f, cy = h * 0.5f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = SdfRoundBox(x + 0.5f - cx, y + 0.5f - cy, w, h, h * 0.5f);
                    float shape = Mathf.Clamp01(0.5f - d);
                    float v = Mathf.Clamp01((y + 0.5f) / h);
                    float a = Mathf.Lerp(0.55f, 0.30f, v) * shape;
                    px[y * w + x] = new Color(tint.r, tint.g, tint.b, a);
                }
            tex.SetPixels(px);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, false);
            float b = Mathf.Min(h * 0.5f + 4f, h * 0.5f - 1f);
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }

        // iOS knob:白→浅灰渐变圆。
        Sprite BakeKnob(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var px = new Color[size * size];
            float c = size * 0.5f, R = size * 0.5f - 1f;
            Color top = new Color32(255, 255, 255, 255), bot = new Color32(232, 232, 238, 255);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(c, c));
                    float a = Mathf.Clamp01(R - d + 0.5f);
                    float v = Mathf.Clamp01((y + 0.5f) / size);
                    Color col = Color.Lerp(top, bot, v);
                    col.a = a;
                    px[y * size + x] = col;
                }
            tex.SetPixels(px);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        // Logo 蓝渐变圆角块(网页 linear-gradient(160deg,...))。
        Sprite BakeLogo(int size, float radius)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var px = new Color[size * size];
            float cx = size * 0.5f, cy = size * 0.5f;
            Color top = new Color(0.47f, 0.75f, 1f, 0.45f), bot = new Color(0.27f, 0.47f, 1f, 0.28f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = SdfRoundBox(x + 0.5f - cx, y + 0.5f - cy, size, size, radius);
                    float shape = Mathf.Clamp01(0.5f - d);
                    float v = Mathf.Clamp01((y + 0.5f) / size);
                    Color c = Color.Lerp(top, bot, v);
                    c.a = shape;
                    px[y * size + x] = c;
                }
            tex.SetPixels(px);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        static void BoxBlur(float[] a, int size, int p)
        {
            var tmp = new float[size * size];
            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                for (int x = 0; x < size; x++)
                {
                    float s = 0; int n = 0;
                    for (int k = -p; k <= p; k++)
                    {
                        s += a[row + Mathf.Clamp(x + k, 0, size - 1)];
                        n++;
                    }
                    tmp[row + x] = s / n;
                }
            }
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    float s = 0; int n = 0;
                    for (int k = -p; k <= p; k++)
                    {
                        s += tmp[Mathf.Clamp(y + k, 0, size - 1) * size + x];
                        n++;
                    }
                    a[y * size + x] = s / n;
                }
            }
        }

        Sprite BakeCircleSprite(int size, bool ring, float ringW)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var px = new Color[size * size];
            float c = size * 0.5f, R = size * 0.5f - 1f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(c, c));
                    float a = ring ? Mathf.Clamp01(ringW * 0.5f + 0.5f - Mathf.Abs(d - R))
                                   : Mathf.Clamp01(R - d + 0.5f);
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            tex.SetPixels(px);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        Sprite BakeSheenSprite(int size)
        {
            return BakeSheenSprite(size, 0.18f);
        }

        // 柔光点:中心 peak,60% 半径处衰减到 0(网页 radial-gradient 60% 停止位)。
        Sprite BakeSheenSprite(int size, float peak)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var px = new Color[size * size];
            float c = size * 0.5f;
            float rSoft = size * 0.5f * 0.6f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(c, c));
                    float k = Mathf.Clamp01((rSoft - d) / rSoft);
                    float a = peak * k * k * (3f - 2f * k);
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            tex.SetPixels(px);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        // ---- lucide 风格矢量图标(SDF 烘焙,24 设计网格 / 2 线宽) ----
        static readonly Vector2[] ZapPoly =
        {
            new Vector2(13f, 1.5f), new Vector2(4.5f, 13.5f), new Vector2(10.5f, 13.5f),
            new Vector2(9f, 22.5f), new Vector2(19.5f, 9.5f), new Vector2(12.5f, 9.5f)
        };
        static readonly Vector2[] ShieldPoly =
        {
            new Vector2(12f, 2.5f), new Vector2(18.5f, 5.2f), new Vector2(18.5f, 10.8f),
            new Vector2(17.4f, 15f), new Vector2(14.6f, 18.4f), new Vector2(12f, 20.9f),
            new Vector2(9.4f, 18.4f), new Vector2(6.6f, 15f), new Vector2(5.5f, 10.8f),
            new Vector2(5.5f, 5.2f)
        };

        static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(0.0001f, ab.sqrMagnitude));
            return Vector2.Distance(p, a + ab * t);
        }

        static float EllipseDist(Vector2 p, Vector2 c, float rx, float ry)
        {
            float nx = (p.x - c.x) / rx, ny = (p.y - c.y) / ry;
            return (Mathf.Sqrt(nx * nx + ny * ny) - 1f) * Mathf.Min(rx, ry);
        }

        static float PolyDist(Vector2 p, Vector2[] pts)
        {
            float d = 1e9f;
            bool inside = false;
            for (int i = 0; i < pts.Length; i++)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % pts.Length];
                d = Mathf.Min(d, SegDist(p, a, b));
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside ? -d : d;
        }

        float IconSDF(string kind, float x, float y)
        {
            var p = new Vector2(x, y);
            float best = 1e9f;
            switch (kind)
            {
                case "aim":
                    best = Mathf.Min(best, Mathf.Abs(Vector2.Distance(p, new Vector2(12f, 12f)) - 8.2f));
                    best = Mathf.Min(best, SegDist(p, new Vector2(12f, 1.8f), new Vector2(12f, 5.4f)));
                    best = Mathf.Min(best, SegDist(p, new Vector2(12f, 18.6f), new Vector2(12f, 22.2f)));
                    best = Mathf.Min(best, SegDist(p, new Vector2(1.8f, 12f), new Vector2(5.4f, 12f)));
                    best = Mathf.Min(best, SegDist(p, new Vector2(18.6f, 12f), new Vector2(22.2f, 12f)));
                    return best;
                case "fly":
                    best = Mathf.Min(best, SegDist(p, new Vector2(2.5f, 7f), new Vector2(15.5f, 7f)));
                    best = Mathf.Min(best, SegDist(p, new Vector2(2.5f, 12f), new Vector2(21f, 12f)));
                    best = Mathf.Min(best, SegDist(p, new Vector2(2.5f, 17f), new Vector2(13f, 17f)));
                    return best;
                case "punch":
                    return PolyDist(p, ZapPoly);
                case "shield":
                    for (int i = 0; i < ShieldPoly.Length; i++)
                        best = Mathf.Min(best, SegDist(p, ShieldPoly[i], ShieldPoly[(i + 1) % ShieldPoly.Length]));
                    return best;
                case "coin":
                    best = Mathf.Min(best, Mathf.Abs(Vector2.Distance(p, new Vector2(9f, 9.5f)) - 6f));
                    best = Mathf.Min(best, Mathf.Abs(Vector2.Distance(p, new Vector2(15f, 14.5f)) - 6f));
                    return best;
                case "fish":
                    best = Mathf.Min(best, Mathf.Abs(EllipseDist(p, new Vector2(10.5f, 12.5f), 6.3f, 4f)));
                    best = Mathf.Min(best, SegDist(p, new Vector2(16.6f, 12.5f), new Vector2(21.3f, 8.8f)));
                    best = Mathf.Min(best, SegDist(p, new Vector2(16.6f, 12.5f), new Vector2(21.3f, 16.2f)));
                    best = Mathf.Min(best, Vector2.Distance(p, new Vector2(7.6f, 11.6f)) - 1f);
                    return best;
            }
            return best;
        }

        Sprite GetIcon(string kind)
        {
            Sprite sp;
            if (_icons.TryGetValue(kind, out sp)) return sp;
            const int size = 96;                       // 显示 20px → 4.8x,带 mipmap 才不会糊
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, true);
            var px = new Color[size * size];
            float g = size / 24f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // 描边宽度 = 2 设计单位(lucide 默认),转成纹理像素后再抗锯齿,缩到 19px 显示仍有 ~1.6px 清晰线条
                    float dPx = IconSDF(kind, (x + 0.5f) / g, (y + 0.5f) / g) * g;
                    float half = g;
                    px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(half - Mathf.Abs(dPx) + 0.5f));
                }
            tex.SetPixels(px);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.Apply(true, false);
            sp = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            _icons[kind] = sp;
            return sp;
        }

        // ---- RectTransform / 组件辅助 ----
        static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.localScale = Vector3.one;
            return rt;
        }

        static RectTransform RT(Component c) { return (RectTransform)c.transform; }

        static RectTransform NodeC(Transform parent, string name, float cx, float cy, float w, float h)
        {
            var rt = NewRect(parent, name);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(cx, cy);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        static void FullStretch(RectTransform rt, float inset)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }

        /// <summary>布局子项:显式中心锚点 + 尺寸,并注册 LayoutElement 首选值(双保险)。</summary>
        static LayoutElement LE(Component c, float w, float h)
        {
            var rt = (RectTransform)c.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(Mathf.Max(0f, w), Mathf.Max(0f, h));
            var le = c.gameObject.AddComponent<LayoutElement>();
            le.minWidth = Mathf.Max(0f, w);
            le.preferredWidth = Mathf.Max(0f, w);
            le.minHeight = Mathf.Max(0f, h);
            le.preferredHeight = Mathf.Max(0f, h);
            return le;
        }

        Image Img(Transform parent, string name, Sprite sp, Color col, bool raycast, Image.Type type = Image.Type.Simple)
        {
            var go = new GameObject(name, typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var im = go.GetComponent<Image>();
            im.sprite = sp;
            im.color = col;
            im.raycastTarget = raycast;
            im.type = type;
            var rt = (RectTransform)im.transform;
            rt.localScale = Vector3.one;
            // 关键:新建 RectTransform 默认是 100×100 且锚点在左下,会让图标底/开关圆钮变成巨型色块。
            // 这里统一改成"铺满父节点",尺寸完全由父级决定。
            FullStretch(rt, 0f);
            return im;
        }

        Text Txt(Transform parent, string name, string s, int size, Color col, TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            var go = new GameObject(name, typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = _font;
            t.text = s;
            t.fontSize = size;
            t.color = col;
            t.alignment = anchor;
            t.supportRichText = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            // 1px 深色投影:玻璃透出场景杂色时文字依然清晰
            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.5f);
            shadow.effectDistance = new Vector2(0f, -1f);
            shadow.useGraphicAlpha = true;
            var rt = (RectTransform)t.transform;
            rt.localScale = Vector3.one;
            FullStretch(rt, 0f);
            return t;
        }
    }

    // ================================================================
    // 交互组件
    // ================================================================
    class SwitchView : MonoBehaviour, IPointerClickHandler
    {
        public Image track;
        public RectTransform knob;
        public Image glow;
        public Sprite sprOn, sprOff;
        public float xOff, xOn;
        public bool on;
        public Action<bool> onChanged;
        Coroutine _co;
        static readonly Color GlowCol = new Color(0.24f, 0.86f, 0.43f, 0.35f);

        public void Set(bool v, bool animate)
        {
            on = v;
            if (_co != null) { StopCoroutine(_co); _co = null; }
            // 轨道颜色立刻切换,只有圆钮滑动/光晕做动画(否则要等 0.28s 动画结束才变色)
            ApplyTrack();
            if (!animate)
            {
                knob.anchoredPosition = new Vector2(on ? xOn : xOff, 0f);
                ApplyGlow(on ? GlowCol.a : 0f);
            }
            else _co = StartCoroutine(Co(v));
        }

        void ApplyTrack()
        {
            if (track != null) { track.sprite = on ? sprOn : sprOff; track.color = Color.white; }
        }

        void ApplyGlow(float a)
        {
            if (glow != null) glow.color = new Color(GlowCol.r, GlowCol.g, GlowCol.b, a);
        }

        void ApplyVisual()
        {
            ApplyTrack();
            ApplyGlow(on ? GlowCol.a : 0f);
        }

        IEnumerator Co(bool v)
        {
            Vector2 from = knob.anchoredPosition, to = new Vector2(v ? xOn : xOff, 0f);
            float g0 = glow != null ? glow.color.a : 0f;
            float t = 0;
            while (t < 1f)
            {
                t = Mathf.Min(1f, t + Time.unscaledDeltaTime / 0.28f);
                knob.anchoredPosition = Vector2.LerpUnclamped(from, to, EaseOutBack(t));
                if (glow != null)
                    glow.color = new Color(GlowCol.r, GlowCol.g, GlowCol.b, Mathf.Lerp(g0, v ? GlowCol.a : 0f, Mathf.Min(1f, t * 1.6f)));
                yield return null;
            }
            knob.anchoredPosition = to;
            ApplyVisual();
            _co = null;
        }

        public void OnPointerClick(PointerEventData e)
        {
            Set(!on, true);
            if (onChanged != null) onChanged(on);
        }

        static float EaseOutBack(float x)
        {
            const float c1 = 1.7f;
            float c3 = c1 + 1f;
            x = Mathf.Clamp01(x);
            return 1f + c3 * (x - 1f) * (x - 1f) * (x - 1f) + c1 * (x - 1f) * (x - 1f);
        }
    }

    class ClickForward : MonoBehaviour, IPointerClickHandler
    {
        public Action onClick;
        public void OnPointerClick(PointerEventData e) { if (onClick != null) onClick(); }
    }

    class RowFX : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        // 深色玻璃下的行底色(半透明浅蓝白),开启/悬停/关闭三档
        public static readonly Color ColOn    = new Color(0.18f, 0.26f, 0.42f, 1f);
        public static readonly Color ColHover = new Color(0.27f, 0.37f, 0.57f, 1f);
        public static readonly Color ColOff   = new Color(0.09f, 0.12f, 0.19f, 1f);

        Image _bg;
        Sprite _sprOn, _sprHover, _sprOff;
        bool _hovered, _selected;
        bool _on = true;

        public void Init(Image bg, Sprite sprOn, Sprite sprHover, Sprite sprOff)
        {
            _bg = bg;
            _sprOn = sprOn;
            _sprHover = sprHover;
            _sprOff = sprOff;
        }

        public void SetOn(bool v) { _on = v; Apply(); }
        public void OnPointerEnter(PointerEventData e) { _hovered = true; Apply(); }
        public void OnPointerExit(PointerEventData e) { _hovered = false; Apply(); }
        public void SetSelected(bool v) { _selected = v; Apply(); }

        void Apply()
        {
            if (_bg == null) return;
            _bg.sprite = !_on ? _sprOff : (_selected || _hovered) ? _sprHover : _sprOn;
            _bg.color = !_on ? ColOff : (_selected || _hovered) ? ColHover : ColOn;
        }
    }

    class DragMove : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public RectTransform target;
        RectTransform _canvasRect;
        Vector2 _grab;
        bool _drag;

        public void Init(RectTransform targetRt, RectTransform canvasRect)
        {
            target = targetRt;
            _canvasRect = canvasRect;
        }

        public void OnPointerDown(PointerEventData e)
        {
            var hitGo = e.pointerCurrentRaycast.gameObject;
            if (hitGo != null && hitGo.transform.parent != null && hitGo.transform.parent.name == "MinBtn") return;
            Vector2 lp;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, e.position, null, out lp))
            {
                _grab = target.anchoredPosition - lp;
                _drag = true;
            }
        }

        // 面板锚点/枢轴都是左上角(0,1):anchoredPosition 即"左上角"在画布中的坐标。
        public void OnDrag(PointerEventData e)
        {
            if (!_drag) return;
            Vector2 lp;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, e.position, null, out lp))
            {
                Vector2 np = lp + _grab;
                float cw = _canvasRect.rect.width, ch = _canvasRect.rect.height;
                float pw = target.rect.width, ph = target.rect.height;
                np.x = Mathf.Clamp(np.x, 8f, Mathf.Max(8f, cw - pw - 8f));
                np.y = Mathf.Clamp(np.y, Mathf.Max(-ch + ph + 8f, -ch + 8f), -8f);
                target.anchoredPosition = np;
            }
        }

        public void OnPointerUp(PointerEventData e) { _drag = false; }
    }

    // ================================================================
    // 数据容器
    // ================================================================
    class RowView
    {
        public BaseUnityPlugin owner;
        public ConfigEntryBase main;
        public bool behaviourToggle;
        public Color iconCol;
        public RectTransform rect, chev, bodyWrap, innerBody;
        public LayoutElement bodyLE;
        public CanvasGroup bodyCg, innerCg;
        public Image bg, chipBg, iconImg;
        public RowFX hover;
        public Text title, sub;
        public SwitchView sw;
        public readonly List<SubToggleItem> subToggles = new List<SubToggleItem>();
        public readonly List<SubSliderItem> subSliders = new List<SubSliderItem>();
        public readonly List<ChipItem> chips = new List<ChipItem>();
        public float bodyH;
        public bool hasBody, open;
    }

    class SubToggleItem { public ConfigEntryBase entry; public SwitchView sw; }
    class SubSliderItem { public ConfigEntryBase entry; public Slider slider; public Text valueText; public bool isInt; public string suffix; public float step; }
    class ChipItem { public ConfigEntryBase entry; public ChipView view; }
    class NavItem { public RowView row; public int kind; public SwitchView sw; public SubSliderItem sl; }

    // 锁目标 Chip 胶囊(网页 lg-chip):开启为蓝白渐变胶囊高亮,关闭为半透明描边。
    class ChipView : MonoBehaviour, IPointerClickHandler
    {
        public static readonly Color OnBorder  = new Color(0.60f, 0.80f, 1f, 0.80f); // rgba(150,200,255,.8)
        public static readonly Color OnText    = new Color(1f, 1f, 1f, 1f);
        public static readonly Color OffBorder = new Color(1f, 1f, 1f, 0.25f);
        public static readonly Color OffFill   = new Color(1f, 1f, 1f, 0.06f);
        public static readonly Color OffText   = new Color(1f, 1f, 1f, 0.75f);

        public Image border, fill;
        public Sprite fillOnSpr, fillOffSpr;
        public Text label;
        public bool on;
        public Action<bool> onChanged;
        Coroutine _co;

        public void Set(bool v, bool animate)
        {
            on = v;
            if (!animate)
            {
                ApplyVisual();
                return;
            }
            if (_co != null) StopCoroutine(_co);
            _co = StartCoroutine(Co());
        }

        void ApplyVisual()
        {
            border.color = on ? OnBorder : OffBorder;
            fill.sprite = on ? fillOnSpr : fillOffSpr;
            fill.color = on ? Color.white : OffFill;
            label.color = on ? OnText : OffText;
        }

        IEnumerator Co()
        {
            Color b0 = border.color, t0 = label.color, f0 = fill.color;
            float t = 0;
            fill.sprite = on ? fillOnSpr : fillOffSpr;
            while (t < 1f)
            {
                t = Mathf.Min(1f, t + Time.unscaledDeltaTime / 0.22f);
                float e = t * t * (3f - 2f * t);
                border.color = Color.Lerp(b0, on ? OnBorder : OffBorder, e);
                fill.color = Color.Lerp(f0, on ? Color.white : OffFill, e);
                label.color = Color.Lerp(t0, on ? OnText : OffText, e);
                yield return null;
            }
            ApplyVisual();
            _co = null;
        }

        public void OnPointerClick(PointerEventData e)
        {
            Set(!on, true);
            if (onChanged != null) onChanged(on);
        }
    }
}
