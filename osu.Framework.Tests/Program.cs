// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Platform;

namespace osu.Framework.Tests
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            bool benchmark = args.Contains(@"--benchmark");
            bool portable = args.Contains(@"--portable");

            var displayOutputMode = Enum.TryParse<DisplayOutputMode>(Environment.GetEnvironmentVariable("EZ_FRAMEWORK_DISPLAY_OUTPUT"), true, out var parsedDisplay)
                ? parsedDisplay
                : DisplayOutputMode.Standard;

            using (GameHost host = Host.GetSuitableDesktopHost(@"visual-tests", new HostOptions
            {
                PortableInstallation = portable,
                DisplayOutputMode = displayOutputMode,
            }))
            {
                if (benchmark)
                    host.Run(new AutomatedVisualTestGame());
                else
                    host.Run(new VisualTestGame());
            }
        }
    }
}
