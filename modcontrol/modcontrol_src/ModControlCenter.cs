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
    /// ModControlCenter —— 游戏内"液态玻璃"风格 Mod 控制悬浮窗。
    /// 通过读取已加载 BepInEx 插件的 ConfigEntry 实现实时开关/调参,并回写配置文件。
    /// 所有动画基于 unscaledTime 的协程缓动,帧率无关。
    /// </summary>
    [BepInPlugin("me.yexiao07.plugin.modcontrol", "ModControlCenter", "1.0.0")]
    public class ModControlCenter : BaseUnityPlugin
    {
        internal static ModControlCenter Instance;
        internal static ManualLogSource Log;

        // ---------------- config ----------------
        ConfigEntry<KeyCode> _toggleKey;
        ConfigEntry<bool> _showOnStart;

        // ---------------- theme ----------------
        public static readonly Color TrackOn  = new Color32(52, 199, 89, 255);
        public static readonly Color TrackOff = new Color(1f, 1f, 1f, 0.24f);
        static readonly Color GlassCol  = new Color32(16, 21, 40, 188);
        static readonly Color RimCol    = new Color(1f, 1f, 1f, 0.32f);
        public static readonly Color RowColor       = new Color(1f, 1f, 1f, 0.115f);
        public static readonly Color RowHoverColor  = new Color(1f, 1f, 1f, 0.20f);
        static readonly Color TextMain  = new Color(1f, 1f, 1f, 0.97f);
        static readonly Color TextSub   = new Color(1f, 1f, 1f, 0.58f);
        static readonly Color GreenDot  = new Color32(74, 222, 128, 255);

        static readonly Color ChipGreen  = new Color32(121, 232, 152, 255);
        static readonly Color ChipBlue   = new Color32(130, 182, 255, 255);
        static readonly Color ChipCyan   = new Color32(111, 214, 232, 255);
        static readonly Color ChipOrange = new Color32(255, 173, 98, 255);
        static readonly Color ChipGold   = new Color32(255, 212, 105, 255);
        static readonly Color ChipWhite  = new Color(1f, 1f, 1f, 0.92f);

        // ---------------- sprites / font ----------------
        Sprite _spPanel, _spPanelRim, _spRow, _spChip, _spPill, _spCircle, _spRing;
        Font _font;

        // ---------------- ui refs ----------------
        GameObject _root;
        RectTransform _panel, _header, _rowsWrap, _rowsRect;
        CanvasGroup _panelCg;
        Text _countText;
        Image _dot;
        readonly List<RowView> _rows = new List<RowView>();
        readonly Dictionary<ConfigFile, Coroutine> _saveCos = new Dictionary<ConfigFile, Coroutine>();
        readonly Dictionary<RowView, Coroutine> _rowAnim = new Dictionary<RowView, Coroutine>();

        bool _built;
        bool _collapsed;
        bool _visible;
        Coroutine _visCo, _colCo;
        float _dotT, _syncT;

        const float HeadH = 62f, RowW = 420f, Gap = 10f, PadSide = 12f, RowClosedH = 60f;

        // ---------------- known mods ----------------
        struct KnownMod { public string guid, title, sub, sec, key, glyph; public Color col; public bool behaviourToggle; }
        static readonly KnownMod[] Known =
        {
            new KnownMod{ guid="me.yexiao07.plugin.fishaimlock", title="自动瞄准", sub="锁定视野内生物 · 小键盘2", sec="基础设置", key="锁头功能启用", col=ChipBlue, glyph="aim" },
            new KnownMod{ guid="me.yexiao07.plugin.flymod", title="飞行模式", sub="朝准星方向飞行 · 可调速度", sec="基础设置", key="飞行启用", col=ChipCyan, glyph="fly" },
            new KnownMod{ guid="me.yexiao07.plugin.boxinginjury", title="拳击伤害改版", sub="空手高额伤害 · 小键盘.", sec="基础设置", key="拳击伤害改版启用", col=ChipOrange, glyph="punch" },
            new KnownMod{ guid="me.yexiao07.plugin.unrivalled", title="无双 · 无敌", sub="免疫全部伤害 · 小键盘+", sec="基础设置", key="无双/无敌启用", col=ChipGreen, glyph="shield" },
            new KnownMod{ guid="me.yexiao.fish.finalmod", title="金钱拉满", sub="货币保持上限 · 开关插件本体", col=ChipGold, glyph="coin", behaviourToggle=true },
        };

        struct SubSpec { public string guid, sec, key, label, suffix; public bool slider; public float min, max, step; }
        static readonly SubSpec[] Subs =
        {
            new SubSpec{ guid="me.yexiao07.plugin.fishaimlock", sec="目标筛选", key="锁定鱼类", label="锁鱼" },
            new SubSpec{ guid="me.yexiao07.plugin.fishaimlock", sec="目标筛选", key="锁定鸟类", label="锁鸟" },
            new SubSpec{ guid="me.yexiao07.plugin.fishaimlock", sec="目标筛选", key="锁定BOSS", label="锁 Boss" },
            new SubSpec{ guid="me.yexiao07.plugin.fishaimlock", sec="目标筛选", key="锁定其他生物", label="锁其他" },
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
            _toggleKey = Config.Bind("界面", "显示/隐藏热键", KeyCode.F1, "按下该键显示或隐藏 Mod 控制悬浮窗");
            _showOnStart = Config.Bind("界面", "启动时显示", true, "进入游戏后是否自动显示悬浮窗");
            Log.LogInfo("[modctl] ModControlCenter v1.0.0 加载成功 (F1 显示/隐藏)");
        }

        IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(1.2f);
            try { BuildUI(); _built = true; }
            catch (Exception ex) { Log.LogError("[modctl] UI 构建失败: " + ex); yield break; }
            DiscoverAndBuildRows();
            _visible = false;
            if (_showOnStart.Value) SetVisible(true, true); else SetVisible(false, true);
            Log.LogInfo("[modctl] 悬浮窗就绪, 共 " + _rows.Count + " 个条目");
        }

        void Update()
        {
            if (_toggleKey != null && Input.GetKeyDown(_toggleKey.Value)) SetVisible(!_visible, true);
            if (!_visible || !_built) return;
            _dotT += Time.unscaledDeltaTime;
            if (_dot != null)
            {
                float s = 1f + 0.1f * Mathf.Sin(_dotT * 2.6f);
                _dot.rectTransform.localScale = new Vector3(s, s, 1f);
            }
            _syncT += Time.unscaledDeltaTime;
            if (_syncT >= 0.5f) { _syncT = 0f; SyncFromEntries(); }
        }

        // ============================================================
        // UI 构建
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

            _spPanel    = BakeSprite(128, 128, 34, false, 0);
            _spPanelRim = BakeSprite(128, 128, 34, true, 3f);
            _spRow      = BakeSprite(96, 96, 24, false, 0);
            _spChip     = BakeSprite(64, 64, 18, false, 0);
            _spPill     = BakeSprite(64, 32, 16, false, 0);
            _spCircle   = BakeCircleSprite(64, false, 0);
            _spRing     = BakeCircleSprite(64, true, 5f);

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

            _panel = Node(_root.transform, "Panel", 24, 84, RowW + PadSide * 2, 200);
            _panelCg = _panel.gameObject.AddComponent<CanvasGroup>();
            Img(_panel, "BG", _spPanel, GlassCol, false, Image.Type.Sliced);
            Img(_panel, "Rim", _spPanelRim, RimCol, false, Image.Type.Sliced);

            // ---- header ----
            _header = Node(_panel, "Header", 0, 0, RowW + PadSide * 2, HeadH);
            Img(_header, "Hit", null, new Color(0f, 0f, 0f, 0.01f), true);
            var drag = _header.gameObject.AddComponent<DragMove>();
            drag.Init(_panel, canvasRt);

            var logo = Node(_header, "Logo", 12, 12, 38, 38);
            Img(logo, "bg", _spChip, new Color(0.40f, 0.57f, 1f, 0.24f), false);
            var logoDot = NodeC(logo, "d", 0, 0, 20, 20);
            Img(logoDot, "i", _spCircle, new Color(0.55f, 0.75f, 1f, 0.95f), false);
            var logoRing = NodeC(logo, "r", 0, 0, 28, 28);
            Img(logoRing, "i", _spRing, new Color(1f, 1f, 1f, 0.85f), false);

            var tt = Node(_header, "T", 62, 10, 210, 22);
            Txt(tt, "s", "Mod 控制中心", 16, TextMain);
            var ts = Node(_header, "S", 62, 33, 230, 16);
            Txt(ts, "s", "How to Fish · BepInEx", 11, TextSub);

            _dot = Img(_header, "Dot", _spCircle, GreenDot, false);
            RT(_dot).sizeDelta = new Vector2(8, 8);
            NodeSet(RT(_dot), 292, 27, 8, 8);

            var ct = Node(_header, "Count", 306, 21, 92, 20);
            _countText = Txt(ct, "s", "0 已启用", 12, new Color(1f, 1f, 1f, 0.85f));

            var minRt = Node(_header, "MinBtn", RowW + PadSide * 2 - 38, 18, 26, 26);
            var minImg = Img(minRt, "bg", _spChip, new Color(1f, 1f, 1f, 0.14f), true);
            var minBar = NodeC(minRt, "bar", 0, 0, 12, 2.6f);
            Img(minBar, "i", null, new Color(1f, 1f, 1f, 0.85f), false);
            var btn = minImg.gameObject.AddComponent<Button>();
            btn.onClick.AddListener(ToggleCollapse);

            // ---- rows ----
            _rowsWrap = Node(_panel, "RowsWrap", PadSide, HeadH + 2, RowW, 0);
            Img(_rowsWrap, "MaskImg", null, new Color(0, 0, 0, 0), false);
            _rowsWrap.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            _rowsRect = Node(_rowsWrap, "Rows", 0, 0, RowW, 0);
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

                var row = AddRow(p, k.title, k.sub, k.col, k.glyph, main, k.behaviourToggle);
                foreach (var s in Subs)
                {
                    if (s.guid != k.guid) continue;
                    var e = GetEntry(p, s.sec, s.key);
                    if (e == null) continue;
                    if (s.slider) AddSubSlider(row, s.label, s.suffix, e, s.min, s.max, s.step);
                    else AddSubToggle(row, s.label, e);
                }
                FinishBody(row);
                _rows.Add(row);
            }

            // 通用兜底:其他插件里含"启用"的布尔配置也生成条目
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
                    var row = AddRow(p, kv.Value.Metadata.Name, m.Definition.Section + " / " + m.Definition.Key, ChipWhite, "shield", m, false);
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

            StackRows();
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
        RowView AddRow(BaseUnityPlugin owner, string title, string sub, Color iconCol, string glyph, ConfigEntryBase main, bool behaviourToggle)
        {
            var row = new RowView();
            row.owner = owner; row.main = main; row.behaviourToggle = behaviourToggle; row.iconCol = iconCol;
            row.rect = Node(_rowsRect, "Row", 0, 0, RowW, RowClosedH);
            row.bg = Img(row.rect, "BG", _spRow, RowColor, false, Image.Type.Sliced);

            var head = Node(row.rect, "Head", 0, 0, RowW, RowClosedH);
            var hit = Img(head, "Hit", null, new Color(0f, 0f, 0f, 0.01f), true);
            var click = hit.gameObject.AddComponent<ClickForward>();
            row.hover = hit.gameObject.AddComponent<RowHoverFX>();
            row.hover.bg = row.bg;
            click.onClick = () => { if (row.openH > row.closedH + 1f) ToggleRow(row); };

            var chip = Node(head, "Icon", 10, 11, 38, 38);
            row.chipBg = Img(chip, "bg", _spChip, Tint(iconCol, 0.17f), false);
            row.iconImg = BuildGlyph(chip, glyph, iconCol);

            var t1 = Node(head, "T", 60, 10, 230, 22);
            row.title = Txt(t1, "s", title, 15, TextMain);
            var t2 = Node(head, "S", 60, 33, 250, 16);
            row.sub = Txt(t2, "s", sub, 11, TextSub);

            bool hasBody = false; // 稍后由 FinishBody 决定;箭头占位始终保留以对齐
            row.chev = Node(head, "Chev", RowW - 28, RowClosedH / 2f - 7, 14, 14);
            var c1 = Node(row.chev, "a", 0.5f, 4f, 8, 2.6f);
            RT(Img(c1, "i", _spPill, new Color(1f, 1f, 1f, 0.55f), false, Image.Type.Sliced)).localEulerAngles = new Vector3(0, 0, 45);
            var c2 = Node(row.chev, "b", 5.5f, 4f, 8, 2.6f);
            RT(Img(c2, "i", _spPill, new Color(1f, 1f, 1f, 0.55f), false, Image.Type.Sliced)).localEulerAngles = new Vector3(0, 0, -45);
            if (!hasBody) row.chev.gameObject.name = "ChevGhost";

            row.sw = AddSwitch(head, RowW - 78, RowClosedH / 2f, false, IsOn(row), 46, 28);
            row.sw.onChanged = v =>
            {
                if (row.behaviourToggle) { if (row.owner != null) ((Behaviour)row.owner).enabled = v; }
                else row.main.BoxedValue = v;
                ApplyRowVisual(row, v, true);
                ScheduleSave(row.owner != null ? row.owner.Config : Config);
                UpdateCount();
            };
            return row;
        }

        void InitBody(RowView row)
        {
            if (row.bodyRect != null) return;
            row.bodyRect = Node(row.rect, "Body", 0, RowClosedH, RowW, 10);
            row.bodyCg = row.bodyRect.gameObject.AddComponent<CanvasGroup>();
            row.bodyCg.alpha = 0f;
            row.bodyCg.blocksRaycasts = false;
            row.bodyY = 4f;
        }

        void FinishBody(RowView row)
        {
            if (row.bodyRect == null) { row.openH = row.closedH; return; }
            float h = row.bodyY + 6f;
            row.bodyRect.sizeDelta = new Vector2(RowW, h);
            row.openH = row.closedH + h;
            row.chev.gameObject.name = "Chev";
        }

        void AddSubToggle(RowView row, string label, ConfigEntryBase e)
        {
            InitBody(row);
            var rt = Node(row.bodyRect, "Sub", 12, row.bodyY, RowW - 24, 30);
            row.bodyY += 36f;
            var lt = Node(rt, "L", 0, 5, 250, 20);
            Txt(lt, "s", label, 13, new Color(1f, 1f, 1f, 0.85f));
            var sw = AddSwitch(rt, RowW - 24 - 44, 15f, true, (bool)e.BoxedValue, 40, 24);
            sw.onChanged = v => { e.BoxedValue = v; ScheduleSave(e.ConfigFile); };
            row.subToggles.Add(new SubToggleItem { entry = e, sw = sw });
        }

        void AddSubSlider(RowView row, string label, string suffix, ConfigEntryBase e, float min, float max, float step)
        {
            InitBody(row);
            bool isInt = e.SettingType == typeof(int);
            var rt = Node(row.bodyRect, "Sl", 12, row.bodyY, RowW - 24, 46);
            row.bodyY += 54f;

            var lt = Node(rt, "L", 0, 0, 180, 16);
            Txt(lt, "s", label, 12, TextSub);
            var vt = Node(rt, "V", RowW - 24 - 150, 0, 150, 16);
            var valueText = Txt(vt, "s", "", 12, TextMain, TextAnchor.MiddleRight);

            var sldRt = Node(rt, "Sld", 0, 20, RowW - 24, 22);
            Img(sldRt, "bg", _spPill, new Color(1f, 1f, 1f, 0.20f), false, Image.Type.Sliced).raycastTarget = false;

            var fill = NewRect(sldRt, "Fill");
            fill.anchorMin = new Vector2(0f, 0.5f); fill.anchorMax = new Vector2(0f, 0.5f);
            fill.pivot = new Vector2(0f, 0.5f);
            fill.sizeDelta = new Vector2(0f, 6f); fill.anchoredPosition = Vector2.zero;
            Img(fill, "i", _spPill, new Color(1f, 1f, 1f, 0.88f), false, Image.Type.Sliced);

            var handle = NewRect(sldRt, "Handle");
            handle.anchorMin = new Vector2(0f, 0.5f); handle.anchorMax = new Vector2(0f, 0.5f);
            handle.pivot = new Vector2(0.5f, 0.5f);
            handle.sizeDelta = new Vector2(20, 20); handle.anchoredPosition = Vector2.zero;
            Img(handle, "i", _spCircle, Color.white, false);

            var slider = sldRt.gameObject.AddComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min; slider.maxValue = max;
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
            row.subSliders.Add(new SubSliderItem { entry = e, slider = slider, valueText = valueText, isInt = isInt, suffix = suffix });
        }

        static string Fmt(bool isInt, string suffix, float v)
        {
            return (isInt ? Mathf.RoundToInt(v).ToString("N0") : v.ToString("0.#")) + suffix;
        }

        SwitchView AddSwitch(RectTransform parent, float x, float cyCenter, bool small, bool on, float w, float h)
        {
            var rt = Node(parent, "Switch", x, cyCenter - h / 2f, w, h);
            var sv = rt.gameObject.AddComponent<SwitchView>();
            sv.track = Img(rt, "track", _spPill, on ? TrackOn : TrackOff, true, Image.Type.Sliced);
            FullStretch(RT(sv.track), 0);
            float travel = (w - h) * 0.5f - 2f;
            sv.xOff = -travel; sv.xOn = travel;
            sv.knob = NodeC(rt, "knob", on ? travel : -travel, 0, h - 4, h - 4);
            var kImg = Img(sv.knob, "k", _spCircle, Color.white, false);
            FullStretch(RT(kImg), 0);
            sv.on = on;
            return sv;
        }

        Image BuildGlyph(RectTransform chip, string kind, Color col)
        {
            Image mainImg;
            switch (kind)
            {
                case "aim":
                {
                    var ring = NodeC(chip, "g", 0, 0, 24, 24);
                    Img(ring, "i", _spRing, col, false);
                    var dot = NodeC(chip, "d", 0, 0, 7, 7);
                    mainImg = Img(dot, "i", _spCircle, col, false);
                    break;
                }
                case "fly":
                {
                    mainImg = GlyphBar(chip, 6, 16, 3, col);
                    GlyphBar(chip, 0, 12, 3, col);
                    GlyphBar(chip, -6, 8, 3, col);
                    break;
                }
                case "punch":
                {
                    var b = NodeC(chip, "b", 0, 0, 20, 3.6f);
                    b.localEulerAngles = new Vector3(0, 0, -45);
                    mainImg = Img(b, "i", _spPill, col, false, Image.Type.Sliced);
                    break;
                }
                case "coin":
                {
                    var c = NodeC(chip, "c", 0, 0, 19, 19);
                    mainImg = Img(c, "i", _spCircle, col, false);
                    var r = NodeC(chip, "r", 0, 0, 26, 26);
                    Img(r, "i", _spRing, Tint(col, 0.9f), false);
                    break;
                }
                default:
                {
                    var c = NodeC(chip, "c", 0, 0, 21, 21);
                    mainImg = Img(c, "i", _spCircle, col, false);
                    var n = NodeC(chip, "n", 0, -7, 12, 4);
                    Img(n, "i", _spPill, new Color(0.10f, 0.14f, 0.25f, 0.55f), false, Image.Type.Sliced);
                    break;
                }
            }
            return mainImg;
        }

        Image GlyphBar(RectTransform chip, float cy, float w, float h, Color col)
        {
            var b = NodeC(chip, "bar", 0, cy, w, h);
            return Img(b, "i", _spPill, col, false, Image.Type.Sliced);
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
            Color iconOn = r.iconCol, iconOff = new Color(0.78f, 0.81f, 0.88f, 0.5f);
            Color tOn = TextMain, tOff = new Color(1f, 1f, 1f, 0.5f);
            Color cbOn = Tint(r.iconCol, 0.17f), cbOff = new Color(0.85f, 0.87f, 0.93f, 0.08f);
            if (!animate)
            {
                r.iconImg.color = on ? iconOn : iconOff;
                r.title.color = on ? tOn : tOff;
                if (r.chipBg != null) r.chipBg.color = on ? cbOn : cbOff;
            }
            else
            {
                if (_rowAnim.ContainsKey(r) && _rowAnim[r] != null) StopCoroutine(_rowAnim[r]);
                _rowAnim[r] = StartCoroutine(CoRowColor(r, on));
            }
        }

        IEnumerator CoRowColor(RowView r, bool on)
        {
            Color i0 = r.iconImg.color, i1 = on ? r.iconCol : new Color(0.78f, 0.81f, 0.88f, 0.5f);
            Color t0 = r.title.color, t1 = on ? TextMain : new Color(1f, 1f, 1f, 0.5f);
            Color c0 = r.chipBg != null ? r.chipBg.color : Color.clear, c1 = on ? Tint(r.iconCol, 0.17f) : new Color(0.85f, 0.87f, 0.93f, 0.08f);
            float t = 0;
            while (t < 1)
            {
                t = Mathf.Min(1f, t + Time.unscaledDeltaTime / 0.25f);
                float e = EaseOutCubic(t);
                r.iconImg.color = Color.Lerp(i0, i1, e);
                r.title.color = Color.Lerp(t0, t1, e);
                if (r.chipBg != null) r.chipBg.color = Color.Lerp(c0, c1, e);
                yield return null;
            }
            r.iconImg.color = i1; r.title.color = t1;
            if (r.chipBg != null) r.chipBg.color = c1;
        }

        void UpdateCount()
        {
            int n = 0;
            foreach (var r in _rows) if (IsOn(r)) n++;
            if (_countText != null) _countText.text = n + " 已启用";
            if (_dot != null) _dot.color = n > 0 ? GreenDot : new Color(1f, 1f, 1f, 0.35f);
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
        // 展开 / 折叠 / 显示隐藏 / 排布
        // ============================================================
        internal void ToggleRow(RowView r)
        {
            r.open = !r.open;
            if (r.bodyCg != null) r.bodyCg.blocksRaycasts = r.open;
            Coroutine c;
            if (_rowAnim.TryGetValue(r, out c) && c != null) StopCoroutine(c);
            _rowAnim[r] = StartCoroutine(AnimRow(r));
        }

        IEnumerator AnimRow(RowView r)
        {
            float from = r.rect.sizeDelta.y, to = r.open ? r.openH : r.closedH;
            float a0 = r.bodyCg != null ? r.bodyCg.alpha : 0f, a1 = r.open ? 1f : 0f;
            float e0 = r.chev != null ? r.chev.localEulerAngles.z : 0f, e1 = r.open ? 180f : 0f;
            float t = 0;
            while (t < 1)
            {
                t = Mathf.Min(1f, t + Time.unscaledDeltaTime / 0.34f);
                float e = EaseOutCubic(t);
                r.rect.sizeDelta = new Vector2(r.rect.sizeDelta.x, Mathf.Lerp(from, to, e));
                if (r.bodyCg != null) r.bodyCg.alpha = Mathf.Lerp(a0, a1, e);
                if (r.chev != null) r.chev.localEulerAngles = new Vector3(0, 0, Mathf.LerpAngle(e0, e1, e));
                StackRows();
                yield return null;
            }
            r.rect.sizeDelta = new Vector2(r.rect.sizeDelta.x, to);
            if (r.bodyCg != null) r.bodyCg.alpha = a1;
            if (r.chev != null) r.chev.localEulerAngles = new Vector3(0, 0, e1);
            StackRows();
        }

        public void ToggleCollapse()
        {
            _collapsed = !_collapsed;
            float from = _rowsWrap.sizeDelta.y;
            float to = _collapsed ? 0f : FullWrapHeight();
            if (_colCo != null) StopCoroutine(_colCo);
            _colCo = StartCoroutine(CoCollapse(from, to));
        }

        float FullWrapHeight()
        {
            float y = 2f;
            foreach (var r in _rows) y += r.rect.sizeDelta.y + Gap;
            return Mathf.Max(0f, y - Gap + 10f);
        }

        IEnumerator CoCollapse(float from, float to)
        {
            float t = 0;
            while (t < 1)
            {
                t = Mathf.Min(1f, t + Time.unscaledDeltaTime / 0.4f);
                float e = EaseOutCubic(t);
                float h = Mathf.Lerp(from, to, e);
                _rowsWrap.sizeDelta = new Vector2(RowW, h);
                _panel.sizeDelta = new Vector2(RowW + PadSide * 2, HeadH + 4 + h + 12);
                yield return null;
            }
            StackRows();
        }

        void StackRows()
        {
            float y = 2f;
            foreach (var r in _rows)
            {
                r.rect.anchoredPosition = new Vector2(0, -y);
                y += r.rect.sizeDelta.y + Gap;
            }
            _rowsRect.sizeDelta = new Vector2(RowW, Mathf.Max(0f, y - Gap));
            if (!_collapsed)
            {
                float full = Mathf.Max(0f, y - Gap + 10f);
                _rowsWrap.sizeDelta = new Vector2(RowW, full);
                _panel.sizeDelta = new Vector2(RowW + PadSide * 2, HeadH + 4 + full + 12);
            }
        }

        public void SetVisible(bool v, bool animate)
        {
            if (!_built) return;
            if (_visCo != null) { StopCoroutine(_visCo); _visCo = null; }
            _visible = v;
            if (!animate)
            {
                _panelCg.alpha = v ? 1f : 0f;
                _panelCg.blocksRaycasts = v;
                _panelCg.interactable = v;
                _panel.localScale = Vector3.one;
                _panel.gameObject.SetActive(v);
                return;
            }
            if (v) _panel.gameObject.SetActive(true);
            _visCo = StartCoroutine(CoVis(_panelCg.alpha, v ? 1f : 0f, v));
        }

        IEnumerator CoVis(float a0, float a1, bool endOn)
        {
            _panelCg.blocksRaycasts = true;
            float t = 0;
            while (t < 1)
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
            if (!endOn) _panel.gameObject.SetActive(false);
            _visCo = null;
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

        static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static RectTransform RT(Component c) { return (RectTransform)c.transform; }

        static RectTransform Node(Transform parent, string name, float x, float y, float w, float h)
        {
            var rt = NewRect(parent, name);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        static void NodeSet(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

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

        Image Img(Transform parent, string name, Sprite sp, Color col, bool raycast, Image.Type type = Image.Type.Simple)
        {
            var go = new GameObject(name, typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var im = go.GetComponent<Image>();
            im.sprite = sp;
            im.color = col;
            im.raycastTarget = raycast;
            im.type = type;
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
        public float xOff, xOn;
        public bool on;
        public Action<bool> onChanged;
        Coroutine _co;

        public void Set(bool v, bool animate)
        {
            on = v;
            if (_co != null) { StopCoroutine(_co); _co = null; }
            if (!animate)
            {
                knob.anchoredPosition = new Vector2(on ? xOn : xOff, 0f);
                track.color = on ? ModControlCenter.TrackOn : ModControlCenter.TrackOff;
            }
            else _co = StartCoroutine(Co(v));
        }

        IEnumerator Co(bool v)
        {
            Vector2 from = knob.anchoredPosition, to = new Vector2(v ? xOn : xOff, 0f);
            Color c0 = track.color, c1 = v ? ModControlCenter.TrackOn : ModControlCenter.TrackOff;
            float t = 0;
            while (t < 1f)
            {
                t = Mathf.Min(1f, t + Time.unscaledDeltaTime / 0.24f);
                knob.anchoredPosition = Vector2.LerpUnclamped(from, to, EaseOutBack(t));
                track.color = Color.Lerp(c0, c1, Mathf.Min(1f, t * 1.5f));
                yield return null;
            }
            knob.anchoredPosition = to;
            track.color = c1;
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

    class RowHoverFX : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Image bg;
        Coroutine _co;

        public void OnPointerEnter(PointerEventData e) { StartTo(ModControlCenter.RowHoverColor); }
        public void OnPointerExit(PointerEventData e) { StartTo(ModControlCenter.RowColor); }

        void StartTo(Color to)
        {
            if (bg == null) return;
            if (_co != null) StopCoroutine(_co);
            _co = StartCoroutine(Co(bg.color, to));
        }

        IEnumerator Co(Color from, Color to)
        {
            float t = 0;
            while (t < 1f)
            {
                t = Mathf.Min(1f, t + Time.unscaledDeltaTime / 0.18f);
                bg.color = Color.Lerp(from, to, ModControlCenter.EaseOutCubic(t));
                yield return null;
            }
            bg.color = to;
            _co = null;
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
                float lo = -(ch - ph) + 8f, hi = -8f;
                if (lo > hi) { lo = -8f; hi = -8f; }
                np.y = Mathf.Clamp(np.y, lo, hi);
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
        public RectTransform rect, chev, bodyRect;
        public CanvasGroup bodyCg;
        public Image bg, chipBg, iconImg;
        public RowHoverFX hover;
        public Text title, sub;
        public SwitchView sw;
        public readonly List<SubToggleItem> subToggles = new List<SubToggleItem>();
        public readonly List<SubSliderItem> subSliders = new List<SubSliderItem>();
        public float bodyY;
        public float closedH = 60f, openH = 60f;
        public bool open;
    }

    class SubToggleItem { public ConfigEntryBase entry; public SwitchView sw; }
    class SubSliderItem { public ConfigEntryBase entry; public Slider slider; public Text valueText; public bool isInt; public string suffix; }
}
