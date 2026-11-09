using System;
using System.Collections.Generic;

namespace WorldTerrain
{
    /// <summary>
    /// 生成上下文的**分档缓存**（V3.5 起）。
    ///
    /// 背景：V2.x 辛苦把 8192 档的峰值压到 1 GB 内，而「切回上一张图免重建」
    /// 需要把整份 <c>TerrainContext</c>（逐格数组数百 MB）常驻。两者直接冲突。
    ///
    /// 策略（按地图宽度分档）：
    ///   - width ≤ 2048：保留 **2 槽**（A→B→A 免重建）。2048×1024 的上下文约
    ///     100 MB，两份可接受
    ///   - width ≥ 4096：**0 槽**，生成后不保留任何上下文（不缓存历史、不常驻）
    ///
    /// 这样大分辨率切图会重新跑管线（结果逐位一致，只是慢），但内存不会再被
    /// 历史上下文顶上去。缓存只服务于 <c>GenerateFromRecipe</c> 的「命中免重建」；
    /// 「导出当前地图」由 <c>WorldTerrainGenerator</c> 在无缓存时按需重建一份临时
    /// 上下文处理，用完即弃，不进缓存。
    ///
    /// 本类只做**槽位管理**（LRU），不碰 UnityEngine、不碰 IO，可离线单测。
    /// </summary>
    public sealed class MapContextCache
    {
        /// <summary>缓存条目：配方 + 生成上下文 + 地图名。</summary>
        public sealed class Entry
        {
            public MapRecipe recipe;
            public TerrainContext context;
            public string baseName;
        }

        /// <summary>该宽度允许缓存的槽位数。</summary>
        public static int SlotsFor(int width)
        {
            // 吸附到档位再判断，避免调用方传非档位值
            int snapped = MapResolution.Snap(width);
            return snapped <= 2048 ? 2 : 0;
        }

        /// <summary>该宽度是否允许缓存上下文。</summary>
        public static bool IsCacheable(int width) => SlotsFor(width) > 0;

        private readonly List<Entry> _entries = new List<Entry>(2);

        /// <summary>当前缓存条目数（供测试/日志）。</summary>
        public int Count => _entries.Count;

        /// <summary>
        /// 按配方查找命中。命中会把该条目移到**最近使用**端（LRU）。
        /// </summary>
        public Entry Find(MapRecipe recipe)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].recipe.SameAs(recipe))
                {
                    Entry hit = _entries[i];
                    _entries.RemoveAt(i);
                    _entries.Add(hit);          // 移到队尾 = 最近使用
                    return hit;
                }
            }
            return null;
        }

        /// <summary>
        /// 放入一个新上下文。按该宽度的槽位上限淘汰**最久未使用**的条目。
        /// 若该宽度不允许缓存（0 槽），**不放入也不动已有缓存**，返回 false
        /// （生成一张 4096 图不该把之前缓存的两张 2048 图清掉）。
        /// </summary>
        public bool Put(MapRecipe recipe, TerrainContext context, string baseName)
        {
            int width = context != null ? context.Width : recipe.width;
            int slots = SlotsFor(width);
            if (slots <= 0 || context == null)
                return false;

            // 同一配方已存在则先移除（避免重复）
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (_entries[i].recipe.SameAs(recipe))
                    _entries.RemoveAt(i);

            _entries.Add(new Entry { recipe = recipe, context = context, baseName = baseName });

            // 淘汰超出的最久未使用条目（队首）
            while (_entries.Count > slots)
                _entries.RemoveAt(0);

            return true;
        }

        /// <summary>清空所有缓存条目（不再持有任何上下文引用）。</summary>
        public void Clear()
        {
            _entries.Clear();
        }

        /// <summary>
        /// 更新某条目的地图名（缓存命中后沿用新名字时用）。
        /// 找不到时静默返回。
        /// </summary>
        public void Rename(MapRecipe recipe, string newBaseName)
        {
            foreach (Entry e in _entries)
            {
                if (e.recipe.SameAs(recipe))
                {
                    e.baseName = newBaseName;
                    return;
                }
            }
        }
    }
}
