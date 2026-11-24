# 文字与 SVG 栈说明

本文档说明 Ez2Lazer / osu!framework 里多套「文字 / 矢量 / 字体」原生栈各自管什么、Linux 上 `#51` 为何崩溃、以及如何在不删减资源能力的前提下收敛架构。与 **#52**（glyph 纹理池 / 批绘）、规划中的 **#53**（更大 Skia / 文本重构）衔接。

**性能优先级约定（Ez 侧讨论共识）**：帧延迟 > 内存；对局内热路径应只做 cache 读取与 GPU batch，冷路径（加载、换肤）可承担更多开销。

---

## 1. 两个都叫「SVG」的东西

| | **文件 SVG**（纹理资源） | **OT-SVG**（OpenType 表） |
|---|---------------------------|---------------------------|
| **是什么** | `.svg` 资源：`path`、渐变、`<text>` 等 | TTF/OTF **字形**内嵌的一小段 SVG（彩色 emoji 等） |
| **典型路径** | `TextureLoaderStore` → `SvgImageDecoder`（Svg.Skia） | `OutlineFont` → FreeType → **plutosvgft** 钩子 |
| **输出** | `RgbaImage` / 纹理上传 | 单字形位图 → glyph 纹理 |
| **与 Skia 的关系** | Skia 负责**整张图**栅格化 | 通常**不**走「读 .svg 文件」那条线；由 FreeType 调 PlutoSVG 画字体表里的迷你 SVG |

记忆：`ln-azimuth.svg` 里的 ψ 属于**左列**；`NotoColorEmoji-Regular.ttf` 里某 codepoint 属于**右列**。名字都带 SVG，层级完全不同。

---

## 2. HarfBuzz 是什么、出现在哪

**HarfBuzz** 做 **text shaping（整形）**：把 Unicode 字符串 + 字体变成「用哪些 glyph、advance 多少、怎么摆」，**不**负责把像素画出来。

在**本项目**里，它主要体现在：

- **Svg.Skia 解码带 `<text>` 的纹理 SVG** 时（选字体、复杂脚本、OpenType 特性等）。
- **不**是游戏 UI `SpriteText` / `OutlineFont` 的主整形路径（那条以 FreeType 为主）。

```

Unicode 文本 → HarfBuzz（字形序列 + 位置） → Skia / FreeType（栅格化成位图）
```

`SKSvgSettings.EnableSvgFonts` 在 Svg.Skia **5.2.4** 中几乎未参与渲染路径，不能当作关闭 HarfBuzz 的开关（见 `b42b69428` 专题修复）。

---

## 3. 当前后端一览（按职责，不按库名）

| 车道 | 组件 | 何时运行 | 延迟敏感? |
|------|------|----------|-----------|
| **A. 资源成像** | Skia（PNG/WebP/…）、Svg.Skia、可选 **SvgWorker** | 加载纹理、剪贴板等 | 冷路径；应对局内接近 0 次 |
| **B. UI 字体** | FreeType、`OutlineFont`、GlyphStore / FontStore | 首次见字形时栅格，之后 cache | 热路径 = cache hit；#52 改 GPU 侧 |
| **C. OT-SVG emoji** | plutosvgft（懒注册） | 打开 OT-SVG 字体、栅格彩色 emoji 字形 | 冷路径、面积极窄 |
| **D. 绘制** | OpenGL / Veldrid、`SpriteText`、#52 glyph array | 每帧 | **最热** |

混乱的根源：**A 的 HarfBuzz 与 B/C 的 FreeType 常在同一 Linux 进程、同一 drawable 宿主里同时活着**，而不是「库太老」。

---

## 4. Linux `free(): invalid pointer`（#51）

### 现象

- 最小复现：`TestSceneResourceStores.TestGetTextureStore`（OpenGL 可视化宿主、枚举并加载含 `ln-azimuth` 的纹理）。
- 带 `<text>` 的 SVG（如 ψ）可触发；纯几何 SVG、DummyRenderer、无宿主单测解码通常不触发。
- **非** TIFF 独有问题；**非**简单「Linux 不能跑测试」。

### 根因（结论）

在**已初始化游戏字体子系统（FreeType / 可选 plutosvgft）**的进程里，**Svg.Skia 经 HarfBuzz 绘制 SVG `<text>`**，与 FreeType 生态在同一堆上交错分配/释放 → 原生堆损坏。macOS/头less 路径往往不叠满这条链。

### 本 PR 修复策略（非删字、非永久关字体）

1. **Linux + 文档含 `<text>`**：`dotnet exec osu.Framework.SvgWorker.dll` 子进程栅格化，与宿主字体栈 **进程隔离**；宿主侧修复 `RgbaImage` 在 `finally` 中被误 dispose 的问题。
2. **进程内回退**：`DefaultTypefaceProvider` + 自定义 `ISvgTextGlyphRunResolver`（Skia `SKFont` 整形，绕过 HarfBuzz）；`SvgImageDecodeScheduler` 序列化同进程解码。
3. **`OutlineFont`**：**延后** `plutosvgft` 注册（仅在 OT-SVG 面相 / `FT_Err_Missing_SVG_Hooks` 时），避免无谓改动共享 `FT_Library`。
4. **资源**：`ln-azimuth.svg` 保留 `<text>ψ</text>` 作为回归用例。

### 部署

- 测试工程构建后会复制 `osu.Framework.SvgWorker.dll`（及 deps/runtimeconfig）到输出目录。
- **Linux 正式宿主**须在 `AppContext.BaseDirectory` 能解析到该 worker（后续可像 `plutosvgft` 一样收进 NuGet / MSBuild targets；当前以 Tests 目标为主）。

---

## 5. 能否「简化统一」？（延迟优先）

目标不是「全站只剩 Skia」一句口号，而是：

- **冷路径**：允许存在 2 种栅格实现（FreeType vs 未来 Skia），但 **不共享会崩的进程内原生态**。
- **热路径**：统一为 **glyph/纹理 cache → #52 array → batch draw**，不在帧内做整形/栅格。

### 建议收敛三层

| 层 | 统一方向 |
|----|----------|
| **A 纹理 SVG** | 视为成像管线，永远不是 FontStore；Linux 带字 SVG 默认 worker 或 Skia-only shape，**不**让 HarfBuzz 与 OutlineFont 同进程硬刚。 |
| **B UI 字体** | 先统一 **出口**（`TextureUpload` / glyph array）；#52 优先；#53 再议 **入口** 是否从 FreeType 换 Skia 栅格。 |
| **C emoji** | plutosvgft **保持窄**；若移除须同步定 emoji 产品策略（位图字体 / 预烘图集 / 平台彩色字体），不与 A/B 绑在一个 PR。 |

### 与 #52 / #53 分工

| PR | 建议范围 | 避免 |
|----|----------|------|
| **#51** | 成像、TIFF、纹理 SVG 安全与 Linux 文字崩溃 | 不要求在此完成 Skia 全站字体 |
| **#52** | Glyph 纹理池、R8 array、`SpriteText` 批绘 | 不等于删除 FreeType / plutosvgft |
| **#53**（规划） | `GlyphTextureProducer` 契约、可选 Skia 栅格 A/B | 不要先换栅格再补 #52；不要假设 OT-SVG 会被 Skia 文件 SVG 替代 |

### 延迟 vs 内存

| 选择 | 延迟 | 内存 |
|------|------|------|
| 纹理 SVG **加载期**栅格（worker / 一次性 decode） | 对局内最优 | 纹理常驻 |
| 运行时保留矢量 SVG + 每帧缩放 | 差 | 可能更小 |
| #52 glyph array | 热路径优 | 略增显存，换更少 bind/upload |
| 子进程 SvgWorker | 仅加载时毫秒级 | 短暂多进程 |

---

## 6. 目标架构草图（延迟第一）

```text
冷路径（加载）
  PNG/WebP/TIFF/SVG/TTF
    → Skia 成像 / SvgWorker(含 text 的 SVG) / OutlineFont 栅格
    → Glyph & Texture Cache

热路径（对局）
  Cache 命中 → #52 Glyph Array → SpriteText Batch → GPU
```

**HarfBuzz**：视为纹理 SVG **可选**依赖；默认 Linux 宿主路径应 **不可达** 或 **隔离**。UI 正文是否长期依赖 HarfBuzz 由 #53 单独定案。

---

## 7. 相关代码入口

| 话题 | 位置 |
|------|------|
| 纹理 SVG 解码 | `osu.Framework/Graphics/Imaging/SvgImageDecoder*.cs` |
| Linux worker | `osu.Framework.SvgWorker/Program.cs` |
| OT-SVG / plutosvgft | `osu.Framework/Text/OutlineFont.cs`, `NativePlutoSvgFt.cs` |
| 回归 | `ln-azimuth.svg`, `TestSceneResourceStores.TestGetTextureStore` |

---

## 8. 修订记录

| 日期 | 说明 |
|------|------|
| 2025-11-24 | 初版：#51 Linux SVG 文字崩溃、栈分工、#52/#53 衔接 |