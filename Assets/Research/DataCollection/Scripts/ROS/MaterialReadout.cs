using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using AGXUnity;
using AGXUnity.Collide;
using AGXUnity.Model;

namespace PWRISimulator.ROS
{
    /// <summary>
    /// AGX 本体の材料・接触材料・土の設定を読み出して Console に出す（読むだけ。物理は変えない）。
    /// フェーズ1 の T1（δ・固定のパラメータ・粒子の密度・締固めの上限）のための記録。
    ///
    /// 出力の各行は "[MaterialReadout]" で始まる。Editor.log からまとめて取り出せる。
    ///   LogSetup     : SimEpisodeManager の setup の直後に1回。土の設定・材料・接触材料
    ///   LogParticles : phase が "raising" になったとき（排土板の前に粒子が残っている時点）。粒子の質量と半径
    ///
    /// AGX のバージョンで関数の名前や引数が違ってもコンパイルエラーにならないよう、
    /// 存在が確かでない関数はリフレクションで呼ぶ（CompactionProbe と同じ方針）。
    /// 呼べなかったものは "n/a（理由）" と出す。
    /// </summary>
    public static class MaterialReadout
    {
        const string Tag = "[MaterialReadout]";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void LogSetup(DeformableTerrain terrain, DeformableTerrainShovel shovel, string episodeName, int requestId)
        {
            var sb = new StringBuilder();
            void Line(string s) => sb.Append(Tag).Append(' ').Append(s).Append('\n');
            Line($"==== setup episode={episodeName} request_id={requestId} ====");
            try
            {
                var native = terrain.Native;
                var tmat = native.getTerrainMaterial();
                var b0 = tmat.getBulkProperties();
                Line($"maxDensity={F(b0.getMaximumDensity())} density={F(b0.getDensity())} swell={F(b0.getSwellFactor())} " +
                     $"maxDensity/density={F(b0.getMaximumDensity() / b0.getDensity())} 1/swell={F(1.0 / b0.getSwellFactor())}");
                Dump(Line, "bulk", b0);
                Dump(Line, "compaction", tmat.getCompactionProperties());
                Dump(Line, "particle", tmat.getParticleProperties());
                Dump(Line, "excavation_contact", tmat.getExcavationContactProperties());
                Dump(Line, "terrain_properties", TryCall(native, "getProperties"));

                // 材料（agx.Material）
                var terrainMat = GetTerrainShapeMaterial(native, "TERRAIN");
                var particleMat = GetTerrainShapeMaterial(native, "PARTICLE");
                Line($"material.terrain = {MatName(terrainMat)}");
                Line($"material.particle = {MatName(particleMat)}");

                // 排土板（ショベルの剛体）の形状の材料
                var bladeMats = new List<agx.Material>();
                if (shovel != null && shovel.RigidBody != null)
                {
                    foreach (var shape in shovel.RigidBody.GetComponentsInChildren<Shape>())
                    {
                        var g = shape.NativeGeometry;
                        var m = g?.getMaterial();
                        Line($"blade.shape {shape.name}: asset={(shape.Material != null ? shape.Material.name : "null")} native={MatName(m)} enabled={g?.getEnableCollisions()}");
                        if (m != null && !bladeMats.Any(b => b.getName() == m.getName())) bladeMats.Add(m);
                    }
                }

                // 材料の組み合わせの接触材料（明示されたもの。なければ暗黙のものを計算して出す）
                var mm = Simulation.Instance.Native.getMaterialManager();
                foreach (var bm in bladeMats)
                {
                    DumpPair(Line, mm, "blade-terrain", bm, terrainMat);
                    DumpPair(Line, mm, "blade-particle", bm, particleMat);
                }
                DumpPair(Line, mm, "particle-terrain", particleMat, terrainMat);
                DumpPair(Line, mm, "particle-particle", particleMat, particleMat);

                // 地形が内部で使う接触材料
                if (shovel != null && shovel.Native != null)
                {
                    Dump(Line, "terrain.getShovelAggregateContactMaterial", TryCall(native, "getShovelAggregateContactMaterial", shovel.Native));
                    var tc = TryCall(native, "getToolCollection", shovel.Native);
                    var dc = TryCall(tc, "getDeformController");
                    Dump(Line, "deformController.getAggregateTerrainContactMaterial", TryCall(dc, "getAggregateTerrainContactMaterial"));
                    Dump(Line, "deformController.getAggregateShovelContactMaterial", TryCall(dc, "getAggregateShovelContactMaterial"));
                    foreach (var bm in bladeMats)
                        Dump(Line, $"terrain.getContactMaterial({bm.getName()})", TryCall(native, "getContactMaterial", bm));
                    Dump(Line, "shovel.excavationSettings(PRIMARY)", TryCall(shovel.Native, "getExcavationSettings", EnumValue(shovel.Native, "getExcavationSettings", "PRIMARY")));
                    Dump(Line, "shovel", shovel.Native);
                }

                // シミュレーションに登録されているすべての接触材料
                var vec = TryCall(mm, "getContactMaterialVector");
                int k = 0;
                foreach (var cm in Enumerate(vec))
                    Dump(Line, $"registered[{k++}]", cm);
                Line($"registered contact materials: {k}");
            }
            catch (System.Exception e)
            {
                Line($"error: {e.GetType().Name}: {e.Message}");
            }
            Debug.Log(sb.ToString());
        }

        public static void LogParticles(DeformableTerrain terrain, string episodeName)
        {
            var sb = new StringBuilder();
            void Line(string s) => sb.Append(Tag).Append(' ').Append(s).Append('\n');
            Line($"==== particles episode={episodeName} ====");
            try
            {
                var bulk = terrain.Native.getTerrainMaterial().getBulkProperties();
                double rho = bulk.getDensity(), s = bulk.getSwellFactor();
                var pp = terrain.Native.getTerrainMaterial().getParticleProperties();
                Line($"bulk.density={F(rho)} swell={F(s)} rho/s={F(rho / s)} particle.getParticleDensity={F(pp.getParticleDensity())}");

                var particles = terrain.GetParticles();
                int n = particles == null ? 0 : (int)particles.size();
                double sumM = 0, sumV = 0;
                var dens = new List<double>();
                for (int i = 0; i < n; i++)
                {
                    var p = particles.at((uint)i);
                    double r = p.getRadius();
                    double m = Convert.ToDouble(TryCall(p, "getMass") ?? double.NaN);
                    double v = 4.0 / 3.0 * Math.PI * r * r * r;
                    sumM += m; sumV += v;
                    if (v > 0) dens.Add(m / v);
                }
                Line($"n={n} sum_mass={F(sumM)} sum_sphere_volume={F(sumV)} mass/volume={F(sumV > 0 ? sumM / sumV : double.NaN)}");
                if (dens.Count > 0)
                {
                    dens.Sort();
                    Line($"per-particle m/V: min={F(dens[0])} median={F(dens[dens.Count / 2])} max={F(dens[dens.Count - 1])}");
                }
                Line($"dynamic_mass={F(Convert.ToDouble(TryCall(terrain.Native, "getDynamicMass") ?? double.NaN))}");
            }
            catch (System.Exception e)
            {
                Line($"error: {e.GetType().Name}: {e.Message}");
            }
            Debug.Log(sb.ToString());
        }

        // ---- 以下、補助 ----

        static agx.Material GetTerrainShapeMaterial(agxTerrain.Terrain native, string typeName)
        {
            object type;
            try { type = Enum.Parse(typeof(agxTerrain.Terrain.MaterialType), typeName); }
            catch { return null; }
            return TryCall(native, "getMaterial", type) as agx.Material;
        }

        static void DumpPair(Action<string> line, agxSDK.MaterialManager mm, string label, agx.Material a, agx.Material b)
        {
            if (a == null || b == null) { line($"{label}: material null"); return; }
            string pair = $"{label} ({a.getName()} x {b.getName()})";
            var explicitCm = mm.getContactMaterial(a, b);
            if (explicitCm != null) { Dump(line, pair + " explicit", explicitCm); return; }
            // 登録せずに、暗黙の接触材料（2つの材料から作られるもの）を計算する
            Dump(line, pair + " implicit", TryCall(mm, "calculateContactMaterial", a, b));
        }

        static string MatName(agx.Material m) => m == null ? "null" : m.getName();

        static string F(double v) => v.ToString("G6", Inv);

        /// <summary>引数なしの get*/is* で、数値・真偽・文字列・列挙を返す関数をすべて呼んで1行に出す</summary>
        static void Dump(Action<string> line, string label, object obj)
        {
            if (obj == null) { line($"{label}: n/a（null または関数なし）"); return; }
            var parts = new List<string>();
            foreach (var mi in obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).OrderBy(m => m.Name))
            {
                if (mi.GetParameters().Length != 0) continue;
                if (!(mi.Name.StartsWith("get") || mi.Name.StartsWith("is"))) continue;
                if (mi.Name == "GetType" || mi.Name == "GetHashCode") continue;
                var rt = mi.ReturnType;
                bool simple = rt.IsPrimitive || rt == typeof(string) || rt.IsEnum;
                bool material = typeof(agx.Material).IsAssignableFrom(rt);
                bool frictionModel = rt.Name.Contains("FrictionModel");
                if (!simple && !material && !frictionModel) continue;
                try
                {
                    var v = mi.Invoke(obj, null);
                    string s = v == null ? "null"
                        : material ? ((agx.Material)v).getName()
                        : frictionModel ? v.GetType().Name
                        : v is double d ? F(d)
                        : v is float f ? F(f)
                        : v.ToString();
                    parts.Add($"{mi.Name}={s}");
                }
                catch (System.Exception e)
                {
                    parts.Add($"{mi.Name}=err({(e.InnerException ?? e).GetType().Name})");
                }
            }
            line($"{label} [{obj.GetType().Name}]: {string.Join(" ", parts)}");
        }

        /// <summary>名前と引数の数が合う公開の関数を探して呼ぶ。なければ null</summary>
        static object TryCall(object obj, string name, params object[] args)
        {
            if (obj == null) return null;
            foreach (var mi in obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (mi.Name != name) continue;
                var ps = mi.GetParameters();
                if (ps.Length != args.Length) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length && ok; i++)
                    ok = args[i] == null ? !ps[i].ParameterType.IsValueType : ps[i].ParameterType.IsInstanceOfType(args[i]);
                if (!ok) continue;
                try { return mi.Invoke(obj, args); }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"{Tag} {name} の呼び出しに失敗: {(e.InnerException ?? e).Message}");
                    return null;
                }
            }
            return null;
        }

        /// <summary>関数 methodName の1つ目の引数が列挙なら、その値 valueName を返す</summary>
        static object EnumValue(object obj, string methodName, string valueName)
        {
            if (obj == null) return null;
            foreach (var mi in obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (mi.Name != methodName) continue;
                var ps = mi.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsEnum)
                {
                    try { return Enum.Parse(ps[0].ParameterType, valueName); } catch { }
                }
            }
            return null;
        }

        /// <summary>SWIG の配列（size()/at(i) または Count/インデクサ、IEnumerable）を列挙する</summary>
        static IEnumerable<object> Enumerate(object vec)
        {
            if (vec == null) yield break;
            if (vec is System.Collections.IEnumerable en)
            {
                foreach (var x in en) yield return x;
                yield break;
            }
            var size = TryCall(vec, "size");
            if (size == null) yield break;
            long n = Convert.ToInt64(size);
            for (long i = 0; i < n; i++)
            {
                var x = TryCall(vec, "at", (uint)i) ?? TryCall(vec, "at", (ulong)i) ?? TryCall(vec, "at", (int)i);
                if (x != null) yield return x;
            }
        }
    }
}
