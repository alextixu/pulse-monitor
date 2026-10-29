using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Pulse.Agent.Ipc;
using Pulse.Agent.Tray;
using Pulse.Core.Ipc;
using Pulse.Core.Localization;
using Pulse.Core.Models;
using Pulse.Core.Tray;
using Pulse.Engine;

namespace Pulse.Agent;

/// <summary>
/// <c>Pulse.exe --tray-selftest</c>: exercises the Win32 tray pieces without the engine for about 10 s and exits by itself
/// (0 = all checks passed, 1 = a check failed). Shows the real icon (the static glyph, then a few rendered contents),
/// stress-renders every glyph kind and checks that GDI/USER object counts return to their baseline, checks the pipe's
/// integrity label on a private pipe name, the device-change throttle, the Post queue, session-end and TaskbarCreated
/// handling (synthetic messages to its own window) and writes a glyph contact sheet. It does not take the single-instance
/// mutex, open the real pipe, read settings or touch %APPDATA% / %LOCALAPPDATA%: the log and the PNG go next to the exe.
/// It never opens the context menu or a balloon (both would grab the user's attention / focus).
/// </summary>
internal static unsafe class TraySelfTest
{
    private const int StressRounds = 50;

    public static int Run()
    {
        var directory = AppContext.BaseDirectory;
        var logPath = Path.Combine(directory, "tray-selftest.log");
        try { File.Delete(logPath); } catch { /* best effort */ }

        using var fileLogger = new FileLoggerProvider(logPath);
        using var loggers = AppLogging.CreateFactory(fileLogger);
        var log = loggers.CreateLogger("Pulse.Agent.TraySelfTest");
        var failures = new List<string>();

        void Report(string line)
        {
            log.LogInformation("{Line}", line);
            try { Console.WriteLine(line); } catch { /* no console */ }
        }

        void Check(bool ok, string what)
        {
            if (!ok) failures.Add(what);
            Report((ok ? "PASS " : "FAIL ") + what);
        }

        var clock = Stopwatch.StartNew();
        Report($"tray self-test: pid {Environment.ProcessId}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
               $"GC.Concurrent={AppContext.GetData("System.GC.Concurrent") ?? "default"} GC.ConserveMemory={AppContext.GetData("System.GC.ConserveMemory") ?? "default"} " +
               $"Invariant={AppContext.GetData("System.Globalization.Invariant") ?? "default"} TieredPGO={AppContext.GetData("System.Runtime.TieredPGO") ?? "default"}");

        CheckThrottleRules(Check);

        var gdiStart = Gdi();
        var userStart = User();
        Report($"baseline (before window): GDI {gdiStart} USER {userStart}");

        using var watchdog = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(40), Timeout.InfiniteTimeSpan);

        var window = new TrayWindow(log);
        var renderer = new GdiTrayIconRenderer(log);
        var icon = new NotifyIcon(window, renderer.Render(TrayIconContent.Static), log);
        Check(icon.Add(), "Shell_NotifyIcon NIM_ADD + NIM_SETVERSION(4)");
        Report($"icon added: GDI {Gdi()} USER {User()} at {clock.ElapsedMilliseconds} ms");

        // ── renderer: every glyph kind, rendered and destroyed many times; GDI/USER must come back to the same count.
        var contents = SampleContents();
        var gdiBefore = Gdi();
        var userBefore = User();
        var renderFailures = 0;
        var stress = Stopwatch.StartNew();
        for (var round = 0; round < StressRounds; round++)
        {
            foreach (var content in contents)
            {
                var h = renderer.Render(content);
                if (h == IntPtr.Zero) renderFailures++;
                else Native.DestroyIcon(h);
            }
        }
        stress.Stop();
        var renders = StressRounds * contents.Count;
        Check(renderFailures == 0, $"renderer: {renders} renders, {renderFailures} failed");
        Check(Gdi() == gdiBefore && User() == userBefore,
            $"renderer: GDI {gdiBefore}->{Gdi()}, USER {userBefore}->{User()} after {renders} render+DestroyIcon");
        Report($"renderer: {stress.Elapsed.TotalMilliseconds / renders:0.000} ms per 32 px render + CreateIconIndirect + DestroyIcon");

        try
        {
            var sheet = Path.Combine(directory, "tray-selftest-glyphs.png");
            WriteContactSheet(sheet, contents);
            Report($"contact sheet: {sheet}");
        }
        catch (Exception ex)
        {
            Check(false, $"contact sheet: {ex.Message}");
        }

        // ── pipe security on a private name (the real pipe belongs to the agent).
        CheckPipeLabel(Check, Report, log);

        // ── window behaviour, driven by window timers so it all happens inside the real message loop.
        var deviceChanges = new List<long>();
        var shutdownRequests = new List<string>();
        var shutdownWaits = 0;
        window.DevicesChanged = () => deviceChanges.Add(clock.ElapsedMilliseconds);
        window.ShutdownRequested = source => shutdownRequests.Add(source);
        window.WaitForShutdown = _ => { shutdownWaits++; return true; };
        window.TaskbarCreated = icon.Readd;
        var taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");

        var tickGdi = new List<uint>();
        var tick = 0;
        void CycleIcon()
        {
            var content = contents[tick % contents.Count];
            var summary = new TraySummary
            {
                ConnectedDevices = new[]
                {
                    new BatteryDevice { Id = "selftest:1", Name = "Self-test mouse", Source = "SelfTest", Percent = 10 + tick * 9, IsConnected = true },
                    new BatteryDevice { Id = "selftest:2", Name = "Self-test keyboard with a rather long name", Source = "SelfTest", Percent = 77, IsConnected = true },
                },
                CpuLoadPercent = 12 + tick,
                CpuTempC = 48 + tick,
            };
            icon.Update(renderer.Render(content), TrayContent.BuildTooltip(summary));
            tickGdi.Add(Gdi());
            tick++;
            if (tick < 10) window.StartTimer(TimeSpan.FromSeconds(1), CycleIcon);
        }
        window.StartTimer(TimeSpan.FromMilliseconds(500), CycleIcon);

        var posted = 0;
        window.StartTimer(TimeSpan.FromSeconds(1.5), () =>
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (var i = 0; i < 1000; i++) window.Post(() => posted++);
            });
        });

        // Device changes: a burst of three plus a display change → one call now, one trailing call ~2 s later.
        long burstAt = 0;
        window.StartTimer(TimeSpan.FromSeconds(2), () =>
        {
            burstAt = clock.ElapsedMilliseconds;
            for (var i = 0; i < 3; i++) Native.PostMessageW(window.Handle, Native.WM_DEVICECHANGE, Native.DBT_DEVNODES_CHANGED, IntPtr.Zero);
            Native.PostMessageW(window.Handle, Native.WM_DISPLAYCHANGE, 32, IntPtr.Zero);
        });
        window.StartTimer(TimeSpan.FromSeconds(2.3), () =>
            Check(deviceChanges.Count == 1 && deviceChanges[0] - burstAt < 250,
                $"device change: leading edge fired once, {(deviceChanges.Count > 0 ? deviceChanges[0] - burstAt : -1)} ms after the burst"));
        window.StartTimer(TimeSpan.FromSeconds(4.6), () =>
            Check(deviceChanges.Count == 2 && deviceChanges[1] - deviceChanges[0] is >= 1900 and < 2600,
                $"device change: one trailing call {(deviceChanges.Count > 1 ? deviceChanges[1] - deviceChanges[0] : -1)} ms after the first ({deviceChanges.Count} calls)"));
        long lateAt = 0;
        window.StartTimer(TimeSpan.FromSeconds(7), () =>
        {
            lateAt = clock.ElapsedMilliseconds;
            Native.PostMessageW(window.Handle, Native.WM_DEVICECHANGE, Native.DBT_DEVNODES_CHANGED, IntPtr.Zero);
        });
        window.StartTimer(TimeSpan.FromSeconds(7.3), () =>
            Check(deviceChanges.Count == 3 && deviceChanges[2] - lateAt < 250,
                $"device change: a quiet period later the next broadcast fires at once ({deviceChanges.Count} calls)"));

        // A menu is built and destroyed (not shown: TrackPopupMenuEx would take the foreground from the user).
        window.StartTimer(TimeSpan.FromSeconds(5), () =>
        {
            uint gdi = Gdi(), user = User();
            for (var i = 0; i < 20; i++)
            {
                var menu = Native.CreatePopupMenu();
                Native.AppendMenuW(menu, Native.MF_STRING, 1, Strings.MenuOpen);
                Native.AppendMenuW(menu, Native.MF_STRING, 2, Strings.MenuRefresh);
                Native.AppendMenuW(menu, Native.MF_STRING, 3, Strings.MenuElevate);
                Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
                Native.AppendMenuW(menu, Native.MF_STRING, 4, Strings.MenuSettings);
                Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
                Native.AppendMenuW(menu, Native.MF_STRING, 5, Strings.MenuQuit);
                Native.DestroyMenu(menu);
            }
            Check(Gdi() == gdi && User() == user, $"menu: 20 build/destroy cycles, GDI {gdi}->{Gdi()} USER {user}->{User()}");
        });

        // Session end and Explorer restart, as synthetic messages to our own window.
        window.StartTimer(TimeSpan.FromSeconds(8), () =>
        {
            Native.PostMessageW(window.Handle, Native.WM_QUERYENDSESSION, IntPtr.Zero, IntPtr.Zero);
            Native.PostMessageW(window.Handle, Native.WM_ENDSESSION, 1, IntPtr.Zero);
            Native.PostMessageW(window.Handle, taskbarCreated, IntPtr.Zero, IntPtr.Zero);
            Native.PostMessageW(window.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        });
        window.StartTimer(TimeSpan.FromSeconds(8.5), () =>
        {
            Check(shutdownRequests.SequenceEqual(new[] { "WM_QUERYENDSESSION", "WM_CLOSE" }) && shutdownWaits == 1,
                $"session end: shutdown requested by [{string.Join(", ", shutdownRequests)}], WM_ENDSESSION joined {shutdownWaits}x");
            Check(icon.IsAdded, "TaskbarCreated: icon re-added");
            Check(posted == 1000, $"Post queue: {posted}/1000 actions ran on the window thread");
        });

        window.StartTimer(TimeSpan.FromSeconds(10.5), window.RequestExit);
        window.Run();

        Check(tickGdi.Count == 10 && tickGdi.Min() == tickGdi.Max(),
            $"icon swaps: GDI per tick {(tickGdi.Count == 0 ? "-" : $"{tickGdi.Min()}..{tickGdi.Max()}")} over {tickGdi.Count} NIM_MODIFY swaps");
        Report($"before removal: GDI {Gdi()} USER {User()}");
        icon.Dispose();
        window.Dispose();
        Report($"after NIM_DELETE + DestroyIcon + DestroyWindow: GDI {Gdi()} USER {User()} (baseline {gdiStart} / {userStart})");
        using (var process = Process.GetCurrentProcess())
        {
            process.Refresh();
            Report($"process: WS {process.WorkingSet64 / 1048576.0:0.0} MB, private {process.PrivateMemorySize64 / 1048576.0:0.0} MB, " +
                   $"threads {process.Threads.Count}, handles {process.HandleCount}, CPU {process.TotalProcessorTime.TotalMilliseconds:0} ms");
        }

        Report(failures.Count == 0 ? "RESULT: all checks passed" : $"RESULT: {failures.Count} check(s) failed: {string.Join("; ", failures)}");
        return failures.Count == 0 ? 0 : 1;
    }

    private static uint Gdi() => Native.GuiResources(Native.GR_GDIOBJECTS);

    private static uint User() => Native.GuiResources(Native.GR_USEROBJECTS);

    private static void CheckThrottleRules(Action<bool, string> check)
    {
        var t = new LeadingEdgeThrottle(TimeSpan.FromSeconds(2));
        var ok = t.Signal(0) == (true, null)
                 && t.Signal(100) == (false, 1900)
                 && t.Signal(500) == (false, null)
                 && t.TimerElapsed(2000)
                 && t.Signal(2500) == (false, 1500)
                 && t.TimerElapsed(4000)
                 && t.Signal(7000) == (true, null)
                 && !t.TimerElapsed(7100);
        check(ok, "LeadingEdgeThrottle rules (leading, folded, trailing, quiet)");
    }

    private static void CheckPipeLabel(Action<bool, string> check, Action<string> report, ILogger log)
    {
        var name = $"{IpcProtocol.PipeName}.selftest.{Environment.ProcessId}";
        try
        {
            var factory = AgentPipeFactory.Create(name, log);
            using var first = factory();
            using var second = factory();
            var a = AgentPipeFactory.DescribeSecurity(first);
            var b = AgentPipeFactory.DescribeSecurity(second);
            report($"pipe security (first instance):  {a}");
            report($"pipe security (second instance): {b}");
            check(a.Contains("(ML;;NW;;;ME)", StringComparison.Ordinal) && b.Contains("(ML;;NW;;;ME)", StringComparison.Ordinal),
                "pipe: medium-integrity label on both instances");
            check(!a.Contains(";;;AU)", StringComparison.Ordinal) && !a.Contains(";;;WD)", StringComparison.Ordinal),
                "pipe: only the current user is granted access");

            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var accept = first.WaitForConnectionAsync();
            client.Connect(2000);
            check(accept.Wait(2000) && first.IsConnected, "pipe: a client connects");
        }
        catch (Exception ex)
        {
            check(false, $"pipe: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static List<TrayIconContent> SampleContents()
    {
        var list = new List<TrayIconContent>
        {
            TrayIconContent.Static,
            new(TrayGlyphStyle.Battery, null, null, TrayTone.Good),
            new(TrayGlyphStyle.Battery, null, null, TrayTone.Warning),
            new(TrayGlyphStyle.Battery, null, null, TrayTone.Danger),
            new(TrayGlyphStyle.Battery, null, null, TrayTone.Charging),
            new(TrayGlyphStyle.Ring, "0", 0, TrayTone.Danger),
            new(TrayGlyphStyle.Ring, "5", 0.05, TrayTone.Danger),
            new(TrayGlyphStyle.Ring, "19", 0.19, TrayTone.Danger),
            new(TrayGlyphStyle.Ring, "42", 0.42, TrayTone.Warning),
            new(TrayGlyphStyle.Ring, "87", 0.87, TrayTone.Good),
            new(TrayGlyphStyle.Ring, "99", 0.99, TrayTone.Danger),
            new(TrayGlyphStyle.Ring, "100", 1.0, TrayTone.Good),
            new(TrayGlyphStyle.Ring, "100", 1.0, TrayTone.Charging),
            new(TrayGlyphStyle.Ring, "7", 0.07, TrayTone.Charging),
            new(TrayGlyphStyle.Ring, null, 0.6, TrayTone.Neutral),
        };
        // Through the real rules as well: lowest battery 64 % charging, CPU 83 °C, GPU load 105 % (clamped).
        list.Add(TrayContent.Build(new TraySummary { Lowest = new BatterySummary("x", "x", 64, true, DeviceKind.Mouse) }, TrayIconMode.LowestBattery));
        list.Add(TrayContent.Build(new TraySummary { CpuTempC = 83.4 }, TrayIconMode.CpuTemperature));
        list.Add(TrayContent.Build(new TraySummary { GpuLoadPercent = 105 }, TrayIconMode.GpuTemperature));
        return list;
    }

    // ───────────── glyph contact sheet (PNG, no imaging library) ─────────────

    /// <summary>Every sample at 3× (nearest neighbour) over light, mid-grey and dark backgrounds.</summary>
    private static void WriteContactSheet(string path, IReadOnlyList<TrayIconContent> contents)
    {
        const int size = GdiTrayIconRenderer.Size, scale = 3, cell = size * scale, gap = 6;
        var backgrounds = new uint[] { 0xFFF3F3F3, 0xFF808080, 0xFF202020 };
        int width = contents.Count * (cell + gap) + gap, height = backgrounds.Length * (cell + gap) + gap;
        var sheet = new uint[width * height];
        Array.Fill(sheet, 0xFFFFFFFF);

        var glyph = stackalloc uint[size * size];
        for (var i = 0; i < contents.Count; i++)
        {
            GdiTrayIconRenderer.RenderPixels(contents[i], glyph);
            for (var row = 0; row < backgrounds.Length; row++)
            {
                int ox = gap + i * (cell + gap), oy = gap + row * (cell + gap);
                for (var y = 0; y < cell; y++)
                for (var x = 0; x < cell; x++)
                {
                    sheet[(oy + y) * width + ox + x] = Blend(glyph[(y / scale) * size + x / scale], backgrounds[row]);
                }
            }
        }
        WritePng(path, width, height, sheet);
    }

    private static uint Blend(uint src, uint dst)
    {
        var a = (src >> 24) / 255.0;
        uint Channel(int shift) => (uint)Math.Round(((src >> shift) & 0xFF) * a + ((dst >> shift) & 0xFF) * (1 - a));
        return 0xFF000000 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }

    private static void WritePng(string path, int width, int height, uint[] bgra)
    {
        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(file, "IHDR", header);

        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var line = new byte[1 + width * 4];
            for (var y = 0; y < height; y++)
            {
                line[0] = 0; // filter: none
                for (var x = 0; x < width; x++)
                {
                    var p = bgra[y * width + x];
                    line[1 + x * 4] = (byte)(p >> 16);
                    line[2 + x * 4] = (byte)(p >> 8);
                    line[3 + x * 4] = (byte)p;
                    line[4 + x * 4] = (byte)(p >> 24);
                }
                zlib.Write(line);
            }
        }
        WriteChunk(file, "IDAT", raw.ToArray());
        WriteChunk(file, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        stream.Write(four);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = Crc32(typeBytes, data);
        BinaryPrimitives.WriteUInt32BigEndian(four, crc);
        stream.Write(four);
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var part in new[] { a, b })
        {
            foreach (var value in part)
            {
                crc ^= value;
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return ~crc;
    }
}
