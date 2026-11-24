# 字体性能与 BMFont 迁移追踪

核对日期 2026-11-24。Framework 实测提交 `fb9ce02c5`，游戏注册核对提交 `1d1704bb03`，资源核对提交
`646f7d05`。[Framework PR #52](https://github.com/SK-la/ez2lazer-framework/pull/52)

## 目标与当前决策

- 以可分发的 TTF/OTF/TTC 作为游戏 UI 主字体。待替代字体的覆盖、观感、内存和响应延迟通过验证后，移除 Torus、Torus
  Alternate、Venera 及其他授权敏感的随包 BMFont。最终保留 1–2 套许可明确的 BMFont 作为末级兜底，具体字库待覆盖测试选定。
- 现阶段继续通过 R8 字形纹理数组降低跨页绘制和纹理占用。Windows D3D11 已测得收益。最新跨平台 CI 仍有失败，TTF 与 BMFont
  的同源性能比较也待完成。
- 游戏字体注册、资源包和 Framework API 分别追踪。随包 BMFont 下线后，皮肤和下游调用仍可能使用 `AddFont`、`GlyphStore`
  或自定义位图字形存储。

## 当前管线

### 字体选择和布局

Ez2Lazer 当前同时注册系统字体、随包字体文件和 BMFont。`TextBuilder` 对每个 Unicode
标量先查询指定完整字体名，再查对应字重后缀、空字体名回退，最后查替代字符。配置的本地化系统字体在空字体名链中排在 Noto BMFont
前面。当前游戏对部分 UI 系统字体槽只注册单个
face，显式字重会随覆盖映射丢弃，多字重迁移需要单独核对。[TextBuilder](../osu.Framework/Text/TextBuilder.cs) · [字体注册入口](../osu.Framework/Game.cs)

布局缓存失效时，`TextBuilder` 重新查字形、推进宽度、应用相邻字距和 baseline。字体加载代次变化会重建曾在加载途中查过的布局。温热纹理命中仍可能发生
FreeType kerning 查询。当前标量级处理可用来验证常见 CJK 和拉丁排版，复杂文字整形、组合标记与 emoji
ZWJ、肤色、旗帜及变体选择符需要按序列验证。[SpriteText](../osu.Framework/Graphics/Sprites/SpriteText.cs) · [OutlineGlyphStore](../osu.Framework/IO/Stores/OutlineGlyphStore.cs)

### 冷字形与内存

- **BMFont** 使用预栅格化字形。游戏多数 BMFont 通过 `RawCachingGlyphStore` 加载。首次访问某页会读取并校验压缩页面，磁盘
  alpha 缓存缺失时解码整页并写入缓存。首次查找页内新字形仍会读取覆盖所需的整行片段，再生成字形 RGBA 上传。页文件流和页信息随
  store 持有，解析后的 BMFont
  元数据由进程级静态缓存保留。磁盘页缓存大小与进程常驻内存应分别测量。[GlyphStore](../osu.Framework/IO/Stores/GlyphStore.cs) · [RawCachingGlyphStore](../osu.Framework/IO/Stores/RawCachingGlyphStore.cs)
- **字体文件** 由 FreeType 异步打开，face 生命周期中持有底层流。常规 `SpriteText` 字形未命中时，度量和栅格化仍走同步路径，查找和变体选择可多次进入
  FreeType。同一 face 的字形访问受锁串行化。共享 variable face 可以减少 face
  和流数量，也会让多个变体共用锁。当前轮廓路径依赖进程内字形缓存，重新启动时新字形要重新栅格化。[OutlineFont](../osu.Framework/Text/OutlineFont.cs) · [OutlineFontStore](../osu.Framework/IO/Stores/OutlineFontStore.cs)
- **上传** 两种来源目前都生成 RGBA 字形。R8 池再提取 alpha 到带 padding 的字节缓冲，Veldrid
  上传队列还会复制一份。冷字形的读盘、栅格化和转换发生在入队前。上传有帧预算，首次布局完成与最后一个可见字形上传完成是两个不同的时间点，应分别记录。[GlyphTexturePool](../osu.Framework/Graphics/Textures/GlyphTexturePool.cs) · [VeldridGlyphTextureArray](../osu.Framework/Graphics/Veldrid/Textures/VeldridGlyphTextureArray.cs) · [Renderer](../osu.Framework/Graphics/Rendering/Renderer.cs)

### 为什么使用 TTF 仍会看到 atlas

TTF 栅格化后的字形也需要 GPU 纹理。即时 Veldrid D3D11 和 Metal 默认将符合条件的单色 `SpriteText` 字形放到共享 R8 纹理数组。数组有
8 个 2048×2048 层，逻辑纹理占用 32 MiB，首次上传创建整组资源。数组层编号随顶点提交，换层可共用同一个纹理绑定。回收复用发生在
glyph region 没有布局、draw node 或缓存强引用之后，池自身的 GPU
分配仍保留。[GlyphTexturePool](../osu.Framework/Graphics/Textures/GlyphTexturePool.cs) · [SpriteText 绘制](../osu.Framework/Graphics/Sprites/SpriteText.DrawNode.cs)

以下情况会产生传统字形纹理，使用图集的 store 会把它们放入 atlas。

- 即时 Vulkan、遗留 OpenGL/GLES 和 Deferred 当前使用传统路径。进程启动前设置 `EZ2LAZER_GLYPH_TEXTURE_ARRAY=0` 同样使用传统路径。
- 彩色 face、容量不足、上传像素不符合单色筛选、自定义文字 shader 及 `SpriteIcon` 等传统查找入口都可产生旧纹理。最新代码会检查非
  outline store 提供的上传像素，拒绝 RGB 分量不相等的样本。
- 当前 `SpriteText` 需要整段可绘制字形都适用数组才使用数组布局。混合彩色或旧字形时会用旧查找重建整段。已经缓存的单色数组字形可能因此同时拥有一份
  RGBA
  表示。纹理巡检应计入两份缓存及所有回退页。[FontStore](../osu.Framework/IO/Stores/FontStore.cs) · [SpriteText](../osu.Framework/Graphics/Sprites/SpriteText.cs) · [SpriteIcon](../osu.Framework/Graphics/Sprites/SpriteIcon.cs)

## 已测基线

Windows D3D11，AMD Radeon RX 6800，Release，`ez2lazer.game.resources 2026.1112.0-ez2lazer`。同一 Noto BMFont、28 个
`SpriteText`、960×720、21 px。两种模式各运行两个独立进程，顺序为旧路径、数组、数组、旧路径。每次先记录 60 帧首用，再记录 360
帧热滚动。跨页组预热 4000 个 Han 字形，13 个标题样本含从预热集中等距抽取的 32 字压力行，标题共 125 个不同非 ASCII
标量。旧路径实际用到 2 张原生纹理，数组用到 6 层。[基准实现](../osu.Framework.Benchmarks/BenchmarkSpriteText.cs)

### 4000 字形跨页组

- 热滚动 p50 **1.87–2.51 → 1.05–1.32 ms**，p95 **2.58–3.53 → 1.54–3.34 ms**。四个进程的 p95 中位数约 **3.11 → 1.68 ms**。
- p99 旧路径 **2.84–4.19 ms**；数组路径 **2.06–43.91 ms**，有一次明显 outlier。本次样本不足以承诺数组路径的尾延迟上限。
- 平均绘制 **95.21 → 4.00 次/帧**，纹理绑定 **93.99 → 3.00 次/帧**，纹理绑定引起的批次刷新 **92.99 → 0 次/帧**。
- 框架跟踪的纹理峰值 **128 → 约 32 MiB**。热滚动的进程级托管分配 **19,784–19,807 → 249–273 B/帧**。
- 字体注册、预热和行构造的累计托管分配 **168.2–184.4 → 73.8–80.6 MB**。耗时 **2.33–2.67 → 2.67–3.95 秒**，数组预热未显示耗时优势。
- 当前 8 层池在该样本中 **28/28 行使用数组布局，容量回退为 0**。此前 4 层试验观察到 1006 次回退。本次容量结果适用于这一字库、字号和语料组合。
- 同一提交再跑一组交替顺序测试，绘制、刷新、纹理峰值和容量结果一致。其中一次数组运行的 p99 达到 43.91 ms，这组数据不足以承诺尾延迟上限。

### 平台与正确性状态

- PR head `fb9ce02c5` 的 Code Style、Sonar 和 Android/iOS 构建成功；Windows、macOS、Linux 桌面测试任务均失败。TRX 显示 Windows `TestBitmapGlyphStoreUsesArrayCoverage` 预期样本进入数组，实际取得普通纹理。测试 PNG 有 744,561/1,048,576 个像素 RGB 为 `(71,112,76)`，是有色 fixture，不应满足 R8 灰度筛选；需换真正灰度 BMFont 样本，同时保留有色图回退检查。
- 同一 PR head 的 macOS 与 Linux TRX 在 `TestGlyphArrayLayerSample` 两个参数用例中报告 graphics pipeline 创建时 NullReferenceException。测试已补绑第二 sampler，但跨后端故障仍未解决。Vulkan 生产路径关闭；Metal 虽默认启用，但 CI 失败，目前不能视为已验证支持。[CI 运行 45323853842](https://github.com/SK-la/ezlazer-framework/actions/runs/45323853842)

## 字体下线追踪

- [ ] **Torus、Torus Alternate**。正文与标题仍注册 4
  个字重各一套，资源包含定义和页面。选择许可明确的替代字体后，对照基线、窄宽差异、截断和换行。检查备用标题字形和既有配置映射。原资源的分发条件见各族许可文件。[Torus 许可](https://github.com/SK-la/ez2lazer-resources/blob/646f7d05/osu.Game.Resources/Fonts/Torus/LICENCE) · [Torus Alternate 许可](https://github.com/SK-la/ez2lazer-resources/blob/646f7d05/osu.Game.Resources/Fonts/Torus-Alternate/LICENCE)
- [ ] **Venera**。数字、比分和 HUD 当前依赖
  Light/Bold/Black。替代方案要核对等宽数字推进、基线、分数位数变化时的对齐、旧皮肤及设置项。资源许可文件标注分发需商业许可。[Venera 许可](https://github.com/SK-la/ez2lazer-resources/blob/646f7d05/osu.Game.Resources/Fonts/Venera/LICENCE)
- [ ] **Inter 与 Noto 分组**。游戏仍注册 Inter 的 8 个样式和 Noto 的 Basic、Bopomofo、CJK Basic/Compatibility、Hangul、Thai
  BMFont。核对具体替代文件的版本、来源、许可和字符覆盖。Inter 现有资源附 OFL，Noto 位图目录附 Apache
  许可文本。新字体文件的许可按实际选用的发行版本记录。[游戏注册](https://github.com/SK-la/Ez2Lazer/blob/1d1704bb03/osu.Game/OsuGameBase.cs)
- [ ] **末级兜底与 emoji**。按缺字报告选出 1–2 套有清晰分发依据的 BMFont，逐套说明覆盖和回退顺序。包内已有 Twemoji Mozilla
  TTF，另有 Noto Emoji 单色 BMFont。系统彩色 face 和 OT-SVG
  钩子的保留与否由彩色渲染、原生打包和序列级验证决定。[字体资源](https://github.com/SK-la/ez2lazer-resources/tree/646f7d05/osu.Game.Resources/Fonts)
- [ ] **FontAwesome 与皮肤**。Framework 仍内置 Solid、Regular、Brands 三组 BMFont，Solid OTF 仅在基准资源中。图标迁移单独核对
  PUA 映射、显示大小、居中与调用入口。皮肤自定义的纹理字形按其接口兼容性验证。[Framework 字体注册](../osu.Framework/Game.cs)
- [ ] **发布验证**。每族完成切换后，核对游戏注册、默认与已有配置、资源生成配置及实际发布包。确认待移除字体的定义和 atlas PNG
  已退出分发，保留所选替代资源的许可和署名。

## 优先级与测量计划

1. **修复并重跑当前 CI。** 新建真正灰度 BMFont fixture 与有色 fixture，分别比对数组和 RGBA 像素。单色 coverage 转 R8 前验证视觉等价；有色 RGB 不相同必须保留 RGBA。定位 Vulkan/Metal 的离屏 pipeline NullReference。再验证单页与跨页、裁剪阴影、混合彩色 glyph、切字体和池满回退。后端默认范围由每个平台通过结果决定。
2. **隔离字体来源成本。** 首选同一份许可明确的轮廓源，生成对应 BMFont 做对照。固定同一 GPU
   后端与图集模式、同样字形集合和回退链，匹配字重、可见高度与基线。以独立进程交替运行，区分冷应用缓存、BMFont 的热磁盘缓存和暖
   GPU 字形缓存。再对实际替代 Torus/Venera 的字体做独立视觉对照。**当前状态：** Roboto 字符覆盖与度量/栅格诊断已完成；现有 BMFont 与 TTF
   不等价，且仓库和本机均未找到可用的 BMFont 导出器，因此尚未完成同源成本对照。继续前需提供/确认导出工具与参数，或提交由同一 TTF 生成并带来源记录的 BMFont fixture。
3. **优先验证直出 R8。** 分别用 FreeType 单色覆盖和 BMFont alpha 缓存直接填充一次拥有明确生命周期的上传缓冲，测量能减少多少中间
   RGBA 分配与复制。保持彩色纹理语义。记录冷字形 p95/p99 和连续滚动遇到新字形时的峰值。**当前状态：** TTF 与 RawCaching BMFont
   都实现了 alpha coverage 直出 R8；其他存储继续使用 RGBA 路径，彩色 outline 字体不进入直出路径。直接 alpha 对照测试通过。Debug 离屏样本确认 R8
   模式下热滚动分配约 273–296 bytes/frame，传统路径约 43.8–44.0 KB/frame；样本量不足以形成性能结论，尚需 Release 多轮及冷字形 p95/p99 基准。
4. **按响应预算安排预热。** 先测首屏与窗口外字形各占多少时间，再对比可见字形优先、受预算约束的后台预热。验证多任务去重、字体切换、旧请求取消和首次完整显示。基于采样再决定是否缓存
   kerning、复用字体 face、增加持久字形缓存或调整固定数组容量。**当前状态：** `FontStore.PreloadGlyphsAsync()` 提供调用方排序、按时长分批让出线程及取消支持；单测验证重复输入去重和预取消。调用方应把可见字形排前，并在字体切换时取消旧令牌。尚未接入游戏默认启动或保存预热状态。
5. **逐族替换。** 先提交可分发且通过视觉验证的替代字体，然后切换默认注册，最后从发布包删除旧资产。每族记录首屏完整显示时间、滚动
   p95/p99、托管分配、进程 private bytes、字体 face 和流数量、纹理峰值、缺字、回退数与资源包体积。只有数据能解释变化且满足该场景延迟和内存预算时，才推进该族的切换。
6. **独立跟踪文本对象重建。** `FPSCounterTooltip.Update()` 每 100 ms 清空并重建线程文本行，和 Drawable
   构造日志吻合。后续可对比复用文本控件和当前行为，记录 `OsuSpriteText`、`NewLineContainer` 构造次数及 Update 帧时。该问题属于游戏
   UI。[FPSCounterTooltip](https://github.com/SK-la/Ez2Lazer/blob/1d1704bb03/osu.Game/Graphics/UserInterface/FPSCounterTooltip.cs)

每次更新追踪记录附上 Framework、游戏和资源版本、设备、后端、构建、缓存状态、样本或复现命令、进程次数及未覆盖项。首屏至少分别计时
face 就绪、布局就绪和最后一个可见字形上传完成。连续滚动应报告热缓存阶段和首次遇到新字形阶段，避免把二者平均成一个数。

## 复跑当前 CJK 基准

在 Framework 根目录运行。替换资源 DLL 路径，分别在独立子进程测试，`0` 是单页对照的预热字形计数。可在预热计数后传入同一份
UTF-8 标题文件用于实际语料重放。

```powershell
dotnet build osu.Framework.Benchmarks/osu.Framework.Benchmarks.csproj -c Release
$fontResources = '<path-to-osu.Game.Resources.dll>'
$fontBenchmark = 'osu.Framework.Benchmarks/bin/Release/net11.0/osu.Framework.Benchmarks.dll'
foreach ($fontPreload in @(4000, 0)) {
    foreach ($fontMode in @('0', '1', '1', '0')) {
        $env:EZ2LAZER_GLYPH_TEXTURE_ARRAY = $fontMode
        dotnet $fontBenchmark --sprite-text-cjk $fontResources $fontPreload
    }
}
```

检查输出中的原生纹理数、数组层数和容量回退数。开关在进程首次初始化时读取。4000 个预热字形是压力输入，只有确认可见标题用到多个存储页时才能用来解释跨页性能。

## 复跑 Roboto 字体来源对照

基准使用 Framework 内的 Roboto variable TTF，并与测试资源中的 `Roboto-Regular` BMFont 对照。两种模式使用相同拉丁语料、21 px、Roboto Regular、离屏窗口及帧采样；自定义语料可传入相同的 UTF-8 文本文件。每次命令启动独立进程并创建新的临时缓存目录。建议交替运行来源以减轻进程顺序影响：

```powershell
dotnet build osu.Framework.Benchmarks/osu.Framework.Benchmarks.csproj -c Release
$benchmark = 'osu.Framework.Benchmarks/bin/Release/net11.0/osu.Framework.Benchmarks.dll'
$previousGlyphMode = $env:EZ2LAZER_GLYPH_TEXTURE_ARRAY
foreach ($arrayMode in @('1', '0')) {
    $env:EZ2LAZER_GLYPH_TEXTURE_ARRAY = $arrayMode
    foreach ($fontSource in @('outline', 'bmfont', 'bmfont', 'outline')) {
        dotnet $benchmark --sprite-text-roboto $fontSource
    }
}
if ($null -eq $previousGlyphMode) {
    Remove-Item Env:EZ2LAZER_GLYPH_TEXTURE_ARRAY
} else {
    $env:EZ2LAZER_GLYPH_TEXTURE_ARRAY = $previousGlyphMode
}
```

`outline` 从 Framework 资源加载 variable TTF 的 `Roboto-Regular` named instance；`bmfont` 使用 `osu.Framework.Tests` 中的预栅格化字形页。基准在启动时逐码点验证两者都覆盖语料，并报告字体存储设置与行构造、首次布局、热滚动、托管分配、纹理和批次指标。比较时固定同一 GPU、图形后端和 `EZ2LAZER_GLYPH_TEXTURE_ARRAY` 设置；数组与传统路径应分开成组测量。该基准用于观察来源管线成本，不替代字形视觉、字宽和基线等价性验证。

响应预算诊断可以在字体来源后追加 `none`、`visible` 或 `all`；默认值为 `all`。不传标题文件时，三种模式共享 28 行、80 个唯一拉丁字符的语料，`visible` 预热前 24 行。例：

```powershell
dotnet $benchmark --sprite-text-roboto outline none
dotnet $benchmark --sprite-text-roboto outline visible
dotnet $benchmark --sprite-text-roboto outline all
```

### Roboto 首轮烟测

2026-11-24，Windows Debug、D3D11、AMD Radeon RX 6800，960×720，21 px，5 条默认拉丁语料、80 个不同标量、28 行。数组模式每个来源交替运行 2 个独立进程；传统路径每个来源运行 3 个独立进程。所有进程均覆盖 80/80 标量并正常退出，数组模式为 28/28 行使用数组且零容量回退，传统模式为 0/28 行使用数组。数组峰值跟踪纹理约 32 MiB，传统路径约 64 MiB。

- 字体存储设置、字形预取和行构造总分配：TTF 约 5.3 MB（数组）/5.5 MB（传统）；BMFont 约 11.8 MB/12.0 MB。该阶段包含各来源自己的加载、栅格化或页面缓存成本，不能等同于每帧分配。
- 热滚动 p50：TTF 1.91–2.00 ms（数组）/2.13–2.78 ms（传统）；BMFont 2.14 ms/2.02–2.73 ms。每组样本很少，区间重叠。
- 首次布局 p99 在所有来源和布局中都有 150 ms 以上尖峰，说明该短样本受启动或调度离群值影响，不能用于尾延迟结论。当前结果只验证基准路径、资源覆盖和模式切换，不构成 TTF/BMFont 性能胜负判断。
- 字体文件 SHA-256 已核对：Framework 与测试目录中的 Roboto TTF 相同。BMFont fixture 与 TTF 的家族及 Regular 字重命名相符，但其生成版本和原始字库文件尚未核验。

### Roboto 度量与栅格诊断

此诊断独立于帧性能采样，直接比较两个 glyph store 的原始度量、相邻字距及纹理 alpha。默认语料与性能基准一致，也可传入 UTF-8 标题文件：

```powershell
$benchmark = 'osu.Framework.Benchmarks/bin/Release/net11.0/osu.Framework.Benchmarks.dll'
dotnet $benchmark --sprite-text-roboto-metrics
dotnet $benchmark --sprite-text-roboto-metrics '<path-to-UTF-8-titles.txt>'
```

2026-11-24 的默认语料结果：80/80 字符有覆盖；TTF/BMFont baseline 分别为 84/80；80 个字符至少一项 X/Y offset、advance 或 baseline 相差超过 0.01；72 个字符纹理尺寸不同，79 个字符的共同纹理区域含 alpha 差异。比较了 161,654 个重叠像素，平均绝对 alpha 差异为 17.436/255，最大值 255；语料中的相邻 kerning 配对无差异。

alpha 比较按字形纹理左上角对齐，仅统计两张纹理共同宽高内的像素；不同的 glyph offset 仍需结合实际排版与截图检查。该诊断证明当前两份 fixture 并非度量/栅格等价，不能据此推断其中哪份正确，也不能在 BMFont 来源未核验前作为严格同源性能结论。后续需要核对 BMFont 生成工具、源文件版本、字号和导出参数，再决定是否以它作为迁移验收基准。

### 响应预算预热首轮测量

2026-11-25，Windows Debug、D3D11、AMD Radeon RX 6800、R8 数组、TTF，三种模式各启动一个独立进程。无预热/可见 72 字/全量 80 字的字体预热及行构造阶段分别为 7.99/6.46/6.32 秒；首次使用帧 p99 为 1487/1158/1928 ms，热滚动 p50 为 0.696/0.736/0.904 ms，p99 为 21.587/7.620/9.684 ms。极端首用帧和模式间无稳定单调关系，Debug 离屏启动样本不能支撑响应收益判断；不据此做自动全量预热或缓存策略决策。

`FontStore.PreloadGlyphsAsync(fontName, codepoints, maximumBatchDuration, cancellationToken)` 把同步字形查询放到后台线程，按传入顺序处理，达到批次时长后让出执行，并可由调用方在字体切换时取消。建议先构造可见区域优先的码点序列，并在目标应用中分别记录布局完成和最后一个可见字形上传完成；当前 Framework 基准尚不能测出这两个应用级时点。
