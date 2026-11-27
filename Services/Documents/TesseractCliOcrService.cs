using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace StudyBuddy.Services.Documents;

/// <summary>
/// Local Tesseract OCR runner using ProcessStartInfo.ArgumentList (zero shell injection),
/// strict timeouts, cancellation support, and guaranteed temporary file cleanup.
/// Never sends private documents to external APIs and never fabricates text when OCR is unavailable.
/// </summary>
public sealed class TesseractCliOcrService : IOcrService
{
    private static readonly Dictionary<string, string> LanguageLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "English",
        ["ben"] = "Bengali (বাংলা)",
        ["eng+ben"] = "English + Bengali"
    };

    private readonly IOptionsMonitor<DocumentExtractionOptions> _optionsMonitor;
    private readonly ILogger<TesseractCliOcrService> _logger;

    public TesseractCliOcrService(
        IOptionsMonitor<DocumentExtractionOptions> optionsMonitor,
        ILogger<TesseractCliOcrService> logger)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public OcrAvailability GetAvailability()
    {
        var ocr = _optionsMonitor.CurrentValue.Ocr;
        if (!ocr.Enabled)
        {
            return new OcrAvailability(
                false,
                null,
                Array.Empty<OcrLanguageOption>(),
                "Local OCR is disabled in application configuration.");
        }

        var exePath = ResolveExecutablePath(ocr.ExecutablePath);
        if (exePath is null)
        {
            return new OcrAvailability(
                false,
                null,
                Array.Empty<OcrLanguageOption>(),
                "Tesseract OCR is not installed on this machine. Install Tesseract OCR (and eng/ben language data) to enable image and scanned PDF OCR.");
        }

        var installedLangs = DetectInstalledLanguages(exePath, ocr.TessDataPath);
        var supported = new List<OcrLanguageOption>();

        foreach (var candidate in ocr.SupportedLanguages ?? new[] { "eng", "ben", "eng+ben" })
        {
            var code = candidate.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            var parts = code.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && parts.All(p => installedLangs.Contains(p)))
            {
                var label = LanguageLabels.TryGetValue(code, out var known) ? known : code;
                supported.Add(new OcrLanguageOption(code, label));
            }
        }

        if (supported.Count == 0)
        {
            return new OcrAvailability(
                false,
                exePath,
                Array.Empty<OcrLanguageOption>(),
                "Tesseract OCR was found, but no supported language models (eng.traineddata / ben.traineddata) were detected in tessdata.");
        }

        return new OcrAvailability(
            true,
            exePath,
            supported,
            $"Local Tesseract OCR ready ({string.Join(", ", supported.Select(s => s.Label))}).");
    }

    public async Task<OcrResult> RecognizeImageAsync(
        byte[] pngImageBytes,
        string? languageCode = null,
        CancellationToken cancellationToken = default)
    {
        if (pngImageBytes is null || pngImageBytes.Length == 0)
        {
            return new OcrResult(false, string.Empty, "No image data was provided for OCR.");
        }

        var availability = GetAvailability();
        if (!availability.IsAvailable || string.IsNullOrWhiteSpace(availability.ExecutablePath))
        {
            return new OcrResult(false, string.Empty, availability.StatusMessage);
        }

        var ocr = _optionsMonitor.CurrentValue.Ocr;
        var resolvedLang = ResolveLanguageCode(languageCode, ocr.DefaultLanguage, availability.AvailableLanguages);
        if (resolvedLang is null)
        {
            return new OcrResult(
                false,
                string.Empty,
                $"Requested OCR language '{languageCode}' is not installed in local tessdata.");
        }

        var tempInputPath = Path.Combine(Path.GetTempPath(), $"sb-ocr-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(tempInputPath, pngImageBytes, cancellationToken);

            var psi = new ProcessStartInfo
            {
                FileName = availability.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.ArgumentList.Add(tempInputPath);
            psi.ArgumentList.Add("stdout");
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add(resolvedLang);
            psi.ArgumentList.Add("--psm");
            psi.ArgumentList.Add("3");

            if (!string.IsNullOrWhiteSpace(ocr.TessDataPath) && Directory.Exists(ocr.TessDataPath))
            {
                psi.ArgumentList.Add("--tessdata-dir");
                psi.ArgumentList.Add(ocr.TessDataPath.Trim());
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return new OcrResult(false, string.Empty, "Unable to start the local Tesseract OCR process.");
            }

            var timeout = TimeSpan.FromSeconds(Math.Clamp(ocr.TimeoutSeconds <= 0 ? 30 : ocr.TimeoutSeconds, 5, 120));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new OcrResult(false, string.Empty, $"Local OCR timed out after {(int)timeout.TotalSeconds} seconds.");
            }

            var stdout = await stdoutTask;
            _ = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Tesseract OCR exited with code {ExitCode}.", process.ExitCode);
                return new OcrResult(false, string.Empty, "Local OCR failed to recognize text from this image.");
            }

            return new OcrResult(true, stdout.Trim(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing local Tesseract OCR.");
            return new OcrResult(false, string.Empty, "An unexpected error occurred while running local OCR.");
        }
        finally
        {
            TryDeleteTempFile(tempInputPath);
        }
    }

    private static string? ResolveLanguageCode(
        string? requested,
        string? defaultLang,
        IReadOnlyList<OcrLanguageOption> available)
    {
        if (available.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(requested) &&
            !string.Equals(requested.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            var match = available.FirstOrDefault(a =>
                string.Equals(a.Code, requested.Trim(), StringComparison.OrdinalIgnoreCase));
            return match?.Code;
        }

        if (!string.IsNullOrWhiteSpace(defaultLang))
        {
            var defMatch = available.FirstOrDefault(a =>
                string.Equals(a.Code, defaultLang.Trim(), StringComparison.OrdinalIgnoreCase));
            if (defMatch is not null)
            {
                return defMatch.Code;
            }
        }

        return available[0].Code;
    }

    private static string? ResolveExecutablePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var trimmed = configuredPath.Trim();
            if (File.Exists(trimmed))
            {
                return trimmed;
            }
        }

        var windowsDefaults = new[]
        {
            @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe"
        };

        foreach (var path in windowsDefaults)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var exeName = OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore invalid PATH segments.
            }
        }

        return null;
    }

    private static HashSet<string> DetectInstalledLanguages(string exePath, string? configuredTessData)
    {
        var langs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidateDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredTessData))
        {
            candidateDirs.Add(configuredTessData.Trim());
        }

        var envPrefix = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        if (!string.IsNullOrWhiteSpace(envPrefix))
        {
            candidateDirs.Add(envPrefix.Trim());
            candidateDirs.Add(Path.Combine(envPrefix.Trim(), "tessdata"));
        }

        var exeDir = Path.GetDirectoryName(exePath);
        if (!string.IsNullOrWhiteSpace(exeDir))
        {
            candidateDirs.Add(Path.Combine(exeDir, "tessdata"));
        }

        foreach (var dir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.traineddata"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        langs.Add(name);
                    }
                }
            }
            catch
            {
                // Ignore unreadable directories.
            }
        }

        return langs;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort process cleanup.
        }
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort temp file cleanup.
        }
    }
}

