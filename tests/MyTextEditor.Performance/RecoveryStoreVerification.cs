using System.Diagnostics;
using System.IO;
using System.Reflection;
using MyTextEditor.Core;
using MyTextEditor.Services;

internal static class RecoveryStoreVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static RecoverySnapshot Snapshot(Guid id, string text) => new(id, "C:\\logs\\한글.log", DocumentEncodingKind.Utf8Bom,
        "\r\n", 2, 3, text, DateTimeOffset.UtcNow);

    private static async Task CheckNoClaims(RecoveryStore observer, string message)
    {
        var claims = await observer.ClaimOrphanSessionsAsync();
        try { Check(claims.Count == 0, message); }
        finally { foreach (var claim in claims) claim.Dispose(); }
    }

    public static int Run(string[] args)
    {
        var childIndex = Array.IndexOf(args, "--recovery-child");
        if (childIndex >= 0)
        {
            using var childStore = new RecoveryStore(args[childIndex + 1]);
            childStore.WriteAsync(Snapshot(Guid.Parse(args[childIndex + 2]), "강제 종료 전 미저장 내용 😀\r\nnext")).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(args[childIndex + 1], "ready"), "ready");
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        RunAsync().GetAwaiter().GetResult();
        return 0;
    }

    private static async Task RunAsync()
    {
        var artifactsRoot = Path.GetFullPath(Path.Combine("artifacts", "recovery-tests"));
        var root = Path.GetFullPath(Path.Combine(artifactsRoot, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var id = Guid.NewGuid();
            var expected = Snapshot(id, "saved recovery 한글😀\r\nsecond");
            string documentFile;
            using (var source = new RecoveryStore(root))
            using (var observer = new RecoveryStore(root))
            {
                await source.WriteAsync(expected);
                await CheckNoClaims(observer, "Active process/session must not be offered");
                var orderedId = Guid.NewGuid();
                // Calls intentionally overlap; public API order must survive worker scheduling.
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    var write = source.WriteAsync(Snapshot(orderedId, new string('x', 262144)));
                    var discard = source.DiscardAsync(orderedId);
                    await Task.WhenAll(write, discard);
                    Check(Directory.GetFiles(root, orderedId.ToString("N") + ".recovery", SearchOption.AllDirectories).Length == 0,
                        "A queued write resurrected recovery after discard");
                    var first = source.WriteAsync(Snapshot(orderedId, "earlier"));
                    var remove = source.DiscardAsync(orderedId);
                    var last = source.WriteAsync(Snapshot(orderedId, "latest"));
                    await Task.WhenAll(first, remove, last);
                    var orderedPath = Directory.GetFiles(root, orderedId.ToString("N") + ".recovery", SearchOption.AllDirectories).Single();
                    Check(File.ReadAllText(orderedPath).EndsWith("\nlatest", StringComparison.Ordinal),
                        "Overlapping write/discard operations were reordered");
                    await source.DiscardAsync(orderedId);
                }
                documentFile = Directory.GetFiles(root, "*.recovery", SearchOption.AllDirectories).Single();
                await using (var denyReplace = new FileStream(documentFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try
                    {
                        await source.WriteAsync(expected with { Text = "must not replace" });
                        throw new InvalidOperationException("Expected locked destination write failure");
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                try
                {
                    await source.WriteAsync(expected with { Text = "cancelled replacement" }, cancelled.Token);
                    throw new InvalidOperationException("Expected write cancellation");
                }
                catch (OperationCanceledException) { }
                await File.WriteAllTextAsync(documentFile + ".damaged.tmp", "incomplete");
                await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(documentFile)!, Guid.NewGuid().ToString("N") + ".recovery"), "invalid header\npartial body");
            }

            using (var observer = new RecoveryStore(root))
            using (var otherObserver = new RecoveryStore(root))
            {
                var claims = await observer.ClaimOrphanSessionsAsync();
                try
                {
                    Check(claims.Count == 1 && claims[0].Documents.Count == 1, "Only complete recoveries should be listed");
                    Check(claims[0].Issues.Count == 1, "Damaged completed recovery must be reported without hiding healthy documents");
                    await CheckNoClaims(otherObserver, "Two apps must not claim same recovery session");
                    var recovered = await claims[0].ReadAsync(id);
                    Check(recovered == expected, "Metadata/text did not roundtrip or failed write replaced valid recovery");
                    await claims[0].DiscardAsync(id);
                    Check(!File.Exists(documentFile), "Explicit discard did not remove recovery");
                    // Keep the failed recovery intact until its owner explicitly chooses cleanup.
                    var damaged = Directory.GetFiles(Path.GetDirectoryName(documentFile)!, "*.recovery").Single();
                    Check(File.ReadAllText(damaged) == "invalid header\npartial body", "Invalid recovery was changed or deleted");
                    File.Delete(damaged);
                }
                finally { foreach (var claim in claims) claim.Dispose(); }
            }
            Console.WriteLine("PASS recovery: metadata/body roundtrip, live-session and claim isolation, ordered overlapping writes/discards, write failure/cancellation preservation, temporary-file exclusion, explicit discard");

            var crashedId = Guid.NewGuid();
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test executable");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--recovery-store");
            start.ArgumentList.Add("--recovery-child");
            start.ArgumentList.Add(root);
            start.ArgumentList.Add(crashedId.ToString());
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not launch recovery crash child");
            try
            {
                var timer = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(root, "ready")) && !child.HasExited && timer.Elapsed < TimeSpan.FromSeconds(15))
                    await Task.Delay(50);
                Check(File.Exists(Path.Combine(root, "ready")), "Crash child did not complete recovery checkpoint");
                using var observer = new RecoveryStore(root);
                await CheckNoClaims(observer, "Running child recovery exposed as abandoned");
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                var claims = await observer.ClaimOrphanSessionsAsync();
                try
                {
                    Check(claims.Count == 1, "Forced exit did not release session lock");
                    var recovered = await claims[0].ReadAsync(crashedId);
                    Check(recovered.Text == "강제 종료 전 미저장 내용 😀\r\nnext", "Forced-exit recovery body changed");
                    Check(recovered.Encoding == DocumentEncodingKind.Utf8Bom, "Forced-exit encoding metadata changed");
                    await claims[0].DiscardAsync(crashedId);
                }
                finally { foreach (var claim in claims) claim.Dispose(); }
                Console.WriteLine("PASS recovery: actual child-process forced termination retains latest complete checkpoint");
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            }
        }
        finally
        {
            // Only delete this test's explicitly resolved workspace artifact directory.
            if (!root.StartsWith(artifactsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe recovery test cleanup path");
            Directory.Delete(root, recursive: true);
        }
    }
}
