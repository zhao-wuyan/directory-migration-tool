/*
 * 思考等级像素点阵能量渲染引擎（纯 C# .NET 10 WPF WriteableBitmap 实现）
 *
 * 视觉灵感与三阶段像素火焰模拟算法参考自开源项目：
 *   - dsh-effort-slider (https://github.com/2768651338/dsh-effort-slider) — BSD 3-Clause License
 *     Copyright (c) 2026, dsh-effort-slider contributors
 *   - dsh-ui-web / aurora skin (https://github.com/CAPTAIN1275/dsh-ui-web) — BSD 3-Clause License
 *     Copyright (c) 2026, dsh-web-ui-custom contributors
 *
 * 本文件为面向 .NET 10 WPF 的独立原生 C# 重写，采用预分配缓冲区零 GC 像素渲染，
 * 与原项目无隶属关系（non-affiliation）。
 */

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MigrationCore.Models.Ai;

namespace MoveWithSymlinkWPF.Controls;

/// <summary>
/// 单个思考等级对应的 Fluent 亮色融冰主题配色与能量色温规格。
/// </summary>
public sealed record EffortVisualSpec(
    AiReasoningEffort Effort,
    string ZhLabel,
    string EnLabel,
    string ShortDesc,
    string AccentHex,
    string PillFgHex,
    string PillBorderHex,
    string PillBgHex,
    string PillDotHex,
    float EmberR, float EmberG, float EmberB,
    float MidR, float MidG, float MidB,
    float HotR, float HotG, float HotB);

/// <summary>
/// 纯 CPU 零 GC 像素矩阵能量/火焰渲染引擎：
/// 在 56×6 单元格上运行三阶段模拟（余晖衰减 + 多频火焰/火花模拟 → 可分离 Bloom → Tone-Mapping 栅格化），
/// 单帧耗时 &lt; 0.25ms，直接写入 WPF WriteableBitmap (Bgra32)。
/// </summary>
public sealed class EffortEnergyRenderer
{
    public const int GridCols = 56;
    public const int GridRows = 6;
    public const int CellPixelSize = 8;
    public const int BitmapWidth = GridCols * CellPixelSize;   // 448 px
    public const int BitmapHeight = GridRows * CellPixelSize;  // 48 px

    private const int CellCount = GridCols * GridRows;

    private static readonly IReadOnlyDictionary<AiReasoningEffort, EffortVisualSpec> Specs =
        new Dictionary<AiReasoningEffort, EffortVisualSpec>
        {
            [AiReasoningEffort.Default] = new(
                AiReasoningEffort.Default, "默认", "AUTO", "自适应 · 由模型服务端决定",
                "#0284C7", "#0369A1", "#BAE6FD", "#F0F9FF", "#0EA5E9",
                0.06f, 0.28f, 0.45f, 0.16f, 0.68f, 0.92f, 0.88f, 0.98f, 1.00f),
            [AiReasoningEffort.None] = new(
                AiReasoningEffort.None, "关闭", "OFF", "直出回答 · 不开启思维链",
                "#64748B", "#64748B", "#CBD5E1", "#F8FAFC", "#94A3B8",
                0.16f, 0.16f, 0.22f, 0.34f, 0.36f, 0.46f, 0.72f, 0.75f, 0.85f),
            [AiReasoningEffort.Minimal] = new(
                AiReasoningEffort.Minimal, "极低", "MIN", "极速轻量思考 · 低延迟",
                "#0D9488", "#0F766E", "#99F6E4", "#F0FDFA", "#14B8A6",
                0.05f, 0.26f, 0.32f, 0.14f, 0.72f, 0.68f, 0.85f, 1.00f, 0.96f),
            [AiReasoningEffort.Low] = new(
                AiReasoningEffort.Low, "低", "LOW", "轻量推理 · 日常问答推荐",
                "#0284C7", "#0369A1", "#93C5FD", "#EFF6FF", "#3B82F6",
                0.08f, 0.22f, 0.50f, 0.22f, 0.56f, 0.98f, 0.88f, 0.96f, 1.00f),
            [AiReasoningEffort.Medium] = new(
                AiReasoningEffort.Medium, "中", "MEDIUM", "均衡深度 · 速度与质量平衡",
                "#4F46E5", "#4338CA", "#A5B4FC", "#EEF2FF", "#6366F1",
                0.14f, 0.16f, 0.55f, 0.36f, 0.44f, 1.00f, 0.92f, 0.95f, 1.00f),
            [AiReasoningEffort.High] = new(
                AiReasoningEffort.High, "高", "HIGH", "深度分析 · 适合复杂诊断",
                "#7C3AED", "#6D28D9", "#C4B5FD", "#F5F3FF", "#8B5CF6",
                0.28f, 0.10f, 0.58f, 0.62f, 0.32f, 1.00f, 1.00f, 0.94f, 0.98f),
            [AiReasoningEffort.XHigh] = new(
                AiReasoningEffort.XHigh, "超高", "XHIGH", "高强多步推理 · 复杂规则推演",
                "#C026D3", "#A21CAF", "#F0ABFC", "#FDF4FF", "#D946EF",
                0.46f, 0.08f, 0.48f, 0.88f, 0.28f, 0.92f, 1.00f, 0.92f, 0.98f),
            [AiReasoningEffort.Max] = new(
                AiReasoningEffort.Max, "极限", "MAX", "极限超频推理 · 全算力输出",
                "#DB2777", "#BE185D", "#F9A8D4", "#FDF2F8", "#EC4899",
                0.55f, 0.12f, 0.42f, 0.98f, 0.34f, 0.62f, 1.00f, 0.96f, 0.86f),
        };

    // 预分配缓冲区：每帧 0 GC 分配
    private readonly float[] _prevR = new float[CellCount];
    private readonly float[] _prevG = new float[CellCount];
    private readonly float[] _prevB = new float[CellCount];
    private readonly float[] _curR = new float[CellCount];
    private readonly float[] _curG = new float[CellCount];
    private readonly float[] _curB = new float[CellCount];
    private readonly float[] _blurHR = new float[CellCount];
    private readonly float[] _blurHG = new float[CellCount];
    private readonly float[] _blurHB = new float[CellCount];
    private readonly float[] _blurVR = new float[CellCount];
    private readonly float[] _blurVG = new float[CellCount];
    private readonly float[] _blurVB = new float[CellCount];
    private readonly float[] _hashA = new float[CellCount];
    private readonly float[] _hashB = new float[CellCount];
    private readonly uint[] _pixels = new uint[BitmapWidth * BitmapHeight];

    private float _springPos = 0.5f;
    private float _springVel;
    private double _elapsedSec;

    public WriteableBitmap Bitmap { get; }

    public EffortEnergyRenderer()
    {
        for (int r = 0; r < GridRows; r++)
        {
            for (int c = 0; c < GridCols; c++)
            {
                int i = r * GridCols + c;
                _hashA[i] = Hash2(c, r);
                _hashB[i] = Hash2(c + 99, r + 33);
            }
        }

        Bitmap = new WriteableBitmap(BitmapWidth, BitmapHeight, 96, 96, PixelFormats.Bgra32, null);
    }

    public static EffortVisualSpec GetVisualSpec(AiReasoningEffort effort) =>
        Specs.TryGetValue(effort, out EffortVisualSpec? spec) ? spec : Specs[AiReasoningEffort.Default];

    /// <summary>
    /// 面板重新打开时复位点火计时与余晖，重放从滑块向尾端蔓延的点火波动画。
    /// </summary>
    public void Reignite(double initialSliderRatio)
    {
        _elapsedSec = 0;
        _springPos = (float)Math.Clamp(initialSliderRatio, 0.08, 1.0);
        _springVel = 0f;
        Array.Clear(_prevR);
        Array.Clear(_prevG);
        Array.Clear(_prevB);
    }

    /// <summary>
    /// 推进一帧能量模拟并刷新 WriteableBitmap。
    /// 返回已点亮像素单元格数量（供测试校验）。
    /// </summary>
    public int RenderFrame(double timeSec, double dtSec, double sliderRatio, AiReasoningEffort effort, bool isDragging)
    {
        float dt = (float)Math.Clamp(dtSec, 0.001, 0.05);
        _elapsedSec += dt;

        EffortVisualSpec spec = GetVisualSpec(effort);
        bool isAuto = effort == AiReasoningEffort.Default;
        bool isOff = effort == AiReasoningEffort.None;
        bool isHighHeat = effort is AiReasoningEffort.XHigh or AiReasoningEffort.Max;

        // 火焰前沿弹簧阻尼跟随
        float clampedSlider = (float)Math.Clamp(sliderRatio, 0.0, 1.0);
        float target = isAuto ? 1.0f : (isOff ? 0.08f : 0.12f + clampedSlider * 0.88f);
        if (_springPos < target)
        {
            float force = (target - _springPos) * 11.0f;
            _springVel += force * dt;
            _springVel *= MathF.Max(0f, 1f - 0.58f * dt * 6f);
            _springPos += _springVel * dt;
            if (_springPos > target)
            {
                _springPos = target;
                _springVel = 0f;
            }
        }
        else
        {
            float follow = MathF.Min(1f, dt * 18f);
            _springPos += (target - _springPos) * follow;
            _springVel = 0f;
        }

        float t = (float)timeSec;
        float elapsed = (float)_elapsedSec;
        float uSlider = _springPos;
        float speedMul = isHighHeat ? 1.32f : (isDragging ? 1.15f : 1.0f);
        float boost = isHighHeat ? 1.18f : (isDragging ? 1.12f : 1.0f);

        // =============================================================
        // Pass 1: 56×6 像素细胞能量模拟 + 帧间余晖衰减
        // =============================================================
        for (int r = 0; r < GridRows; r++)
        {
            float uvY = (r + 0.5f) / GridRows;
            float vy = MathF.Abs(uvY - 0.5f) * 2.0f;
            float vf = MathF.Pow(MathF.Max(1.0f - vy * vy * 0.42f, 0.0f), 0.72f);
            int rowOff = r * GridCols;

            for (int c = 0; c < GridCols; c++)
            {
                int idx = rowOff + c;
                float uvX = (c + 0.5f) / GridCols;
                float h1 = _hashA[idx];
                float h2 = _hashB[idx];

                if (isAuto)
                {
                    float dR = _prevR[idx] * 0.86f;
                    float dG = _prevG[idx] * 0.86f;
                    float dB = _prevB[idx] * 0.86f;

                    float scanX = MathF.Sin(t * 1.35f) * 0.42f + 0.5f;
                    float dxScan = (uvX - scanX) * 5.2f;
                    float wave = MathF.Exp(-(dxScan * dxScan));
                    float shimmer = 0.5f + 0.5f * MathF.Sin(uvX * 14.0f - t * 3.2f + h1 * 6.283185f);
                    float sparkAuto = h2 > 0.86f ? (0.5f + 0.5f * MathF.Sin(t * 7.0f + h1 * 18.0f)) * 0.35f : 0f;
                    float energyAuto = (0.09f + wave * 0.48f + shimmer * 0.12f + sparkAuto) * vf;
                    float mixT = Clamp01(wave * 0.85f + 0.15f);
                    float hotW = wave * wave * wave * 0.32f;

                    float cR = Lerp(spec.EmberR, spec.MidR, mixT) * energyAuto + spec.HotR * hotW;
                    float cG = Lerp(spec.EmberG, spec.MidG, mixT) * energyAuto + spec.HotG * hotW;
                    float cB = Lerp(spec.EmberB, spec.MidB, mixT) * energyAuto + spec.HotB * hotW;

                    _curR[idx] = MathF.Min(dR + cR * 0.35f, 1.3f);
                    _curG[idx] = MathF.Min(dG + cG * 0.35f, 1.3f);
                    _curB[idx] = MathF.Min(dB + cB * 0.35f, 1.3f);
                    continue;
                }

                if (isOff)
                {
                    float breathe = 0.05f + 0.03f * MathF.Sin(t * 1.8f + h1 * 6.283185f);
                    float nearLeft = Smoothstep(0.28f, 0.0f, uvX);
                    float baseVal = breathe * nearLeft * vf;
                    _curR[idx] = _prevR[idx] * 0.82f + spec.MidR * baseVal;
                    _curG[idx] = _prevG[idx] * 0.82f + spec.MidG * baseVal;
                    _curB[idx] = _prevB[idx] * 0.82f + spec.MidB * baseVal;
                    continue;
                }

                float fadeMask = Smoothstep(0.0f, 0.24f, uvX);
                float decayScale = 0.88f * (0.35f + 0.65f * fadeMask);
                float decayR = _prevR[idx] * decayScale;
                float decayG = _prevG[idx] * decayScale;
                float decayB = _prevB[idx] * decayScale;

                float intensity = Smoothstep(0.0f, 0.18f, uSlider) * Lerp(0.16f, 1.0f, MathF.Pow(uSlider, 0.55f));
                float cellDelay = h1 * 0.75f;
                float cellAge = MathF.Max(elapsed - cellDelay, 0.0f);
                float ignited = cellAge > 0.001f ? 1.0f : 0.0f;
                float cellSpd = 0.85f + h1 * 0.32f;
                float normAge = 1.0f - Clamp01(cellAge / 1.8f);
                float eased = 1.0f - normAge * normAge * normAge;
                float dist = eased * uSlider * cellSpd * ignited;
                float cellOff = (h1 - 0.5f) * 0.05f;
                float front = MathF.Max(uSlider - dist - cellOff, 0.01f);
                float tail = MathF.Max(uSlider - front, 0.001f);

                float inZ = (uvX >= front - 0.004f && uvX <= uSlider + 0.006f) ? 1.0f : 0.0f;
                float dn = Clamp01(MathF.Max(uSlider - uvX, 0.0f) / tail);
                float bright = MathF.Pow(1.0f - dn, 0.62f);
                bright = MathF.Max(bright, 0.06f * ignited) * inZ;
                bright *= 1.0f - Smoothstep(0.92f, 1.05f, dn);

                float es = Lerp(0.22f, 0.56f, MathF.Min(elapsed / 0.8f, 1.0f));
                float ts = Lerp(0.85f, 1.0f, MathF.Min(elapsed / 1.2f, 1.0f)) * speedMul;

                float f1 = MathF.Sin(uvX * 28.0f + t * 14.0f * ts + h1 * 6.283185f);
                float f2 = MathF.Sin(uvX * 16.0f + t * 7.5f * ts + h1 * 3.141593f);
                float f3 = MathF.Sin(uvX * 48.0f + t * 23.0f * ts + h1 * 10.0f);
                float flame = Smoothstep(0.08f, 0.92f, (f1 + f2 * 0.5f + f3 * 0.25f) * 0.35f + 0.5f);

                float r1 = MathF.Sin(dn * 15.0f - t * 5.0f * ts + h1 * 3.0f);
                float r2 = MathF.Sin(dn * 7.5f - t * 2.5f * ts + h1 * 5.0f);
                float rhythm = Smoothstep(-0.15f, 0.55f, r1) * (r2 * 0.5f + 0.5f);
                rhythm = MathF.Pow(MathF.Max(rhythm, 0.0f), 1.2f);

                float avgSpd = dist / MathF.Max(cellAge, 0.001f);
                float age = MathF.Max(cellAge - MathF.Max(uSlider - uvX, 0.0f) / MathF.Max(avgSpd, 0.001f), 0.0f);
                float flash = MathF.Exp(-age * 3.4f);

                float spRaw = t * (0.42f + h1 * 0.18f) * speedMul + h1 * 7.0f;
                float sp = spRaw - MathF.Floor(spRaw);
                float sX = uSlider - sp * tail;
                float sY = 0.5f + MathF.Sin(sp * 11.0f + h1 * 6.283185f) * 0.32f;
                float spark = Smoothstep(0.032f, 0.0f, MathF.Abs(uvX - sX))
                            * Smoothstep(0.24f, 0.0f, MathF.Abs(uvY - sY))
                            * (1.0f - sp) * (1.0f - sp) * es;

                float twinkle = MathF.Sin(t * (3.2f + h2 * 5.0f) * speedMul + h1 * 24.0f);
                float starCell = (inZ > 0f && h2 > 0.78f && twinkle > 0.72f)
                    ? MathF.Pow((twinkle - 0.72f) / 0.28f, 1.8f) * (0.35f + 0.65f * (1.0f - dn * 0.6f)) * intensity
                    : 0.0f;

                float energy = (bright * vf * (flame * 0.44f + rhythm * 0.38f)
                             + flash * bright * vf * 0.48f
                             + spark * 0.85f * inZ) * es * intensity * boost;

                float dxFront = (uvX - front) * 16.0f;
                float edgeBase = MathF.Exp(-(dxFront * dxFront));
                float ef1 = MathF.Sin(uvX * 42.0f + t * 18.0f * ts + h1 * 6.283185f) * 0.5f + 0.5f;
                float ef2 = MathF.Sin(uvX * 26.0f + t * 10.0f * ts + h1 * 3.141593f) * 0.5f + 0.5f;
                float edge = edgeBase * (0.25f + ef1 * ef2 * 1.4f) * 1.35f * intensity * es;

                float leadD = front - uvX;
                float leadZone = leadD >= 0f ? Smoothstep(0.08f, 0.0f, leadD) * vf : 0f;
                float leadSpark = (leadZone > 0f && h2 > 0.6f)
                    ? leadZone * (MathF.Sin(leadD * 90.0f + t * 18.0f * ts + h2 * 6.283185f) * 0.5f + 0.5f) * intensity * es * 0.55f
                    : 0f;

                float total = energy + edge + leadSpark;
                float temp = 1.0f - dn;
                float hotMix = MathF.Pow(temp, 3.8f);

                float colR = Lerp(Lerp(spec.EmberR, spec.MidR, temp), spec.HotR, hotMix) * total + spec.HotR * starCell * 0.65f;
                float colG = Lerp(Lerp(spec.EmberG, spec.MidG, temp), spec.HotG, hotMix) * total + spec.HotG * starCell * 0.65f;
                float colB = Lerp(Lerp(spec.EmberB, spec.MidB, temp), spec.HotB, hotMix) * total + spec.HotB * starCell * 0.65f;

                float pulse = MathF.Sin(t * 3.0f * speedMul) * 0.14f + 1.0f;
                float dxCore = (uvX - uSlider) * 14.0f;
                float dxAura = (uvX - uSlider) * 3.6f;
                float core = MathF.Exp(-(dxCore * dxCore)) * 1.55f * pulse;
                float aura = MathF.Exp(-(dxAura * dxAura)) * 0.16f;
                float coreScale = intensity * es * boost;

                colR += (spec.HotR * core + spec.MidR * aura) * coreScale;
                colG += (spec.HotG * core + spec.MidG * aura) * coreScale;
                colB += (spec.HotB * core + spec.MidB * aura) * coreScale;

                float leftSoft = 0.25f + 0.75f * fadeMask;
                _curR[idx] = MathF.Min(decayR + colR * leftSoft, 1.55f);
                _curG[idx] = MathF.Min(decayG + colG * leftSoft, 1.55f);
                _curB[idx] = MathF.Min(decayB + colB * leftSoft, 1.55f);
            }
        }

        // =============================================================
        // Pass 2: 可分离高斯 Bloom（横向 + 纵向）
        // =============================================================
        for (int r = 0; r < GridRows; r++)
        {
            int rowOff = r * GridCols;
            for (int c = 0; c < GridCols; c++)
            {
                float sr = 0f, sg = 0f, sb = 0f;
                AccumulateBloomH(rowOff, c, 0.34f, ref sr, ref sg, ref sb);
                AccumulateBloomH(rowOff, c - 1, 0.24f, ref sr, ref sg, ref sb);
                AccumulateBloomH(rowOff, c + 1, 0.24f, ref sr, ref sg, ref sb);
                AccumulateBloomH(rowOff, c - 2, 0.09f, ref sr, ref sg, ref sb);
                AccumulateBloomH(rowOff, c + 2, 0.09f, ref sr, ref sg, ref sb);

                int i = rowOff + c;
                _blurHR[i] = sr;
                _blurHG[i] = sg;
                _blurHB[i] = sb;
            }
        }

        for (int r = 0; r < GridRows; r++)
        {
            int rUp = Math.Max(0, r - 1) * GridCols;
            int rMid = r * GridCols;
            int rDn = Math.Min(GridRows - 1, r + 1) * GridCols;

            for (int c = 0; c < GridCols; c++)
            {
                int i = rMid + c;
                _blurVR[i] = _blurHR[i] * 0.42f + (_blurHR[rUp + c] + _blurHR[rDn + c]) * 0.29f;
                _blurVG[i] = _blurHG[i] * 0.42f + (_blurHG[rUp + c] + _blurHG[rDn + c]) * 0.29f;
                _blurVB[i] = _blurHB[i] * 0.42f + (_blurHB[rUp + c] + _blurHB[rDn + c]) * 0.29f;
            }
        }

        // 保存当前帧供下一帧衰减
        Array.Copy(_curR, _prevR, CellCount);
        Array.Copy(_curG, _prevG, CellCount);
        Array.Copy(_curB, _prevB, CellCount);

        // =============================================================
        // Pass 3: Tone-Mapping 合成并栅格化为带微圆角间隙的 Bgra32 像素
        // =============================================================
        Array.Clear(_pixels);
        const uint dormantCellColor = 0x10_94A3B8u; // ARGB -> 极淡冷灰未点亮底格
        float clipPos = isAuto ? 1.05f : clampedSlider;
        int litCells = 0;

        for (int r = 0; r < GridRows; r++)
        {
            int pyBase = r * CellPixelSize;
            int rowOff = r * GridCols;
            for (int c = 0; c < GridCols; c++)
            {
                float uvX = (c + 0.5f) / GridCols;
                float mask = isAuto ? 1.0f : (1.0f - Smoothstep(clipPos - 0.018f, clipPos + 0.024f, uvX));

                uint packedColor = dormantCellColor;
                if (mask > 0.005f)
                {
                    int i = rowOff + c;
                    float sR = _curR[i], sG = _curG[i], sB = _curB[i];
                    float gR = _blurVR[i], gG = _blurVG[i], gB = _blurVB[i];

                    float outR = (1.0f - MathF.Exp(-(sR + gR * 1.15f + sR * gR * 0.35f) * 1.25f)) * mask;
                    float outG = (1.0f - MathF.Exp(-(sG + gG * 1.15f + sG * gG * 0.35f) * 1.25f)) * mask;
                    float outB = (1.0f - MathF.Exp(-(sB + gB * 1.15f + sB * gB * 0.35f) * 1.25f)) * mask;

                    float lum = outR * 0.299f + outG * 0.587f + outB * 0.114f;
                    if (lum >= 0.02f)
                    {
                        byte red = (byte)Math.Clamp((int)(outR * 255f + 0.5f), 0, 255);
                        byte green = (byte)Math.Clamp((int)(outG * 255f + 0.5f), 0, 255);
                        byte blue = (byte)Math.Clamp((int)(outB * 255f + 0.5f), 0, 255);
                        packedColor = (0xFFu << 24) | ((uint)red << 16) | ((uint)green << 8) | blue;
                        litCells++;
                    }
                }

                // 在 8×8 单元内绘制 6×6 圆角方格（留出四周 1px 暗缝，并切去四角 1px 形成微圆角）
                int pxBase = c * CellPixelSize;
                for (int dy = 1; dy < CellPixelSize - 1; dy++)
                {
                    int dstRow = (pyBase + dy) * BitmapWidth + pxBase;
                    bool isEdgeY = dy == 1 || dy == CellPixelSize - 2;
                    for (int dx = 1; dx < CellPixelSize - 1; dx++)
                    {
                        if (isEdgeY && (dx == 1 || dx == CellPixelSize - 2))
                        {
                            // 四角保留半透明抗锯齿，让每个像素格圆润精致
                            uint cornerAlpha = ((packedColor >> 24) * 140u) >> 8;
                            _pixels[dstRow + dx] = (cornerAlpha << 24) | (packedColor & 0x00FFFFFFu);
                        }
                        else
                        {
                            _pixels[dstRow + dx] = packedColor;
                        }
                    }
                }
            }
        }

        Bitmap.WritePixels(
            new Int32Rect(0, 0, BitmapWidth, BitmapHeight),
            _pixels,
            BitmapWidth * 4,
            0);

        return litCells;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AccumulateBloomH(int rowOff, int col, float weight, ref float sr, ref float sg, ref float sb)
    {
        int clamped = col < 0 ? 0 : (col >= GridCols ? GridCols - 1 : col);
        int k = rowOff + clamped;
        float r = _curR[k], g = _curG[k], b = _curB[k];
        if (r * 0.2126f + g * 0.7152f + b * 0.0722f >= 0.24f)
        {
            sr += r * weight;
            sg += g * weight;
            sb += b * weight;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Hash2(int x, int y)
    {
        double s = Math.Sin(x * 127.1 + y * 311.7) * 43758.5453123;
        return (float)(s - Math.Floor(s));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Smoothstep(float e0, float e1, float x)
    {
        float t = Clamp01((x - e0) / (e1 - e0));
        return t * t * (3f - 2f * t);
    }
}
