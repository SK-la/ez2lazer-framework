// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Bindables;

namespace osu.Framework.Audio.EzLatency
{
    /// <summary>
    /// EzLatency延迟测试管理器，提供与程序设置关联的公共接口
    /// </summary>
    public class EzLatencyManager
    {
        /// <summary>
        /// Pluggable hardware timestamp provider. Defaults to a null provider which indicates no hardware timestamps available.
        /// Override this with a platform-specific provider if desired.
        /// </summary>
        public static IHwTimestampProvider HwProvider = new NullHwTimestampProvider();

        /// <summary>
        /// Global lightweight manager instance for best-effort framework instrumentation.
        /// Use this to record input/playback events from low-level framework code without resolving instances.
        /// </summary>
        public static readonly EzLatencyManager GLOBAL = new EzLatencyManager();

        /// <summary>
        /// 延迟测试启用状态的绑定值，可与程序设置关联
        /// </summary>
        public readonly BindableBool Enabled = new BindableBool(false);

        private readonly EzLatencyAnalyzer analyzer;
        private readonly EzLatencyCollector collector = new EzLatencyCollector();
        private readonly Action<EzLatencyRecord> serviceHandler;
        private readonly object probeSync = new object();

        private AcousticClosedLoopProbe? acousticProbe;
        private string? currentOutputDriverId;
        private float acousticThreshold = 0.02f;

        public EzLatencyManager()
        {
            analyzer = new EzLatencyAnalyzer();
            // 将启用状态与分析器同步
            Enabled.BindValueChanged(v =>
            {
                analyzer.Enabled = v.NewValue;

                if (v.NewValue)
                    tryStartAcousticProbe();
                else
                    stopAcousticProbe();
            }, true);

            // 统一通过 EzLatencyService 的事件通道接收所有记录（包括来自其它分析器/线程的）
            serviceHandler = record =>
            {
                collector.AddRecord(record);
                OnNewRecord?.Invoke(record);
            };

            EzLatencyService.Instance.OnMeasurement += serviceHandler;
        }

        /// <summary>
        /// 当有新的延迟记录时触发
        /// </summary>
        public event Action<EzLatencyRecord>? OnNewRecord;

        /// <summary>
        /// Whether the acoustic loopback probe is currently capturing.
        /// </summary>
        public bool AcousticProbeRunning
        {
            get
            {
                lock (probeSync)
                    return acousticProbe?.IsRunning == true;
            }
        }

        /// <summary>
        /// RMS threshold for loopback level detection (0–1 linear).
        /// </summary>
        public void SetAcousticThreshold(float threshold)
        {
            acousticThreshold = Math.Clamp(threshold, 0.0001f, 1f);

            lock (probeSync)
            {
                if (acousticProbe != null)
                    acousticProbe.Threshold = acousticThreshold;
            }
        }

        /// <summary>
        /// Called when the game's output device changes so loopback can follow the same endpoint.
        /// </summary>
        public void NotifyOutputDeviceChanged(string? bassDriverId)
        {
            currentOutputDriverId = bassDriverId;

            if (!Enabled.Value)
                return;

            tryStartAcousticProbe();
        }

        /// <summary>
        /// 在gameplay期间记录输入事件
        /// </summary>
        /// <param name="keyValue">按键值或输入标识</param>
        public void RecordInputEvent(object? keyValue = null)
        {
            if (!analyzer.Enabled) return;

            double inputTime = analyzer.GetCurrentTimestamp();
            analyzer.RecordInputData(inputTime, keyValue);

            lock (probeSync)
                acousticProbe?.Arm(inputTime);
        }

        /// <summary>
        /// 在gameplay期间记录判定事件
        /// </summary>
        public void RecordJudgeEvent()
        {
            if (!analyzer.Enabled) return;

            double judgeTime = analyzer.GetCurrentTimestamp();
            analyzer.RecordJudgeData(judgeTime);
        }

        /// <summary>
        /// 在gameplay期间记录播放事件
        /// </summary>
        public void RecordPlaybackEvent()
        {
            if (!analyzer.Enabled) return;

            double playbackTime = analyzer.GetCurrentTimestamp();
            analyzer.RecordPlaybackData(playbackTime);
        }

        /// <summary>
        /// 在gameplay期间记录硬件数据
        /// </summary>
        /// <param name="driverTime">驱动时间</param>
        /// <param name="outputHardwareTime">输出硬件时间</param>
        /// <param name="inputHardwareTime">输入硬件时间</param>
        /// <param name="latencyDifference">延迟差异</param>
        public void RecordHardwareData(double driverTime, double outputHardwareTime, double inputHardwareTime, double latencyDifference)
        {
            if (!analyzer.Enabled) return;

            analyzer.RecordHardwareData(driverTime, outputHardwareTime, inputHardwareTime, latencyDifference);
        }

        /// <summary>
        /// 获取当前时间戳
        /// </summary>
        /// <returns>当前时间戳（毫秒）</returns>
        public double GetCurrentTimestamp() => analyzer.GetCurrentTimestamp();

        /// <summary>
        /// 获取聚合的延迟统计数据（best-effort）。
        /// </summary>
        public EzLatencyStatistics GetStatistics() => collector.GetStatistics();

        /// <summary>
        /// 清空统计收集器与当前未完成槽位。
        /// </summary>
        public void ClearStatistics()
        {
            collector.Clear();
            analyzer.ClearCurrentData();

            lock (probeSync)
                acousticProbe?.Disarm();
        }

        /// <summary>
        /// Clear the in-flight input/playback slot without touching aggregate stats.
        /// </summary>
        public void ClearPendingMeasurement()
        {
            analyzer.ClearCurrentData();

            lock (probeSync)
                acousticProbe?.Disarm();
        }

        /// <summary>
        /// Create a simple file logger for latency records. Convenience factory to make EzLoggerAdapter discoverable.
        /// </summary>
        public IEzLatencyLogger CreateLogger(string filePath) => new EzLoggerAdapter(null, filePath);

        /// <summary>
        /// Create a basic in-process tracker which emits measurements into the global pipeline.
        /// </summary>
        public IEzLatencyTracker CreateBasicTracker()
        {
            var t = new BasicEzLatencyTracker();
            t.OnMeasurement += r => EzLatencyService.Instance.PushRecord(r);
            return t;
        }

        /// <summary>
        /// 已收集的完整记录数量
        /// </summary>
        public int RecordCount => collector.Count;

        /// <summary>
        /// 在gameplay开始时启用延迟测试
        /// </summary>
        public void StartGameplayTest()
        {
            Enabled.Value = true;
        }

        /// <summary>
        /// 在gameplay结束时禁用延迟测试
        /// </summary>
        public void StopGameplayTest()
        {
            Enabled.Value = false;
        }

        private void tryStartAcousticProbe()
        {
            if (!OperatingSystem.IsWindows())
            {
                analyzer.AwaitAcoustic = false;
                return;
            }

            lock (probeSync)
            {
                acousticProbe ??= new AcousticClosedLoopProbe(
                    analyzer.GetCurrentTimestamp,
                    analyzer.PollTimeout,
                    RecordHardwareData);

                acousticProbe.Threshold = acousticThreshold;

                bool running = acousticProbe.TryStart(currentOutputDriverId);
                analyzer.AwaitAcoustic = running;

                if (!running)
                    analyzer.AwaitAcoustic = false;
            }
        }

        private void stopAcousticProbe()
        {
            lock (probeSync)
            {
                analyzer.AwaitAcoustic = false;
                acousticProbe?.Stop();
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            Enabled.UnbindAll();
            stopAcousticProbe();

            lock (probeSync)
            {
                acousticProbe?.Dispose();
                acousticProbe = null;
            }

            EzLatencyService.Instance.OnMeasurement -= serviceHandler;
        }
    }
}
