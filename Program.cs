using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using VisionLanguageAgent;
using NativeFileDialogSharp;

namespace VisionLanguageAgent;

internal static class Program
{
    private static Process? _ollamaProcess = null;

    [STAThread]
    static void Main(string[] args)
    {
        MainAsync(args).GetAwaiter().GetResult();
    }

    static async Task MainAsync(string[] args)
    {
        Console.WriteLine("=======================================");
        Console.WriteLine("         Vision Language Agent         ");
        Console.WriteLine("=======================================\n");

        // 1. Klasör Seçimi (Cross-platform Native Dialog)
        Console.WriteLine("Lütfen açılan pencereden resimlerin bulunduğu klasörü seçin...");
        var result = Dialog.FolderPicker();
        
        if (result.IsCancelled || result.IsError || string.IsNullOrWhiteSpace(result.Path))
        {
            Console.WriteLine("Klasör seçimi iptal edildi veya bir hata oluştu. Uygulama kapatılıyor.");
            return;
        }

        string targetDirectory = result.Path;
        Console.WriteLine($"[BAŞARILI] Seçilen Klasör: {targetDirectory}\n");
        Environment.SetEnvironmentVariable("TargetDirectory", targetDirectory);

        // 2. Ollama Yaşam Döngüsü Kontrolü (Lifecycle)
        string modelName = "llama3.2-vision";
        await EnsureOllamaRunningAsync();
        await EnsureModelPulledAsync(modelName);

        // 3. Worker Servisini Başlat
        try
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddHttpClient();
            builder.Services.AddHostedService<Worker>();
            
            var host = builder.Build();
            Console.WriteLine("\n[Hazır] Arka plan servisi çalışıyor. İşlemleri bitirmek ve çıkmak için Ctrl+C'ye basın...\n");
            
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[HATA] Uygulama çalışırken kritik bir hata oluştu: {ex.Message}");
        }
        finally
        {
            // 4. Kapanış İşlemleri (Modeli bellekten düşür ve servisi durdur)
            await CleanupAsync(modelName);
        }
    }

    private static async Task EnsureOllamaRunningAsync()
    {
        Console.WriteLine("[OLLAMA] Servis durumu kontrol ediliyor...");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            var response = await client.GetAsync("http://localhost:11434/");
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("[OLLAMA] Servis zaten çalışıyor.");
                return;
            }
        }
        catch (Exception)
        {
            // Timeout veya bağlantı reddedildiyse Ollama çalışmıyor demektir.
        }

        Console.WriteLine("[OLLAMA] Servis çalışmıyor. Arka planda başlatılıyor...");

        var startInfo = new ProcessStartInfo
        {
            FileName = "ollama",
            Arguments = "serve",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _ollamaProcess = Process.Start(startInfo);
            Console.WriteLine("[OLLAMA] Servis başlatıldı, hazır olması bekleniyor...");
            
            // Ayağa kalkması için kısa bir bekleme ve ping
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                try
                {
                    var response = await client.GetAsync("http://localhost:11434/");
                    if (response.IsSuccessStatusCode) 
                    {
                        Console.WriteLine("[OLLAMA] Servis API'si aktif olarak yanıt veriyor.");
                        return;
                    }
                }
                catch { /* ignore */ }
            }
            Console.WriteLine("[OLLAMA] Uyarı: Ollama başlatıldı ancak API şu an yanıt vermiyor olabilir.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OLLAMA-HATA] Ollama başlatılamadı! Sistemde kurulu olduğundan ve yolun doğru olduğundan emin olun. Hata: {ex.Message}");
        }
    }

    private static async Task EnsureModelPulledAsync(string modelName)
    {
        Console.WriteLine($"\n[MODEL] Sistemde '{modelName}' modeli kontrol ediliyor...");
        using var client = new HttpClient();
        
        try
        {
            var response = await client.GetAsync("http://localhost:11434/api/tags");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>();
                if (json?.Models != null && json.Models.Any(m => m.Name.StartsWith(modelName, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"[MODEL] '{modelName}' modeli mevcut, indirmeye gerek yok.");
                    return;
                }
            }
        }
        catch
        {
            Console.WriteLine("[MODEL-HATA] Ollama API model listesine ulaşılamadı.");
        }

        Console.WriteLine($"[MODEL] '{modelName}' bulunamadı. İndiriliyor (Pull)...");
        Console.WriteLine("[MODEL] Lütfen bekleyin, bu işlem internet hızınıza göre zaman alabilir (Örn: 5-10 dk).");

        var startInfo = new ProcessStartInfo
        {
            FileName = "ollama",
            Arguments = $"pull {modelName}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process != null)
            {
                process.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Console.WriteLine($"   {e.Data}"); };
                process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Console.WriteLine($"   {e.Data}"); };
                
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                
                await process.WaitForExitAsync();
                Console.WriteLine($"[MODEL] '{modelName}' başarıyla indirildi.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MODEL-HATA] Model indirilirken hata oluştu: {ex.Message}");
        }
    }

    private static async Task CleanupAsync(string modelName)
    {
        Console.WriteLine("\n[TEMİZLİK] Kapanış işlemleri yapılıyor...");
        try
        {
            // Modeli bellekten (VRAM/RAM) düşürmek için keep_alive: 0 parametresiyle boş bir istek atıyoruz.
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var payload = new
            {
                model = modelName,
                keep_alive = 0
            };
            
            await client.PostAsJsonAsync("http://localhost:11434/api/generate", payload);
            Console.WriteLine("[TEMİZLİK] Model bellekten (VRAM) başarılı şekilde temizlendi.");
        }
        catch (Exception)
        {
            Console.WriteLine("[TEMİZLİK] Model bellekten boşaltılamadı (Servis çoktan kapanmış olabilir).");
        }

        // Eğer Ollama servisini bu uygulama kendisi başlattıysa, uygulamayla birlikte sonlandır
        if (_ollamaProcess != null && !_ollamaProcess.HasExited)
        {
            try
            {
                Console.WriteLine("[TEMİZLİK] Arka plandaki Ollama servisi durduruluyor...");
                _ollamaProcess.Kill();
                _ollamaProcess.Dispose();
                Console.WriteLine("[TEMİZLİK] Ollama servisi sonlandırıldı.");
            }
            catch { /* ignore */ }
        }
    }
}

public class OllamaTagsResponse
{
    [JsonPropertyName("models")]
    public List<OllamaModelItem>? Models { get; set; }
}

public class OllamaModelItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
