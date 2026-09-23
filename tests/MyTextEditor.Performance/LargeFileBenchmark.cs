using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor.Controls;
using MyTextEditor.Core;
using MyTextEditor.Core.Macros;
using Application = System.Windows.Application;

internal static class LargeFileBenchmark
{
    public static int Run(string path, string scenario)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var host = new ScintillaEditorHost();
        var window = new Window { Width = 1000, Height = 600, Content = host, ShowInTaskbar = false };
        int exitCode = 0;
        window.Loaded += async (_, _) =>
        {
            try
            {
                var process = Process.GetCurrentProcess();
                var load = Stopwatch.StartNew();
                var buffer = await new DocumentFileService().LoadBufferAsync(path);
                host.LoadUtf8(buffer.Utf8Buffer);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                load.Stop();
                var capture = Stopwatch.StartNew();
                var source = host.GetText();
                capture.Stop();
                var sourceHash = Hash(source);
                var macro = new MacroDefinition { Name = "Large file benchmark", Steps =
                [
                    new MacroStep { Operation = MacroOperation.Search, AllTerms = ["AAA", "BBB"], ExcludeTerms = ["CC"] },
                    new MacroStep { Operation = MacroOperation.AddPrefix, Target = MacroTarget.MatchedLines, Value = "> " }
                ] };
                if (scenario == "cleanup") macro.Steps.AddRange([
                    new MacroStep { Operation = MacroOperation.Replace, Target = MacroTarget.MatchedLines, Value = "AAA", Replacement = "XYZ" },
                    new MacroStep { Operation = MacroOperation.TrimWhitespace }
                ]);
                else if (scenario != "prefix") throw new ArgumentException("Use prefix or cleanup.");
                var heartbeat = Stopwatch.StartNew();
                double lastTick = 0, maxGap = 0;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) => { double now = heartbeat.Elapsed.TotalMilliseconds; maxGap = Math.Max(maxGap, now - lastTick); lastTick = now; };
                timer.Start();
                long allocations = GC.GetTotalAllocatedBytes();
                var watch = Stopwatch.StartNew();
                var result = await Task.Run(() => new TextMacroRunner().Run(source, buffer.NewLine, macro));
                watch.Stop();
                timer.Stop();
                maxGap = Math.Max(maxGap, heartbeat.Elapsed.TotalMilliseconds - lastTick);
                process.Refresh();
                Console.WriteLine($"{Path.GetFileName(path)} scenario={scenario} bytes={buffer.Utf8Buffer.Length} lines={host.LineCount}");
                Console.WriteLine($"load={load.Elapsed.TotalSeconds:F3}s snapshot={capture.Elapsed.TotalSeconds:F3}s macro={watch.Elapsed.TotalSeconds:F3}s ui-gap={maxGap:F0}ms");
                Console.WriteLine($"macro-allocated={(GC.GetTotalAllocatedBytes() - allocations) / 1048576d:F1}MiB working={process.WorkingSet64 / 1048576d:F1}MiB peak={process.PeakWorkingSet64 / 1048576d:F1}MiB");
                var resultHash = Hash(result.Text);
                Console.WriteLine($"result-sha256-utf16={resultHash} changed={result.Steps[^1].ChangedLines}");
                watch.Restart(); host.ReplaceAll(result.Text); watch.Stop();
                Console.WriteLine($"apply={watch.Elapsed.TotalSeconds:F3}s");
                if (Hash(host.GetText()) != resultHash) throw new InvalidOperationException("Apply mismatch");
                host.Undo();
                if (Hash(host.GetText()) != sourceHash) throw new InvalidOperationException("Undo mismatch");
                host.Redo();
                if (Hash(host.GetText()) != resultHash) throw new InvalidOperationException("Redo mismatch");
                process.Refresh();
                Console.WriteLine($"PASS apply/Undo/Redo; total-peak={process.PeakWorkingSet64 / 1048576d:F1}MiB");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { host.ReleaseResources(); window.Close(); app.Shutdown(); }
        };
        window.Show();
        app.Run();
        return exitCode;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan())));
}
