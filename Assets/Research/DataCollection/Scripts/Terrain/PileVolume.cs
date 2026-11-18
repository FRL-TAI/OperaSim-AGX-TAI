using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using AGXUnity.Model;
using AGXUnity.Utils;

namespace PWRISimulator.ROS
{
    /// <summary>
    /// 排土板の前の土の山の体積 V_pile を、地形の高さと動く土（粒子）から求める（T15）。
    /// notes decisions/2026-11-17_土の山の体積の定義と入力の決め方.md
    ///
    ///   V_pile = Σ（範囲内のマス）max(h − h0, 0)·A_cell ＋ M_dyn / (ρ/s)
    ///
    /// 高さは AGX の地形の高さ（Terrain.getHeight。地形の座標）を使う。Unity の TerrainData は1ステップ遅れて
    /// 更新されるので、setup の直後の高さを読むと地形の加工が入っていないことがある。
    /// h0   : シーンを読み込んだ直後（最初のステップ）の地形の高さ。エピソードごとにシーンを読み込み直し、
    ///        setup の ResetHeights も同じ初期の高さに戻すので、エピソード開始時の高さに当たる。
    ///        pile_push の山（loose_layer_rect）は setup で積むので、h0 には入らない（山を積む前の地表）。
    /// h_setup : setup（地形の加工）の直後の高さ。山を含む初期の地形との差を見るための参考。
    /// 範囲 : 刃先の中点を原点、水平の前方 lx・左方 ly。後ろは排土板の上端の水平位置（−backOffset）、
    ///        前は frontLength、横は ±halfWidth。マス（TerrainData の頂点）の中心が範囲に入るものを数える。
    /// M_dyn: 範囲内のマスの上にある粒子の質量（粒子の位置を最寄りのマスに割り当てる）。
    ///        fluid mass は、2.37 の TerrainGridControl に種類を指定してボクセルの質量を読む関数がないため、
    ///        範囲では数えない。地形全体の fluid mass（calculateTotalFluidMass）を上限の目安として別に出す
    ///        （T15 の試しでは 0〜0.9 kg で、粒子の 0.1% 未満）。
    /// ρ/s  : 地形の材料の density / swellFactor（その時点の値）。
    ///
    /// 感度を見るため、前 1.5 m・2.5 m、横に1マス広げた幅、横の畝（範囲の外側1マス）も同時に求める。
    /// getDynamicMass が数える範囲を確かめるため、地形全体の動く土の合計も出す。
    ///
    /// QueryCells を与えると、そのマス（Unity の番号）に割り当てた粒子の上端（中心の高さ ＋ 半径、ワールド y）の
    /// 最大を QueryParticleTop に入れる（粒子がなければ −∞）。前方の高さの確かめ用（T23、BladeStatePublisher の ground_ptop_*）。
    ///
    /// 粒子を含む高さ場から求める幾何学的な山の体積（T24。notes decisions/2026-11-18_地形の経路の観測は粒子を含む高さ場から求める体積にする.md）：
    ///   VisVol     = Σ（V_pile と同じ範囲のマス）max(max(hB, h) − h0, 0)·A_cell
    ///   VisVolWide = 同じ計算を、刃先の後ろ VisWideBack 〜 前 VisWideFront・±VisWideHalf で行う
    ///   hB は AGX の Terrain.getSoilParticleBoundedHeightField()（地形と同じ頂点・同じ基準）、h は地形の高さ、h0 は上と同じ。
    ///   粒子の質量を ρ/s で換算しないので、s にラベルの値を使わない（V_pile と違い、地形の経路の観測に使える）。
    ///   粒子を含む高さ場が取れないか、解像度が地形と違うときは NaN。
    ///
    /// 道すじ全体の体積の出入り（T25。notes decisions/2026-11-18_sの判定は道すじ全体の体積の出入りで行う.md）：
    ///   収支の窓（WindowBack・WindowFront・WindowHalf。bal_vol_* と同じ）で、粒子を含む高さ hB と h0'（シーンを読み込んだ直後の
    ///   同じ粒子を含む高さ場）の差を足す。VisNetPos = Σ max(hB − h0', 0)·A、VisNetNeg = Σ max(h0' − hB, 0)·A。
    ///   VisNetEvery 回ごと（50 Hz で 1 秒ごと）に求め、間は前の値を出す。ただし、エピソードの進行状況が raising の間と、
    ///   進行状況が変わった回は毎回求める（エピソードの終わりの値を必ず新しくするため）。
    /// </summary>
    public class PileVolume
    {
        public float FrontLength = 2.0f;
        public float FrontShort = 1.5f;
        public float FrontLong = 2.5f;
        public float HalfWidth = 1.48f;
        public float NearFront = 0.5f;          // 排土板のすぐ前（−backOffset〜NearFront）の粒子の質量（参考）

        // 範囲ごとの集計
        public enum Bin { Base, Front150, Front250, Wide, Ridge, Count }
        public readonly double[] TerrainVol = new double[(int)Bin.Count];   // Σ max(h − h0, 0)·A [m3]
        public readonly double[] ParticleMass = new double[(int)Bin.Count]; // [kg]
        public double TerrainVolSetup;          // 基本の範囲で Σ max(h − h_setup, 0)·A [m3]
        public double ParticleMassNear;         // −backOffset〜NearFront・±HalfWidth の粒子の質量 [kg]
        public double BackOffset;               // 刃先から排土板の上端までの水平距離 [m]
        public double BulkDensity;              // ρ/s [kg/m3]
        public double TotalParticleMass;        // 地形全体の粒子の質量 [kg]
        public int NumParticles;
        public double TotalFluidMass = double.NaN;   // 地形全体の fluid mass [kg]
        public double TotalDynamicMass = double.NaN; // calculateTotalDynamicMass [kg]
        public bool Valid;

        // 前方の高さの確かめ用（T23）：マスごとの粒子の上端の最大 [ワールド y]
        public Vector2Int[] QueryCells;
        public float[] QueryParticleTop;

        // 粒子を含む高さ場から求める山の体積（T24）[m3]
        public float VisWideBack = 1f, VisWideFront = 3f, VisWideHalf = 2f;
        public double VisVol = double.NaN, VisVolWide = double.NaN;
        double tVis;

        // 道すじ全体の体積の出入り（T25）[m3]
        public int VisNetEvery = 50;
        public double VisNetPos = double.NaN, VisNetNeg = double.NaN;
        float[,] hVis0;                          // [iy, ix] シーンを読み込んだ直後の粒子を含む高さ（AGX の地形の座標）
        string lastPhase;
        double tNet, tNetMax; int tNetCount;

        // 収支の確認用：刃先の後ろ WindowBack 〜 前 WindowFront・±WindowHalf の広い窓（通った道すじ全体を含む）での
        // Σ max(h − h0, 0)·A と Σ min(h − h0, 0)·A
        // （地形全体の質量 calculateTotalMass / calculateTotalSolidMass は1回に約 0.3 s かかり、Unity が止まる間に
        //   深さの制御が乱れたので外した。T15 の記録 12 本で、エピソードの間の変化は 0.0 kg だった）
        public float WindowBack = 16f, WindowFront = 3f, WindowHalf = 3f;
        public double WindowVolPos, WindowVolNeg;     // [m3]（負の方は負の値）
        public int WindowEvery = 25;                  // 窓はこの回数ごとに求め、間は前の値を出す（マスが多いため）
        public int TimingLogInterval = 250;           // この回数ごとに処理時間を Console に出す（0 で出さない）
        int callCount;
        readonly System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
        double tBase, tWindow, tParticle, tMaxTotal; int tCount, tWindowCount;

        readonly DeformableTerrain terrain;
        float[,] h0, hSetup;                    // [iy, ix] の AGX の地形の高さ
        int lastResetCount = -1;
        SimEpisodeManager episodeManager;
        bool managerSearched;
        bool apiResolved, diagLogged;
        MethodInfo miTotalFluid, miPackingRatio;

        public PileVolume(DeformableTerrain terrain) { this.terrain = terrain; }

        public double Volume(Bin b) =>
            TerrainVol[(int)b] + ParticleMass[(int)b] / BulkDensity;

        // AGX のマス番号 (ax, ay) の、AGX の地形の高さ（地形の座標。差だけを使う）
        static float AgxHeight(agxTerrain.Terrain t, int ax, int ay)
        {
            return (float)t.getHeight(new agx.Vec2i(ax, ay));
        }

        float[,] Snapshot()
        {
            int res = terrain.TerrainDataResolution;
            var t = terrain.Native;
            var a = new float[res, res];
            for (int iy = 0; iy < res; iy++)
                for (int ix = 0; ix < res; ix++)
                    a[iy, ix] = AgxHeight(t, res - 1 - ix, res - 1 - iy);
            return a;
        }

        public void Update(Vector3 edgeMid, Vector3 topMid, Vector3 forward)
        {
            Valid = false;
            var t = terrain.Native;
            if (t == null) return;
            if (!apiResolved) ResolveApis(t);

            // h0：最初のステップの高さ。h_setup：setup（ResetHeights と地形の加工）の後の最初のステップの高さ
            if (h0 == null) h0 = Snapshot();
            if (episodeManager == null && !managerSearched)
            {
                managerSearched = true;
                episodeManager = UnityEngine.Object.FindObjectOfType<SimEpisodeManager>();
            }
            int rc = episodeManager != null ? episodeManager.Status.reset_count : 0;
            if (hSetup == null || rc != lastResetCount) { hSetup = Snapshot(); lastResetCount = rc; }

            var bulk = t.getTerrainMaterial().getBulkProperties();
            BulkDensity = bulk.getDensity() / bulk.getSwellFactor();

            Vector3 fwd = SoilSimUtils.Flat(forward);
            Vector3 left = -Vector3.Cross(Vector3.up, fwd);   // Unity は左手系
            BackOffset = System.Math.Max(0.0, -Vector3.Dot(topMid - edgeMid, fwd));

            int res = terrain.TerrainDataResolution;
            var size = terrain.TerrainData.size;
            double dx = size.x / (res - 1), dz = size.z / (res - 1);
            double area = dx * dz;
            float cell = (float)System.Math.Max(dx, dz);
            float wideHalf = HalfWidth + cell;

            Array.Clear(TerrainVol, 0, TerrainVol.Length);
            Array.Clear(ParticleMass, 0, ParticleMass.Length);
            TerrainVolSetup = 0; ParticleMassNear = 0; TotalParticleMass = 0; NumParticles = 0;
            bool query = QueryCells != null && QueryCells.Length > 0;
            if (query)
            {
                if (QueryParticleTop == null || QueryParticleTop.Length != QueryCells.Length) QueryParticleTop = new float[QueryCells.Length];
                for (int q = 0; q < QueryParticleTop.Length; q++) QueryParticleTop[q] = float.NegativeInfinity;
            }

            Vector3 tpos = terrain.Terrain.transform.position;
            // Unity のマス番号 → 刃先を原点とした前方 lx・左方 ly（UnityIndexToWorld と同じ式。Unity の呼び出しを減らすため手で書く）
            void Local(int ix, int iy, out float lx, out float ly)
            {
                var d = new Vector3(tpos.x + size.x * ix / (res - 1) - edgeMid.x, 0f, tpos.z + size.z * iy / (res - 1) - edgeMid.z);
                lx = Vector3.Dot(d, fwd); ly = Vector3.Dot(d, left);
            }

            callCount++;
            sw.Restart();
            // ---- 地形の高さ（いちばん広い長方形のマスを回り、範囲ごとに足す）----
            SoilSimUtils.ForEachCellInRect(terrain, edgeMid, fwd, (float)-BackOffset, FrontLong, -wideHalf, wideHalf, idx =>
            {
                int ix = res - 1 - (int)idx.x, iy = res - 1 - (int)idx.y;
                Local(ix, iy, out float lx, out float ly);
                float h = AgxHeight(t, (int)idx.x, (int)idx.y);
                double dv = System.Math.Max(h - h0[iy, ix], 0f) * area;
                foreach (var b in BinsOf(lx, ly)) TerrainVol[(int)b] += dv;
                if (lx <= FrontLength && System.Math.Abs(ly) <= HalfWidth)
                    TerrainVolSetup += System.Math.Max(h - hSetup[iy, ix], 0f) * area;
            });

            // ---- 粒子を含む高さ場から求める山の体積（T24）----
            double tv0 = sw.Elapsed.TotalMilliseconds;
            VisVol = VisVolWide = double.NaN;
            agxCollide.HeightField hfB = null;
            try { hfB = t.getSoilParticleBoundedHeightField(); } catch (System.Exception) { hfB = null; }
            if (hfB != null && hfB.getResolutionX() == res && hfB.getResolutionY() == res)
            {
                double HVis(int ax, int ay, int ix, int iy) =>
                    System.Math.Max(System.Math.Max(hfB.getHeight((uint)ax, (uint)ay), AgxHeight(t, ax, ay)) - h0[iy, ix], 0.0) * area;
                double vb = 0, vw = 0;
                SoilSimUtils.ForEachCellInRect(terrain, edgeMid, fwd, (float)-BackOffset, FrontLength, -HalfWidth, HalfWidth, idx =>
                {
                    int ix = res - 1 - (int)idx.x, iy = res - 1 - (int)idx.y;
                    vb += HVis((int)idx.x, (int)idx.y, ix, iy);
                });
                SoilSimUtils.ForEachCellInRect(terrain, edgeMid, fwd, -VisWideBack, VisWideFront, -VisWideHalf, VisWideHalf, idx =>
                {
                    int ix = res - 1 - (int)idx.x, iy = res - 1 - (int)idx.y;
                    vw += HVis((int)idx.x, (int)idx.y, ix, iy);
                });
                VisVol = vb; VisVolWide = vw;
            }
            tVis += sw.Elapsed.TotalMilliseconds - tv0;

            double t1 = sw.Elapsed.TotalMilliseconds;

            // ---- 収支の確認用の広い窓（WindowEvery 回ごと）----
            if (WindowEvery <= 1 || callCount % WindowEvery == 1)
            {
                double pos = 0, neg = 0;
                SoilSimUtils.ForEachCellInRect(terrain, edgeMid, fwd, -WindowBack, WindowFront, -WindowHalf, WindowHalf, idx =>
                {
                    int ix = res - 1 - (int)idx.x, iy = res - 1 - (int)idx.y;
                    double dh = AgxHeight(t, (int)idx.x, (int)idx.y) - h0[iy, ix];
                    if (dh > 0) pos += dh * area; else neg += dh * area;
                });
                WindowVolPos = pos; WindowVolNeg = neg;
                tWindowCount++;
            }
            // ---- 道すじ全体の体積の出入り（粒子を含む高さ場、T25）----
            bool hfOk = hfB != null && hfB.getResolutionX() == res && hfB.getResolutionY() == res;
            if (hfOk && hVis0 == null)
            {
                hVis0 = new float[res, res];
                for (int iy = 0; iy < res; iy++)
                    for (int ix = 0; ix < res; ix++)
                        hVis0[iy, ix] = (float)hfB.getHeight((uint)(res - 1 - ix), (uint)(res - 1 - iy));
            }
            string phase = episodeManager != null ? episodeManager.Status.phase : null;
            bool phaseChanged = phase != lastPhase;
            lastPhase = phase;
            if (hfOk && hVis0 != null && (VisNetEvery <= 1 || callCount % VisNetEvery == 1 || phase == "raising" || phaseChanged))
            {
                double tn0 = sw.Elapsed.TotalMilliseconds;
                double pos = 0, neg = 0;
                SoilSimUtils.ForEachCellInRect(terrain, edgeMid, fwd, -WindowBack, WindowFront, -WindowHalf, WindowHalf, idx =>
                {
                    int ix = res - 1 - (int)idx.x, iy = res - 1 - (int)idx.y;
                    double dh = hfB.getHeight((uint)idx.x, (uint)idx.y) - hVis0[iy, ix];
                    if (dh > 0) pos += dh * area; else neg -= dh * area;
                });
                VisNetPos = pos; VisNetNeg = neg;
                double dt = sw.Elapsed.TotalMilliseconds - tn0;
                tNet += dt; tNetMax = System.Math.Max(tNetMax, dt); tNetCount++;
            }
            else if (!hfOk) { VisNetPos = VisNetNeg = double.NaN; }
            double t2 = sw.Elapsed.TotalMilliseconds;

            // ---- 粒子（位置を最寄りのマスに割り当てる）----
            var particles = t.getSoilSimulationInterface()?.getSoilParticles();
            if (particles != null)
            {
                int n = (int)particles.size();
                NumParticles = n;
                for (int i = 0; i < n; i++)
                {
                    var g = particles.at((uint)i);
                    double m = g.getMass();
                    TotalParticleMass += m;
                    Vector3 p = g.position().ToHandedVector3();
                    if (!diagLogged && i == 0) LogParticleDiag(t, g.getRadius(), m);
                    float top = query ? p.y + (float)g.getRadius() : 0f;
                    g.ReturnToPool();
                    int uix = Mathf.RoundToInt((p.x - tpos.x) / size.x * (res - 1));
                    int uiy = Mathf.RoundToInt((p.z - tpos.z) / size.z * (res - 1));
                    if (uix < 0 || uiy < 0 || uix >= res || uiy >= res) continue;
                    if (query)
                        for (int q = 0; q < QueryCells.Length; q++)
                            if (QueryCells[q].x == uix && QueryCells[q].y == uiy && top > QueryParticleTop[q]) QueryParticleTop[q] = top;
                    Local(uix, uiy, out float lx, out float ly);
                    foreach (var b in BinsOf(lx, ly)) ParticleMass[(int)b] += m;
                    if (lx >= -BackOffset && lx <= NearFront && System.Math.Abs(ly) <= HalfWidth) ParticleMassNear += m;
                }
            }

            TotalDynamicMass = t.calculateTotalDynamicMass();
            TotalFluidMass = miTotalFluid != null ? Convert.ToDouble(miTotalFluid.Invoke(t, null)) : double.NaN;
            double t3 = sw.Elapsed.TotalMilliseconds;
            LogTiming(t1, t2 - t1, t3 - t2);
            Valid = true;
        }

        void LogTiming(double b, double w, double p)
        {
            if (TimingLogInterval <= 0) return;
            tBase += b; tWindow += w; tParticle += p; tMaxTotal = System.Math.Max(tMaxTotal, b + w + p); tCount++;
            if (tCount < TimingLogInterval) return;
            Debug.Log($"PileVolume: {tCount} 回の平均 範囲 {tBase / tCount:F3} ms（うち粒子を含む体積 {tVis / tCount:F3} ms）、"
                      + $"道すじの出入り {tNet / System.Math.Max(1, tNetCount):F3} ms/回（{tNetCount} 回、最大 {tNetMax:F3} ms）、"
                      + $"窓 {tWindow / System.Math.Max(1, tWindowCount):F3} ms/回（{tWindowCount} 回）、"
                      + $"粒子 {tParticle / tCount:F3} ms（{NumParticles} 個）、合計の最大 {tMaxTotal:F3} ms（1ステップ {Time.fixedDeltaTime * 1000:F1} ms）");
            tBase = tWindow = tParticle = tMaxTotal = tVis = tNet = tNetMax = 0; tCount = tWindowCount = tNetCount = 0;
        }

        readonly List<Bin> binBuf = new List<Bin>(5);
        List<Bin> BinsOf(float lx, float ly)
        {
            binBuf.Clear();
            float cell = terrain.TerrainData.size.x / (terrain.TerrainDataResolution - 1);
            if (lx < -BackOffset - 1e-4f || lx > FrontLong) return binBuf;
            float ay = System.Math.Abs(ly);
            bool inW = ay <= HalfWidth;
            bool inWide = ay <= HalfWidth + cell;
            if (inW && lx <= FrontLength) binBuf.Add(Bin.Base);
            if (inW && lx <= FrontShort) binBuf.Add(Bin.Front150);
            if (inW) binBuf.Add(Bin.Front250);
            if (inWide && lx <= FrontLength) binBuf.Add(Bin.Wide);
            if (!inW && inWide && lx <= FrontLength) binBuf.Add(Bin.Ridge);
            return binBuf;
        }

        void ResolveApis(agxTerrain.Terrain t)
        {
            apiResolved = true;
            miTotalFluid = t.GetType().GetMethods().FirstOrDefault(m => m.Name == "calculateTotalFluidMass" && m.GetParameters().Length == 0);
            miPackingRatio = t.GetType().GetMethods().FirstOrDefault(m => m.Name == "getTerrainParticlePackingRatio" && m.GetParameters().Length == 0);
            if (miTotalFluid == null)
                Debug.LogWarning("PileVolume: calculateTotalFluidMass が見つかりません");
        }

        void LogParticleDiag(agxTerrain.Terrain t, double r, double m)
        {
            diagLogged = true;
            double rhoParticle = m / (4.0 / 3.0 * System.Math.PI * r * r * r);
            string packing = miPackingRatio != null ? Convert.ToDouble(miPackingRatio.Invoke(t, null)).ToString("F4") : "取得できず";
            Debug.Log($"PileVolume: 粒子 半径 {r:F4} m、質量 {m:F3} kg、材料密度 {rhoParticle:F1} kg/m3、"
                      + $"ρ/s {BulkDensity:F1} kg/m3、比 {BulkDensity / rhoParticle:F4}、packing ratio {packing}");
        }
    }
}
