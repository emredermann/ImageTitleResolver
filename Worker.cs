using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VisionLanguageAgent;

public class Worker(
    ILogger<Worker> logger,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    private readonly SemaphoreSlim _semaphore = new(2, 2);
    private readonly string _targetDirectory = configuration["TargetDirectory"] 
        ?? throw new InvalidOperationException("CRITICAL: 'TargetDirectory' MUST be configured in appsettings.json or via environment variables.");

    private int _totalFilesFound;
    private int _processedFilesCount;
    private int _successfullyRenamed;
    private int _namingCollisionsHandled;
    private int _errorsFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!Directory.Exists(_targetDirectory))
        {
            logger.LogError("Target directory does not exist: {TargetDirectory}", _targetDirectory);
            applicationLifetime.StopApplication();
            return;
        }

        logger.LogInformation("Batch Worker started. Scanning directory recursively: {TargetDirectory}", _targetDirectory);

        try
        {
            string[] files = Directory.EnumerateFiles(_targetDirectory, "*.*", SearchOption.AllDirectories)
                .Where(s => s.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || 
                            s.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            _totalFilesFound = files.Length;

            if (files.Length > 0)
            {
                List<Task> tasks = [];
                foreach (var file in files)
                {
                    await _semaphore.WaitAsync(stoppingToken);

                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            await ProcessImageAsync(file, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _errorsFailures);
                            logger.LogError(ex, "Error processing file: {FileName}", file);
                        }
                        finally
                        {
                            _semaphore.Release();
                        }
                    }, stoppingToken);

                    tasks.Add(task);
                }

                await Task.WhenAll(tasks);
            }
            else
            {
                logger.LogInformation("No image files found in the target directory.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while scanning the directory.");
        }
        finally
        {
            stopwatch.Stop();
            PrintExecutionReport(stopwatch.Elapsed);
            applicationLifetime.StopApplication();
        }
    }

    private async Task ProcessImageAsync(string filePath, CancellationToken stoppingToken)
    {
        var currentFileNumber = Interlocked.Increment(ref _processedFilesCount);
        double percentage = _totalFilesFound > 0 ? (double)currentFileNumber / _totalFilesFound * 100 : 0;
        
        var extension = Path.GetExtension(filePath);
        var originalFileName = Path.GetFileName(filePath);
        var directory = Path.GetDirectoryName(filePath) ?? _targetDirectory;

        logger.LogInformation("[{Current}/{Total} - {Percentage:F1}%] Processing: {OriginalFileName}", currentFileNumber, _totalFilesFound, percentage, originalFileName);

        byte[] imageBytes = await File.ReadAllBytesAsync(filePath, stoppingToken);
        string base64Image = Convert.ToBase64String(imageBytes);

        var prompt = """
                     You are a strict file naming assistant. Look at the image and output ONLY a 2-3 word kebab-case filename describing the main subject. 
                     Do not include file extensions. Do not write any sentences, introductory text, or use any punctuation other than hyphens.

                     Example 1: black-motorcycle
                     Example 2: coffee-cup
                     Example 3: dell-monitor
                     Example 4: white-cat

                     Now, describe the attached image exactly in this format:
                     """;

        string[] images = [base64Image];
        var payload = new
        {
            model = "llama3.2-vision",
            prompt = prompt,
            stream = false,
            images = images,
            options = new
            {
                temperature = 0.0, // Forces deterministic, concise outputs
                top_p = 0.1
            }
        };

        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        var response = await client.PostAsJsonAsync("http://localhost:11434/api/generate", payload, stoppingToken);
        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken: stoppingToken);
        if (string.IsNullOrWhiteSpace(jsonResponse?.Response))
        {
            Interlocked.Increment(ref _errorsFailures);
            logger.LogWarning("[{Current}/{Total} - {Percentage:F1}%] Ollama returned an empty response for file: {OriginalFileName}", currentFileNumber, _totalFilesFound, percentage, originalFileName);
            return;
        }

        var newNameBase = SanitizeFileName(jsonResponse.Response);
        if (string.IsNullOrWhiteSpace(newNameBase))
        {
            Interlocked.Increment(ref _errorsFailures);
            logger.LogWarning("[{Current}/{Total} - {Percentage:F1}%] Model returned invalid or empty name for file: {OriginalFileName}. Raw Response: {RawResponse}", currentFileNumber, _totalFilesFound, percentage, originalFileName, jsonResponse.Response);
            return;
        }

        var newFilePath = GetUniqueFilePath(directory, newNameBase, extension, out bool collisionHandled);
        
        if (collisionHandled)
        {
            Interlocked.Increment(ref _namingCollisionsHandled);
        }

        File.Move(filePath, newFilePath);
        Interlocked.Increment(ref _successfullyRenamed);
        logger.LogInformation("[{Current}/{Total} - {Percentage:F1}%] Successfully renamed {OriginalFileName} to {NewFileName}", currentFileNumber, _totalFilesFound, percentage, originalFileName, Path.GetFileName(newFilePath));
    }

    private void PrintExecutionReport(TimeSpan elapsed)
    {
        var report = $"""
            ==================================================
                        BATCH EXECUTION REPORT
            ==================================================
            Target Directory       : {_targetDirectory}
            Total Elapsed Time     : {elapsed:hh\:mm\:ss\.fff}
            --------------------------------------------------
            Total Files Found      : {_totalFilesFound}
            Successfully Renamed   : {_successfullyRenamed}
            Naming Collisions Fixed: {_namingCollisionsHandled}
            Errors / Failures      : {_errorsFailures}
            ==================================================
            """;
        
        logger.LogInformation("\n{Report}", report);
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = Regex.Replace(name.ToLowerInvariant(), @"[^a-z-]", "");
        sanitized = Regex.Replace(sanitized, @"-+", "-").Trim('-');
        return sanitized;
    }

    private static string GetUniqueFilePath(string directory, string nameBase, string extension, out bool collisionHandled)
    {
        var newFilePath = Path.Combine(directory, $"{nameBase}{extension}");
        collisionHandled = false;

        if (File.Exists(newFilePath))
        {
            collisionHandled = true;
            var counter = 1;
            while (File.Exists(newFilePath))
            {
                newFilePath = Path.Combine(directory, $"{nameBase}_{counter}{extension}");
                counter++;
            }
        }

        return newFilePath;
    }
}

public class OllamaResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("response")]
    public string? Response { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }
}
