using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScriptAppDashboard.Models;

namespace ScriptAppDashboard.Services;

/// <summary>
/// Reads the text of a picture with the Windows OCR engine (Windows.Media.Ocr), through Windows PowerShell 5.1 — the
/// only PowerShell that can load Windows Runtime classes — so no package is needed. The text is what makes a
/// screenshot searchable, and its rectangles let the AI point at things in the picture. Never throws.
/// </summary>
public static class OcrService
{
    public static bool IsAvailable => OperatingSystem.IsWindows();

    private const string Script = """
        param([string]$Path)
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null = [Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
        $null = [Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics,ContentType=WindowsRuntime]
        $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
        function Await($op, $type) {
            $task = $asTask.MakeGenericMethod($type).Invoke($null, @($op))
            $null = $task.Wait(-1)
            $task.Result
        }
        $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
        $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
        if ($null -eq $engine) { '[]'; exit 0 }
        $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        $lines = @()
        foreach ($line in $result.Lines) {
            $x1 = [double]::MaxValue; $y1 = [double]::MaxValue; $x2 = 0.0; $y2 = 0.0
            foreach ($w in $line.Words) {
                $r = $w.BoundingRect
                if ($r.X -lt $x1) { $x1 = $r.X }
                if ($r.Y -lt $y1) { $y1 = $r.Y }
                if ($r.X + $r.Width -gt $x2) { $x2 = $r.X + $r.Width }
                if ($r.Y + $r.Height -gt $y2) { $y2 = $r.Y + $r.Height }
            }
            $lines += [pscustomobject]@{ Text = $line.Text; X = $x1; Y = $y1; W = ($x2 - $x1); H = ($y2 - $y1) }
        }
        ConvertTo-Json -InputObject $lines -Compress
        """;

    /// <summary>The text lines of a picture file (PNG), top to bottom; empty when there is none or OCR is unavailable.</summary>
    public static Task<List<OcrLine>> ReadAsync(string imagePath, CancellationToken cancel = default)
        => Task.Run(() => Read(imagePath, cancel), cancel);

    private static List<OcrLine> Read(string imagePath, CancellationToken cancel)
    {
        var lines = new List<OcrLine>();
        if (!IsAvailable) return lines;
        var script = Path.Combine(Path.GetTempPath(), $"toolshed360-ocr-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(script, Script, new UTF8Encoding(true));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, imagePath })
                psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi);
            if (proc is null) return lines;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(60_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* gone */ }
                return lines;
            }
            cancel.ThrowIfCancellationRequested();
            var json = stdout.GetAwaiter().GetResult().Trim();
            if (!json.StartsWith('[')) return lines;
            var parsed = JsonSerializer.Deserialize<List<OcrLine>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is not null)
                foreach (var l in parsed)
                    if (!string.IsNullOrWhiteSpace(l.Text)) lines.Add(l);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // OCR is a bonus: a capture is saved and searchable by its annotations without it
        }
        finally
        {
            try { File.Delete(script); } catch { /* temp folder */ }
        }
        return lines;
    }

    /// <summary>Runs OCR for a saved capture in the background and stores the lines (once). Returns the updated item.</summary>
    public static async Task<CaptureItem> EnsureAsync(CaptureItem item, CancellationToken cancel = default)
    {
        if (item.OcrDone || !IsAvailable || !File.Exists(CaptureService.ImagePath(item.Id))) return item;
        var lines = await ReadAsync(CaptureService.ImagePath(item.Id), cancel);
        // reload: the user may have annotated or titled it meanwhile, and that must not be overwritten
        var fresh = CaptureService.Load(item.Id) ?? item;
        fresh.Ocr = lines;
        fresh.OcrDone = true;
        CaptureService.Save(fresh);
        return fresh;
    }
}
