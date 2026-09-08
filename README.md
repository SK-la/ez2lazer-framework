<p align="center">
  <img width="500px" src="assets/o!f Logo Large FC.svg">
</p>

# Ez2Lazer Framework

<p align="center">
  <a href="#readme-zh"><img src="https://img.shields.io/badge/语言-中文-1f6feb?style=for-the-badge" alt="中文"></a>
  <a href="#readme-en"><img src="https://img.shields.io/badge/Language-English-238636?style=for-the-badge" alt="English"></a>
</p>

<p align="center">
  <a href="https://github.com/SK-la/osu-framework/actions/workflows/ci.yml"><img src="https://github.com/SK-la/osu-framework/actions/workflows/ci.yml/badge.svg?branch=master&event=push" alt="Build status"></a>
  <a href="https://github.com/SK-la/osu-framework/releases/latest"><img src="https://img.shields.io/github/release/SK-la/osu-framework.svg" alt="GitHub release"></a>
</p>

> GitHub README 无法运行脚本做真正的「一键切换」；请点上方徽章跳转到对应语言块，并用折叠标题展开/收起。  
> GitHub READMEs cannot run scripts for a real language toggle — use the badges above to jump, then expand/collapse the sections.

NuGet：`ez2lazer.Framework` · 主仓：[Ez2Lazer](https://github.com/SK-la/Ez2Lazer) · 上游：[ppy/osu-framework](https://github.com/ppy/osu-framework)

---

<a id="readme-zh"></a>
<details open>
<summary><strong>中文</strong>（点击折叠 / 展开）</summary>

## 简介

本仓库是 [ppy/osu-framework](https://github.com/ppy/osu-framework) 的 **Ez2Lazer 专用 fork**，为 [Ez2Lazer](https://github.com/SK-la/Ez2Lazer) 提供底层能力（低延迟音频、毛玻璃 UI、透视着色、多设备输入等）。

日常开发与发布走 [SK-la/osu-framework](https://github.com/SK-la/osu-framework)；`upstream`（ppy）仅作只读同步源。

NuGet 包名：`ez2lazer.Framework`（由 Ez2Lazer 主仓的 `Ez2Lazer.Dependencies.props` 引用）。

## 相对上游的改动

### 音频

- **ASIO 输出**：`EzAsioDeviceManager` 管理设备初始化、采样率/缓冲区、切换与释放；打包包含 `bass.asio`；支持外部 PCM / 直通（`AsioUseExternalPCM`）等配置。
- **WASAPI 独占**：在共享模式之外提供独占路径，便于低延迟出声。
- **输出模式枚举**：`AudioOutputMode`（Shared / Exclusive / ASIO），与游戏侧音频设置对接。
- **EzLatency**：输入–播放延迟采样与统计框架（`osu.Framework/Audio/EzLatency/`），供 Ez 侧延迟分析使用。
- **稳定性**：Mixer 未就绪防护、通道初始化顺序、设备列表与重配置生命周期等修复。

### 图形

- **局部毛玻璃** `BackdropBlurDrawable`：复用捕获源当帧 draw node，对指定子树做高斯虚化（如 Mania 双 stage）。
- **全局亚克力** `AcrylicBackdropDrawable`：内容无关地虚化「自身矩形下方已渲染内容」；D3D11 下支持 backbuffer region snapshot，避免闪烁/管线状态破坏。
- **透视着色**：`BufferedContainer` 真透视 shader（`sh_Perspective.fs`），用于轨道等 2.5D 透视效果。
- **无边框不锁帧**：Fullscreen / Borderless 允许 tearing，Unlimited 帧同步不再被合成器顶死。

### 输入

- **按设备摇杆轴** `JoystickDeviceAxis`：多手柄 / 多转盘按 InstanceId + GUID 隔离轴值（SDL2/SDL3），避免多设备互相覆盖——BMS 转盘等场景需要。

### 其它

- 字体双字符（代理对）解析，基本 emoji 显示。
- **OT-SVG 彩色 emoji**：`plutosvgft`（PlutoSVG FreeType hooks）随 `ez2lazer.Framework` 的 `runtimes/*/native` 分发；`OutlineFont` 启动时注册 `ot-svg`/`svg-hooks`，可渲染 `NotoColorEmoji-Regular.ttf`。
- SDL3 退出时 native 清理超时，减轻关进程后后台僵持。
- WaveformGraph、故事板/采样 LRU 与内存相关修复等维护性改动。
- CI / NuGet 可信发布面向 SK-la fork 调整。

上游常规能力（UI、输入、VisualTests、跨平台宿主等）仍保留；完整上游说明见 [ppy/osu-framework](https://github.com/ppy/osu-framework)。

## 与主仓的关系

```
SK-la/osu-framework  →  NuGet: ez2lazer.Framework
SK-la/osu-resources  →  NuGet: ez2lazer.Game.Resources
SK-la/Ez2Lazer       →  游戏本体（默认引用上述 NuGet；可切本地工程引用）
```

本地联调：在 Ez2Lazer 的 `Ez2Lazer.Dependencies.props` 中声明 `UseEz2LazerLocalFrameworkProject` / `UseEz2LazerLocalResourcesProject` 为 `true`，并保持本仓库与主仓同级目录。

### NuGet 发布顺序（OT-SVG）

主仓 `PackEz2GameNuGet` 会设 `UseEz2LazerLocalFrameworkProject=false`，因此彩色 SVG emoji **依赖已发布的** `ez2lazer.Framework` 包内 `plutosvgft`：

1. 确认 `osu.Framework/runtimes/win-x64/native/plutosvgft.dll` 与 `linux-x64/native/libplutosvgft.so` 已提交。
2. 打 tag `*.*.*-ez2lazer` → `deploy-pack` 发布 `ez2lazer.Framework`。
3. 用 `scripts/Verify-FrameworkNupkgPlutoSvg.ps1 -Nupkg <nupkg>` 验收 runtimes。
4. 主仓把 `Ez2LazerFrameworkVersion` 升到该版本后再打包游戏 / 安装包。

缺某 RID 的 native 时 hooks 注册失败并回退 BMFont，不应导致启动崩溃。

文档与功能总览：[Ez2Lazer Wiki](https://github.com/SK-la/Ez2Lazer/wiki)

## 构建

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Linux 需系统级 ffmpeg（视频解码）
- 推荐用 Visual Studio / Rider / VS Code，加载平台对应的 `.slnf`，组件开发优先跑 `VisualTests`

```bash
git clone https://github.com/SK-la/osu-framework
# 与 Ez2Lazer、osu-resources 同级克隆后按 Dependencies.props 切换引用
```

代码分析：`powershell ./InspectCode.ps1` 或 `./InspectCode.sh`

## 同步上游

定期 `git fetch upstream` 后在本地 merge/rebase；**不要**向 `ppy/*` 推送或开 PR。发布与 PR 一律针对 `SK-la/osu-framework`。

## 许可

基于 [MIT](https://opensource.org/licenses/MIT)，见 [LICENCE](LICENCE)。BASS / BASS ASIO 为商业音频库：非商业免费；商业分发请自行取得 [un4seen 许可](http://www.un4seen.com/bass.html#license)。

上游版权归 [ppy Pty Ltd](https://github.com/ppy)；本 fork 的 Ez 专用改动同样以 MIT 发布。

<p align="right"><a href="#ez2lazer-framework">↑ 回顶</a> · <a href="#readme-en">English ↓</a></p>

</details>

---

<a id="readme-en"></a>
<details>
<summary><strong>English</strong> (click to expand / collapse)</summary>

## Overview

This repository is the **Ez2Lazer fork** of [ppy/osu-framework](https://github.com/ppy/osu-framework). It powers [Ez2Lazer](https://github.com/SK-la/Ez2Lazer) with low-latency audio, acrylic UI, perspective shaders, multi-device input, and more.

Day-to-day development and releases target [SK-la/osu-framework](https://github.com/SK-la/osu-framework). `upstream` (ppy) is **read-only** sync only.

NuGet package: `ez2lazer.Framework` (referenced from Ez2Lazer’s `Ez2Lazer.Dependencies.props`).

## What we add on top of upstream

### Audio

- **ASIO output**: `EzAsioDeviceManager` handles device init, sample rate / buffer size, switching and teardown; ships `bass.asio`; supports external PCM / passthrough (`AsioUseExternalPCM`).
- **WASAPI exclusive**: exclusive path alongside shared mode for lower-latency output.
- **Output mode enum**: `AudioOutputMode` (Shared / Exclusive / ASIO), wired to game-side audio settings.
- **EzLatency**: input–playback latency sampling and stats (`osu.Framework/Audio/EzLatency/`) for Ez analysis tools.
- **Stability**: mixer-not-ready guards, channel init ordering, device listing and reconfiguration lifecycle fixes.

### Graphics

- **Local frosted glass** `BackdropBlurDrawable`: reuses capture sources’ current-frame draw nodes and Gaussian-blurs a subtree (e.g. dual Mania stages).
- **Global acrylic** `AcrylicBackdropDrawable`: content-agnostic blur of whatever is already rendered under the card rect; on D3D11, backbuffer region snapshots avoid flicker / pipeline corruption.
- **Perspective shader**: true perspective on `BufferedContainer` (`sh_Perspective.fs`) for 2.5D track-style effects.
- **Uncapped borderless**: tearing allowed in Fullscreen / Borderless so Unlimited frame sync is not clamped by the compositor.

### Input

- **Per-device joystick axes** `JoystickDeviceAxis`: isolates axes by InstanceId + GUID across multiple pads / turntables (SDL2/SDL3) — required for BMS turntable setups.

### Misc

- Surrogate-pair font parsing for basic emoji.
- **OT-SVG colour emoji**: `plutosvgft` (PlutoSVG FreeType hooks) ships in `ez2lazer.Framework` under `runtimes/*/native`; `OutlineFont` registers `ot-svg`/`svg-hooks` so `NotoColorEmoji-Regular.ttf` can rasterize.
- SDL3 native cleanup timeout on exit to reduce hung background processes.
- WaveformGraph, storyboard / sample LRU and other memory-related maintenance.
- CI / trusted NuGet publish adapted for the SK-la fork.

Core upstream capabilities (UI, input, VisualTests, cross-platform host, etc.) remain; see [ppy/osu-framework](https://github.com/ppy/osu-framework) for the full upstream docs.

## Relation to Ez2Lazer

```
SK-la/osu-framework  →  NuGet: ez2lazer.Framework
SK-la/osu-resources  →  NuGet: ez2lazer.Game.Resources
SK-la/Ez2Lazer       →  game (defaults to NuGet; can switch to sibling project refs)
```

Local sibling refs: set `UseEz2LazerLocalFrameworkProject` / `UseEz2LazerLocalResourcesProject` in Ez2Lazer’s `Ez2Lazer.Dependencies.props`.

### NuGet publish order (OT-SVG)

Game packing (`PackEz2GameNuGet`) sets `UseEz2LazerLocalFrameworkProject=false`, so colour SVG emoji needs a published `ez2lazer.Framework` that contains `plutosvgft`:

1. Ensure `osu.Framework/runtimes/win-x64/native/plutosvgft.dll` and `linux-x64/native/libplutosvgft.so` are committed.
2. Tag `*.*.*-ez2lazer` → `deploy-pack` publishes `ez2lazer.Framework`.
3. Verify with `scripts/Verify-FrameworkNupkgPlutoSvg.ps1 -Nupkg <nupkg>`.
4. Bump `Ez2LazerFrameworkVersion` in the game repo, then pack the game / installer.

Missing RID natives soft-fail to BMFont; they must not crash startup.
Local wiring: set `UseEz2LazerLocalProjects` to `true` in Ez2Lazer’s `Ez2Lazer.Dependencies.props` (NuGet is the default when unset), and keep this repo next to the game repo.

Docs: [Ez2Lazer Wiki](https://github.com/SK-la/Ez2Lazer/wiki)

## Building

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- On Linux, a system-wide ffmpeg install is required for video decoding
- Prefer Visual Studio / Rider / VS Code; load the platform `.slnf`; develop components under `VisualTests`

```bash
git clone https://github.com/SK-la/osu-framework
# Clone beside Ez2Lazer and osu-resources, then toggle refs in Dependencies.props
```

Code analysis: `powershell ./InspectCode.ps1` or `./InspectCode.sh`

## Syncing upstream

`git fetch upstream` then merge/rebase locally. **Do not** push to or open PRs against `ppy/*`. Releases and PRs go to `SK-la/osu-framework` only.

## Licence

[MIT](https://opensource.org/licenses/MIT) — see [LICENCE](LICENCE). BASS / BASS ASIO are commercial libraries: free for non-commercial use; obtain a [un4seen licence](http://www.un4seen.com/bass.html#license) for commercial distribution.

Portions of this software are copyright © 2025 The FreeType Project (https://freetype.org). All rights reserved.

Upstream copyright belongs to [ppy Pty Ltd](https://github.com/ppy); Ez-specific changes in this fork are also released under MIT.

<p align="right"><a href="#ez2lazer-framework">↑ Top</a> · <a href="#readme-zh">中文 ↑</a></p>

</details>
